using PavlovBot.Core.Data;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>
/// Every moderation action, recorded.
/// </summary>
/// <remarks>
/// Two separate audiences, and the split matters:
///
///   THE APPLICATION LOG is for whoever is debugging the bot. It rotates, it is on the
///   host, and nobody reads it during an argument about a ban.
///
///   THIS is the record staff and players are shown - what <c>/staffactivity</c> reads and
///   what answers "who banned them and when". It has to survive a restart, which the
///   application log does not.
///
/// BOUNDED. A busy server produces thousands of actions a month and the whole dataset is
/// deserialised on every read, so it is trimmed to a ceiling. Losing the oldest entries is
/// the right trade against a file that eventually takes a second to parse - and the ones
/// anybody asks about are recent.
/// </remarks>
public sealed class AuditLog(
    SerializedStore store,
    FeedWebhooks? feeds = null,
    PavlovBot.Host.Events.EventRecorder? timeline = null)
{
    /// <summary>Roughly a year of a busy server, and small enough to parse instantly.</summary>
    public const int MaxEntries = 5000;

    private IStaffLogSink? _sink;

    /// <summary>
    /// Attach a channel to publish actions to. See <see cref="IStaffLogSink"/>.
    /// </summary>
    /// <remarks>
    /// Set from the composition root rather than injected, because posting to a channel needs
    /// the gateway, the gateway is built from every command, and nearly every command needs
    /// this type - a constructor dependency closes that loop and the container cannot resolve
    /// it. The audit log works with no sink at all, which is what every test and every
    /// unconfigured deployment does.
    /// </remarks>
    public void UseSink(IStaffLogSink sink) => _sink = sink;

    public async Task RecordAsync(string action, string moderator, string player, string? reason = null, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;

        await store.UpdateAsync<List<ModAction>>(Datasets.ModLog, [], log =>
        {
            log.Add(new ModAction(action, moderator, player, Sanitize.Message(reason ?? ""), at));

            // Trim from the FRONT - the oldest go first.
            if (log.Count > MaxEntries) log.RemoveRange(0, log.Count - MaxEntries);
            return log;
        }, ct).ConfigureAwait(false);

        /* AND ONTO THE TIMELINE, at the same funnel and for the same reason. Every staff
           action in the bot records an audit entry, so recording here means the timeline is
           complete by construction rather than by seventeen call sites each remembering.

           BEFORE the webhook, because this one cannot fail: Record is a queue write that
           returns immediately and swallows its own errors. Putting it after a network call
           would make the timeline depend on a webhook being reachable. */
        timeline?.Record(new PavlovBot.Core.Events.ServerEvent(
            at,
            PavlovBot.Core.Events.EventMapping.CategoryOf(action),
            PavlovBot.Core.Events.EventMapping.KindOf(action),
            Player: player,
            Actor: moderator,
            Detail: reason));

        /* AND TO THE STAFF CHANNEL, from here rather than from each command.

           Every staff action in the bot already funnels through this method - seventeen call
           sites, from bans and kicks to /serverswitch and the automatic VPN bans. Posting at
           the funnel means the feed is complete by construction: a command added next year
           appears in the staff channel because it records an audit entry, not because
           somebody remembered to post one too. Doing it per command is how a staff log ends
           up missing exactly the action nobody thought to wire up.

           RECORDED FIRST, POSTED SECOND, and the failure of the second never touches the
           first. The dataset is the record that has to survive; the channel is a convenience,
           and a dead webhook must not be able to lose a ban from the audit trail. */
        if (feeds is not null)
        {
            try
            {
                await feeds.PostStaffAsync(action, moderator, player, reason, at, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // FeedWebhooks already logs and records its own failures, and /feeds reports
                // them. Rethrowing here would fail the audit write for a cosmetic reason.
            }
        }

        /* AND TO A CHANNEL BY ID, if one is configured. Both destinations can be on at once
           and they are not redundant: a webhook posts under its own name and does not need
           the gateway connected, a channel id posts as the bot and survives being moved. */
        if (_sink is not null)
        {
            try
            {
                await _sink.PostAsync(
                    new ModAction(action, moderator, player, Sanitize.Message(reason ?? ""), at), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Same rule: the record is written and cannot be un-written.
            }
        }
    }

    public IReadOnlyList<ModAction> All() => store.Read<List<ModAction>>(Datasets.ModLog, []);

    /// <summary>
    /// Newest first, with ties broken by insertion order rather than left to chance.
    /// </summary>
    /// <remarks>
    /// Timestamps are stored as whole milliseconds (see <c>EpochMillisecondsConverter</c> -
    /// the format the Node bot reads), so two actions taken in the same millisecond carry
    /// the SAME <c>At</c>. <c>OrderByDescending</c> is a stable sort, so it leaves tied
    /// entries in source order - which is append order, oldest first - and a staffer who
    /// kicks two players in one burst sees them listed backwards.
    ///
    /// Reversing before the sort is what fixes it: the log is append-only and trimmed from
    /// the front, so its order IS chronological order. Reversed, the stable sort keeps the
    /// later-recorded action ahead of the earlier one whenever their timestamps tie.
    /// </remarks>
    private static IEnumerable<ModAction> NewestFirst(IEnumerable<ModAction> actions) =>
        actions.Reverse().OrderByDescending(a => a.At);

    /// <summary>One staffer's actions, most recent first.</summary>
    public IReadOnlyList<ModAction> By(string moderator, DateTimeOffset? since = null) =>
        NewestFirst(All())
            .Where(a => string.Equals(a.Moderator, moderator, StringComparison.OrdinalIgnoreCase))
            .Where(a => since is null || a.At >= since)
            .ToList();

    /// <summary>Everything done TO one player, most recent first.</summary>
    public IReadOnlyList<ModAction> Against(string player) =>
        NewestFirst(All())
            .Where(a => string.Equals(a.Player, player, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
