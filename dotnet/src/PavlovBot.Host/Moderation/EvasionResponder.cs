using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>
/// Acts on a join that matched a standing flag - the ban-evasion catch.
/// </summary>
/// <remarks>
/// THIS WAS THE MISSING HALF OF BAN EVASION. <see cref="IpTrackingService"/> detected the
/// flagged join, logged it, incremented a counter and raised <c>Flagged</c> - and nothing
/// subscribed. The detection worked perfectly and produced no consequence, so an evader
/// reconnected and played, with a warning in the application log that read as if something
/// had been done about it.
///
/// The protections below are not defensive padding. Each is a way this feature bans
/// somebody it must not, and every one of them is reachable on a normal server:
///
///   A MASTER ACCOUNT is never banned by any automated path. Households share addresses,
///   so an owner playing from the same address as somebody they banned matches an address
///   flag. Locking yourself out of your own server needs console access to undo.
///
///   AN EXEMPT ACCOUNT is somebody who just SERVED a ban. Their flags linger until the
///   clean-up sweep, so without this the first reconnection after a temp ban expires turns
///   it into a permanent one - the worst outcome this system can produce.
///
///   AN ALREADY-BANNED PLAYER is enforced, not re-recorded. Rewriting the record would
///   silently promote an active temp ban to permanent every time they retried.
/// </remarks>
public sealed class EvasionResponder
{
    private readonly BanService _bans;
    private readonly IpTrackingService _tracking;
    private readonly IMasterNames _masters;
    private readonly SerializedStore _store;
    private readonly AuditLog _audit;
    private readonly FeedWebhooks _feeds;
    private readonly MetricsRegistry _metrics;
    private readonly ILogger<EvasionResponder> _logger;
    private readonly SecurityAlerts? _alerts;

    /// <param name="alerts">
    /// Where a catch is announced to a person. OPTIONAL - with none, every existing route
    /// (the log, the connect feed, the ban itself) is unchanged and nobody is DM'd.
    /// </param>
    public EvasionResponder(
        IpTrackingService tracking,
        BanService bans,
        IMasterNames masters,
        SerializedStore store,
        AuditLog audit,
        FeedWebhooks feeds,
        MetricsRegistry metrics,
        ILogger<EvasionResponder> logger,
        SecurityAlerts? alerts = null)
    {
        _alerts = alerts;
        ArgumentNullException.ThrowIfNull(tracking);
        _tracking = tracking;
        _bans = bans;
        _masters = masters;
        _store = store;
        _audit = audit;
        _feeds = feeds;
        _metrics = metrics;
        _logger = logger;

        tracking.Flagged += OnFlaggedAsync;
    }

