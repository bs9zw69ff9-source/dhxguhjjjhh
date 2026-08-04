using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Restarting a crashed server, and the four times it must not.
/// </summary>
/// <remarks>
/// This is an automation that restarts game servers with nobody watching, so almost every
/// test here is about NOT restarting. The dangerous failure is not "a crash went unnoticed" -
/// it is a loop that hammers the box, or a fight with an admin who took a server down
/// deliberately and cannot work out why it keeps coming back.
/// </remarks>
public class CrashRecoveryTests
{
    private readonly TestClock _time = new();

    /// <summary>systemd, as a dictionary the test writes to.</summary>
    private sealed class FakeUnits(params string[] units) : IUnitControl
    {
        public IReadOnlyList<string> Units { get; } = units;

        public Dictionary<string, UnitState> States { get; } = new(StringComparer.Ordinal);

        /// <summary>Every restart asked for, in order.</summary>
        public List<string> Restarts { get; } = [];

        /// <summary>Units whose restart command fails.</summary>
        public HashSet<string> RestartFails { get; } = new(StringComparer.Ordinal);

        /// <summary>Whether a successful restart actually brings the unit back.</summary>
        public bool RestartHeals { get; set; }

        public Task<UnitState> StateAsync(string unit, CancellationToken ct = default) =>
            Task.FromResult(States.GetValueOrDefault(unit, UnitState.Active));

        public Task<UnitResult> RunAsync(string unit, UnitAction action, CancellationToken ct = default)
        {
            Restarts.Add(unit);

            if (RestartFails.Contains(unit))
                return Task.FromResult(new UnitResult(false, unit, "job failed"));

            if (RestartHeals) States[unit] = UnitState.Active;
            return Task.FromResult(new UnitResult(true, unit, "restart ok"));
        }
    }

    private CrashRecovery Build(FakeUnits units) =>
        new(units, NullLogger<CrashRecovery>.Instance, _time);

    // ---- the one case it acts on ----

    [Fact]
    public async Task AFailedUnitIsRestarted()
    {
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;

        var sweep = await Build(units).SweepAsync();

        Assert.Equal(["pavlovserver"], sweep.Restarted);
        Assert.Equal(["pavlovserver"], units.Restarts);
    }

    [Fact]
    public async Task OnlyTheFailedUnitIsTouched()
    {
        var units = new FakeUnits("pavlovserver", "pavlovserver1", "pavlovserver2");
        units.States["pavlovserver1"] = UnitState.Failed;

        var sweep = await Build(units).SweepAsync();

        Assert.Equal(["pavlovserver1"], sweep.Restarted);
        Assert.Single(units.Restarts);
    }

    // ---- the cases it must leave alone ----

    [Theory]
    [InlineData(UnitState.Inactive)]
    [InlineData(UnitState.Unknown)]
    [InlineData(UnitState.Active)]
    [InlineData(UnitState.Starting)]
    public async Task NothingButFailedIsRestarted(UnitState state)
    {
        /* INACTIVE IS THE IMPORTANT ONE. systemd distinguishes a unit somebody STOPPED from
           one that DIED, and /serverswitch stop produces the former. Restarting on inactive
           means an admin takes a server down, this puts it back, and they conclude the bot is
           broken - which it would be.

           UNKNOWN matters nearly as much: a systemctl that could not be read is not evidence
           of anything, and treating "cannot tell" as "crashed" turns a monitoring outage into
           a restart storm. */
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = state;

        var sweep = await Build(units).SweepAsync();

        Assert.Empty(units.Restarts);
        Assert.False(sweep.DidAnything);
    }

    // ---- the brake ----

