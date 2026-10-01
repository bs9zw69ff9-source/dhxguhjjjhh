using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Factions;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>
/// Master names get moderator (through <c>mods.txt</c>), every whitelist (each server's
/// <c>whitelist.txt</c> and every faction roster file) and the RCON+ menu plus access manager
/// (through RCON, every time they join).
/// </summary>
/// <remarks>
/// A PORT GAP. The Node bot granted a master a menu on every join; the C# port kept master
/// names only as ban protection, so the owner's own accounts joined with no powers at all.
///
/// TWO ROUTES, ON PURPOSE. <c>mods.txt</c>, <c>whitelist.txt</c> and the rosters are files the
/// server reads, so they are kept right by checking the files: a missing name is appended,
/// everything else is left alone. That happens at start and again on each join, so a roster
/// wipe or a hand edit that dropped a master is repaired the next time they connect. The menu and
/// access manager are live, per-session state the server drops on disconnect - there is no
/// file for them - so they go out over RCON after each join.
///
/// ONLY THE SERVER THEY JOINED is sent the grant, and only once per
/// <see cref="RegrantCooldown"/>, so a reconnect flurry does not spam RCON.
///
/// whitelist.txt IS SAFE TO ADD TO. It is only enforced when Game.ini has <c>bWhitelist=true</c>;
/// otherwise the server ignores it, so adding the owner cannot turn a whitelist on.
///
/// EVERY ROSTER, EVERY RANK. That puts a master in several factions at once, which the bot
/// refuses for anybody else; see <see cref="RosterService.EnsureOnEveryRosterAsync"/>.
///
/// THE GRANT WAITS <see cref="GrantDelay"/>. The join line is written before the player is
/// fully in; a menu sent at that instant targets somebody the server does not list yet.
/// </remarks>
public sealed class MasterAccess : IHostedService, IAsyncDisposable
{
    /// <summary>How long after the join line the grant goes out. The Node bot's figure.</summary>
    public static readonly TimeSpan DefaultGrantDelay = TimeSpan.FromSeconds(8);

    /// <summary>Minimum gap between two grants to one master on one server.</summary>
    public static readonly TimeSpan RegrantCooldown = TimeSpan.FromMinutes(2);

    private readonly MasterNames _masters;
    private readonly IpTrackingService? _tracking;
    private readonly RconRegistry _rcon;
    private readonly ServerLabels _servers;
    private readonly IReadOnlyList<string> _installs;
    private readonly WhitelistFile _files;
    private readonly RosterService? _rosters;
    private readonly PavlovBot.Core.Data.SerializedStore? _store;
    private readonly ILogger<MasterAccess> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _grantDelay;

    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastGrant = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _pending = new();

    public MasterAccess(
        MasterNames masters,
        IpTrackingService? tracking,
        RconRegistry rcon,
        ServerLabels servers,
        IReadOnlyList<string> installs,
        WhitelistFile files,
        ILogger<MasterAccess> logger,
        TimeProvider? clock = null,
        TimeSpan? grantDelay = null,
        RosterService? rosters = null,
        PavlovBot.Core.Data.SerializedStore? store = null)
    {
        _rosters = rosters;
        _store = store;
        _masters = masters;
        _tracking = tracking;
        _rcon = rcon;
        _servers = servers;
        _installs = installs;
        _files = files;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _grantDelay = grantDelay ?? DefaultGrantDelay;
    }

    /// <summary>The <c>mods.txt</c> inside an install.</summary>
    public static string ModsPath(string installRoot) =>
        Path.Combine(installRoot, "Pavlov", "Saved", "Config", "mods.txt");

