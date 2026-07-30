using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Core.Time;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <param name="Servers">How many servers accepted a command. Zero means the ban did NOT land.</param>
/// <param name="Target">The identifier actually sent, which may differ from the display name.</param>
public readonly record struct EnforcementResult(int Servers, string? Target)
{
    public bool Landed => Servers > 0;
}

/// <summary>
/// Applying and lifting bans against the game servers.
/// </summary>
/// <remarks>
/// The rule that governs everything here: <b>Pavlov's Ban, Kick and Unban take a UniqueId,
/// not a display name.</b> Passing a display name does not error - the server accepts the
/// command and does nothing, so the ban "succeeds" and the player stays on the server. That
/// silent no-op is why every path prefers a UniqueId from RefreshList and logs loudly when
/// the identifier it sent differs from the one it was given.
///
/// The OS firewall is deliberately NEVER touched from here. A ufw rule is an owner-managed
/// manual action through <c>/firewall</c>; automating it means a false-positive ban can cut
/// off a whole household or a shared NAT, and nothing in the bot would know to undo it.
/// </remarks>
public sealed class BanService
{
    private readonly RconRegistry _rcon;
    private readonly SerializedStore _store;
    private readonly IMasterNames _masterNames;
    private readonly ILogger<BanService> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// A full reconcile opens a connection per active ban, so it is rate-limited hard.
    /// </summary>
    /// <remarks>
    /// This used to run 15 seconds after every BOT restart, which does not touch the game
    /// server's ban list at all - so on a large ban list every deploy hammered RCON with
    /// dozens of fresh connections for nothing. Native bans survive a bot restart on their
    /// own; only a GAME server restart loses them, and that is rare enough for 30 minutes.
    /// </remarks>
    public static readonly TimeSpan ReconcileMinimumInterval = TimeSpan.FromMinutes(30);

    /// <summary>Pacing between re-bans during a reconcile, so a big list does not flood RCON.</summary>
    private static readonly TimeSpan ReconcilePacing = TimeSpan.FromMilliseconds(400);

