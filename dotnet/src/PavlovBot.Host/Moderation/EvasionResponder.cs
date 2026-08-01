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

    public EvasionResponder(
        IpTrackingService tracking,
        BanService bans,
        IMasterNames masters,
        SerializedStore store,
        AuditLog audit,
        FeedWebhooks feeds,
        MetricsRegistry metrics,
        ILogger<EvasionResponder> logger)
    {
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
            return AutoBanOutcome.Master;
        }

        if (_masters.IsExempt(name))
        {
            // They served a ban and their flags have not been swept yet.
            _logger.LogInformation("Auto-ban skipped - {Name} is exempt after serving a ban", name);
            _metrics.Increment("autoban_refused_total", MetricLabels.Of("reason", "exempt"));
            return AutoBanOutcome.Exempt;
        }

        var already = _bans.ActiveBans().Any(b => BanRules.SamePlayer(b.PlayerId, name));
        var reason = $"Ban evasion - {join.Verdict.Detail ?? join.Verdict.Match.ToString()}";

        if (!already)
        {
            var now = DateTimeOffset.UtcNow;
            var record = new BanRecord
            {
                PlayerId = name,
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

            /* Flag the NEW account id too, so the next alt is caught by the id rather than
               only by an address they can change. Permanent ban, so the id flag is correct
               here - it is exactly what a manual /permban does. */
            await _tracking.RequestFlagAsync(join.AccountId, flagAccountId: true, ct).ConfigureAwait(false);
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

        return already ? AutoBanOutcome.EnforcedExisting : AutoBanOutcome.Banned;
    }

    private async Task PostAsync(string name, FlaggedJoin join, bool already)
    {
        var line = $"[AUTO-BAN] {Sanitize.Message(name)}  |  {join.Verdict.Detail}" +
                   (already ? "  |  already banned, removed again" : "");
        try
        {
            await _feeds.PostAsync(FeedWebhooks.Connect, line).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not post the auto-ban to the connect feed");
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

    /// <summary>Skipped: they served a ban and their flags have not been swept.</summary>
    Exempt,

    /// <summary>Already banned - enforced again, record untouched.</summary>
    EnforcedExisting,

    /// <summary>Banned.</summary>
    Banned,
}
