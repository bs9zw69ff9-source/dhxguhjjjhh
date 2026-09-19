using PavlovBot.Core.Monitoring;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The monitoring decision, exercised as pure data - no server, no gateway. Every scenario the
/// monitor has to get right is here, because this is the piece whose being wrong is silent.
/// </summary>
public class ServerHealthTests
{
    private static readonly MonitorSettings S = MonitorSettings.Default;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static HealthProbe Ok(int seconds, int? players = 20, int? max = 50, double latencyMs = 50,
        bool? proc = true, DateTimeOffset? log = null) =>
        new(true, TimeSpan.FromMilliseconds(latencyMs), players, max, "Datacenter", proc, false,
            log ?? At(seconds), null, At(seconds));

    private static HealthProbe Fail(int seconds, bool intentional = false, bool? proc = null) =>
        new(false, null, null, null, null, proc, intentional, null, "rcon timeout", At(seconds));

    private static (ServerHealth Health, List<MonitorSignal> Signals) Run(ServerHealth start, params HealthProbe[] probes)
    {
        var h = start;
        var all = new List<MonitorSignal>();
        foreach (var p in probes)
        {
            var (next, signals) = h.Observe(p, S);
            h = next;
            all.AddRange(signals);
        }
        return (h, all);
    }

    private static IEnumerable<SignalKind> Kinds(IEnumerable<MonitorSignal> s) => s.Select(x => x.Kind);

    // ---- baseline / bot restart -------------------------------------------------------------

    [Fact]
    public void StartsUnknownAndAFirstHealthyProbeDoesNotAlert()
    {
        // Bot restart: the machine begins UNKNOWN and must NOT invent a recovery or a fault
        // from the first look at a server that is simply fine.
        Assert.Equal(HealthState.Unknown, ServerHealth.Initial.State);

        var (h, signals) = Run(ServerHealth.Initial, Ok(0));

        Assert.Equal(HealthState.Online, h.State);
        Assert.Empty(signals);
    }

    // ---- going offline ----------------------------------------------------------------------

    [Fact]
    public void ThreeFailuresGoOfflineWithExactlyOneAlert()
    {
        var (h, signals) = Run(ServerHealth.Initial, Ok(0), Fail(30), Fail(60), Fail(90));

        Assert.Equal(HealthState.Offline, h.State);
        Assert.Equal([SignalKind.ServerOffline], Kinds(signals));
    }

    [Fact]
    public void OneFailureIsReconnectingNotOffline()
    {
        // "Do not immediately declare a server offline because of one failed request."
        var (h, signals) = Run(ServerHealth.Initial, Ok(0), Fail(30));

        Assert.Equal(HealthState.Reconnecting, h.State);
        Assert.Empty(signals);
    }

    [Fact]
    public void ATransientFailureThatRecoversNeverAlerts()
    {
        var (h, signals) = Run(ServerHealth.Initial, Ok(0), Fail(30), Ok(60), Ok(90));

        Assert.Equal(HealthState.Online, h.State);
        Assert.Empty(signals);   // never crossed the threshold, so nobody was paged
    }

    // ---- recovery ---------------------------------------------------------------------------

    [Fact]
    public void RecoveryNeedsTheRecoveryThresholdThenReportsDowntime()
    {
        var (mid, s1) = Run(ServerHealth.Initial, Ok(0), Fail(30), Fail(60), Fail(90));
        Assert.Equal([SignalKind.ServerOffline], Kinds(s1));

        // One good probe is not enough (RecoveryThreshold = 2): still reconnecting, silent.
        var (h1, s2) = mid.Observe(Ok(120), S);
        Assert.Equal(HealthState.Reconnecting, h1.State);
        Assert.Empty(s2);

        // Second good probe confirms recovery.
        var (h2, s3) = h1.Observe(Ok(150), S);
        Assert.Equal(HealthState.Online, h2.State);
        Assert.Contains(SignalKind.ServerOnline, Kinds(s3));
        Assert.Contains(SignalKind.UnexpectedRestart, Kinds(s3));  // outage nobody asked for
    }

    // ---- repeated RCON failure (process up) -------------------------------------------------

    [Fact]
    public void ProcessUpButRconFailingIsAnRconFaultNotAnOffline()
    {
        var (h, signals) = Run(ServerHealth.Initial,
            Ok(0), Fail(30, proc: true), Fail(60, proc: true), Fail(90, proc: true));

        Assert.Equal(HealthState.Offline, h.State);
        Assert.Equal([SignalKind.RepeatedRconFailures], Kinds(signals));

        // ...and recovery of an RCON-only fault is not reported as a restart.
        var (h2, s2) = Run(h, Ok(120, proc: true), Ok(150, proc: true));
        Assert.Equal(HealthState.Online, h2.State);
        Assert.Contains(SignalKind.RconReconnected, Kinds(s2));
        Assert.DoesNotContain(SignalKind.UnexpectedRestart, Kinds(s2));
    }

