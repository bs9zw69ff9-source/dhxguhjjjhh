using System.Globalization;

namespace PavlovBot.Core.Monitoring;

/// <summary>
/// The monitoring decision, as a pure state machine.
/// </summary>
/// <remarks>
/// THE RELIABILITY OF THE WHOLE FEATURE LIVES HERE, so it is deliberately a value with no I/O.
/// <see cref="Observe"/> takes one probe result and the settings and returns the next state plus
/// whatever alerts that transition warrants. The loop that owns sockets and Discord is thin glue
/// around it, and every scenario worth trusting - offline, recovery, a transient blip, a flap, a
/// latency threshold, a crash, a player drop, an alert that must not repeat - is exercised
/// against this record with plain data, no server and no gateway.
///
/// DEDUPLICATION IS INTRINSIC, not a layer bolted on. A signal is only produced on a genuine
/// CHANGE - crossing the failure threshold, recovering, a latency band moving, a drop happening -
/// so a server that is down for ten minutes yields one OFFLINE and then silence until it either
/// recovers or crosses the escalation cadence. There is no path that emits per tick.
///
/// UNKNOWN IS THE START STATE. After a bot restart the machine begins here and says so, rather
/// than assuming the last thing it saw. The first probe establishes reality; a first success is
/// not a "recovery" and does not alert.
/// </remarks>
public sealed record ServerHealth
{
    public HealthState State { get; init; } = HealthState.Unknown;

    public int ConsecutiveFailures { get; init; }
    public int ConsecutiveSuccesses { get; init; }

    public LatencyStats Latency { get; init; } = LatencyStats.Empty;

    /// <summary>The latency band we last raised an alert about, so a steady band does not re-alert.</summary>
    public LatencyBand ReportedBand { get; init; } = LatencyBand.Unknown;

    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastProbeAt { get; init; }

    // ---- outage bookkeeping ----
    public DateTimeOffset? OutageStart { get; init; }
    public bool IntentionalOutage { get; init; }
    public bool DeclaredOffline { get; init; }
    /// <summary>True when the confirmed outage is "process up, RCON dead" rather than the box being down.</summary>
    public bool RconOnlyOutage { get; init; }
    public int ChecksWhileOffline { get; init; }

    // ---- reconnection pacing ----
    public int ReconnectAttempts { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }

    // ---- last-known facts, for the dashboard ----
    public int? Players { get; init; }
    public int? MaxPlayers { get; init; }
    public string? Map { get; init; }
    public bool? ProcessRunning { get; init; }
    public DateTimeOffset? LastLogActivity { get; init; }

    // ---- anomaly / capacity / log dedup ----
    public int? PrevPlayers { get; init; }
    public DateTimeOffset? PrevPlayersAt { get; init; }
    public bool AtCapacityReported { get; init; }
    public bool LogInactiveReported { get; init; }

    public bool EverOnline { get; init; }
    public RestartKind LastRestart { get; init; } = RestartKind.None;

    public static ServerHealth Initial { get; } = new();

    /// <summary>True while a reconnect back-off is pending, so the loop can pace attempts.</summary>
    public bool IsReconnecting => State is HealthState.Reconnecting or HealthState.Offline;

    /// <summary>Whether an attempt may run now, honouring the back-off the last failure set.</summary>
    public bool MayAttemptAt(DateTimeOffset now) => NextRetryAt is not { } next || now >= next;

    /// <summary>Fold one probe in, returning the next state and any alerts it warrants.</summary>
    public (ServerHealth Next, IReadOnlyList<MonitorSignal> Signals) Observe(HealthProbe probe, MonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(settings);

        var signals = new List<MonitorSignal>();
        return probe.RconOk
            ? (OnSuccess(probe, settings, signals), signals)
            : (OnFailure(probe, settings, signals), signals);
    }

    // ---------------------------------------------------------------- success

    private ServerHealth OnSuccess(HealthProbe probe, MonitorSettings settings, List<MonitorSignal> signals)
    {
        var latency = probe.Latency is { } l ? Latency.With(l) : Latency;
        var band = settings.BandOf(probe.Latency);

        var next = this with
        {
            LastProbeAt = probe.At,
            LastSuccessAt = probe.At,
            ConsecutiveSuccesses = ConsecutiveSuccesses + 1,
            ConsecutiveFailures = 0,
            Latency = latency,
            Players = probe.Players ?? Players,
            MaxPlayers = probe.MaxPlayers ?? MaxPlayers,
            Map = probe.Map ?? Map,
            ProcessRunning = probe.ProcessRunning ?? ProcessRunning,
            LastLogActivity = probe.LastLogActivity ?? LastLogActivity,
            EverOnline = true,
        };

        // Coming back from an outage.
        if (State is HealthState.Offline or HealthState.Reconnecting)
        {
            // A blip that never crossed the threshold was never announced, so its recovery is
            // announced to nobody either: restore silently the moment a good probe arrives.
            if (!DeclaredOffline)
                return RestoreQuietly(next, probe, band);

            // A confirmed outage needs several clean probes before it is called recovered, so
            // one lucky reply does not flap the state back to green.
            if (next.ConsecutiveSuccesses < Math.Max(1, settings.RecoveryThreshold))
                return next with { State = HealthState.Reconnecting };

            return Recover(next, probe, settings, band, signals);
        }

        // Steady state: watch latency, players and log activity for changes worth a word.
        band = ReportLatency(band, signals, ref next);
        next = CheckPlayers(next, probe, settings, signals);
        next = CheckLogActivity(next, probe, settings, signals);

        return next with
        {
            State = band == LatencyBand.Healthy ? HealthState.Online : HealthState.Degraded,
            ReportedBand = band,
        };
    }