    public BanService(
        RconRegistry rcon,
        SerializedStore store,
        IMasterNames masterNames,
        ILogger<BanService> logger,
        TimeProvider? time = null)
    {
        _rcon = rcon;
        _store = store;
        _masterNames = masterNames;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    public IReadOnlyList<BanRecord> LoadBans()
    {
        var all = _store.Read<List<BanRecord>>(Datasets.TempBans, []);

        /* Invalid records are SKIPPED, not repaired and not fatal. A corrupt entry
           enforces nothing either way; dropping the whole store because one row is bad
           would mean a single malformed record unbans everybody. */
        var valid = all.Where(b => b.IsValid).ToList();
        if (valid.Count != all.Count)
        {
            _logger.LogError("{Bad} of {Total} ban records are unusable and enforce nothing until repaired",
                all.Count - valid.Count, all.Count);
        }
        return valid;
    }

    public IReadOnlyList<BanRecord> ActiveBans() => BanRules.Active(LoadBans(), Now);

    /// <summary>
    /// Ban and kick one player on every server.
    /// </summary>
    /// <param name="uniqueId">
    /// The UniqueId from RefreshList when known. Strongly preferred over the display name:
    /// a display name can contain spaces, which the sanitizer strips, producing a target
    /// the server has never heard of and a ban that silently does nothing.
    /// </param>
    public async Task<EnforcementResult> HardEnforceAsync(
        string name, string? uniqueId = null, bool ban = true, bool kick = true, CancellationToken ct = default)
    {
        var source = uniqueId ?? name;
        var target = Sanitize.Id(source);

        if (target.Length == 0)
        {
            _logger.LogError(
                "ENFORCE FAILED - no usable RCON target for \"{Name}\" (uniqueId={UniqueId}): the identifier sanitized to empty",
                name, uniqueId ?? "none");
            return new EnforcementResult(0, null);
        }

        if (!string.Equals(source, target, StringComparison.Ordinal))
        {
            /* Loud, because the alternative is a ban that appears to succeed. If the ban
               does not land, this mismatch is the answer, and it needs to be in the log
               before anyone goes looking. */
            _logger.LogWarning(
                "ENFORCE TARGET REWRITTEN for \"{Source}\" -> \"{Target}\" (sanitizer stripped characters). " +
                "If the ban does not land, this mismatch is why - the server knows them as \"{Source}\"",
                source, target, source);
        }

        var verbs = new List<string>();
        if (ban) verbs.Add($"Ban {target}");
        if (kick) verbs.Add($"Kick {target}");
        if (verbs.Count == 0) return new EnforcementResult(0, target);

        var accepted = 0;
        await Task.WhenAll(_rcon.Servers.Select(async server =>
        {
            var served = false;
            foreach (var command in verbs)
            {
                try
                {
                    var reply = await _rcon.SendAsync(server, command, ct).ConfigureAwait(false);
                    served = true;
                    _logger.LogInformation("{Server} < {Command} -> {Reply}", server, command, Summarise(reply));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("{Server} < {Command} FAILED: {Message}", server, command, ex.Message);
                }
            }
            if (served) Interlocked.Increment(ref accepted);
        })).ConfigureAwait(false);

        if (accepted == 0)
        {
            _logger.LogError(
                "ENFORCE FAILED - \"{Target}\" was accepted by 0/{Total} server(s); the player is NOT removed",
                target, _rcon.Servers.Count);
        }
        return new EnforcementResult(accepted, target);
    }

    private static string Summarise(string? reply)
    {
        var text = string.Join(" ", (reply ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return "(no response)";
        return text.Length > 140 ? text[..140] : text;
    }

    /// <summary>
    /// Lift a ban on every server.
    /// </summary>
    /// <remarks>
    /// Per-server failures are reported individually rather than rolled into one verdict:
    /// a player left natively banned on one of three servers is a support ticket that
    /// makes no sense to anyone unless this line is in the log.
    /// </remarks>
    public async Task<EnforcementResult> UnbanEverywhereAsync(string name, string? uniqueId = null, CancellationToken ct = default)
    {
        var target = Sanitize.Id(uniqueId ?? name);
        if (target.Length == 0)
        {
            _logger.LogError("unban for \"{Name}\" has NO usable RCON target - the native server ban was NOT lifted", name);
            return new EnforcementResult(0, null);
        }

        var accepted = 0;
        foreach (var server in _rcon.Servers)
        {
            try
            {
                await _rcon.SendAsync(server, $"Unban {target}", ct).ConfigureAwait(false);
                accepted++;
                _logger.LogInformation("unban RCON accepted | \"{Target}\" | {Server}", target, server);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    "unban RCON FAILED | \"{Target}\" | {Server} | {Message} - they may still be natively banned there",
                    target, server, ex.Message);
            }
        }
        return new EnforcementResult(accepted, target);
    }

    /// <summary>
    /// The safety net: remove anyone online who is banned.
    /// </summary>
    /// <remarks>
    /// RefreshList is the AUTHORITATIVE answer to who is actually in the game. Cross-
    /// referencing it against the ban list every sweep is what gets out a player whose
    /// join-time kick missed, or who was already in the game when the ban landed.
    ///
    /// Each offender is enforced ONCE per sweep even when sitting on several servers,
    /// because <see cref="HardEnforceAsync"/> already fans out to all of them. Without
    /// that, an N-server setup fired N enforcements x N servers - N² connections per
    /// offender, on the game thread.
    ///
    /// The sweep deliberately creates NO ban record. The flag that caught them already
    /// persists the auto-ban; recording here made a shared IP mass-create ban entries
    /// wrongly attributed to a person.
    /// </remarks>
    public async Task<int> EnforceSweepAsync(CancellationToken ct = default)
    {
        var banned = ActiveBans()
            .Select(b => b.PlayerId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var server in _rcon.Servers)
        {
            var roster = _rcon.Roster(server);
            foreach (var player in roster.Players)
            {
                var name = player.Name;
                if (name.Length == 0) continue;
                if (_masterNames.IsMaster(name) || _masterNames.IsExempt(name)) continue;
                if (!handled.Add(name)) continue;
                if (!banned.Contains(name)) continue;

                _logger.LogWarning(
                    "ENFORCING - \"{Name}\" [uid={Uid}] is online on {Server} but matches a ban record - removing",
                    name, player.UniqueId.Length > 0 ? player.UniqueId : "unknown", server);

                var result = await HardEnforceAsync(
                    name, player.UniqueId.Length > 0 ? player.UniqueId : null, ct: ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "RESULT - \"{Name}\" target=\"{Target}\" accepted={Accepted}/{Total} -> {Outcome}",
                    name, result.Target ?? "none", result.Servers, _rcon.Servers.Count,
                    result.Landed ? "removed" : "FAILED");

                if (result.Landed) removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Re-apply every active ban to every server.
    /// </summary>
    /// <remarks>
    /// The bot's ban store is the source of truth. A GAME server restart wipes its native
    /// bans, so without this a banned player walks straight back in. Re-banning somebody
    /// already banned is a harmless no-op, which is what makes the ban list self-healing.
    ///
    /// Each ban is RE-VERIFIED at the moment it is issued rather than trusted from the
    /// snapshot taken at the start: an <c>/unban</c> during a long pass removes the record
    /// and sends its Unban, and re-banning from the stale list would natively re-ban them
    /// with nothing left to lift it.
    /// </remarks>
    public async Task<int> ReconcileAsync(bool force = false, CancellationToken ct = default)
    {
        var state = _store.Read<ReconcileState>(Datasets.BanReconcileState, new ReconcileState(null));
        if (!force && state.LastRun is { } last && Now - last < ReconcileMinimumInterval)
        {
            _logger.LogInformation(
                "Skipped ban reconcile - last ran {Minutes}m ago (minimum {Limit}m). " +
                "Native bans persist on the game server across bot restarts on their own",
                (int)(Now - last).TotalMinutes, (int)ReconcileMinimumInterval.TotalMinutes);
            return 0;
        }

        var snapshot = ActiveBans();
        var reconciled = 0;

        foreach (var ban in snapshot)
        {
            ct.ThrowIfCancellationRequested();

            var name = Sanitize.Id(ban.PlayerId);
            if (name.Length == 0 || _masterNames.IsMaster(ban.PlayerId) || _masterNames.IsExempt(ban.PlayerId)) continue;

            // Re-verify at ISSUE time, not from the snapshot. See the remarks.
            var stillBanned = ActiveBans().Any(b => BanRules.SamePlayer(b.PlayerId, ban.PlayerId));
            if (!stillBanned) continue;

            foreach (var server in _rcon.Servers)
            {
                try { await _rcon.SendAsync(server, $"Ban {name}", ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* logged by the client */ }
            }
            reconciled++;
            await Task.Delay(ReconcilePacing, _time, ct).ConfigureAwait(false);
        }

        await _store.WriteAsync(Datasets.BanReconcileState, new ReconcileState(Now), ct).ConfigureAwait(false);
        if (reconciled > 0)
            _logger.LogInformation("Reconciled {Count} active ban(s) across {Servers} server(s)", reconciled, _rcon.Servers.Count);
        return reconciled;
    }

    /// <summary>
    /// Lift bans whose time has been served.
    /// </summary>
    /// <returns>The players released.</returns>
    public async Task<IReadOnlyList<BanRecord>> ProcessExpiredAsync(CancellationToken ct = default)
    {
        var now = Now;
        var expired = new List<BanRecord>();

        await _store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            expired = bans.Where(b => b.IsValid && !b.Permanent && b.Expires is { } e && e <= now).ToList();
            if (expired.Count == 0) return null;   // veto: nothing to write
            return bans.Where(b => !expired.Contains(b)).ToList();
        }, ct).ConfigureAwait(false);

        foreach (var ban in expired)
        {
            await UnbanEverywhereAsync(ban.PlayerId, ct: ct).ConfigureAwait(false);
            _logger.LogInformation("Expired ban lifted: {Player}", ban.PlayerId);
        }
        return expired;
    }

    public sealed record ReconcileState(DateTimeOffset? LastRun);
}

/// <summary>
/// Names that must never be banned, and players exempt from auto-ban re-catching.
/// </summary>
/// <remarks>
/// Two separate protections that both have to be consulted before enforcement:
///
///   MASTER NAMES are the owner's own accounts. Nothing may ban them - not a command, not
///   the auto-ban, not the sweep. Locking yourself out of your own server through a
///   false-positive is unrecoverable without console access.
///
///   EXEMPT players have served a ban. Their flags linger until the clean-up sweep runs,
///   so without the exemption the sweep re-catches them the moment they reconnect and a
///   served ban becomes permanent.
/// </remarks>
public interface IMasterNames
{
    bool IsMaster(string name);
    bool IsExempt(string name);
}