    private async Task OnFlaggedAsync(FlaggedJoin join)
    {
        try
        {
            await RespondAsync(join, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            /* This runs inside log ingestion. An exception here would abort the rest of the
               line's processing, so a failure to ban would also stop the address being
               recorded - losing the evidence as well as the response. */
            _logger.LogError(ex, "Auto-ban failed for {Account}", join.AccountId);
            _metrics.Increment("autoban_errors_total", help: "Auto-ban attempts that threw");
        }
    }

    internal async Task<AutoBanOutcome> RespondAsync(FlaggedJoin join, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(join);

        // The display name is what RCON bans by; the account id is what identifies them.
        var name = join.Name ?? _tracking.Account(join.AccountId)?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            /* No name means nothing to hand RCON. Recording a ban that cannot be enforced
               would be a ban list entry that never removes anybody. */
            _logger.LogWarning("Flagged join for {Account} has no known name - cannot enforce", join.AccountId);
            return AutoBanOutcome.NoName;
        }

        if (_masters.IsMaster(name))
        {
            _logger.LogWarning(
                "AUTO-BAN REFUSED - {Name} is a master account (matched: {Detail}). " +
                "Check whether that flag should exist at all", name, join.Verdict.Detail);
            _metrics.Increment("autoban_refused_total", MetricLabels.Of("reason", "master"),
                help: "Auto-bans refused by a protection");

            /* ALERTED EVEN THOUGH NOTHING WAS DONE, and especially because nothing was done.
               A master account matching an evasion flag is either a stale flag on an owner's
               own address or somebody the bot will never stop - both need a human. */
            await AlertAsync(name, join, "Matched a standing flag, but they are a master account. Nothing was done.", ct)
                .ConfigureAwait(false);
            return AutoBanOutcome.Master;
        }

        if (_masters.IsProtected(name))
        {
            /* An owner has looked at this player and said no. Logged at warning because a
               protection that keeps firing is worth seeing - it means the flag behind it is
               still there and still catching somebody who should not be caught. */
            _logger.LogWarning(
                "AUTO-BAN REFUSED - {Name} is on the never-ban list (matched: {Detail})",
                name, join.Verdict.Detail);
            _metrics.Increment("autoban_refused_total", MetricLabels.Of("reason", "never-ban"),
                help: "Auto-bans refused by a protection");

            await AlertAsync(name, join, "Matched a standing flag, but they are on the never-ban list. Nothing was done.", ct)
                .ConfigureAwait(false);
            return AutoBanOutcome.Protected;
        }

        if (_masters.IsExempt(name))
        {
            // They served a ban and their flags have not been swept yet.
            _logger.LogInformation("Auto-ban skipped - {Name} is exempt after serving a ban", name);
            _metrics.Increment("autoban_refused_total", MetricLabels.Of("reason", "exempt"));
            return AutoBanOutcome.Exempt;
        }

        var now = DateTimeOffset.UtcNow;
        var reason = $"Ban evasion - {join.Verdict.Detail ?? join.Verdict.Match.ToString()}";

        /* THE SERVED-BAN CHECK. A temp ban leaves address and account flags behind, and
           nothing removes them until the ban is lifted - so from the moment the ban expires
           until the expiry sweep gets to it, that player's own leftovers look exactly like
           evasion. Banning there converts the two days they served into forever.

           BanRules.AutoBanDecision has drawn this distinction since it was written. It was
           never called: this method decided for itself from ActiveBans() alone, which cannot
           see an expired record at all, so the served-ban case took the default path and was
           re-banned permanently. The one-hour exemption /unban sets was the only thing
           standing in the way, and it lapses long before the player comes back. */
        var existing = _bans.LoadBans()
            .Where(b => BanRules.SamePlayer(b.PlayerId, name))
            .OrderByDescending(b => b.At)
            .FirstOrDefault();

        if (BanRules.AutoBanDecision(existing, join.Verdict.Detail, now) == AutoBanAction.Lift)
            return await ReleaseServedAsync(name, join, ct).ConfigureAwait(false);

        var already = _bans.ActiveBans().Any(b => BanRules.SamePlayer(b.PlayerId, name));

        if (!already)
        {
            var record = new BanRecord
            {
                PlayerId = name,
                // The id this is enforced against below, so a later lift names the same thing.
                UniqueId = join.AccountId,
                Reason = Sanitize.Message(reason),
                Moderator = "auto",
                At = now,
                Expires = null,
                Permanent = true,
                DurationLabel = "Permanent",
                // No staff tier: an automated ban must not outrank a human, or a mod could
                // not lift a false positive without an owner.
                Tier = null,
            };

            await _store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], bans =>
            {
                // Replace rather than append - two records for one player means an unban
                // lifts one and the other re-catches them on the next sweep.
                bans.RemoveAll(b => BanRules.SamePlayer(b.PlayerId, name));
                bans.Add(record);
                return bans;
            }, ct).ConfigureAwait(false);

