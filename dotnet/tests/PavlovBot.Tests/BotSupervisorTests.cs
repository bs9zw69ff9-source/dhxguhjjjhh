using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// /provisionserver and /deleteserver restart the bot through whatever is supervising it.
/// </summary>
public class BotSupervisorTests
{
    private const string SystemdCgroup = "0::/system.slice/pavlov-bot.service\n";

    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void ASystemdServiceRestartsItsOwnUnitWithoutBlocking()
    {
        // The live bot: moved off pm2, and /deleteserver then said "restart the bot yourself".
        var supervisor = BotSupervisor.Detect(Env(("INVOCATION_ID", "abc")), SystemdCgroup);

        Assert.Equal(new BotSupervisor(SupervisorKind.Systemd, "pavlov-bot"), supervisor);
        var (file, args) = supervisor.RestartCommand()!.Value;
        Assert.Equal("systemctl", file);
        Assert.Equal(["restart", "--no-block", "pavlov-bot"], args);
    }

    [Fact]
    public void CgroupV1IsReadFromTheSystemdHierarchy()
    {
        const string v1 = "12:cpu,cpuacct:/system.slice/other.service\n1:name=systemd:/system.slice/pavlov-bot.service\n";
        Assert.Equal("pavlov-bot.service", BotSupervisor.UnitFromCgroup(v1));
    }

    [Fact]
    public void Pm2WinsEvenInsideASystemdUnit()
    {
        // pm2's daemon runs from pm2-root.service; restarting that would bounce every pm2 app.
        var supervisor = BotSupervisor.Detect(
            Env(("pm_id", "0"), ("name", "pavlov-bot-fallout"), ("INVOCATION_ID", "abc")),
            "0::/system.slice/pm2-root.service\n");

        Assert.Equal(new BotSupervisor(SupervisorKind.Pm2, "pavlov-bot-fallout"), supervisor);
        var (file, args) = supervisor.RestartCommand()!.Value;
        Assert.Equal("pm2", file);
        Assert.Equal(["restart", "pavlov-bot-fallout"], args);
    }

    [Fact]
    public void AShellInsideAServiceCgroupIsNotTheService()
    {
        Assert.Equal(SupervisorKind.None, BotSupervisor.Detect(Env(), SystemdCgroup).Kind);
    }

    [Fact]
    public void ALoginSessionIsUnmanaged()
    {
        var supervisor = BotSupervisor.Detect(Env(("INVOCATION_ID", "abc")), "0::/user.slice/user-0.slice/session-4.scope\n");

        Assert.Equal(SupervisorKind.None, supervisor.Kind);
        Assert.Null(supervisor.RestartCommand());
    }

    [Theory]
    [InlineData("pavlov-bot.service", "pavlov-bot")]
    [InlineData("-evil.service", null)]
    [InlineData("pavlov-bot.timer", null)]
    public void TheUnitOverrideIsValidatedLikeAnyUnitName(string unit, string? expected)
    {
        var supervisor = BotSupervisor.Detect(Env(("INVOCATION_ID", "abc"), ("PAVLOV_SYSTEMD_UNIT", unit)), null);

        Assert.Equal(expected, supervisor.Kind == SupervisorKind.Systemd ? supervisor.Name : null);
    }
}
