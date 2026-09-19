using System.Diagnostics;
using PavlovBot.Core.Monitoring;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon.Protocol;

namespace PavlovBot.Host.Monitoring;

/// <summary>Takes one health reading of a server. Behind an interface so the monitor is tested without a socket.</summary>
public interface IServerProbe
{
    Task<HealthProbe> ProbeAsync(string server, CancellationToken ct = default);
}

/// <summary>
/// The real probe: one timed <c>ServerInfo</c> round trip, plus the facts the bot already keeps.
/// </summary>
/// <remarks>
/// ONE ROUND TRIP PER CHECK, and no more. Latency cannot be measured without a request, so the
/// probe sends exactly one - <c>ServerInfo</c>, which also yields the map and capacity - and reads
/// the player count from the roster cache the bot already maintains rather than adding a second
/// call. Adding RCON load to watch for RCON load would be its own fault.
///
/// A TIMEOUT IS A FAILED CHECK, not an exception that escapes. The single round trip is bounded by
/// the configured RCON timeout; the machine decides what a failure MEANS. Caller cancellation
/// (shutdown) is distinct from a probe timeout and is rethrown, so stopping the bot is never
/// mistaken for a server being down.
///
/// PROCESS STATE is left null here: the lifecycle interface can say a server was stopped ON
/// PURPOSE but not that a crashed process is still up, so the probe does not guess. Null makes the
/// machine treat a confirmed outage as OFFLINE rather than an RCON-only fault, which is the safe
/// side to be wrong on.
/// </remarks>
public sealed class RconServerProbe(
    RconRegistry rcon,
    MonitorSettings settings,
    TimeProvider time,
    IServerLifecycle? lifecycle = null,
    Func<string, DateTimeOffset?>? lastLogActivity = null) : IServerProbe
{
    public async Task<HealthProbe> ProbeAsync(string server, CancellationToken ct = default)
    {
        var at = time.GetUtcNow();

        var intentional = false;
        if (lifecycle is not null)
        {
            try { intentional = await lifecycle.IsIntentionallyStoppedAsync(server, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* uncertain: fault stands */ }
        }

        var log = SafeLog(server);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.RconTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Uncached on purpose: a monitor must time a real round trip and must not read a
            // cached success for a server that has already gone down.
            var reply = await rcon.ProbeAsync(server, "ServerInfo", timeout.Token).ConfigureAwait(false);
            stopwatch.Stop();

            string? map = null;
            int? max = null;
            if (RconReply.TryParse(reply, out var document))
            {
                using (document)
                {
                    var info = ServerInfoReply.Read(document!.RootElement);
                    map = info.MapLabel;
                    max = info.MaxPlayers;
                }
            }

            var roster = rcon.Roster(server);
            int? players = roster.TakenAt > DateTimeOffset.MinValue ? roster.Players.Count : null;

            return new HealthProbe(
                RconOk: true, Latency: stopwatch.Elapsed, Players: players, MaxPlayers: max, Map: map,
                ProcessRunning: null, IntentionallyStopped: intentional, LastLogActivity: log,
                FailureReason: null, At: at);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the bot is shutting down - not a server outage
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var reason = timeout.IsCancellationRequested
                ? $"no reply within {settings.RconTimeout.TotalSeconds:0}s"
                : ex.Message;
            return new HealthProbe(
                RconOk: false, Latency: null, Players: null, MaxPlayers: null, Map: null,
                ProcessRunning: null, IntentionallyStopped: intentional, LastLogActivity: log,
                FailureReason: reason, At: at);
        }
    }

    private DateTimeOffset? SafeLog(string server)
    {
        if (lastLogActivity is null) return null;
        try { return lastLogActivity(server); }
        catch (Exception) { return null; }   // a log we cannot read is "unknown", never a crash
    }
}