    /// <summary>Return from an un-declared blip with no alert, resetting the outage bookkeeping.</summary>
    private static ServerHealth RestoreQuietly(ServerHealth next, HealthProbe probe, LatencyBand band) =>
        next with
        {
            State = band == LatencyBand.Healthy ? HealthState.Online : HealthState.Degraded,
            ReportedBand = band,
            OutageStart = null,
            IntentionalOutage = false,
            ReconnectAttempts = 0,
            NextRetryAt = null,
            ChecksWhileOffline = 0,
            // Reset the anomaly baseline to now, so a reading from before the blip is not
            // compared to one after it.
            PrevPlayers = next.Players,
            PrevPlayersAt = probe.At,
            LogInactiveReported = false,
        };

    private ServerHealth Recover(ServerHealth next, HealthProbe probe, MonitorSettings settings, LatencyBand band, List<MonitorSignal> signals)
    {
        var downtime = OutageStart is { } start ? probe.At - start : TimeSpan.Zero;
        var latencyText = probe.Latency is { } l ? $", RCON latency {Ms(l)}" : "";
        var players = next.Players is { } p ? $", players {p}{(next.MaxPlayers is { } m ? $"/{m}" : "")}" : "";

        var restart = RestartKind.None;

        if (DeclaredOffline && !IntentionalOutage && !RconOnlyOutage)
        {
            // The box was down and came back, and nobody asked for it: an unexpected restart.
            restart = RestartKind.Unexpected;
            signals.Add(new MonitorSignal(SignalKind.UnexpectedRestart, Severity.Warning,
                $"Came back after an unexplained outage of {Humanize(downtime)}. Looks like a crash/restart, not a planned one."));
        }
        else if (IntentionalOutage)
        {
            restart = RestartKind.Expected;
        }
        else if (DeclaredOffline)
        {
            restart = RestartKind.Unknown;
        }

        if (settings.RecoveryMessagesEnabled)
        {
            var kind = RconOnlyOutage ? SignalKind.RconReconnected : SignalKind.ServerOnline;
            signals.Add(new MonitorSignal(kind, Severity.Info,
                $"Back online after {Humanize(downtime)}{latencyText}{players}."));
        }

        return next with
        {
            State = band == LatencyBand.Healthy ? HealthState.Online : HealthState.Degraded,
            ReportedBand = band,
            OutageStart = null,
            IntentionalOutage = false,
            DeclaredOffline = false,
            RconOnlyOutage = false,
            ChecksWhileOffline = 0,
            ReconnectAttempts = 0,
            NextRetryAt = null,
            LastRestart = restart,
            // A restart resets the anomaly baseline: 0 -> 40 right after a restart is a recovery,
            // not a drop, so the next reading is compared to this one, not to the pre-outage count.
            PrevPlayers = next.Players,
            PrevPlayersAt = probe.At,
            AtCapacityReported = next.MaxPlayers is { } mx && next.Players is { } pl && pl >= mx,
            LogInactiveReported = false,
        };
    }

    private LatencyBand ReportLatency(LatencyBand band, List<MonitorSignal> signals, ref ServerHealth next)
    {
        if (band is LatencyBand.Degraded or LatencyBand.Critical && band != ReportedBand)
        {
            var sev = band == LatencyBand.Critical ? Severity.Critical : Severity.Warning;
            signals.Add(new MonitorSignal(SignalKind.HighLatency, sev,
                $"RCON latency is {band.ToString().ToUpperInvariant()} at {Ms(next.Latency.Current ?? TimeSpan.Zero)} (avg {Ms(next.Latency.Average ?? TimeSpan.Zero)})."));
        }
        else if (band == LatencyBand.Healthy && ReportedBand is LatencyBand.Degraded or LatencyBand.Critical)
        {
            signals.Add(new MonitorSignal(SignalKind.LatencyRecovered, Severity.Info,
                $"RCON latency back to healthy at {Ms(next.Latency.Current ?? TimeSpan.Zero)}."));
        }
        return band;
    }

