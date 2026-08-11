using PavlovBot.Rcon;
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
    private readonly IBanEvidence? _evidence;
    private readonly IBanFileExport? _banFile;
    private readonly ILogger<BanService> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// How long a lifted player is protected from being auto-banned again.
    /// </summary>
    /// <remarks>
    /// A BELT-AND-BRACES WINDOW, not the actual mechanism. <see cref="LiftAsync"/> clears the
    /// flags, so nothing should catch them at all; this covers what clearing cannot reach - a
    /// pending flag that a disconnect resolves moments later, or a flag on a different account
    /// that shares their address.
    ///
    /// It used to be the ONLY protection, sized at an hour against a "clean-up sweep" that
    /// was referenced in three files and implemented in none. When it lapsed, the flags were
    /// still there.
    /// </remarks>
    public static readonly TimeSpan LiftExemption = TimeSpan.FromHours(1);

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
        TimeProvider? time = null,
        IBanEvidence? evidence = null,
        IBanFileExport? banFile = null)
    {
        _rcon = rcon;
        _store = store;
        _masterNames = masterNames;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _evidence = evidence;
        _banFile = banFile;
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
        /* LOOK THE ID UP when the caller has none, rather than falling straight to the display
           name. VpnResponder called this with the name alone, so a VPN auto-ban was issued as
           "Ban SomeName" - accepted, answered, and enforcing nothing until the sweep happened
           to catch them online and re-issue it against a real id.

           The same resolution as UnbanEverywhereAsync, deliberately: a ban and its lift have
           to name the same thing, and the cheapest way to guarantee that is for both to
           resolve it the same way in one place. */
        var source = uniqueId ?? _evidence?.AccountIdFor(name) ?? name;
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
        /* THE ID FIRST, ALWAYS, and fall back to looking it up rather than to the display
           name. Pavlov's Unban takes a UniqueId; a display name is accepted, answers
           normally, and lifts nothing. Every ban this bot issues against a known player is
           enforced by id, so unbanning by name could only ever have been a no-op. */
        var resolved = uniqueId ?? _evidence?.AccountIdFor(name);
        var target = Sanitize.Id(resolved ?? name);

        if (target.Length == 0)
        {
            _logger.LogError("unban for \"{Name}\" has NO usable RCON target - the native server ban was NOT lifted", name);
            return new EnforcementResult(0, null);
        }

        if (resolved is null)
        {
            _logger.LogWarning(
                "unban for \"{Name}\" is being sent BY DISPLAY NAME - no account id is recorded for them. " +
                "Pavlov unbans by UniqueId, so this may lift nothing. Check the ban record and the tracked accounts",
                name);
        }

        var accepted = 0;
        foreach (var server in _rcon.Servers)
        {
            try
            {
                await _rcon.SendVerifiedAsync(server, $"Unban {target}", ct).ConfigureAwait(false);
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
        var applied = 0;

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
                /* THE REPLY IS THE ANSWER. This used to discard it and count the ban either
                   way, so a refusal - which the server states plainly - reached nobody and
                   the player stayed unbanned while the log said otherwise. */
                try
                {
                    await _rcon.SendVerifiedAsync(server, $"Ban {name}", ct).ConfigureAwait(false);
                    applied++;
                }
                catch (RconRejectedException ex)
                {
                    _logger.LogError("ban RCON REFUSED | \"{Name}\" | {Server} | {Reply} - they are NOT banned there",
                        name, server, ex.Reply);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError("ban RCON FAILED | \"{Name}\" | {Server} | {Message} - they may NOT be banned there",
                        name, server, ex.Message);
                }
            }
            reconciled++;
            await Task.Delay(ReconcilePacing, _time, ct).ConfigureAwait(false);
        }

        await _store.WriteAsync(Datasets.BanReconcileState, new ReconcileState(Now), ct).ConfigureAwait(false);
        if (reconciled > 0)
        {
            /* BOTH NUMBERS. "Reconciled 12" said nothing about whether any of the twelve
               landed, which is the only part a moderator cares about. */
            var attempts = reconciled * _rcon.Servers.Count;
            _logger.LogInformation(
                "Reconciled {Count} active ban(s) across {Servers} server(s) - {Applied}/{Attempts} accepted",
                reconciled, _rcon.Servers.Count, applied, attempts);
            if (applied < attempts)
                _logger.LogWarning("{Missed} ban command(s) were refused or failed - those players are NOT banned",
                    attempts - applied);
        }
        return reconciled;
    }

    /// <summary>
    /// Lift a ban completely: drop the record, clear the evidence, exempt, and unban natively.
    /// </summary>
    /// <remarks>
    /// ONE OPERATION, because it was two and they disagreed. A ban leaves four things behind
    /// and lifting it has to undo all of them:
    ///
    ///   THE RECORD, or the sweep and the reconcile put the ban straight back.
    ///   THE FLAGS, or the evasion responder issues a fresh PERMANENT ban the next time they
    ///     connect - which is how a served temp ban turned permanent by reconnecting.
    ///   THE NATIVE BAN on each server, by UniqueId.
    ///   And an EXEMPTION covering what clearing cannot reach: a pending flag, or a flag on
    ///     another account sharing their address.
    ///
    /// /unban did the first three and no exemption. The expiry sweep did the record, an
    /// exemption, and an Unban BY DISPLAY NAME that lifted nothing - and never touched the
    /// flags at all. Neither path was complete, and the gaps were different, so the symptom
    /// changed depending on how the ban ended.
    /// </remarks>
    /// <param name="name">The display name the ban is filed under.</param>
    /// <param name="uniqueId">The recorded account id. Looked up when null.</param>
    public async Task<EnforcementResult> LiftAsync(string name, string? uniqueId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        await _store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
        {
            // Removes EVERY record for them. Two records for one player means the lift takes
            // one and the other re-catches them on the next sweep.
            return bans.RemoveAll(b => BanRules.SamePlayer(b.PlayerId, name)) > 0 ? bans : null;
        }, ct).ConfigureAwait(false);

        var account = uniqueId ?? _evidence?.AccountIdFor(name);
        if (account is not null && _evidence is not null)
        {
            try
            {
                await _evidence.ClearFlagsAsync(account, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* LOUD, and it does not abort the lift. The native unban below is what the
                   player is waiting on; leaving them banned because the bookkeeping failed
                   would be the wrong trade. But a lift whose flags survived WILL re-ban them,
                   so this line is the only warning anybody gets. */
                _logger.LogError(ex,
                    "Could not clear the evasion flags for \"{Name}\" [{Account}] - they may be " +
                    "auto-banned again on their next connection. Clear them by hand with /configure",
                    name, account);
            }
        }

        await _masterNames.ExemptAsync(name, LiftExemption, ct).ConfigureAwait(false);

        /* ---- stop the lift being undone ----

           THE BUG THIS FIXES. ModsaveBanlist syncs the game's own ban file every five
           minutes, IMPORTING first: any name in the file that is not in the store is treated
           as a ban to create. Removing the record above leaves the FILE still listing them,
           so the next import re-created the ban that had just been lifted, the export wrote
           it back, and the sweep enforced it. Bans returned on their own, minutes later, and
           /unban reported success every time.

           Two halves, and both are needed:

             THE TOMBSTONE stops the importer resurrecting them. It is the half that survives
             an export that fails, a MODSAVE_BLACKLIST_PATH that is wrong, or a file somebody
             edits by hand - none of which the export below can do anything about.

             THE EXPORT stops the GAME banning them. The server reads that file itself, so a
             player left listed in it stays banned however many Unban commands RCON accepts. */
        await _store.UpdateAsync(Datasets.UnbanTombstones,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            tombstones => { tombstones[name.Trim()] = Now; return tombstones; }, ct).ConfigureAwait(false);

        var result = await UnbanEverywhereAsync(name, account, ct).ConfigureAwait(false);

        if (_banFile is not null)
        {
            try
            {
                await _banFile.ExportAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* NEVER RETHROWN, and the tombstone is why that is safe. The record is gone
                   and the native unban has been sent; a failed file rewrite means the game
                   may keep refusing them until the next sync, which the log has to say. */
                _logger.LogError(ex,
                    "Lifted the ban on \"{Name}\" but could not rewrite the game's ban file. " +
                    "They may still be refused by the server itself until the next banlist sync",
                    name);
            }
        }

        return result;
    }

    /// <summary>
    /// Lift bans whose time has been served.
    /// </summary>
    /// <returns>The players released.</returns>
    public async Task<IReadOnlyList<BanRecord>> ProcessExpiredAsync(CancellationToken ct = default)
    {
        var now = Now;

        /* READ, then lift each one through the shared path. This used to remove them all in
           one store update and then send an Unban per name, which is why the expiry path was
           the one missing the flag clearing - the removal did not go anywhere near it. */
        var expired = LoadBans()
            .Where(b => !b.Permanent && b.Expires is { } e && e <= now)
            .ToList();

        foreach (var ban in expired)
        {
            var result = await LiftAsync(ban.PlayerId, ban.UniqueId, ct).ConfigureAwait(false);
            _logger.LogInformation("Expired ban lifted: {Player} (unban accepted by {Servers} server(s))",
                ban.PlayerId, result.Servers);
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

    /// <summary>Protect a player from auto-ban re-catching for a while.</summary>
    /// <remarks>
    /// ON THE INTERFACE because lifting a ban is where an exemption is granted, and that
    /// lives in <see cref="BanService"/>. It was reachable only through the concrete
    /// MasterNames, so the caller that remembered to grant one was whichever background
    /// service happened to hold that type - which is how the expiry path got an exemption
    /// and /unban did not.
    /// </remarks>
    Task ExemptAsync(string name, TimeSpan? duration = null, CancellationToken ct = default);
}

/// <summary>
/// The evasion evidence a ban leaves behind, behind the narrowest surface that can undo it.
/// </summary>
/// <remarks>
/// AN INTERFACE RATHER THAN IpTrackingService ITSELF, because this is the only part of that
/// class a lift has any business touching, and because the failure it exists to stop needs a
/// test that does not involve a log tailer.
///
/// The failure: lifting a ban removed the record and sent the Unban, and left the address and
/// account-id flags the ban had created. The flags outlive the ban, so the next time that
/// player connected the evasion responder saw a flagged join with no covering ban and issued
/// a fresh PERMANENT one. A served two-day ban became permanent by reconnecting.
/// </remarks>
/// <summary>The game's own ban file, rewritten from the store.</summary>
/// <remarks>
/// A NARROW SEAM so BanService does not depend on ModsaveBanlist. Lifting a ban has to
/// rewrite that file: the GAME reads it, so a player who stays listed there stays banned
/// however many Unban commands RCON accepts - and the importer would re-create the record
/// on its next pass anyway.
/// </remarks>
public interface IBanFileExport
{
    /// <summary>Rewrite the game's ban file from the active bans. Returns how many were written.</summary>
    Task<int> ExportAsync(CancellationToken ct = default);
}

public interface IBanEvidence
{
    /// <summary>The account id a display name belongs to, or null if never seen.</summary>
    string? AccountIdFor(string name);

    /// <summary>Drop the address, name and id flags a ban created for one account.</summary>
    Task ClearFlagsAsync(string accountId, CancellationToken ct = default);
}
