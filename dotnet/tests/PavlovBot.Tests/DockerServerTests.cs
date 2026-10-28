using PavlovBot.Core.Provisioning;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Game servers in Docker: the entry format, the exact docker command lines, and the parsing.
/// </summary>
/// <remarks>
/// The sample outputs are copied from a real run of powersjk/pavlov-vr-shack-server:1.8.0 under
/// Docker 29, so a change to how they are read is checked against what Docker actually prints.
/// </remarks>
public class DockerServerTests
{
    private static ServerProvisionSpec Spec() => new()
    {
        Slot = 3,
        UnitName = "docker:pavlovserver2",
        InstallDir = "/home/steam/pavlovserver2",
        ServerName = "Greenwood County [#3]",
        GamePort = 7779,
        QueryPort = 8179,
        RconPort = 9300,
        RconPassword = "s3cretRconPassword",
        MaxPlayers = 24,
        Maps = [new MapEntry("UGC5616264", "CUSTOM")],
    };

    // ---- the PAVLOV_UNITS entry ----

    [Theory]
    [InlineData("pavlovserver1", ServerRuntime.Systemd, "pavlovserver1")]
    [InlineData("docker:pavlovserver2", ServerRuntime.Docker, "pavlovserver2")]
    [InlineData("DOCKER:pavlovserver2", ServerRuntime.Docker, "pavlovserver2")]
    [InlineData("  docker:pavlov.ttt-2  ", ServerRuntime.Docker, "pavlov.ttt-2")]
    public void AnEntrySaysWhatRunsIt(string entry, ServerRuntime runtime, string name)
    {
        var unit = ServerUnit.Parse(entry);

        Assert.NotNull(unit);
        Assert.Equal(runtime, unit!.Value.Runtime);
        Assert.Equal(name, unit.Value.Name);
    }

    [Theory]
    [InlineData("docker:")]
    [InlineData("docker:--rm")]          // would be read as a flag by docker
    [InlineData("docker:-v/:/host")]
    [InlineData("docker:a b")]
    [InlineData("docker:pavlov@1")]      // fine for systemd, not a container name
    [InlineData("--force")]
    [InlineData("")]
    public void ANameThatCouldBeAFlagIsRefused(string entry)
    {
        Assert.Null(ServerUnit.Parse(entry));
        Assert.False(ServiceControl.IsPlausibleUnitName(entry));
    }

    [Fact]
    public void AnEntryWritesBackAsItWasRead() =>
        Assert.Equal("docker:pavlovserver2", ServerUnit.Parse("docker:pavlovserver2")!.Value.ToString());

    [Fact]
    public void BothKindsSurviveTheUnitList()
    {
        // The Fallout bot's systemd servers and a new container, in one list.
        var units = ServiceControl.ParseUnits("pavlovserver,pavlovserver1,docker:pavlovserver2");

        Assert.Equal(["pavlovserver", "pavlovserver1", "docker:pavlovserver2"], units);
    }

    [Theory]
    [InlineData("docker:pavlovserver2", "docker logs --tail 50 pavlovserver2")]
    [InlineData("pavlovserver2", "systemctl status pavlovserver2")]
    public void TheHintNamesTheRightTool(string entry, string hint) =>
        Assert.Equal(hint, ServerUnit.Parse(entry)!.Value.StatusHint());

    [Fact]
    public void TheBotsOwnUnitIsStillASystemdName()
    {
        // BotSupervisor reads "pavlov-bot.service" out of a cgroup; a docker: prefix is not a unit.
        Assert.True(ServiceControl.IsPlausibleSystemdName("pavlov-bot.service"));
        Assert.False(ServiceControl.IsPlausibleSystemdName("docker:pavlov-bot"));
    }

    // ---- the exact command lines ----