    [Fact]
    public async Task ItGivesUpAfterTheAttemptCap()
    {
        /* A server that dies on startup - bad config, corrupt map, full disk - fails again
           within seconds of every restart. Without a cap this is an infinite loop that buries
           the real error under a thousand identical log lines. */
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;
        var recovery = Build(units);

        for (var i = 0; i < CrashRecovery.MaxAttempts; i++)
        {
            await recovery.SweepAsync();
            _time.Advance(CrashRecovery.Cooldown + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(CrashRecovery.MaxAttempts, units.Restarts.Count);

        var afterCap = await recovery.SweepAsync();

        Assert.Equal(CrashRecovery.MaxAttempts, units.Restarts.Count);   // no fourth attempt
        Assert.Contains("pavlovserver", afterCap.Suppressed.Keys);
        Assert.Contains("human", afterCap.Suppressed["pavlovserver"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GivingUpIsAnnouncedOnceRatherThanEverySweep()
    {
        /* The sweep runs every minute. A give-up that reports itself every time is how a
           staff channel becomes something people mute - and muting it loses the alerts that
           matter. */
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;
        var recovery = Build(units);

        for (var i = 0; i < CrashRecovery.MaxAttempts; i++)
        {
            await recovery.SweepAsync();
            _time.Advance(CrashRecovery.Cooldown + TimeSpan.FromSeconds(1));
        }

        var first = await recovery.SweepAsync();
        var second = await recovery.SweepAsync();
        var third = await recovery.SweepAsync();

        Assert.NotEmpty(first.Suppressed);
        Assert.Empty(second.Suppressed);
        Assert.Empty(third.Suppressed);
    }

    [Fact]
    public async Task TheCooldownStopsTwoRestartsInAMinute()
    {
        // A Pavlov server takes the better part of a minute to come up. Restarting one that
        // is still starting reports a failure for a unit that was fine.
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;
        var recovery = Build(units);

        await recovery.SweepAsync();
        await recovery.SweepAsync();          // immediately after - inside the cooldown
        _time.Advance(TimeSpan.FromSeconds(30));
        await recovery.SweepAsync();          // still inside it

        Assert.Single(units.Restarts);

        _time.Advance(CrashRecovery.Cooldown);
        await recovery.SweepAsync();

        Assert.Equal(2, units.Restarts.Count);
    }

    [Fact]
    public async Task ACooldownIsNotReportedAsASuppression()
    {
        // Waiting for a server to come up is the normal path, not something to alert on.
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;
        var recovery = Build(units);

        await recovery.SweepAsync();
        var duringCooldown = await recovery.SweepAsync();

        Assert.Empty(duringCooldown.Suppressed);
        Assert.Empty(duringCooldown.Restarted);
    }

    // ---- recovery resets the brake ----

    [Fact]
    public async Task ComingBackHealthyClearsTheHistory()
    {
        /* A crash next week is new information. Judging it against attempts from a fault that
           has since been fixed would give up on the first failure. */
        var units = new FakeUnits("pavlovserver") { RestartHeals = true };
        units.States["pavlovserver"] = UnitState.Failed;
        var recovery = Build(units);

        await recovery.SweepAsync();
        Assert.Equal(1, recovery.AttemptsFor("pavlovserver"));

        await recovery.SweepAsync();                       // healthy now - history cleared
        Assert.Equal(0, recovery.AttemptsFor("pavlovserver"));

        // Crashes again much later: it gets the full allowance again.
        _time.Advance(TimeSpan.FromHours(2));
        units.States["pavlovserver"] = UnitState.Failed;
        await recovery.SweepAsync();

        Assert.Equal(2, units.Restarts.Count);
    }

    [Fact]
    public async Task AttemptsOutsideTheWindowStopCounting()
    {
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;
        var recovery = Build(units);

        await recovery.SweepAsync();
        _time.Advance(CrashRecovery.AttemptWindow + TimeSpan.FromMinutes(1));

        Assert.Equal(0, recovery.AttemptsFor("pavlovserver"));
    }

    // ---- a restart that itself fails ----

    [Fact]
    public async Task AFailedRestartIsReportedRatherThanCountedAsSuccess()
    {
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;
        units.RestartFails.Add("pavlovserver");

        var sweep = await Build(units).SweepAsync();

        Assert.Empty(sweep.Restarted);
        Assert.Contains("pavlovserver", sweep.Suppressed.Keys);
        Assert.Contains("job failed", sweep.Suppressed["pavlovserver"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRestartStillCountsTowardTheCap()
    {
        // Otherwise a unit whose restart command always fails retries forever, which is the
        // loop the cap exists to prevent.
        var units = new FakeUnits("pavlovserver");
        units.States["pavlovserver"] = UnitState.Failed;
        units.RestartFails.Add("pavlovserver");
        var recovery = Build(units);

        for (var i = 0; i < CrashRecovery.MaxAttempts + 2; i++)
        {
            await recovery.SweepAsync();
            _time.Advance(CrashRecovery.Cooldown + TimeSpan.FromSeconds(1));
        }

        Assert.Equal(CrashRecovery.MaxAttempts, units.Restarts.Count);
    }
}
