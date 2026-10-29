namespace PavlovBot.Core.Network;

/// <summary>One second of inbound traffic on the server's link.</summary>
public readonly record struct LinkSecond(DateTimeOffset At, long Packets, long Bytes)
{
    public double Mbps => Bytes * 8 / 1_000_000.0;
}

/// <summary>When traffic counts as an attack.</summary>
/// <param name="Pps">Packets per second that always count, however busy the box normally is.</param>
/// <param name="Mbps">Megabits per second that always count.</param>
/// <param name="BaselineFactor">
/// How many times normal traffic it takes. A busy box's normal is above a quiet box's floor, so
/// the trigger is whichever is HIGHER: the floor, or this multiple of the recent median.
/// </param>
/// <param name="StartSeconds">Consecutive seconds over the trigger before an attack is declared.</param>
/// <param name="QuietSeconds">Consecutive seconds back under it before the attack is declared over.</param>
public sealed record FloodThresholds(
    long Pps = 20_000,
    double Mbps = 200,
    double BaselineFactor = 4,
    int StartSeconds = 3,
    int QuietSeconds = 15);

/// <summary>What one second changed.</summary>
public enum FloodSignal
{
    /// <summary>Nothing worth acting on.</summary>
    Quiet,

    /// <summary>Traffic is over half the trigger: start capturing, an attack may be coming.</summary>
    Watch,

    /// <summary>An attack has begun. Sent once.</summary>
    Started,

    /// <summary>Still under attack.</summary>
    Ongoing,

    /// <summary>The attack is over. Sent once.</summary>
    Ended,
}

/// <summary>
/// Turns per-second link counters into "an attack started" and "it ended".
/// </summary>
/// <remarks>
/// PURE AND CLOCK-FREE: it sees only the samples it is handed, so every edge is testable with a
/// list of numbers.
///
/// A FLOOR AND A MULTIPLE, because each alone is wrong somewhere. A fixed number misfires on a
/// box whose three full servers are normally busy, and a pure multiple of normal fires on an
/// idle box when one player joins. Normal is the median of the last five minutes of
/// non-attack seconds: a median, so one burst does not drag it up, and only non-attack seconds,
/// so a long attack cannot teach the detector that being flooded is normal.
///
/// HYSTERESIS BOTH WAYS. A few seconds over to start and fifteen under to end, so one spike is
/// not an attack and a flood that pulses every few seconds is one attack rather than twenty.
/// </remarks>
public sealed class FloodDetector(FloodThresholds thresholds)
{
    private const int BaselineWindow = 300;

    private readonly Queue<long> _normalPackets = new();
    private readonly Queue<long> _normalBytes = new();
    private int _over;
    private int _under;

    public FloodThresholds Thresholds { get; } = thresholds;

    public bool InAttack { get; private set; }

    /// <summary>The first second over the trigger in the current attack, or of the run leading up to one.</summary>
    public DateTimeOffset? RunStartedAt { get; private set; }

    /// <summary>The packet rate that counts as an attack right now.</summary>
    public long TriggerPps => Math.Max(Thresholds.Pps, (long)(Median(_normalPackets) * Thresholds.BaselineFactor));

    /// <summary>The byte rate that counts as an attack right now, in bytes per second.</summary>
    public long TriggerBytes => Math.Max((long)(Thresholds.Mbps * 1_000_000 / 8),
        (long)(Median(_normalBytes) * Thresholds.BaselineFactor));

    /// <summary>Whether this second is over the trigger on either axis.</summary>
    public bool IsOver(LinkSecond s) => s.Packets >= TriggerPps || s.Bytes >= TriggerBytes;

    public FloodSignal Observe(LinkSecond second)
    {
        var over = IsOver(second);
        var watching = second.Packets >= TriggerPps / 2 || second.Bytes >= TriggerBytes / 2;

        if (!InAttack)
        {
            if (over)
            {
                RunStartedAt ??= second.At;
                if (++_over >= Thresholds.StartSeconds)
                {
                    InAttack = true;
                    _under = 0;
                    return FloodSignal.Started;
                }
                return FloodSignal.Watch;
            }

            _over = 0;
            RunStartedAt = null;
            Learn(second);
            return watching ? FloodSignal.Watch : FloodSignal.Quiet;
        }

        if (over)
        {
            _under = 0;
            return FloodSignal.Ongoing;
        }

        if (++_under < Thresholds.QuietSeconds) return FloodSignal.Ongoing;

        InAttack = false;
        _over = 0;
        RunStartedAt = null;
        return FloodSignal.Ended;
    }

    private void Learn(LinkSecond second)
    {
        _normalPackets.Enqueue(second.Packets);
        _normalBytes.Enqueue(second.Bytes);
        if (_normalPackets.Count > BaselineWindow)
        {
            _normalPackets.Dequeue();
            _normalBytes.Dequeue();
        }
    }

    private static long Median(Queue<long> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}