            /* THE ACCOUNT ID, AND NOTHING ELSE.

               This used to call RequestFlagAsync, which flags every address the caught
               account has ever confirmed, and then FlagAttachedAccountsAsync, which flagged
               every account that had ever shared one of those addresses. A flag is an
               automatic permanent ban on the next connection, so both of those turned one
               match into several new standing bans - written by the machine, from the
               machine's own conclusion, with nothing human in between.

               That is a feedback loop, and on a residential ISP it is a spreading one: a
               single false positive flags the victim's other addresses, then everyone who
               ever shared any of them, and each of those bans flags more addresses when it
               fires. Nothing expires, so it only ever grows.

               The id is the one thing here that is evidence rather than inference: THIS
               account connected and matched a standing flag. Flag it, ban it, and stop. An
               address flag is a human's call, through /configure. */
            await _tracking.FlagAccountAsync(join.AccountId, ct).ConfigureAwait(false);
        }

        var enforcement = await _bans.HardEnforceAsync(name, join.AccountId, ct: ct).ConfigureAwait(false);

        await _audit.RecordAsync("autoban", "auto", name, reason, ct).ConfigureAwait(false);
        _metrics.Increment("autobans_total", MetricLabels.Of("match", join.Verdict.Match.ToString()),
            help: "Auto-bans issued for a flagged join");

        _logger.LogWarning("AUTO-BANNED {Name} [{Account}] on {Servers} server(s): {Reason}",
            name, join.AccountId, enforcement.Servers, reason);

        // The connect feed is where staff watch joins, so the catch belongs beside the join
        // it responded to rather than only in a channel nobody has open.
        await PostAsync(name, join, already).ConfigureAwait(false);

        await AlertAsync(name, join, already
                ? "Already banned. The ban was enforced again on every server."
                : "Banned permanently, on every server.", ct)
            .ConfigureAwait(false);

        return already ? AutoBanOutcome.EnforcedExisting : AutoBanOutcome.Banned;
    }

    /// <summary>
    /// Release a player whose ban was already served, instead of banning them again.
    /// </summary>
    /// <remarks>
    /// The lift is the point. Returning early would leave the record and the flags exactly
    /// as they are, so the very next connection would arrive here again and again, and the
    /// player would sit unable to play with nothing in any log explaining why. Lifting
    /// clears the record, the flags, the native ban on every server and sets the exemption,
    /// which is what should have happened when the ban expired.
    /// </remarks>
    private async Task<AutoBanOutcome> ReleaseServedAsync(string name, FlaggedJoin join, CancellationToken ct)
    {
        _logger.LogWarning(
            "AUTO-BAN REFUSED - {Name} [{Account}] matched {Detail}, but that is their own served " +
            "ban's leftovers. Lifting it rather than escalating to permanent",
            name, join.AccountId, join.Verdict.Detail);
        _metrics.Increment("autoban_refused_total", MetricLabels.Of("reason", "served"));

        try
        {
            await _bans.LiftAsync(name, join.AccountId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Swallowed deliberately. The player is NOT banned by this path either way - the
               only cost of a failed lift is that the stale flags survive and the next
               connection takes this same branch again. Rethrowing would abort log ingestion
               for the rest of the line. */
            _logger.LogError(ex, "Could not lift the served ban on \"{Name}\" - the stale flags remain", name);
            return AutoBanOutcome.Served;
        }

        await _audit.RecordAsync("autoban-released", "auto", name,
            $"Served ban's flags cleared instead of re-banning - {join.Verdict.Detail}", ct).ConfigureAwait(false);

        await SafePostAsync($"[AUTO-BAN SKIPPED] {Sanitize.Message(name)}  |  {join.Verdict.Detail}" +
                            "  |  their own served ban, lifted instead of re-banning").ConfigureAwait(false);

        await AlertAsync(name, join,
                "Their own served ban's leftovers, not evasion. The stale flags were lifted rather than re-banning.",
                ct).ConfigureAwait(false);

        return AutoBanOutcome.Served;
    }

    private Task PostAsync(string name, FlaggedJoin join, bool already) =>
        /* WHICH LIST THE FLAG CAME FROM. Both read "blacklisted ip 1.2.3.4", and the answer
           to "why was this person banned" is completely different depending on whether an
           owner typed that address in or a previous ban left it behind. Working that out
           used to mean opening /configure and comparing lists by eye. */
        SafePostAsync($"[AUTO-BAN] {Sanitize.Message(name)}  |  {join.Verdict.Detail}" +
                      $" ({(join.Verdict.Manual ? "set by an owner" : "from a ban")})" +
                      (already ? "  |  already banned, removed again" : ""));

    /// <summary>
    /// Tell somebody, whatever was decided.
    /// </summary>
    /// <remarks>
    /// EVERY OUTCOME, including the refusals. "We caught an evader and banned him" and "we
    /// caught an evader and let him in because of a protection" are both things the person
    /// who owns the server wants to hear about, and only one of them is visible from the
    /// ban list afterwards.
    /// </remarks>
    private Task AlertAsync(string name, FlaggedJoin join, string action, CancellationToken ct)
    {
        if (_alerts is not { Enabled: true }) return Task.CompletedTask;

        var source = join.Verdict.Manual ? "set by an owner" : "left by a previous ban";

        return _alerts.PostAsync(new SecurityAlert(
            SecurityAlertKind.Evasion,
            name,
            $"Their join matched {join.Verdict.Detail ?? join.Verdict.Match.ToString()} ({source}).",
            action,
            AccountId: join.AccountId,
            Ip: join.Ip,
            Alts: _tracking.AltsOf(join.AccountId).Select(a => a.Name ?? a.Id).ToList(),
            At: DateTimeOffset.UtcNow), ct);
    }

    private async Task SafePostAsync(string line)
    {
        try
        {
            await _feeds.PostAsync(FeedWebhooks.Connect, line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not post to the connect feed");
        }
    }
}

/// <summary>What the responder did, so the decision is testable without a gateway.</summary>
public enum AutoBanOutcome
{
    /// <summary>No usable name, so nothing could be enforced.</summary>
    NoName,

    /// <summary>Refused: a protected account.</summary>
    Master,

    /// <summary>Refused: an owner put them on the never-ban list.</summary>
    Protected,

    /// <summary>Skipped: they served a ban and their flags have not been swept.</summary>
    Exempt,

    /// <summary>Their ban was already served: lifted rather than escalated to permanent.</summary>
    Served,

    /// <summary>Already banned - enforced again, record untouched.</summary>
    EnforcedExisting,

    /// <summary>Banned.</summary>
    Banned,
}
