using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Servers;

/// <param name="Restarted">Units that had failed and were restarted this sweep.</param>
/// <param name="Suppressed">Units that had failed but were left alone, and why.</param>
public sealed record RecoverySweep(
    IReadOnlyList<string> Restarted, IReadOnlyDictionary<string, string> Suppressed)
{
    public bool DidAnything => Restarted.Count > 0 || Suppressed.Count > 0;

    public static RecoverySweep Nothing { get; } =
        new([], new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>
/// Restarts a Pavlov server that systemd says has crashed.
/// </summary>
/// <remarks>
/// ONLY <c>failed</c>. systemd distinguishes a unit somebody STOPPED (<c>inactive</c>) from
/// one that DIED (<c>failed</c>), and the difference is the whole feature. Restarting on
/// inactive would fight an admin who deliberately took a server down with
/// <c>/serverswitch</c> - they stop it, this starts it, and they conclude the bot is broken.
/// <c>unknown</c> is also left alone: a systemctl that could not be read is not evidence of
/// anything, and treating "cannot tell" as "crashed" is how a monitoring outage becomes a
/// restart storm.
///
/// WHY THIS NEEDS A BRAKE. A server that crashes on startup - bad config, a corrupt map, a
/// full disk - fails again within seconds of every restart. Without a limit this becomes an
/// infinite restart loop that hammers the box, spams the staff channel, and buries the real
/// error under a thousand identical lines. So: at most <see cref="MaxAttempts"/> restarts per
/// unit inside <see cref="AttemptWindow"/>, then it STOPS and says so once. A server that is
/// genuinely broken needs a human, and the most useful thing an automation can do at that
/// point is stand back and be loud about it.
///
/// A COOLDOWN BETWEEN ATTEMPTS on top of the cap, because a Pavlov server takes the better
/// part of a minute to come up. Restarting one that is still starting reports failure for a
/// unit that was fine.
///
/// STATE IS IN MEMORY, deliberately. A bot restart clearing the attempt counters is the
/// correct behaviour: the operator has just intervened, and the next crash after that is new
/// information rather than a continuation.
/// </remarks>
public sealed class CrashRecovery(
    IUnitControl services, ILogger<CrashRecovery> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, Attempts> _attempts = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    private sealed class Attempts
    {
        public List<DateTimeOffset> At { get; } = [];
        public bool GaveUpAnnounced { get; set; }
    }

    /// <summary>Restarts allowed per unit inside <see cref="AttemptWindow"/> before giving up.</summary>
    public static int MaxAttempts { get; } = 3;

    /// <summary>The window the attempt cap is measured over.</summary>
    public static TimeSpan AttemptWindow { get; } = TimeSpan.FromMinutes(30);

    /// <summary>Minimum gap between two restarts of the same unit.</summary>
    /// <remarks>Longer than a Pavlov server takes to come up, so a slow start is never mistaken for a crash.</remarks>
    public static TimeSpan Cooldown { get; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Check every unit and restart the ones that have crashed.
    /// </summary>
    public async Task<RecoverySweep> SweepAsync(CancellationToken ct = default)
    {
        var restarted = new List<string>();
        var suppressed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var unit in services.Units)
        {
            ct.ThrowIfCancellationRequested();

            var state = await services.StateAsync(unit, ct).ConfigureAwait(false);

            if (state is not UnitState.Failed)
            {
                // Back to healthy: forget its history, so a crash next week is judged on its
                // own rather than against something that was already fixed.
                if (state is UnitState.Active) Forget(unit);
                continue;
            }

            var gate = Check(unit);
            if (!gate.Allow)
            {
                // Only a REPORTABLE block goes in the sweep result. A cooldown is a normal
                // part of waiting for a server to come up, not something to alert on.
                if (gate.Report is { } reason) suppressed[unit] = reason;
                continue;
            }

            logger.LogError("{Unit} is in the FAILED state - restarting it", unit);
            RecordAttempt(unit);

            var result = await services.RunAsync(unit, UnitAction.Restart, ct).ConfigureAwait(false);

            if (result.Ok)
            {
                restarted.Add(unit);
                logger.LogWarning("{Unit} had crashed and has been restarted automatically", unit);
            }
            else
            {
                suppressed[unit] = $"restart failed: {result.Detail}";
                logger.LogError("{Unit} had crashed and the automatic restart FAILED: {Detail}", unit, result.Detail);
            }
        }

        return restarted.Count == 0 && suppressed.Count == 0
            ? RecoverySweep.Nothing
            : new RecoverySweep(restarted, suppressed);
    }

    /// <param name="Allow">Whether to restart it now.</param>
    /// <param name="Report">
    /// A reason worth surfacing to staff, or null to skip QUIETLY. The two are not the same:
    /// giving up on a unit is an alert, waiting out a cooldown is routine.
    /// </param>
    private readonly record struct Gate(bool Allow, string? Report);

    /// <summary>
    /// Whether this unit may be restarted right now.
    /// </summary>
    /// <remarks>
    /// The give-up message is produced ONCE per unit. A sweep every minute against a server
    /// that will never start would otherwise post the same alert forever, which is how a
    /// staff channel becomes something people mute.
    /// </remarks>
    private Gate Check(string unit)
    {
        var now = _time.GetUtcNow();

        lock (_sync)
        {
            if (!_attempts.TryGetValue(unit, out var attempts)) return new Gate(true, null);

            attempts.At.RemoveAll(at => now - at > AttemptWindow);

            if (attempts.At.Count >= MaxAttempts)
            {
                if (attempts.GaveUpAnnounced) return new Gate(false, null);   // already said; stay quiet

                attempts.GaveUpAnnounced = true;
                /* Repeated placeholders each consume an argument in a structured template,
                   so the unit is passed once per mention. It is worth the repetition: the
                   two commands to run are what makes this line actionable at 4am. */
                logger.LogCritical(
                    "{Unit} has failed {Count} times in {Minutes} minutes. AUTOMATIC RESTARTS ARE NOW OFF " +
                    "for it - something is wrong that restarting does not fix. Check `systemctl status {StatusUnit}` " +
                    "and `journalctl -u {JournalUnit}`",
                    unit, attempts.At.Count, (int)AttemptWindow.TotalMinutes, unit, unit);

                return new Gate(false,
                    $"failed {attempts.At.Count} times in {(int)AttemptWindow.TotalMinutes} minutes - " +
                    "automatic restarts stopped, this needs a human");
            }

            /* STILL COMING UP. A Pavlov server takes the better part of a minute, and
               restarting one mid-start reports a failure for a unit that was fine. Quiet,
               because this is the normal path between two attempts. */
            if (attempts.At.Count > 0 && now - attempts.At[^1] < Cooldown)
                return new Gate(false, null);

            return new Gate(true, null);
        }
    }

    private void RecordAttempt(string unit)
    {
        lock (_sync)
        {
            if (!_attempts.TryGetValue(unit, out var attempts))
                _attempts[unit] = attempts = new Attempts();

            attempts.At.Add(_time.GetUtcNow());
        }
    }

    private void Forget(string unit)
    {
        lock (_sync)
        {
            if (_attempts.Remove(unit, out var previous) && previous.At.Count > 0)
                logger.LogInformation("{Unit} is healthy again - its crash history has been cleared", unit);
        }
    }

    /// <summary>Attempts recorded for a unit inside the window. Exposed so the brake is observable.</summary>
    public int AttemptsFor(string unit)
    {
        var now = _time.GetUtcNow();
        lock (_sync)
        {
            if (!_attempts.TryGetValue(unit, out var attempts)) return 0;
            attempts.At.RemoveAll(at => now - at > AttemptWindow);
            return attempts.At.Count;
        }
    }
}
