namespace PavlovBot.Core.Events;

/// <summary>How bad an incident is.</summary>
public enum IncidentSeverity { Info, Low, Medium, High, Critical }

/// <summary>What went wrong.</summary>
/// <remarks>
/// NAMED FOR THE SYMPTOM, not the cause. "Server hang" is what was OBSERVED - RCON answering
/// slowly with players gone and the process still up - and the cause could be a wedged game
/// thread, a disk stall or a network partition. Naming a type after a cause the detector
/// cannot actually establish is how an alert sends somebody to fix the wrong thing.
/// </remarks>
public enum IncidentKind
{
    /// <summary>RCON is not answering at all.</summary>
    RconDown,

    /// <summary>RCON answers, but slowly enough that commands are timing out.</summary>
    RconSlow,

    /// <summary>The systemd unit is in the failed state.</summary>
    ServiceFailed,

    /// <summary>Players vanished while the process stayed up and RCON stayed reachable.</summary>
    PlayersVanished,

    /// <summary>Restarted repeatedly inside the window - something is not staying up.</summary>
    RestartLoop,
}

/// <param name="Kind">The symptom observed.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Evidence">What was measured, one line each.</param>
/// <param name="Recommended">What a person should consider doing. Never what was done.</param>
public sealed record Incident(
    string Server,
    IncidentKind Kind,
    IncidentSeverity Severity,
    DateTimeOffset At,
    IReadOnlyList<string> Evidence,
    string Recommended)
{
    /// <summary>A stable key for one ongoing problem, so it is not re-announced every tick.</summary>
    public string Key => $"{Server}/{Kind}";
}

/// <param name="Reachable">Whether RCON answered at all.</param>
/// <param name="Latency">How long it took, when it answered.</param>
/// <param name="Players">Players the last successful roster read saw.</param>
/// <param name="PreviousPlayers">Players the read before that saw.</param>
/// <param name="UnitFailed">Whether systemd reports the unit failed.</param>
/// <param name="RestartsInWindow">Restarts recorded for this server recently.</param>
public sealed record ServerHealth(
    string Server,
    bool Reachable,
    TimeSpan Latency,
    int Players,
    int PreviousPlayers,
    bool UnitFailed,
    int RestartsInWindow);

/// <summary>
/// Turning a health sample into an incident, or into nothing.
/// </summary>
/// <remarks>
/// PURE, so every threshold is arguable by reading one function and testable without a server.
/// The bot already has the monitoring - RCON probes, the roster cache, CrashRecovery's unit
/// states - and what it lacked was anything that JOINED those readings into "this looks
/// wrong" rather than three independent numbers on a /health page.
///
/// IT DETECTS AND NEVER RESTARTS. CrashRecovery already restarts a unit systemd reports as
/// failed, with an attempt cap and a cooldown, and that is the only automatic recovery in the
/// bot. Adding a second thing that restarts servers - on softer evidence than "systemd says it
/// is dead" - is how a slow night becomes a restart loop. The recommendation is text.
///
/// AN EMPTY SERVER IS NOT AN INCIDENT, and this is the false positive that matters most.
/// Servers empty out at 4am. What is suspicious is a POPULATED server emptying while the
/// process stays up and RCON keeps answering, which is why the rule needs the previous
/// reading as well as the current one.
/// </remarks>
public static class IncidentDetector
{
    /// <summary>RCON slower than this is treated as degraded.</summary>
    public static TimeSpan SlowRcon { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Players a server must have had for a sudden emptying to mean anything.</summary>
    /// <remarks>
    /// Four. Below that, one squad leaving together is indistinguishable from a crash, and an
    /// alert per group departure is an alert nobody reads.
    /// </remarks>
    public const int PopulatedThreshold = 4;

    /// <summary>Fraction of the population that must disappear at once.</summary>
    public const double VanishFraction = 0.75;

    /// <summary>Restarts inside the window that indicate nothing is staying up.</summary>
    public const int RestartLoopThreshold = 3;

    /// <summary>The incident this reading describes, or null when nothing is wrong.</summary>
    /// <remarks>
    /// ONE INCIDENT, NOT A LIST, and the order below is the priority. A failed unit whose RCON
    /// is also unreachable is one problem with two symptoms; reporting both would double every
    /// alert and bury the one that says what to do.
    /// </remarks>
    public static Incident? Detect(ServerHealth health, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(health);

        if (health.RestartsInWindow >= RestartLoopThreshold)
        {
            return new Incident(health.Server, IncidentKind.RestartLoop, IncidentSeverity.Critical, now,
            [
                $"{health.RestartsInWindow} restarts recorded in the recovery window",
                health.Reachable ? "RCON is reachable now" : "RCON is not answering",
            ],
            "Something is stopping this server staying up. Automatic recovery gives up after " +
            "its attempt cap, so this needs a person to look at the game server's own logs.");
        }

        if (health.UnitFailed)
        {
            return new Incident(health.Server, IncidentKind.ServiceFailed, IncidentSeverity.Critical, now,
                ["systemd reports the unit in the failed state"],
                "Crash recovery restarts this automatically when it is enabled. If it is not, restart it by hand.");
        }

        if (!health.Reachable)
        {
            return new Incident(health.Server, IncidentKind.RconDown, IncidentSeverity.High, now,
            [
                "RCON did not answer",
                "the systemd unit is NOT failed, so the process is still up",
            ],
            "The process is alive and not answering RCON. Check the RCON password and port " +
            "before restarting - a wrong password looks exactly like this.");
        }

        /* CHECKED BEFORE LATENCY, because a hang produces both and the emptying is the more
           actionable symptom. Slow RCON on its own is a busy server; slow RCON on a server
           that just emptied is a server nobody can play on. */
        if (Vanished(health))
        {
            var severity = health.Latency > SlowRcon ? IncidentSeverity.Critical : IncidentSeverity.High;

            return new Incident(health.Server, IncidentKind.PlayersVanished, severity, now,
            [
                $"player count fell {health.PreviousPlayers} -> {health.Players}",
                $"RCON latency {health.Latency.TotalSeconds:F1}s",
                "the process is still running and RCON still answers",
            ],
            "Players left en masse while the server stayed up, which usually means they could " +
            "not play rather than that they chose to leave. Check the server before restarting - " +
            "a restart destroys the evidence of why.");
        }

        if (health.Latency > SlowRcon)
        {
            return new Incident(health.Server, IncidentKind.RconSlow, IncidentSeverity.Medium, now,
            [
                $"RCON latency {health.Latency.TotalSeconds:F1}s, over the {SlowRcon.TotalSeconds:F0}s threshold",
                $"{health.Players} player(s) online",
            ],
            "Pavlov serves RCON on the game thread, so this usually means the game thread is " +
            "busy rather than that RCON is broken. Worth watching; not worth acting on alone.");
        }

        return null;
    }

    /// <summary>
    /// Whether a populated server just emptied.
    /// </summary>
    /// <remarks>
    /// BOTH CONDITIONS ARE REQUIRED. Without the population floor this fires every time a
    /// two-player server goes quiet; without the fraction it fires on ordinary churn. Together
    /// they describe "most of a busy server left at once", which is worth waking somebody for.
    /// </remarks>
    internal static bool Vanished(ServerHealth health) =>
        health.PreviousPlayers >= PopulatedThreshold &&
        health.Players <= health.PreviousPlayers * (1 - VanishFraction);
}
