using PavlovBot.Core.Events;
using PavlovBot.Host.Logs;

namespace PavlovBot.Host.Events;

/// <summary>
/// Player and security events, from the log tracker onto the timeline.
/// </summary>
/// <remarks>
/// THE OTHER HALF OF THE TIMELINE. Staff actions arrive through the audit log, which is one
/// funnel every command already passes through. Joins, confirmations and flagged connections
/// have no such funnel - they are events on <see cref="IpTrackingService"/> - so this
/// subscribes to them in one place rather than having each consumer record its own.
///
/// A SEPARATE CLASS RATHER THAN CODE INSIDE IpTrackingService, because that type's job is
/// turning log lines into account knowledge and it already has five responsibilities. This is
/// a subscriber: it can be left unregistered and nothing else changes behaviour, which is
/// what makes the timeline genuinely optional.
///
/// NO ADDRESSES ARE RECORDED, and that is not an oversight. The timeline is readable by any
/// moderator through /serverstats and the plugin timeline, and an address written into it would be an address in a
/// Discord channel with no redaction step in front of it. The account id is enough to
/// correlate, and /player security is where addresses live behind an Admin gate.
/// </remarks>
public sealed class PlayerEventBridge : IDisposable
{
    private readonly IpTrackingService _tracking;
    private readonly EventRecorder _timeline;

    public PlayerEventBridge(IpTrackingService tracking, EventRecorder timeline)
    {
        ArgumentNullException.ThrowIfNull(tracking);

        _tracking = tracking;
        _timeline = timeline;

        _tracking.Joined += OnJoinedAsync;
        _tracking.Flagged += OnFlaggedAsync;
    }

    private Task OnJoinedAsync(PlayerJoined join)
    {
        _timeline.Record(new ServerEvent(
            join.At,
            EventCategory.Player,
            "player.join",
            Player: join.Name,
            Server: ServerOf(join.File),
            Detail: join.Confident ? null : "address not confirmed at join time"));

        return Task.CompletedTask;
    }

    private Task OnFlaggedAsync(FlaggedJoin flagged)
    {
        /* THE DETECTION, NOT THE RESPONSE. Whatever the evasion responder decides to do
           lands on the timeline separately through the audit log, so recording the match here
           keeps the two distinguishable - "we spotted this" and "we did this about it" are
           different facts and an investigation needs both. */
        _timeline.Record(new ServerEvent(
            flagged.At,
            EventCategory.Security,
            "security.flagged-join",
            Player: flagged.Name,
            Actor: "auto",
            Detail: $"matched a standing flag ({flagged.Verdict.Match})"));

        return Task.CompletedTask;
    }

    /// <summary>
    /// The server a log file belongs to, as a name a person recognises.
    /// </summary>
    /// <remarks>
    /// The tracker identifies a source by log PATH, which is right for it and wrong for a
    /// timeline: nobody filters by "/home/steam/pavlovserver2/Pavlov/Saved/Logs/Pavlov.log".
    /// The install directory is the closest thing to the name used everywhere else.
    /// </remarks>
    internal static string? ServerOf(string? logFile)
    {
        if (string.IsNullOrWhiteSpace(logFile)) return null;

        // .../<install>/Pavlov/Saved/Logs/Pavlov.log -> <install>
        var directory = Path.GetDirectoryName(logFile);
        for (var i = 0; i < 3 && directory is not null; i++) directory = Path.GetDirectoryName(directory);

        var name = Path.GetFileName(directory ?? "");
        return name.Length > 0 ? name : null;
    }

    public void Dispose()
    {
        _tracking.Joined -= OnJoinedAsync;
        _tracking.Flagged -= OnFlaggedAsync;
    }
}
