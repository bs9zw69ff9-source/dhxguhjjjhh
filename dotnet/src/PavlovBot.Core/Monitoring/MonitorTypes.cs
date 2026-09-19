namespace PavlovBot.Core.Monitoring;

/// <summary>The state a monitored server is in, as the machine sees it.</summary>
/// <remarks>
/// UNKNOWN is the START state and the honest answer after a bot restart: nothing has been
/// probed yet, so the server is neither declared healthy nor declared down. Reporting ONLINE
/// before the first probe is exactly the false-green a restart must not produce.
/// </remarks>
public enum HealthState
{
    Unknown = 0,
    Online,
    Degraded,
    Offline,
    Reconnecting,
}

/// <summary>Which latency band the last round trip fell in.</summary>
public enum LatencyBand
{
    Unknown = 0,
    Healthy,
    Degraded,
    Critical,
}

/// <summary>How a return to service is classified, for the restart/crash distinction.</summary>
public enum RestartKind
{
    None = 0,
    Expected,
    Unexpected,
    Unknown,
}

/// <summary>An alert-worthy thing the machine decided happened. One per genuine change.</summary>
public enum SignalKind
{
    ServerOffline,
    ServerOnline,
    StillOffline,          // escalation while an outage persists - NOT one per check
    RconDisconnected,
    RconReconnected,
    HighLatency,
    LatencyRecovered,
    RepeatedRconFailures,
    LogInactive,
    UnexpectedRestart,
    PlayerCountZero,
    PlayerCapacityReached,
    PlayerAnomaly,
}

/// <summary>How loud a signal is. Drives colour and whether the alert role is pinged.</summary>
public enum Severity
{
    Info = 0,
    Warning,
    Critical,
}

/// <summary>
/// Every tunable, in ONE place. Nothing in the monitor hard-codes a threshold.
/// </summary>
/// <param name="CheckInterval">How often the loop probes each server.</param>
/// <param name="FailureThreshold">Consecutive failed probes before a server is declared OFFLINE. Never one.</param>
/// <param name="RecoveryThreshold">Consecutive good probes before an OFFLINE server is declared back ONLINE.</param>
/// <param name="RconTimeout">How long a single probe waits before it counts as a failure.</param>
/// <param name="RetryBaseDelay">First reconnect back-off.</param>
/// <param name="RetryMaxDelay">Ceiling the exponential back-off is clamped to.</param>
/// <param name="LatencyWarn">At or above this, latency is DEGRADED.</param>
/// <param name="LatencyCritical">At or above this, latency is CRITICAL.</param>
/// <param name="PlayerAnomalyDropFraction">A drop of at least this fraction of players inside the window is an anomaly.</param>
/// <param name="PlayerAnomalyWindow">The window a sudden drop is measured over.</param>
/// <param name="LogInactivityThreshold">No log advance for this long, while the server answers, is an alert.</param>
/// <param name="EscalationEvery">While OFFLINE, re-alert once every this many further failed probes. 0 disables.</param>
/// <param name="RecoveryMessagesEnabled">Whether recovery ("back ONLINE") messages are posted.</param>
public sealed record MonitorSettings(
    TimeSpan CheckInterval,
    int FailureThreshold,
    int RecoveryThreshold,
    TimeSpan RconTimeout,
    TimeSpan RetryBaseDelay,
    TimeSpan RetryMaxDelay,
    TimeSpan LatencyWarn,
    TimeSpan LatencyCritical,
    double PlayerAnomalyDropFraction,
    TimeSpan PlayerAnomalyWindow,
    TimeSpan LogInactivityThreshold,
    int EscalationEvery,
    bool RecoveryMessagesEnabled)
{
    /// <summary>Sane defaults that match the specification. Every one is overridable from config.</summary>
    public static MonitorSettings Default { get; } = new(
        CheckInterval: TimeSpan.FromSeconds(30),
        FailureThreshold: 3,
        RecoveryThreshold: 2,
        RconTimeout: TimeSpan.FromSeconds(5),
        RetryBaseDelay: TimeSpan.FromSeconds(2),
        RetryMaxDelay: TimeSpan.FromMinutes(2),
        LatencyWarn: TimeSpan.FromMilliseconds(250),
        LatencyCritical: TimeSpan.FromMilliseconds(750),
        PlayerAnomalyDropFraction: 0.8,
        // Longer than the check interval on purpose: a drop is measured between consecutive
        // probes, so the previous reading must still count as "recent" when the next arrives.
        PlayerAnomalyWindow: TimeSpan.FromSeconds(90),
        LogInactivityThreshold: TimeSpan.FromMinutes(5),
        EscalationEvery: 20,
        RecoveryMessagesEnabled: true);

    /// <summary>The band a latency falls in. A failed probe has no latency and is UNKNOWN here.</summary>
    public LatencyBand BandOf(TimeSpan? latency) => latency switch
    {
        null => LatencyBand.Unknown,
        { } l when l >= LatencyCritical => LatencyBand.Critical,
        { } l when l >= LatencyWarn => LatencyBand.Degraded,
        _ => LatencyBand.Healthy,
    };

    /// <summary>Exponential back-off for reconnect attempt <paramref name="attempt"/> (1-based), clamped.</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        if (attempt < 1) attempt = 1;
        // 2^(attempt-1) * base, clamped. Computed in double to avoid overflow at high attempts.
        var scaled = RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        var ms = Math.Min(scaled, RetryMaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }
}