    private ServerHealth CheckPlayers(ServerHealth next, HealthProbe probe, MonitorSettings settings, List<MonitorSignal> signals)
    {
        if (probe.Players is not { } now) return next;
        var max = probe.MaxPlayers ?? next.MaxPlayers;

        // Capacity: alert once on the way up, re-arm when it drops below.
        var atCapacity = max is { } cap && now >= cap && cap > 0;
        if (atCapacity && !AtCapacityReported)
        {
            signals.Add(new MonitorSignal(SignalKind.PlayerCapacityReached, Severity.Info,
                $"Full: {now}/{max} players."));
        }

        // A sudden drop, correlated with the server still answering (this is the success path,
        // so RCON is up) - so it is a real drop, not the player count going dark in an outage.
        if (PrevPlayers is { } prev && prev > 0 && PrevPlayersAt is { } prevAt
            && probe.At - prevAt <= settings.PlayerAnomalyWindow)
        {
            var dropped = prev - now;
            if (dropped > 0 && dropped / (double)prev >= settings.PlayerAnomalyDropFraction)
            {
                if (now == 0)
                    signals.Add(new MonitorSignal(SignalKind.PlayerCountZero, Severity.Warning,
                        $"Players fell from {prev} to 0 while the server was still answering RCON - worth a look."));
                else
                    signals.Add(new MonitorSignal(SignalKind.PlayerAnomaly, Severity.Warning,
                        $"Players dropped sharply, {prev} to {now}, while RCON stayed up."));
            }
        }

        return next with
        {
            PrevPlayers = now,
            PrevPlayersAt = probe.At,
            AtCapacityReported = atCapacity,
        };
    }

    private ServerHealth CheckLogActivity(ServerHealth next, HealthProbe probe, MonitorSettings settings, List<MonitorSignal> signals)
    {
        if (probe.LastLogActivity is not { } last) return next;

        var quietFor = probe.At - last;
        if (quietFor >= settings.LogInactivityThreshold)
        {
            if (!LogInactiveReported)
            {
                signals.Add(new MonitorSignal(SignalKind.LogInactive, Severity.Warning,
                    $"The server answers RCON but its log has not advanced for {Humanize(quietFor)} - it may be hung."));
                return next with { LogInactiveReported = true };
            }
            return next;
        }

        // Activity resumed: re-arm quietly, no "recovered" spam for a log going quiet.
        return next with { LogInactiveReported = false };
    }

    // ---------------------------------------------------------------- failure

    private ServerHealth OnFailure(HealthProbe probe, MonitorSettings settings, List<MonitorSignal> signals)
    {
        var failures = ConsecutiveFailures + 1;
        var firstOfOutage = State is HealthState.Online or HealthState.Degraded or HealthState.Unknown;
        var outageStart = firstOfOutage ? probe.At : OutageStart ?? probe.At;
        var intentional = IntentionalOutage || probe.IntentionallyStopped;

        var next = this with
        {
            LastProbeAt = probe.At,
            ConsecutiveFailures = failures,
            ConsecutiveSuccesses = 0,
            OutageStart = outageStart,
            IntentionalOutage = intentional,
            ProcessRunning = probe.ProcessRunning ?? ProcessRunning,
            ReconnectAttempts = ReconnectAttempts + 1,
            NextRetryAt = probe.At + settings.BackoffFor(ReconnectAttempts + 1),
        };

        var threshold = Math.Max(1, settings.FailureThreshold);

        // Below the threshold: RECONNECTING, and silent. One failed request never pages anyone.
        if (failures < threshold)
            return next with { State = HealthState.Reconnecting };

        // At the threshold, and not already declared: confirm the outage, once.
        if (!DeclaredOffline)
        {
            var rconOnly = probe.ProcessRunning == true;
            if (!intentional)
            {
                signals.Add(rconOnly
                    ? new MonitorSignal(SignalKind.RepeatedRconFailures, Severity.Critical,
                        $"{failures} RCON checks failed in a row. The process appears to be up, so RCON itself is the fault. Last reply {LastSeen(probe.At)}.")
                    : new MonitorSignal(SignalKind.ServerOffline, Severity.Critical,
                        $"{failures} checks failed in a row. Last successful RCON {LastSeen(probe.At)}. Last known players: {LastPlayers()}."));
            }

            return next with
            {
                State = HealthState.Offline,
                DeclaredOffline = true,
                RconOnlyOutage = rconOnly,
                ChecksWhileOffline = 0,
            };
        }

        // Already OFFLINE: stay quiet, except a single escalation every N further failures.
        var checksWhileOffline = ChecksWhileOffline + 1;
        if (!intentional && settings.EscalationEvery > 0 && checksWhileOffline % settings.EscalationEvery == 0)
        {
            var downFor = OutageStart is { } s ? probe.At - s : TimeSpan.Zero;
            signals.Add(new MonitorSignal(SignalKind.StillOffline, Severity.Warning,
                $"Still down after {Humanize(downFor)} ({failures} failed checks)."));
        }

        return next with { State = HealthState.Offline, ChecksWhileOffline = checksWhileOffline };
    }

    // ---------------------------------------------------------------- text

    private string LastSeen(DateTimeOffset now) =>
        LastSuccessAt is { } at ? $"{Humanize(now - at)} ago" : "never this session";

    private string LastPlayers() =>
        Players is { } p ? $"{p}{(MaxPlayers is { } m ? $"/{m}" : "")}" : "unknown";

    private static string Ms(TimeSpan t) =>
        $"{t.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture)}ms";

    /// <summary>A compact duration: "2m 41s", "3h 5m", "12s".</summary>
    public static string Humanize(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }
}
