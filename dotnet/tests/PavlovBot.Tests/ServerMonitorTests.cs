using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Monitoring;
using PavlovBot.Host.Monitoring;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The monitor loop, exercised with fakes for the socket, the clock and Discord - so reconnection,
/// pacing, persistence and the concurrency guard are asserted, not hoped for.
/// </summary>
public sealed class ServerMonitorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pavlovbot-mon-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly CapturingSink _sink = new();
    private readonly FakeTargets _targets;
    private readonly MonitorHistory _history;

    private static readonly MonitorSettings Settings = MonitorSettings.Default with
    {
        RetryBaseDelay = TimeSpan.FromSeconds(60),
        RetryMaxDelay = TimeSpan.FromSeconds(60),
    };

    public ServerMonitorTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_dir), new SystemTextJsonCodec());
        _history = new MonitorHistory(_store, _clock);
        _targets = new FakeTargets("Server 1", "Server 2");
    }

    private ServerMonitor Monitor(IServerProbe probe) =>
        new(probe, _sink, _history, Settings, _targets, NullLogger<ServerMonitor>.Instance, _clock);

    private HealthProbe Ok(int players = 20) =>
        new(true, TimeSpan.FromMilliseconds(50), players, 50, "Datacenter", null, false, _clock.GetUtcNow(), null, _clock.GetUtcNow());

    private HealthProbe Fail() =>
        new(false, null, null, null, null, null, false, null, "timeout", _clock.GetUtcNow());

    // ---- bot restart persistence ------------------------------------------------------------

    [Fact]
    public async Task ServersHealthyAcrossABotRestartDoNotAlert()
    {
        // A fresh monitor knows nothing: every server starts UNKNOWN. One clean probe must
        // establish ONLINE without inventing an OFFLINE from the ignorance.
        var monitor = Monitor(new ScriptedProbe(_ => Ok()));

        Assert.Equal(HealthState.Unknown, monitor.Snapshot("Server 1").State);

        await monitor.TickAsync();

        Assert.Equal(HealthState.Online, monitor.Snapshot("Server 1").State);
        Assert.Equal(HealthState.Online, monitor.Snapshot("Server 2").State);
        Assert.Empty(_sink.Signals);
    }

    // ---- multiple servers -------------------------------------------------------------------

    [Fact]
    public async Task ServersAreMonitoredIndependently()
    {
        // Server 1 is down, Server 2 is fine. Only Server 1 alerts, and only Server 1 goes offline.
        var monitor = Monitor(new ScriptedProbe(server => server == "Server 1" ? Fail() : Ok()));

        for (var i = 0; i < Settings.FailureThreshold; i++)
        {
            await monitor.TickAsync();
            _clock.Advance(Settings.RetryMaxDelay);   // let the back-off elapse each tick
        }

        Assert.Equal(HealthState.Offline, monitor.Snapshot("Server 1").State);
        Assert.Equal(HealthState.Online, monitor.Snapshot("Server 2").State);

        Assert.Single(_sink.Signals);
        Assert.Equal(("Server 1", SignalKind.ServerOffline), _sink.Signals[0]);
    }

    // ---- offline then recover + reconnect ---------------------------------------------------

    [Fact]
    public async Task AConfirmedOutageRecoversAndResetsTheSession()
    {
        var up = false;
        var monitor = Monitor(new ScriptedProbe(server => server == "Server 1" ? (up ? Ok() : Fail()) : Ok()));

        // Fail until offline.
        for (var i = 0; i < Settings.FailureThreshold; i++)
        {
            await monitor.TickAsync();
            _clock.Advance(Settings.RetryMaxDelay);
        }
        Assert.Equal(HealthState.Offline, monitor.Snapshot("Server 1").State);

        // Recover.
        up = true;
        for (var i = 0; i < Settings.RecoveryThreshold; i++)
        {
            await monitor.TickAsync();
            _clock.Advance(Settings.RetryMaxDelay);
        }

        Assert.Equal(HealthState.Online, monitor.Snapshot("Server 1").State);
        Assert.Contains(_sink.Signals, s => s == ("Server 1", SignalKind.ServerOffline));
        Assert.Contains(_sink.Signals, s => s == ("Server 1", SignalKind.ServerOnline));

        // A wedged session is reset before reconnect attempts.
        Assert.True(_targets.ResetCount("Server 1") > 0);
        Assert.Equal(0, _targets.ResetCount("Server 2"));   // a healthy server is never reset
    }

    // ---- back-off pacing --------------------------------------------------------------------

    [Fact]
    public async Task AnOfflineServerIsNotProbedUntilTheBackoffElapses()
    {
        var probe = new ScriptedProbe(_ => Fail());
        var monitor = Monitor(probe);

        // Fail to OFFLINE. Below the threshold the state is RECONNECTING and ungated, so these
        // probe regardless; the last one declares OFFLINE and arms the back-off from that instant.
        for (var i = 0; i < Settings.FailureThreshold; i++)
        {
            await monitor.TickAsync();
            if (i < Settings.FailureThreshold - 1) _clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(HealthState.Offline, monitor.Snapshot("Server 1").State);

        var before = probe.Count("Server 1");

        // A tick a few seconds later, well inside the 60s back-off, must NOT probe.
        _clock.Advance(TimeSpan.FromSeconds(5));
        await monitor.TickAsync();
        Assert.Equal(before, probe.Count("Server 1"));

        // Past the back-off, it probes again.
        _clock.Advance(Settings.RetryMaxDelay);
        await monitor.TickAsync();
        Assert.Equal(before + 1, probe.Count("Server 1"));
    }

    // ---- concurrency guard ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentTicksNeverOverlapProbesForOneServer()
    {
        // A deliberately slow probe, so two ticks racing would overlap if the gate did not exist.
        var probe = new GatedProbe(Ok());
        var monitor = Monitor(probe);

        var first = monitor.TickAsync();
        // Second tick starts while the first is still inside the slow probe.
        var second = monitor.TickAsync();

        probe.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(1, probe.MaxConcurrent);   // the per-server gate held
    }

    // ---- fakes ------------------------------------------------------------------------------

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CapturingSink : IMonitorAlertSink
    {
        public List<(string Server, SignalKind Kind)> Signals { get; } = [];
        public Task PostAsync(string server, MonitorSignal signal, ServerHealth health, CancellationToken ct = default)
        {
            lock (Signals) Signals.Add((server, signal.Kind));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTargets(params string[] servers) : IMonitorTargets
    {
        private readonly ConcurrentDictionary<string, int> _resets = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> Servers { get; } = servers;
        public int ResetCount(string server) => _resets.GetValueOrDefault(server);
        public Task ResetSessionAsync(string server, CancellationToken ct = default)
        {
            _resets.AddOrUpdate(server, 1, (_, n) => n + 1);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedProbe(Func<string, HealthProbe> next) : IServerProbe
    {
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
        public int Count(string server) => _counts.GetValueOrDefault(server);
        public Task<HealthProbe> ProbeAsync(string server, CancellationToken ct = default)
        {
            _counts.AddOrUpdate(server, 1, (_, n) => n + 1);
            return Task.FromResult(next(server));
        }
    }

    private sealed class GatedProbe(HealthProbe result) : IServerProbe
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _live = new(StringComparer.Ordinal);
        public int MaxConcurrent { get; private set; }
        public void Release() => _gate.TrySetResult();

        public async Task<HealthProbe> ProbeAsync(string server, CancellationToken ct = default)
        {
            var now = _live.AddOrUpdate(server, 1, (_, n) => n + 1);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try { await _gate.Task.ConfigureAwait(false); return result; }
            finally { _live.AddOrUpdate(server, 0, (_, n) => n - 1); }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
