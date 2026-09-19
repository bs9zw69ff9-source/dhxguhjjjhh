using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Monitoring;

namespace PavlovBot.Host.Monitoring;

/// <summary>
/// Drives the health state machine for every server: probe, decide, record, alert, reconnect.
/// </summary>
/// <remarks>
/// THE STATE MACHINE MAKES THE DECISIONS; this owns the sockets and the clock. Each tick probes
/// every server in parallel, folds the result into that server's <see cref="ServerHealth"/>,
/// persists a sample and posts whatever alerts the fold produced.
///
/// ONE CHECK PER SERVER AT A TIME. Every server has its own non-blocking gate, and a tick that
/// finds the gate held simply skips that server - so a slow or hung probe can never be joined by a
/// second one, and there is exactly one reconnection attempt in flight per server. This is the
/// "no duplicate monitoring loops" guarantee, enforced rather than hoped for.
///
/// A FAILING MONITOR IS LOUD. Any exception a check throws is logged at error and the tick moves
/// on to the other servers; it is never swallowed into a false "healthy". The registry that runs
/// this tick also tracks its consecutive failures, so a monitor that cannot run at all shows up as
/// a failed service rather than as silence.
///
/// STARTS BLANK ON PURPOSE. Nothing is assumed about a server across a bot restart: every server
/// begins UNKNOWN and its true state is established by the first live probe, so a restart never
/// manufactures an OFFLINE alert for a server that was fine the whole time.
/// </remarks>
public sealed class ServerMonitor
{
    private readonly IServerProbe _probe;
    private readonly IMonitorAlertSink _alerts;
    private readonly MonitorHistory _history;
    private readonly MonitorSettings _settings;
    private readonly IMonitorTargets _targets;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerMonitor> _logger;

    private readonly ConcurrentDictionary<string, ServerHealth> _state = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public ServerMonitor(
        IServerProbe probe,
        IMonitorAlertSink alerts,
        MonitorHistory history,
        MonitorSettings settings,
        IMonitorTargets targets,
        ILogger<ServerMonitor> logger,
        TimeProvider? time = null)
    {
        _probe = probe;
        _alerts = alerts;
        _history = history;
        _settings = settings;
        _targets = targets;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public MonitorSettings Settings => _settings;

    public IReadOnlyCollection<string> Servers => _targets.Servers;

    /// <summary>The current health of one server, UNKNOWN until the first probe lands.</summary>
    public ServerHealth Snapshot(string server) => _state.GetValueOrDefault(server) ?? ServerHealth.Initial;

    public HealthStats Stats(string server, TimeSpan window) => _history.Stats(server, window);

    /// <summary>One monitoring pass over every server. Registered on the background timer.</summary>
    public Task TickAsync(CancellationToken ct = default) =>
        Task.WhenAll(_targets.Servers.Select(server => CheckAsync(server, ct)));

    private async Task CheckAsync(string server, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(server, _ => new SemaphoreSlim(1, 1));

        // Non-blocking: if a check for this server is already running, skip. That is the whole
        // guard against overlapping probes and duplicate reconnection attempts.
        if (!await gate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) return;

        try
        {
            var current = Snapshot(server);
            var now = _time.GetUtcNow();

            // Honour the back-off: once a server is confirmed OFFLINE, do not hammer it - probe
            // only when the exponential delay the last failure set has elapsed.
            if (current is { DeclaredOffline: true } && !current.MayAttemptAt(now))
                return;

            // Before any reconnect attempt, drop the RCON session so a wedged socket is genuinely
            // reconnected on the next send rather than retried on a dead handle.
            if (current.IsReconnecting)
                await _targets.ResetSessionAsync(server, ct).ConfigureAwait(false);

            var probe = await _probe.ProbeAsync(server, ct).ConfigureAwait(false);
            var (next, signals) = current.Observe(probe, _settings);
            _state[server] = next;

            // Alerts first: they are the time-critical output, and must not be lost to a slow or
            // failing history write. The sink never throws.
            foreach (var signal in signals)
                await _alerts.PostAsync(server, signal, next, ct).ConfigureAwait(false);

            await _history.RecordAsync(server, new MonitorSample(
                At: probe.At,
                State: next.State,
                LatencyMs: probe.Latency?.TotalMilliseconds,
                Players: probe.Players,
                RconOk: probe.RconOk,
                UnexpectedRestart: signals.Any(s => s.Kind == SignalKind.UnexpectedRestart)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Loud, not silent: a monitor that cannot check a server must not look like a server
            // that is fine. The state is left as it was, to be re-established next tick.
            _logger.LogError(ex, "Monitoring check failed for {Server} - state left unchanged, will retry", server);
        }
        finally
        {
            gate.Release();
        }
    }
}
