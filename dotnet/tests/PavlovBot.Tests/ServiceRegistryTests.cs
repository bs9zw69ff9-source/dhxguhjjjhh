using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Services;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Ported from test/services.test.js.
///
/// These are the properties that separate "a timer" from "a supervised service": a failing
/// tick is contained, a slow tick cannot pile up, and shutdown is deterministic.
/// </summary>
public class ServiceRegistryTests
{
    private static ServiceRegistry New(MetricsRegistry? metrics = null) =>
        new(metrics ?? new MetricsRegistry(), NullLogger.Instance);

    private static ServiceDefinition Definition(string name, Func<CancellationToken, Task>? tick = null) => new()
    {
        Name = name,
        Tick = tick ?? (_ => Task.CompletedTask),
    };

    [Fact]
    public void DependenciesDecideStartOrder()
    {
        var ordered = ServiceRegistry.OrderByDependencies([
            Definition("c") with { DependsOn = ["b"] },
            Definition("a"),
            Definition("b") with { DependsOn = ["a"] },
        ]);

        Assert.Equal(["a", "b", "c"], ordered.Select(d => d.Name));
    }

    [Fact]
    public void ACycleIsReportedWithItsParticipants()
    {
        // "Stack overflow" at 3am is not a diagnosis. The message has to name the loop.
        var error = Assert.Throws<InvalidOperationException>(() => ServiceRegistry.OrderByDependencies([
            Definition("a") with { DependsOn = ["b"] },
            Definition("b") with { DependsOn = ["a"] },
        ]));

        Assert.Contains("circular", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a", error.Message, StringComparison.Ordinal);
        Assert.Contains("b", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownDependencyIsReported_NotIgnored()
    {
        // A typo'd dependency that quietly does nothing is worse than a startup failure.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ServiceRegistry.OrderByDependencies([Definition("a") with { DependsOn = ["ghost"] }]));

        Assert.Contains("unknown service \"ghost\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteringTheSameNameTwiceIsRefused()
    {
        var registry = New();
        registry.Register(Definition("only-one"));
        Assert.Throws<InvalidOperationException>(() => registry.Register(Definition("only-one")));
    }

    [Fact]
    public void TheRconSessionOutlastsItsOwnHealthProbe()
    {
        /* THE CONNECTION IS ONLY PERSISTENT BY ACCIDENT OTHERWISE. Nothing sends on an idle
           session, so what keeps it open is the health probe touching it. Raise the probe
           interval past the idle timeout and every probe finds a stale session, tears it
           down and reconnects - a fresh authenticated TCP session per sweep, forever, with
           nothing anywhere saying so. */
        var probe = TimeSpan.FromMinutes(10);

        var held = PavlovBot.Host.Rcon.RconRegistry.HoldOpen(TimeSpan.FromMinutes(5), probe);

        Assert.True(held > probe, $"a {probe.TotalMinutes}min probe would idle out a {held.TotalMinutes}min session");
    }

    [Fact]
    public void AGenerousIdleTimeoutIsLeftAlone()
    {
        // The floor only ever raises it. An operator asking for a longer-lived session gets
        // one; they cannot accidentally ask for one that churns.
        var configured = TimeSpan.FromHours(1);

        Assert.Equal(configured,
            PavlovBot.Host.Rcon.RconRegistry.HoldOpen(configured, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void TheDefaultsAlreadyHoldTheConnectionOpen()
    {
        // 60s probe against a 5min recycle - four probes land inside every window, which is
        // why the connection has in fact been persistent all along.
        Assert.Equal(TimeSpan.FromMinutes(5),
            PavlovBot.Host.Rcon.RconRegistry.HoldOpen(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task AThrowingTickIsContainedAndCounted()
    {
        /* The whole point. In the Node bot this was an unhandled rejection, which can take
           the process down - one failing sweep killing a bot that was otherwise fine. */
        var ticks = 0;
        await using var registry = New();
        registry.Register(Definition("flaky", _ =>
        {
            ticks++;
            throw new InvalidOperationException("nope");
        }) with { Interval = TimeSpan.FromMilliseconds(20), RunOnStart = true });

        await registry.StartAllAsync();
        await WaitUntil(() => ticks >= 3);
        await registry.StopAllAsync();

        var status = registry.Status().Single();
        Assert.Equal(ServiceState.Stopped, status.State);
        Assert.True(status.Failures >= 3);

        /* The TYPE is kept alongside the message now. A bare "Object reference not set to an
           instance of an object" identifies nothing, and it is exactly what a null deref in
           a tick produces. */
        Assert.Equal("InvalidOperationException: nope", status.LastError);
        Assert.NotEmpty(status.Errors);
    }

    [Fact]
    public async Task ASlowTickIsNeverReEntered()
    {
        /* With a bare timer, a tick slower than its interval stacks up copies of itself;
           with N servers and a slow RCON that is unbounded concurrency, which is exactly
           how a healthy bot becomes an unresponsive one. */
        var concurrent = 0;
        var peak = 0;
        await using var registry = New();
        registry.Register(Definition("slow", async ct =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref concurrent));
            await Task.Delay(60, ct);
            Interlocked.Decrement(ref concurrent);
        }) with { Interval = TimeSpan.FromMilliseconds(10), RunOnStart = true });

        await registry.StartAllAsync();
        await Task.Delay(300);
        await registry.StopAllAsync();

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task ASlowTickIsCountedAsAnOverrun()
    {
        // The replacement for Node's "skipped" counter, and a stronger signal: this is
        // exactly "the service is no longer running at its configured frequency".
        await using var registry = New();
        registry.Register(Definition("slow", async ct => await Task.Delay(60, ct))
            with { Interval = TimeSpan.FromMilliseconds(10), RunOnStart = true });

        await registry.StartAllAsync();
        await WaitUntil(() => registry.Status().Single().Overruns >= 2);
        await registry.StopAllAsync();

        Assert.True(registry.Status().Single().Overruns >= 2);
    }

    [Fact]
    public async Task ANonCriticalServiceThatFailsToStartDoesNotAbortStartup()
    {
        // The bot should come up with one feature dark, not not at all.
        await using var registry = New();
        registry.Register(Definition("broken") with { OnStart = _ => throw new InvalidOperationException("no socket") });
        registry.Register(Definition("fine") with { Interval = TimeSpan.FromMinutes(5) });

        await registry.StartAllAsync();

        Assert.Equal(ServiceState.Failed, registry.Status().Single(s => s.Name == "broken").State);
        Assert.Equal(ServiceState.Running, registry.Status().Single(s => s.Name == "fine").State);
    }

    [Fact]
    public async Task ACriticalServiceThatFailsToStartAbortsStartup()
    {
        await using var registry = New();
        registry.Register(Definition("essential") with
        {
            Critical = true,
            OnStart = _ => throw new InvalidOperationException("no database"),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.StartAllAsync());
    }

    [Fact]
    public async Task RunOnStartIsOffByDefault()
    {
        /* Load-bearing default. Services start BEFORE the gateway connects, so an immediate
           tick on anything that posts to Discord fires with no connection - and re-fires on
           every supervisor restart. The Node bot shipped a leaderboard regression this way. */
        var ticks = 0;
        await using var registry = New();
        registry.Register(Definition("board", _ => { ticks++; return Task.CompletedTask; })
            with { Interval = TimeSpan.FromMinutes(10) });

        await registry.StartAllAsync();
        await Task.Delay(50);
        await registry.StopAllAsync();

        Assert.Equal(0, ticks);
    }

    [Fact]
    public async Task StoppingIsDeterministic_NoTickRunsAfterStopReturns()
    {
        // If a tick can still fire after shutdown, teardown races it - and the thing it
        // writes to may already be disposed.
        var ticks = 0;
        await using var registry = New();
        registry.Register(Definition("counter", _ => { Interlocked.Increment(ref ticks); return Task.CompletedTask; })
            with { Interval = TimeSpan.FromMilliseconds(10), RunOnStart = true });

        await registry.StartAllAsync();
        await WaitUntil(() => ticks > 0);
        await registry.StopAllAsync();

        var atStop = ticks;
        await Task.Delay(100);
        Assert.Equal(atStop, ticks);
    }

    [Fact]
    public async Task RepeatedFailuresAreRevivedByTheSupervisor()
    {
        var fail = true;
        await using var registry = New();
        registry.Register(Definition("wobbly", _ => fail ? throw new InvalidOperationException("x") : Task.CompletedTask)
            with { Interval = TimeSpan.FromMilliseconds(10), RunOnStart = true });

        await registry.StartAllAsync();
        await WaitUntil(() => registry.Status().Single().ConsecutiveFailures >= 5);

        /* Revive while it is STILL failing. Clearing the flag first leaves a window in
           which a tick succeeds, resets the counter, and the supervisor correctly finds
           nothing to do - a race in the test, not in the code. */
        var revived = await registry.ReviveFailedAsync(threshold: 5);
        Assert.Equal(["wobbly"], revived);

        fail = false;
        await WaitUntil(() => registry.Status().Single().ConsecutiveFailures == 0);
        await registry.StopAllAsync();
    }

    [Fact]
    public async Task HealthDistinguishesRunning_Stopped_AndFailing()
    {
        await using var registry = New();
        registry.Register(Definition("ok") with { Interval = TimeSpan.FromMinutes(5) });
        await registry.StartAllAsync();
        Assert.Equal(HealthStatus.Healthy, (await registry.HealthAsync()).Status);

        await registry.StopOneAsync("ok");
        // Stopped is DEGRADED, not unhealthy: an intentionally stopped service is not a fault.
        Assert.Equal(HealthStatus.Degraded, (await registry.HealthAsync()).Status);
    }

    [Fact]
    public async Task APerServiceHealthCheckCanDowngradeARunningService()
    {
        // "The timer is ticking" is not the same as "the work is succeeding".
        await using var registry = New();
        registry.Register(Definition("ticking") with
        {
            Interval = TimeSpan.FromMinutes(5),
            HealthCheck = _ => Task.FromResult(HealthResult.Degraded("roster is 20 minutes stale")),
        });

        await registry.StartAllAsync();
        var health = await registry.HealthAsync();

        Assert.Equal(HealthStatus.Degraded, health.Status);
        Assert.Contains("stale", health.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowingHealthCheckIsUnhealthy_NotAnException()
    {
        await using var registry = New();
        registry.Register(Definition("x") with
        {
            Interval = TimeSpan.FromMinutes(5),
            HealthCheck = _ => throw new InvalidOperationException("check blew up"),
        });

        await registry.StartAllAsync();
        Assert.Equal(HealthStatus.Unhealthy, (await registry.HealthAsync()).Status);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "condition was not met within the timeout");
    }
}