    // ---- expected vs unexpected restart -----------------------------------------------------

    [Fact]
    public void AnIntentionalOutageIsNotAlertedAndNotACrash()
    {
        // Bot/admin stopped it (serverswitch). No OFFLINE alert, no crash on the way back.
        var (h, down) = Run(ServerHealth.Initial,
            Ok(0), Fail(30, intentional: true), Fail(60, intentional: true), Fail(90, intentional: true));

        Assert.Equal(HealthState.Offline, h.State);
        Assert.Empty(down);   // intended shutdowns do not page

        var (h2, up) = Run(h, Ok(120), Ok(150));
        Assert.Equal(RestartKind.Expected, h2.LastRestart);
        Assert.DoesNotContain(SignalKind.UnexpectedRestart, Kinds(up));
    }

    [Fact]
    public void IntentionalIsRememberedEvenIfOnlyOneFailingProbeSawTheFlag()
    {
        // The lifecycle flag may only be true on some probes; once seen during an outage it
        // classifies the whole outage as intended.
        var (h, down) = Run(ServerHealth.Initial,
            Ok(0), Fail(30, intentional: true), Fail(60), Fail(90));

        Assert.Empty(down);
        Assert.True(h.IntentionalOutage);
    }

    // ---- alert deduplication / escalation ---------------------------------------------------

    [Fact]
    public void AProlongedOutageAlertsOnceThenOnlyOnEscalation()
    {
        var h = ServerHealth.Initial;
        (h, _) = Run(h, Ok(0));

        // Fail for a long time. One OFFLINE, then silence until the escalation cadence.
        var signals = new List<MonitorSignal>();
        for (var i = 1; i <= S.EscalationEvery + 5; i++)
        {
            var (next, sig) = h.Observe(Fail(30 * i), S);
            h = next;
            signals.AddRange(sig);
        }

        var offlines = signals.Count(x => x.Kind == SignalKind.ServerOffline);
        var escalations = signals.Count(x => x.Kind == SignalKind.StillOffline);

        Assert.Equal(1, offlines);              // never repeated
        Assert.True(escalations >= 1);          // but did escalate once it dragged on
        Assert.True(escalations < 3);           // and did not turn into a flood
    }

    // ---- latency ----------------------------------------------------------------------------