/// <summary>
/// One probe result the loop feeds into the machine. Everything the decision needs, and nothing
/// that ties it to a transport - so the machine is tested with plain records, no sockets.
/// </summary>
/// <param name="RconOk">True when the probe's RCON round trip succeeded.</param>
/// <param name="Latency">The round-trip time, or null when the probe failed.</param>
/// <param name="Players">Player count if known.</param>
/// <param name="MaxPlayers">Capacity if known.</param>
/// <param name="Map">Current map if known.</param>
/// <param name="ProcessRunning">systemd's view of the process, or null when it cannot be asked.</param>
/// <param name="IntentionallyStopped">True when the server was stopped on purpose (serverswitch, an admin, the bot).</param>
/// <param name="LastLogActivity">When the server's log last advanced, or null when unknown.</param>
/// <param name="FailureReason">Why the probe failed, for the alert. Null on success.</param>
/// <param name="At">The probe's wall-clock time.</param>
public sealed record HealthProbe(
    bool RconOk,
    TimeSpan? Latency,
    int? Players,
    int? MaxPlayers,
    string? Map,
    bool? ProcessRunning,
    bool IntentionallyStopped,
    DateTimeOffset? LastLogActivity,
    string? FailureReason,
    DateTimeOffset At);

/// <summary>Rolling latency statistics for a server, over the samples seen this run.</summary>
/// <remarks>Long-run figures ("average over 24h") come from the persisted history, not this.</remarks>
public sealed record LatencyStats(
    TimeSpan? Current,
    TimeSpan? Min,
    TimeSpan? Max,
    TimeSpan? Average,
    long Samples,
    long TotalMilliseconds)
{
    public static LatencyStats Empty { get; } = new(null, null, null, null, 0, 0);

    public LatencyStats With(TimeSpan latency)
    {
        var ms = latency.TotalMilliseconds;
        var samples = Samples + 1;
        var total = TotalMilliseconds + (long)ms;
        return new LatencyStats(
            Current: latency,
            Min: Min is { } min && min <= latency ? min : latency,
            Max: Max is { } max && max >= latency ? max : latency,
            Average: TimeSpan.FromMilliseconds(total / (double)samples),
            Samples: samples,
            TotalMilliseconds: total);
    }
}

/// <summary>An alert the machine decided to raise. The Host adds the server name and formats it.</summary>
/// <param name="Detail">One line, already written for a human.</param>
public sealed record MonitorSignal(SignalKind Kind, Severity Severity, string Detail);
