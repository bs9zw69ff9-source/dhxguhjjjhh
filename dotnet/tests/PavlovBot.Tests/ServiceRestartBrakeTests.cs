using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Services;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The supervisor's brake: it must stop restarting a service that restarting cannot fix.
/// </summary>
/// <remarks>
/// A service whose dependency is simply down - RCON unreachable, a game server off, a full
/// disk - fails every tick, and a restart fixes none of those. StartOneAsync clears
/// ConsecutiveFailures, so the counter climbs back to the threshold and the service is
/// restarted again, for as long as the outage lasts. Each restart awaits the running loop,
/// which can hold for a whole tick budget before letting go.
///
/// CrashRecovery reached this conclusion first for Pavlov servers and caps them. The
/// supervisor for the bot's OWN services was the one place without the rule.
/// </remarks>
public class ServiceRestartBrakeTests
{
    private static ServiceRegistry Registry() =>
        new(new MetricsRegistry(), NullLogger<ServiceRegistry>.Instance);

    /// <summary>A service that fails every tick, counting how often it is started.</summary>
    private static ServiceDefinition AlwaysFails(string name, Action onStart) => new()
    {
        Name = name,
        Interval = TimeSpan.FromMilliseconds(20),
        RunOnStart = true,
        OnStart = _ => { onStart(); return Task.CompletedTask; },
        Tick = _ => throw new InvalidOperationException("its dependency is down"),
    };

    [Fact]
    public async Task ARestartLoopIsCappedRatherThanRunningForever()
    {
        var starts = 0;
        var registry = Registry();
        registry.Register(AlwaysFails("doomed", () => Interlocked.Increment(ref starts)));
        await registry.StartAllAsync(CancellationToken.None);

        // Enough sweeps that an uncapped supervisor would restart on every one of them.
        const int sweeps = 12;
        for (var sweep = 0; sweep < sweeps; sweep++)
        {
            await Task.Delay(30);
            await registry.ReviveFailedAsync(threshold: 1);
        }

        /* TWO ASSERTIONS, because the obvious one is self-fulfilling. Checking only
           `starts <= 1 + MaxRestartsPerWindow` passes for ANY value of that constant, so
           raising it to 1000 would "pass" a supervisor with no brake at all - measured: 13
           starts across these sweeps uncapped, 4 with the cap in place.

           The property that actually matters is that restarts do NOT scale with how long the
           outage lasts. Sweeps is the proxy for elapsed time, so the count must stay well
           under it however many sweeps run. */
        Assert.True(starts <= 1 + ServiceRegistry.MaxRestartsPerWindow,
            $"expected at most {1 + ServiceRegistry.MaxRestartsPerWindow} starts, saw {starts}");
        Assert.True(starts < sweeps,
            $"restarts scale with the length of the outage ({starts} starts over {sweeps} sweeps) - the brake is not holding");

        await registry.DisposeAsync();
    }

    [Fact]
    public async Task GivingUpStillLeavesTheServiceVisiblyBroken()
    {
        /* Stopping the restarts must not stop the REPORTING. Quietly abandoning a service
           would turn a loud failure into a silent one, which is worse than the loop. */
        var registry = Registry();
        registry.Register(AlwaysFails("doomed", () => { }));
        await registry.StartAllAsync(CancellationToken.None);

        for (var sweep = 0; sweep < 12; sweep++)
        {
            await Task.Delay(30);
            await registry.ReviveFailedAsync(threshold: 1);
        }

        var health = await registry.HealthAsync();
        Assert.NotEqual(HealthStatus.Healthy, health.Status);

        await registry.DisposeAsync();
    }

    [Fact]
    public async Task AServiceThatRecoversGetsItsRestartBudgetBack()
    {
        /* Without this the cap is permanent: three restarts inside any half hour would
           disable supervision of that service for the life of the process, so a service that
           recovered and broke again months later would never be revived. */
        var fail = true;
        var starts = 0;
        var registry = Registry();
        registry.Register(new ServiceDefinition
        {
            Name = "flaky",
            Interval = TimeSpan.FromMilliseconds(20),
            RunOnStart = true,
            OnStart = _ => { Interlocked.Increment(ref starts); return Task.CompletedTask; },
            Tick = _ => Volatile.Read(ref fail)
                ? throw new InvalidOperationException("down")
                : Task.CompletedTask,
        });
        await registry.StartAllAsync(CancellationToken.None);

        for (var sweep = 0; sweep < 8; sweep++)
        {
            await Task.Delay(30);
            await registry.ReviveFailedAsync(threshold: 1);
        }
        var afterOutage = starts;

        // It recovers, ticks successfully, then breaks again.
        Volatile.Write(ref fail, false);
        await Task.Delay(120);
        Volatile.Write(ref fail, true);

        for (var sweep = 0; sweep < 8; sweep++)
        {
            await Task.Delay(30);
            await registry.ReviveFailedAsync(threshold: 1);
        }

        Assert.True(starts > afterOutage,
            "a service that recovered should be revivable again, but the brake stayed on");

        await registry.DisposeAsync();
    }
}
