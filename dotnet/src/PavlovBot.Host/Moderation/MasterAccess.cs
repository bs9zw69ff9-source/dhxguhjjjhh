using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>
/// Master names get moderator (through <c>mods.txt</c>) and the RCON+ menu plus access manager
/// (through RCON, every time they join).
/// </summary>
/// <remarks>
/// A PORT GAP. The Node bot granted a master a menu on every join; the C# port kept master
/// names only as ban protection, so the owner's own accounts joined with no powers at all.
///
/// TWO ROUTES, ON PURPOSE. <c>mods.txt</c> is a file the server reads, so it is kept right by
/// checking the file: a missing name is appended, everything else is left alone. The menu and
/// access manager are live, per-session state the server drops on disconnect - there is no
/// file for them - so they go out over RCON after each join.
///
/// ONLY THE SERVER THEY JOINED is sent the grant, and only once per
/// <see cref="RegrantCooldown"/>, so a reconnect flurry does not spam RCON.
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
        TimeSpan? grantDelay = null)
    {
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
        await EnsureModsAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Every configured master name in every install's <c>mods.txt</c>. Never throws.</summary>
    public async Task EnsureModsAsync(CancellationToken ct)
    {
        foreach (var install in _installs)
        {
            foreach (var master in _masters.Masters)
                await EnsureModAsync(install, master, ct).ConfigureAwait(false);
        }
    }

    /// <summary>One name in one install's <c>mods.txt</c>, appended only when it is not there.</summary>
    private async Task EnsureModAsync(string install, string name, CancellationToken ct)
    {
        var entry = WhitelistFile.Entry(name);
        var result = await _files.AddAsync(ModsPath(install), entry, ct).ConfigureAwait(false);

        if (!result.Ok)
            _logger.LogWarning("Could not put master {Name} in {Path}: {Error}", entry, result.Path, result.Error);
        else if (result.Changed)
            _logger.LogInformation("Added master {Name} to {Path}", entry, result.Path);
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

        var work = Task.Run(() => GrantAfterDelayAsync(number, name, _stopping.Token), CancellationToken.None);
        _pending.TryAdd(work, 0);
        _ = work.ContinueWith(t => _pending.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

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
    /// Check <c>mods.txt</c> for that server, then send the menu and access manager.
    /// </summary>
    /// <returns>How many of the RCON lines the server accepted.</returns>
    internal async Task<int> GrantAsync(int number, string name, CancellationToken ct)
    {
        if (number >= 1 && number <= _installs.Count)
            await EnsureModAsync(_installs[number - 1], name, ct).ConfigureAwait(false);

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
