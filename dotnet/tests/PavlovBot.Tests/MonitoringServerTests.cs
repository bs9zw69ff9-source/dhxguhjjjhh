using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Routing and authorisation, exercised without binding a socket.
/// </summary>
public class MonitoringServerTests
{
    private static MonitoringServer Build(
        string? token = null,
        HealthRegistry? health = null,
        MetricsRegistry? metrics = null,
        Func<bool>? readiness = null) =>
        new(new MonitoringOptions(9109, "127.0.0.1", token),
            metrics ?? new MetricsRegistry(),
            health ?? new HealthRegistry(cacheDuration: TimeSpan.Zero),
            NullLogger<MonitoringServer>.Instance,
            readiness);

    [Fact]
    public async Task MetricsAreServedInPrometheusFormat()
    {
        var metrics = new MetricsRegistry();
        metrics.Increment("commands_total", help: "Commands run");

        var response = await Build(metrics: metrics).RouteAsync("GET", "/metrics", null, default);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("version=0.0.4", response.ContentType, StringComparison.Ordinal);
        Assert.Contains("bot_commands_total 1", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyUnhealthyReturns503()
    {
        /* "Degraded" must not take a bot out of a load balancer or trigger a restart loop
           because one optional detector is down. That distinction is the entire operational
           value of the three-state model. */
        var health = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        health.Register("thing", _ => Task.FromResult(HealthResult.Degraded("slow")));
        Assert.Equal(200, (await Build(health: health).RouteAsync("GET", "/health", null, default)).StatusCode);

        var failing = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        failing.Register("thing", _ => Task.FromResult(HealthResult.Unhealthy("down")));
        Assert.Equal(503, (await Build(health: failing).RouteAsync("GET", "/health", null, default)).StatusCode);
    }

    [Fact]
    public async Task TheHealthBodyIsValidJsonWithTheComponentDetail()
    {
        var health = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        health.Register("rcon", _ => Task.FromResult(HealthResult.Degraded("server2: connection refused")));

        var response = await Build(health: health).RouteAsync("GET", "/health", null, default);
        using var document = System.Text.Json.JsonDocument.Parse(response.Body);

        Assert.Equal("degraded", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("server2: connection refused",
            document.RootElement.GetProperty("components").GetProperty("rcon").GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ADetailContainingQuotesDoesNotBreakTheJson()
    {
        // Details carry exception messages, which carry paths, hostnames and quoted input.
        var health = new HealthRegistry(cacheDuration: TimeSpan.Zero);
        health.Register("x", _ => Task.FromResult(HealthResult.Unhealthy("could not open \"C:\\a\\b\"\nretrying")));

        var response = await Build(health: health).RouteAsync("GET", "/health", null, default);
        using var document = System.Text.Json.JsonDocument.Parse(response.Body);   // throws if malformed
        Assert.Equal(503, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessReflectsTheGateway()
    {
        Assert.Equal(200, (await Build(readiness: () => true).RouteAsync("GET", "/ready", null, default)).StatusCode);
        Assert.Equal(503, (await Build(readiness: () => false).RouteAsync("GET", "/ready", null, default)).StatusCode);
    }

    [Fact]
    public async Task ATokenGuardsEverythingExceptLiveness()
    {
        var server = Build(token: "secret");

        Assert.Equal(401, (await server.RouteAsync("GET", "/metrics", null, default)).StatusCode);
        Assert.Equal(401, (await server.RouteAsync("GET", "/health", "Bearer wrong", default)).StatusCode);
        Assert.Equal(200, (await server.RouteAsync("GET", "/metrics", "Bearer secret", default)).StatusCode);

        /* Liveness stays OPEN deliberately: an orchestrator has to be able to restart a bot
           whose configuration is broken, and a wrong metrics token is exactly that case. */
        Assert.Equal(200, (await server.RouteAsync("GET", "/healthz", null, default)).StatusCode);
    }

    [Fact]
    public async Task QueryStringsDoNotDefeatRouting()
    {
        // Prometheus and uptime checkers both append cache-busting parameters.
        Assert.Equal(200, (await Build().RouteAsync("GET", "/metrics?x=1", null, default)).StatusCode);
    }

    [Fact]
    public async Task NonReadMethodsAreRefused()
    {
        // Nothing here mutates state, so anything else is a mistake or a probe.
        Assert.Equal(405, (await Build().RouteAsync("POST", "/metrics", null, default)).StatusCode);
        Assert.Equal(405, (await Build().RouteAsync("DELETE", "/health", null, default)).StatusCode);
        Assert.Equal(200, (await Build().RouteAsync("HEAD", "/healthz", null, default)).StatusCode);
    }

    [Fact]
    public async Task UnknownPathsGiveABare404()
    {
        // Nothing to enumerate: no body, no hint that another path might exist.
        var response = await Build().RouteAsync("GET", "/admin", null, default);
        Assert.Equal(404, response.StatusCode);
        Assert.Equal("", response.Body);
    }
}