    [Fact]
    public void LatencyBandsAreClassifiedAtTheConfiguredThresholds()
    {
        Assert.Equal(LatencyBand.Healthy, S.BandOf(TimeSpan.FromMilliseconds(249)));
        Assert.Equal(LatencyBand.Degraded, S.BandOf(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(LatencyBand.Degraded, S.BandOf(TimeSpan.FromMilliseconds(749)));
        Assert.Equal(LatencyBand.Critical, S.BandOf(TimeSpan.FromMilliseconds(750)));
        Assert.Equal(LatencyBand.Unknown, S.BandOf(null));
    }

    [Fact]
    public void HighLatencyAlertsOncePerBandChangeAndRecovers()
    {
        var (h, signals) = Run(ServerHealth.Initial,
            Ok(0, latencyMs: 50),      // healthy
            Ok(30, latencyMs: 300),    // -> degraded: one alert
            Ok(60, latencyMs: 320),    // still degraded: silent
            Ok(90, latencyMs: 900),    // -> critical: one alert
            Ok(120, latencyMs: 40));   // -> healthy: recovered

        Assert.Equal(HealthState.Online, h.State);
        Assert.Equal(
            [SignalKind.HighLatency, SignalKind.HighLatency, SignalKind.LatencyRecovered],
            Kinds(signals));
    }

    [Fact]
    public void LatencyStatsTrackMinMaxAverage()
    {
        var (h, _) = Run(ServerHealth.Initial, Ok(0, latencyMs: 40), Ok(30, latencyMs: 60), Ok(60, latencyMs: 50));

        Assert.Equal(40, h.Latency.Min!.Value.TotalMilliseconds);
        Assert.Equal(60, h.Latency.Max!.Value.TotalMilliseconds);
        Assert.Equal(50, h.Latency.Average!.Value.TotalMilliseconds);
        Assert.Equal(50, h.Latency.Current!.Value.TotalMilliseconds);
        Assert.Equal(3, h.Latency.Samples);
    }

    // ---- exponential backoff / reconnection -------------------------------------------------

    [Fact]
    public void BackoffGrowsExponentiallyAndIsClamped()
    {
        Assert.Equal(S.RetryBaseDelay, S.BackoffFor(1));
        Assert.Equal(S.RetryBaseDelay * 2, S.BackoffFor(2));
        Assert.Equal(S.RetryBaseDelay * 4, S.BackoffFor(3));
        Assert.Equal(S.RetryMaxDelay, S.BackoffFor(100));   // clamped, no overflow
    }

    [Fact]
    public void AFailureSetsAReconnectDeadlineThatGatesTheNextAttempt()
    {
        var (h, _) = Run(ServerHealth.Initial, Ok(0), Fail(30));

        Assert.True(h.IsReconnecting);
        Assert.NotNull(h.NextRetryAt);
        Assert.False(h.MayAttemptAt(At(30)));      // must wait out the back-off
        Assert.True(h.MayAttemptAt(At(30) + S.RetryMaxDelay));
    }

    // ---- log inactivity ---------------------------------------------------------------------

    [Fact]
    public void LogInactivityAlertsOnceWhileTheServerStillAnswers()
    {
        var stale = At(0);   // log timestamp that never advances
        var (h, signals) = Run(ServerHealth.Initial,
            Ok(0, log: stale),
            Ok(600, log: stale),    // 10 min later, log frozen: one alert
            Ok(630, log: stale));   // still frozen: silent

        Assert.Equal([SignalKind.LogInactive], Kinds(signals));
        Assert.True(h.LogInactiveReported);
    }

    [Fact]
    public void LogActivityResumingReArmsWithoutSpam()
    {
        var stale = At(0);
        var (mid, s1) = Run(ServerHealth.Initial, Ok(0, log: stale), Ok(600, log: stale));
        Assert.Equal([SignalKind.LogInactive], Kinds(s1));

        var (after, s2) = Run(mid, Ok(630, log: At(625)));   // log moved again
        Assert.False(after.LogInactiveReported);
        Assert.Empty(s2);   // no "log recovered" noise
    }

    // ---- player anomaly ---------------------------------------------------------------------

    [Fact]
    public void ASuddenDropToZeroWhileAnsweringIsFlagged()
    {
        var (_, signals) = Run(ServerHealth.Initial, Ok(0, players: 27), Ok(30, players: 0));

        Assert.Equal([SignalKind.PlayerCountZero], Kinds(signals));
    }

    [Fact]
    public void ABigDropShortOfZeroIsAnAnomalyNotAZero()
    {
        var (_, signals) = Run(ServerHealth.Initial, Ok(0, players: 45), Ok(30, players: 2));

        Assert.Equal([SignalKind.PlayerAnomaly], Kinds(signals));
    }

    [Fact]
    public void ARiseAfterARestartIsNotADrop()
    {
        // 0 -> 40 immediately after a restart is a recovery, not an anomaly.
        var (down, _) = Run(ServerHealth.Initial, Ok(0, players: 30), Fail(30), Fail(60), Fail(90));
        var (_, up) = Run(down, Ok(120, players: 0), Ok(150, players: 40));

        Assert.DoesNotContain(SignalKind.PlayerAnomaly, Kinds(up));
        Assert.DoesNotContain(SignalKind.PlayerCountZero, Kinds(up));
    }

    [Fact]
    public void PlayersGoingDarkDuringAnOutageIsNotAPlayerAnomaly()
    {
        // During an outage RCON does not answer, so there is no player reading to misread as a
        // drop - the anomaly detector only runs on a successful probe.
        var (_, signals) = Run(ServerHealth.Initial, Ok(0, players: 27), Fail(30), Fail(60), Fail(90));

        Assert.DoesNotContain(SignalKind.PlayerAnomaly, Kinds(signals));
        Assert.DoesNotContain(SignalKind.PlayerCountZero, Kinds(signals));
    }

    [Fact]
    public void ReachingCapacityAlertsOnceThenReArmsWhenItDrops()
    {
        var (_, signals) = Run(ServerHealth.Initial,
            Ok(0, players: 49, max: 50),
            Ok(30, players: 50, max: 50),   // full: alert
            Ok(60, players: 50, max: 50),   // still full: silent
            Ok(90, players: 48, max: 50),   // dropped: re-arm
            Ok(120, players: 50, max: 50)); // full again: alert

        Assert.Equal(
            [SignalKind.PlayerCapacityReached, SignalKind.PlayerCapacityReached],
            Kinds(signals));
    }

    // ---- multiple servers -------------------------------------------------------------------

    [Fact]
    public void ServersAreIndependent()
    {
        // Each server is its own value; one going down says nothing about another.
        var (a, _) = Run(ServerHealth.Initial, Ok(0), Fail(30), Fail(60), Fail(90));
        var (b, bSignals) = Run(ServerHealth.Initial, Ok(0), Ok(30), Ok(60));

        Assert.Equal(HealthState.Offline, a.State);
        Assert.Equal(HealthState.Online, b.State);
        Assert.Empty(bSignals);
    }
}
