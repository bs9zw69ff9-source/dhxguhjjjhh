using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>Answers a fixed verdict, or throws, without touching systemd.</summary>
internal sealed class StubLifecycle(params string[] stopped) : IServerLifecycle
{
    private readonly HashSet<string> _stopped = new(stopped, StringComparer.Ordinal);
    public Exception? Throws { get; init; }

    public Task<bool> IsIntentionallyStoppedAsync(string rconServerName, CancellationToken ct = default) =>
        Throws is not null ? Task.FromException<bool>(Throws) : Task.FromResult(_stopped.Contains(rconServerName));
}

/// <summary>
/// RCON health, and the difference between a server that is BROKEN and one that is OFF.
/// </summary>
/// <remarks>
/// Stopping a server with /serverswitch makes its RCON port stop answering - the correct and
/// expected consequence of the thing the admin just asked for. Health had no way to know
/// that, so a deliberate shutdown lit /health red and kept it red until somebody started the
/// server again. An alert that fires on an intended action is one people learn to ignore,
/// and this one shares a screen with the faults that matter.
/// </remarks>
public class RconHealthTests
{
    private static RconRegistry Registry(params string[] serverNumbers)
    {
        var options = new BotOptions
        {
            DiscordToken = "t",
            Servers = [.. serverNumbers.Select(n => new RconOptions
            {
                Name = $"server{n}", Host = "127.0.0.1", Port = 9100 + int.Parse(n), Password = "x",
            })],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        };

        return new RconRegistry(options, new MetricsRegistry(), NullLogger<RconRegistry>.Instance);
    }

    /// <summary>Record a probe failure the way ProbeAllAsync would, without a socket.</summary>
    private static async Task<HealthResult> HealthAfterFailingProbes(
        RconRegistry registry, IServerLifecycle? lifecycle)
    {
        if (lifecycle is not null) registry.UseLifecycle(lifecycle);

        // No server is listening on these ports, so every probe fails - which is exactly the
        // state a stopped game server produces.
        await registry.ProbeAllAsync(CancellationToken.None);
        return await registry.HealthAsync();
    }

    [Fact]
    public async Task AServerStoppedOnPurposeDoesNotCountAgainstHealth()
    {
        /* THE POINT. Every server here is unreachable; systemd says both were stopped
           deliberately, so there is no fault to report. */
        var registry = Registry("1", "2");

        var health = await HealthAfterFailingProbes(registry, new StubLifecycle("server1", "server2"));

        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.Contains("stopped on purpose", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerThatIsSupposedToBeUpStillCountsAgainstHealth()
    {
        // The other half: suppressing a real outage would be far worse than reporting an
        // expected one, so only the servers systemd calls stopped are excused.
        var registry = Registry("1", "2");

        var health = await HealthAfterFailingProbes(registry, new StubLifecycle("server1"));

        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("server2", health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoLifecycleSourceEveryUnreachableServerCountsAsBefore()
    {
        /* A deployment without systemd passes nothing and gets exactly the old behaviour.
           Being unable to ask is not an excuse. */
        var registry = Registry("1", "2");

        var health = await HealthAfterFailingProbes(registry, lifecycle: null);

        Assert.Equal(HealthStatus.Unhealthy, health.Status);
    }

    [Fact]
    public async Task ALifecycleLookupThatThrowsLeavesTheFaultStanding()
    {
        // A bug that silences a real outage because a lookup failed is the one outcome
        // worth ruling out explicitly.
        var registry = Registry("1");

        var health = await HealthAfterFailingProbes(
            registry, new StubLifecycle("server1") { Throws = new InvalidOperationException("systemd unreachable") });

        Assert.Equal(HealthStatus.Unhealthy, health.Status);
    }

    [Fact]
    public async Task AStoppedServerDoesNotTradeARedRconCheckForAStaleRosterOne()
    {
        /* Rosters only ever arrive from RUNNING servers. Excusing the RCON failure but then
           reporting "every roster is stale" would be the same false alarm in a different
           hat, and that is exactly what the first version of this did. */
        var registry = Registry("1");

        var health = await HealthAfterFailingProbes(registry, new StubLifecycle("server1"));

        Assert.Equal(HealthStatus.Healthy, health.Status);
        Assert.DoesNotContain("stale", health.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnRconNameResolvesToItsServerNumber()
    {
        // The bridge between the two naming schemes, and it has to survive both directions.
        Assert.Equal(1, PavlovBot.Host.Servers.ServiceControl.NumberFor("server1"));
        Assert.Equal(12, PavlovBot.Host.Servers.ServiceControl.NumberFor("server12"));
        Assert.Null(PavlovBot.Host.Servers.ServiceControl.NumberFor("server0"));
        Assert.Null(PavlovBot.Host.Servers.ServiceControl.NumberFor("prod-a"));
        Assert.Null(PavlovBot.Host.Servers.ServiceControl.NumberFor("server"));
    }
}
