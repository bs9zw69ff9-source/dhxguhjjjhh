using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Moderation;
using PavlovBot.Core.Text;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>What the responder did with a VPN verdict.</summary>
public enum VpnBanOutcome
{
    /// <summary>Nothing to act on - clean, or no verdict at all.</summary>
    NoVerdict,

    /// <summary>Flagged, but not by enough detectors to ban. Reported, not actioned.</summary>
    BelowThreshold,

    /// <summary>Refused: a protected account.</summary>
    Master,

    /// <summary>Skipped: exempt, or already banned.</summary>
    Exempt,

    /// <summary>Already banned - nothing re-recorded.</summary>
    AlreadyBanned,

    /// <summary>Already actioned for this address inside the debounce window.</summary>
    Debounced,

    /// <summary>Banned.</summary>
    Banned,
}

/// <summary>
/// Acts on a VPN verdict: bans when enough detectors agree.
/// </summary>
/// <remarks>
/// THIS WAS THE MISSING HALF OF VPN DETECTION, in exactly the shape ban evasion was missing
/// its own. The screening ran on every confirmed connection, merged six providers, computed
/// <see cref="VpnDecision.Actionable"/>, logged <c>action=BAN</c> - and nothing read it. The
/// pipeline was complete and produced no consequence, so a VPN user connected and played
/// while the log said a ban had been decided on.
///
/// The protections are the same ones <see cref="EvasionResponder"/> needs, for the same
/// reasons, plus one this feature needs on its own:
///
///   DEBOUNCED PER ADDRESS. A VPN exit node is shared. Without this, five players behind one
///   commercial VPN produce five bans in a minute from one verdict, and a reconnect loop
///   produces them indefinitely.
///
/// A BAN OVER A CONFIRMER'S OBJECTION IS LABELLED AS SUCH. Two screeners agreeing is now
/// enough even when the tier-2 confirmer disagreed, so the ban record must not claim the
/// confirmation supported it - a ban that misstates its own evidence is the thing an appeal
/// turns on.
/// </remarks>
public sealed class VpnResponder(
    BanService bans,
    IMasterNames masters,
    SerializedStore store,
    AuditLog audit,
    FeedWebhooks feeds,
    MetricsRegistry metrics,
    ILogger<VpnResponder> logger)
{
    /// <summary>
    /// How long one address stays actioned before it may ban again.
    /// </summary>
    /// <remarks>
    /// An hour, because the failure it prevents is a shared exit node banning a queue of
    /// people off one verdict. Long enough to absorb a reconnect loop; short enough that a
    /// genuinely different player on that address tomorrow is still caught.
    /// </remarks>
    private static readonly TimeSpan ActionDebounce = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _actioned = new(StringComparer.Ordinal);

    /// <summary>
    /// Addresses already REPORTED on, separate from those already actioned.
    /// </summary>
    /// <remarks>
    /// One connection is now screened twice - once on the join line, once on the disconnect -
    /// so every outcome that only talks (below threshold, master, exempt) would say the same
    /// thing twice per session without this. The action debounce cannot serve here: it is
    /// consumed only when something is actually done, and these branches do nothing.
    /// </remarks>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _reported = new(StringComparer.Ordinal);

    /// <param name="record">The screening result. Null means nothing could be determined.</param>
    /// <param name="stage">
    /// Where in the connection this verdict was reached - <c>join</c> or <c>disconnect</c>.
    /// Carried into the log and the metrics because "banned as they arrived" and "banned on
    /// the way out" are the difference between the feature working and the feature being
    /// too late, and from a bare ban record they look identical.
    /// </param>
    /// <param name="uniqueId">
    /// The account id, when the caller has a certain one. Strongly preferred over the display
    /// name: <see cref="BanService.HardEnforceAsync"/> otherwise resolves the name through the
    /// tracker, and two accounts that have used one display name resolve to whichever the
    /// tracker saw last. The disconnect line carries both, so there is no reason to guess.
    /// </param>
    public async Task<VpnBanOutcome> RespondAsync(
        string player, VpnRecord? record, string? uniqueId = null, string stage = "connection",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(player) || record is null) return VpnBanOutcome.NoVerdict;

        var decision = record.Decision;
        if (!decision.Flagged) return VpnBanOutcome.NoVerdict;

        var now = DateTimeOffset.UtcNow;

        /* SAID ONCE PER ADDRESS. The join line and the disconnect line both reach here for
           one connection, and the branches below that only talk would otherwise say the same
           thing twice a session. The ban branch has its own window and is unaffected. */
        var announce = ShouldAction(_reported, record.Ip, now, ActionDebounce);

        if (!decision.Actionable)
        {
            /* Flagged but under the threshold. Said out loud rather than dropped: this is
               the state somebody checks when they ask why a known VPN user is still on. */
            if (announce)
            {
                logger.LogInformation("VPN flag NOT actioned for {Player} @ {Ip} at {Stage}: {Reason}",
                    player, record.Ip, stage, decision.Reason);

                await PostAsync($"[VPN] {Sanitize.Message(player)}  |  {VpnVerdict.Of(record).Headline}  |  NOT BANNED - {decision.Reason}")
                    .ConfigureAwait(false);
            }

            metrics.Increment("vpn_bans_total", MetricLabels.Of("outcome", "below_threshold", "stage", stage),
                help: "VPN verdicts by what was done about them");
            return VpnBanOutcome.BelowThreshold;
        }

        if (masters.IsMaster(player))
        {
            /* A master behind a VPN is an owner on a phone hotspot or a work connection,
               not an evader. Locking yourself out of your own server needs console access
               to undo, so this refusal is loud rather than silent. */
            if (announce)
                logger.LogWarning("VPN AUTO-BAN REFUSED - {Player} is a master account ({Reason})", player, decision.Reason);

            metrics.Increment("vpn_bans_total", MetricLabels.Of("outcome", "master", "stage", stage));
            return VpnBanOutcome.Master;
        }

        if (masters.IsExempt(player))
        {
            if (announce) logger.LogInformation("VPN auto-ban skipped - {Player} is exempt", player);
            metrics.Increment("vpn_bans_total", MetricLabels.Of("outcome", "exempt", "stage", stage));
            return VpnBanOutcome.Exempt;
        }

        if (bans.ActiveBans().Any(b => BanRules.SamePlayer(b.PlayerId, player)))
        {
            // Rewriting the record would silently promote an active temp ban to permanent.
            logger.LogDebug("VPN auto-ban skipped - {Player} is already banned", player);
            metrics.Increment("vpn_bans_total", MetricLabels.Of("outcome", "already_banned", "stage", stage));
            return VpnBanOutcome.AlreadyBanned;
        }

        if (!ShouldAction(_actioned, record.Ip, now, ActionDebounce))
        {
            logger.LogInformation("VPN auto-ban skipped for {Player} at {Stage} - {Ip} was already actioned recently",
                player, stage, record.Ip);
            metrics.Increment("vpn_bans_total", MetricLabels.Of("outcome", "debounced", "stage", stage));
            return VpnBanOutcome.Debounced;
        }

        var reason = Label(record);

        await store.UpdateAsync<List<BanRecord>>(Datasets.TempBans, [], list =>
        {
            list.RemoveAll(b => BanRules.SamePlayer(b.PlayerId, player));
            list.Add(new BanRecord
            {
                PlayerId = player,
                /* THE ID WHEN THE CALLER HAS ONE. This path is driven by an address, so it
                   used to store null and leave BanService to resolve the display name at
                   enforce time and again at lift time - correct only for as long as one name
                   maps to one account. The caller reads both off the same disconnect line,
                   so the certain id is recorded rather than re-derived from the name twice. */
                UniqueId = uniqueId,
                Reason = Sanitize.Message(reason),
                Moderator = "auto",
                At = now,
                Expires = null,
                Permanent = true,
                DurationLabel = "Permanent",
                // No staff tier: an automated ban must not outrank a human, or a mod could
                // not lift a false positive without an owner.
                Tier = null,
            });
            return list;
        }, ct).ConfigureAwait(false);

        var enforcement = await bans.HardEnforceAsync(player, uniqueId, ct: ct).ConfigureAwait(false);
        await audit.RecordAsync("vpnban", "auto", player, reason, ct).ConfigureAwait(false);

        metrics.Increment("vpn_bans_total", MetricLabels.Of("outcome", "banned", "stage", stage));
        logger.LogWarning("VPN AUTO-BANNED {Player} @ {Ip} at {Stage} on {Servers} server(s): {Reason}",
            player, record.Ip, stage, enforcement.Servers, reason);

        await PostAsync($"[VPN BAN] {Sanitize.Message(player)}  |  {reason}").ConfigureAwait(false);
        return VpnBanOutcome.Banned;
    }

    /// <summary>
    /// The ban reason, which must never overstate the evidence behind it.
    /// </summary>
    /// <remarks>
    /// PURE, and separate, because the middle case is easy to get wrong and impossible to
    /// notice: a ban is now possible OVER a confirmer's objection, so the wording cannot say
    /// the confirmation agreed when it did not.
    /// </remarks>
    internal static string Label(VpnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        /* THE VERDICT FIRST, from the same place every other surface gets it. This is the
           reason attached to the ban record and shown to the player, so it is also the thing
           quoted back in an appeal - three different wordings of one screening across the
           card, the feed and the ban was how "the bot said I was confirmed" and "the bot said
           it could not confirm" both ended up being true. */
        var summary = VpnVerdict.Of(record);
        var brand = record.Provider is { Length: > 0 } name ? $" ({Sanitize.Message(name)})" : "";

        return $"{summary.Headline}{brand} — {summary.Plain}";
    }

    /// <summary>False while this address is still inside its action window.</summary>
    /// <remarks>PURE, so the shared-exit-node case is testable without a ban service.</remarks>
    internal static bool ShouldAction(
        IDictionary<string, DateTimeOffset> actioned, string ip, DateTimeOffset now, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(actioned);

        if (actioned.TryGetValue(ip, out var last) && now - last < window) return false;
        actioned[ip] = now;

        if (actioned.Count > 500)
        {
            foreach (var stale in actioned.Where(e => now - e.Value > window).Select(e => e.Key).ToList())
                actioned.Remove(stale);
        }

        return true;
    }

    private async Task PostAsync(string line)
    {
        try { await feeds.PostAsync(FeedWebhooks.Connect, line).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not post the VPN action to the connect feed"); }
    }
}