    [Fact]
    public void RunIsHostNetworkedRestartsItselfAndMountsOnlySaved()
    {
        var argv = DockerCli.RunArgv(Spec(), "pavlovserver2", DockerCli.DefaultImage);

        Assert.Equal(
        [
            "run", "-d",
            "--name", "pavlovserver2",
            "--restart", "unless-stopped",
            "--network", "host",
            "--stop-timeout", "30",
            "--label", "pavlovbot.managed=true",
            "--label", "pavlovbot.slot=3",
            "-e", "SERVER_NAME=pavlovserver2",
            "-e", "SERVER_PORT=7779",
            "-v", "/home/steam/pavlovserver2/Pavlov/Saved:/home/steam/pavlovserver/Pavlov/Saved",
            "powersjk/pavlov-vr-shack-server:1.8.0",
        ], argv);
    }

    [Fact]
    public void TheRconPasswordNeverReachesTheContainerEnvironment()
    {
        /* The bot writes RconSettings.txt itself. In the environment it would only show up in
           `docker inspect` for anybody who can run it. */
        var argv = DockerCli.RunArgv(Spec(), "pavlovserver2", DockerCli.DefaultImage);

        Assert.DoesNotContain(argv, a => a.Contains("s3cretRconPassword", StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.StartsWith("RCON_PWD", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRealServerNameStaysOutOfTheEntrypointsSed()
    {
        // The entrypoint pastes SERVER_NAME into a sed expression; the real name is in Game.ini.
        var argv = DockerCli.RunArgv(Spec(), "pavlovserver2", DockerCli.DefaultImage);

        Assert.DoesNotContain(argv, a => a.Contains("Greenwood", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(UnitAction.Start, new[] { "start", "pavlovserver2" })]
    [InlineData(UnitAction.Stop, new[] { "stop", "-t", "30", "pavlovserver2" })]
    [InlineData(UnitAction.Restart, new[] { "restart", "-t", "30", "pavlovserver2" })]
    public void EachVerbIsOneDockerCommand(UnitAction action, string[] argv) =>
        Assert.Equal(argv, DockerCli.VerbArgv(action, "pavlovserver2"));

    [Fact]
    public void RemovalIsForcedSoAStoppedOrRunningOneBothGo() =>
        Assert.Equal(["rm", "-f", "pavlovserver2"], DockerCli.RemoveArgv("pavlovserver2"));

    [Theory]
    [InlineData("powersjk/pavlov-vr-shack-server:1.8.0", true)]
    [InlineData("powersjk/pavlov-vr-shack-server", true)]
    [InlineData("ghcr.io/someone/pavlov:latest", true)]
    [InlineData("powersjk/pavlov-vr-shack-server@sha256:424bb9f2e994500a56eed11993fed49cc4566316b26ef32e84a5b8396e979b9a", true)]
    [InlineData("--privileged", false)]
    [InlineData("image name", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AnImageIsOneSafeArgument(string? image, bool plausible) =>
        Assert.Equal(plausible, DockerCli.IsPlausibleImage(image));

    [Fact]
    public void TheDefaultImageIsPinned()
    {
        Assert.True(DockerCli.IsPlausibleImage(DockerCli.DefaultImage));
        Assert.DoesNotContain(":latest", DockerCli.DefaultImage, StringComparison.Ordinal);
    }

    // ---- reading what Docker prints ----

    [Theory]
    [InlineData("running", UnitState.Active)]
    [InlineData("restarting", UnitState.Starting)]   // a crash under unless-stopped
    [InlineData("created", UnitState.Starting)]
    [InlineData("exited", UnitState.Inactive)]       // only a deliberate stop stays here
    [InlineData("dead", UnitState.Failed)]
    [InlineData("", UnitState.Unknown)]
    [InlineData("something-new", UnitState.Unknown)]
    public void StatesMapOntoTheSameFiveAsSystemd(string status, UnitState state) =>
        Assert.Equal(state, DockerCli.StateFrom(status));

    [Fact]
    public void InspectGivesStateUptimeAndNoLimit()
    {
        var now = new DateTimeOffset(2026, 10, 28, 22, 37, 30, TimeSpan.Zero);

        var (state, uptime, limit) = DockerCli.ParseInspect("running|2026-10-28T22:27:30.412016668Z|0\n", now);

        Assert.Equal(UnitState.Active, state);
        Assert.NotNull(uptime);
        Assert.InRange(uptime!.Value.TotalMinutes, 9.9, 10.0);
        Assert.Null(limit);   // 0 is "no limit"
    }

    [Fact]
    public void AStoppedContainerHasNoUptime()
    {
        var (state, uptime, _) = DockerCli.ParseInspect("exited|2026-10-28T22:27:30.412016668Z|0", DateTimeOffset.UtcNow);

        Assert.Equal(UnitState.Inactive, state);
        Assert.Null(uptime);
    }

    [Fact]
    public void NineFractionalDigitsStillParse() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 22, 27, 30, 412, TimeSpan.Zero).AddTicks(166),
            DockerCli.StartedAt("2026-10-28T22:27:30.412016668Z"));

    [Fact]
    public void ANeverStartedContainersZeroTimeIsNotAStart() =>
        Assert.Null(DockerCli.StartedAt("0001-01-01T00:00:00Z"));

    [Fact]
    public void AStatsLineFromARealServerParses()
    {
        var stat = DockerCli.ParseStatsLine("pavlovserver2|5.46%|635.1MiB / 15.71GiB|26");

        Assert.NotNull(stat);
        Assert.Equal("pavlovserver2", stat!.Value.Name);
        Assert.Equal(5.46, stat.Value.Cpu!.Value, 2);
        Assert.Equal((long)(635.1 * 1024 * 1024), stat.Value.Memory);
        Assert.Equal(26, stat.Value.Pids);
    }

    [Theory]
    [InlineData("512B", 512L)]
    [InlineData("10kB", 10_000L)]
    [InlineData("1.5KiB", 1536L)]
    [InlineData("2GiB", 2L * 1024 * 1024 * 1024)]
    [InlineData("1.2GB", 1_200_000_000L)]
    [InlineData("--", null)]
    [InlineData("12 parsecs", null)]
    public void SizesInEitherUnitFamily(string raw, long? bytes) => Assert.Equal(bytes, DockerCli.Size(raw));

    // ---- configuration ----

    [Theory]
    [InlineData(null, ServerRuntime.Docker)]
    [InlineData("", ServerRuntime.Docker)]
    [InlineData("docker", ServerRuntime.Docker)]
    [InlineData(" Docker ", ServerRuntime.Docker)]
    [InlineData("systemd", ServerRuntime.Systemd)]
    [InlineData("dokcer", null)]   // a typo refuses rather than quietly building systemd
    public void NewServersAreDockerUnlessSaidOtherwise(string? setting, ServerRuntime? runtime) =>
        Assert.Equal(runtime, FeatureOptions.RuntimeFrom(setting));

    [Fact]
    public void ADockerServerCannotBeAddedBesideASystemdOneOfTheSameName()
    {
        var problems = ProvisionValidation.Check(Spec(), [], ["pavlovserver", "pavlovserver2"], []);

        Assert.Contains(problems, p => p.Contains("already configured", StringComparison.Ordinal));
    }

    // ---- the checklist ----

    [Fact]
    public void ADockerProvisionNeedsNoSteamAccountOrSteamCmd()
    {
        Assert.DoesNotContain(ServerProvisioner.DockerStepNames, s => s.Contains("SteamCMD", StringComparison.Ordinal));
        Assert.DoesNotContain(ServerProvisioner.DockerStepNames, s => s.Contains("Steam user", StringComparison.Ordinal));
        Assert.DoesNotContain(ServerProvisioner.DockerStepNames, s => s.Contains("systemd", StringComparison.Ordinal));
        Assert.Equal("Restart the bot", ServerProvisioner.DockerStepNames[^1]);
    }
}