    /// <summary>
    /// The RCON lines that give a master the menu and access manager.
    /// </summary>
    /// <remarks>
    /// The High Staff bitcode, because it is the fullest menu read off a working grant; see
    /// <see cref="RconMenu"/>. AddAccessManager is sent as well, explicitly, rather than trusting
    /// the bitcode to carry it: this is the owner's account, and the one grant that must not
    /// depend on a positional mask being exactly right.
    /// </remarks>
    public static IReadOnlyList<string> GrantCommands(string player) =>
        [.. RconMenu.Grant(player, RconMenu.HighStaff), $"AddAccessManager {player}"];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_tracking is not null) _tracking.Joined += OnJoinedAsync;
        await RevokeRemovedAsync(cancellationToken).ConfigureAwait(false);
        await EnsureFilesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Take back everything from a name that has left MASTER_NAMES since the last start.
    /// </summary>
    /// <remarks>
    /// THE GRANTS OUTLIVED THE NAME. Removing a master from .env only stopped the bot granting
    /// them again; the mods.txt line, every whitelist.txt line and every roster entry it had added
    /// stayed, so a removed master kept moderator and every faction. The names granted are
    /// recorded (<see cref="Datasets.MasterGrants"/>) so a missing one can be found and undone.
    ///
    /// EVERYTHING A MASTER GETS, NOTHING ELSE: mods.txt and whitelist.txt on every install, every
    /// faction roster, and the RCON+ menu, mod and access manager on every server for a session
    /// still running. A removed master who was ALSO a real faction member loses that membership -
    /// the bot put them on every roster, so it cannot tell which one was theirs.
    ///
    /// A name whose removal did not fully land stays recorded, so the next start tries again.
    /// </remarks>
    internal async Task RevokeRemovedAsync(CancellationToken ct)
    {
        if (_store is null) return;

        var current = _masters.Masters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var granted = _store.Read(Datasets.MasterGrants, new List<string>());
        var removed = granted.Where(n => !current.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var unfinished = new List<string>();

        foreach (var name in removed)
        {
            try
            {
                if (!await RevokeAsync(name, ct).ConfigureAwait(false)) unfinished.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Revoking removed master {Name} failed - will retry on the next start", name);
                unfinished.Add(name);
            }
        }

        await _store.WriteAsync(Datasets.MasterGrants, current.Concat(unfinished).ToList(), ct).ConfigureAwait(false);
    }

    /// <summary>Undo every grant for one former master. False when any file could not be updated.</summary>
    private async Task<bool> RevokeAsync(string name, CancellationToken ct)
    {
        var entry = WhitelistFile.Entry(name);
        var complete = true;

        foreach (var install in _installs)
        {
            foreach (var path in new[] { ModsPath(install), PavlovInstalls.WhitelistPath(install) })
            {
                var result = await _files.RemoveAsync(path, entry, ct).ConfigureAwait(false);
                if (!result.Ok)
                {
                    complete = false;
                    _logger.LogWarning("Could not remove former master {Name} from {Path}: {Error}", entry, result.Path, result.Error);
                }
            }
        }

        if (_rosters is { Enabled: true })
        {
            var (_, failed) = await _rosters.RemoveFromEveryRosterAsync([entry], ct).ConfigureAwait(false);
            if (failed.Count > 0)
            {
                complete = false;
                _logger.LogWarning("Could not take former master {Name} off {Count} roster(s): {Files}",
                    entry, failed.Count, string.Join(", ", failed));
            }
        }

        /* A live session keeps its menu until it ends, so it is taken now too - in the
           background, because this runs during startup and a server that is down would hold the
           whole bot up for its RCON timeouts. Refusals (not online) are expected and quiet. */
        var target = Sanitize.Id(name);
        if (target.Length > 0) Track(Task.Run(() => RevokeSessionsAsync(target, _stopping.Token), CancellationToken.None));

        _logger.LogWarning("MASTER REMOVED | {Name} is no longer in MASTER_NAMES - mods.txt, whitelists, rosters and RCON+ access revoked{Partial}",
            entry, complete ? "" : " (partly - retried next start)");
        return complete;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_tracking is not null) _tracking.Joined -= OnJoinedAsync;
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_pending.Keys).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stopped with {Count} master grant(s) still pending", _pending.Count);
        }
    }

    /// <summary>
    /// Every configured master name in every install's <c>mods.txt</c> and <c>whitelist.txt</c>,
    /// and on every faction roster. Never throws.
    /// </summary>
    public async Task EnsureFilesAsync(CancellationToken ct)
    {
        foreach (var install in _installs)
        {
            foreach (var master in _masters.Masters)
                await EnsureInstallFilesAsync(install, master, ct).ConfigureAwait(false);
        }

        await EnsureRostersAsync(_masters.Masters, ct).ConfigureAwait(false);
    }

    /// <summary>One name in one install's <c>mods.txt</c> and <c>whitelist.txt</c>.</summary>
    private async Task EnsureInstallFilesAsync(string install, string name, CancellationToken ct)
    {
        await EnsureListedAsync(ModsPath(install), name, ct).ConfigureAwait(false);
        await EnsureListedAsync(PavlovInstalls.WhitelistPath(install), name, ct).ConfigureAwait(false);
    }

    /// <summary>One name in one list file, appended only when it is not there.</summary>
    private async Task EnsureListedAsync(string path, string name, CancellationToken ct)
    {
        var entry = WhitelistFile.Entry(name);
        var result = await _files.AddAsync(path, entry, ct).ConfigureAwait(false);

        if (!result.Ok)
            _logger.LogWarning("Could not put master {Name} in {Path}: {Error}", entry, result.Path, result.Error);
        else if (result.Changed)
            _logger.LogInformation("Added master {Name} to {Path}", entry, result.Path);
    }

    private async Task EnsureRostersAsync(IEnumerable<string> names, CancellationToken ct)
    {
        if (_rosters is not { Enabled: true }) return;

        var entries = names.Select(WhitelistFile.Entry).Where(e => e.Length > 0).ToList();
        try
        {
            var (added, failed) = await _rosters.EnsureOnEveryRosterAsync(entries, ct).ConfigureAwait(false);
            if (added > 0)
                _logger.LogInformation("Added master name(s) to the faction rosters: {Added} entr(ies)", added);
            if (failed.Count > 0)
                _logger.LogWarning("Could not put the master name(s) on {Count} roster(s): {Files}",
                    failed.Count, string.Join(", ", failed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Putting the master name(s) on the faction rosters failed");
        }
    }

    private async Task RevokeSessionsAsync(string target, CancellationToken ct)
    {
        foreach (var server in _rcon.Servers)
        {
            foreach (var line in RconMenu.Revoke(target, wasHighStaff: true))
            {
                try { await _rcon.SendAsync(server, line, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    _logger.LogDebug("\"{Line}\" on {Server}: {Message}", line, server, ex.Message);
                }
            }
        }
    }

    /// <summary>Keep a background task until it finishes, so shutdown can wait for it.</summary>
    private void Track(Task work)
    {
        _pending.TryAdd(work, 0);
        _ = work.ContinueWith(t => _pending.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// A join. Masters are handed to a background grant so the log reader is not held up for
    /// the delay; everybody else returns at once.
    /// </summary>
    internal Task OnJoinedAsync(PlayerJoined join)
    {
        ArgumentNullException.ThrowIfNull(join);

        var name = MasterOf(join);
        if (name is null) return Task.CompletedTask;

        if (_servers.NumberOf(join.File) is not { } number)
        {
            _logger.LogWarning("Master {Name} joined through {File}, which is not a numbered server log - no grant sent",
                name, join.File);
            return Task.CompletedTask;
        }

        var key = $"{number}|{name}";
        var now = _clock.GetUtcNow();
        if (_lastGrant.TryGetValue(key, out var last) && now - last < RegrantCooldown) return Task.CompletedTask;
        _lastGrant[key] = now;

        Track(Task.Run(() => GrantAfterDelayAsync(number, name, _stopping.Token), CancellationToken.None));

        return Task.CompletedTask;
    }

    /// <summary>The master name a join belongs to, as sent over RCON; null for anybody else.</summary>
    private string? MasterOf(PlayerJoined join)
    {
        foreach (var candidate in new[] { join.Name, join.AccountId })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && _masters.IsMaster(candidate))
                return Sanitize.Id(candidate) is { Length: > 0 } id ? id : null;
        }
        return null;
    }

    private async Task GrantAfterDelayAsync(int number, string name, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_grantDelay, _clock, ct).ConfigureAwait(false);
            await GrantAsync(number, name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the next join grants again.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Master grant for {Name} on server {Number} failed", name, number);
        }
    }

    /// <summary>
    /// Check that server's <c>mods.txt</c> and <c>whitelist.txt</c> and the rosters, then send the
    /// menu and access manager.
    /// </summary>
    /// <returns>How many of the RCON lines the server accepted.</returns>
    internal async Task<int> GrantAsync(int number, string name, CancellationToken ct)
    {
        if (number >= 1 && number <= _installs.Count)
            await EnsureInstallFilesAsync(_installs[number - 1], name, ct).ConfigureAwait(false);
        // Every master, not just this one: the rosters are shared, and the join name here is the
        // RCON-safe form, which is not always the exact in-game name the files are matched on.
        await EnsureRostersAsync(_masters.Masters, ct).ConfigureAwait(false);

        var server = ServiceControl.RconNameFor(number);
        if (_rcon.Client(server) is null)
        {
            _logger.LogWarning("Master {Name} joined server {Number}, which has no RCON configured - no grant sent",
                name, number);
            return 0;
        }

        var accepted = 0;
        foreach (var line in GrantCommands(name))
        {
            try
            {
                await _rcon.SendVerifiedAsync(server, line, ct).ConfigureAwait(false);
                accepted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* Each line on its own: a refused AddAccessManager must not cost the menu. */
                _logger.LogWarning("Master grant \"{Line}\" on {Server} failed: {Message}", line, server, ex.Message);
            }
        }

        _logger.LogInformation("Master {Name} on {Server}: {Accepted}/{Total} grant command(s) accepted",
            name, server, accepted, GrantCommands(name).Count);
        return accepted;
    }

    public async ValueTask DisposeAsync()
    {
        if (_tracking is not null) _tracking.Joined -= OnJoinedAsync;
        if (!_stopping.IsCancellationRequested) await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
