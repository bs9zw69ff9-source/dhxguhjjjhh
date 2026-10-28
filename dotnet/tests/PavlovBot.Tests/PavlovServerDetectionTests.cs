using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Finding the Pavlov servers from their systemd units, so PAVLOV_UNITS and PAVLOV_BASE_1 can be
/// left blank.
/// </summary>
public sealed class PavlovServerDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pavlovbot-detect-" + Guid.NewGuid().ToString("N"));
    private readonly string _units;

    public PavlovServerDetectionTests()
    {
        _units = Path.Combine(_root, "systemd");
        Directory.CreateDirectory(_units);
    }

    /// <summary>No Docker on this "box", whatever the machine running the tests has.</summary>
    private static IReadOnlyList<DetectedServer> NoContainers() => [];

    /// <summary>A Docker install: only Pavlov/Saved on the host, as /provisionserver leaves it.</summary>
    private string DockerInstall(string folder)
    {
        var install = Path.Combine(_root, "steam", folder);
        Directory.CreateDirectory(Path.Combine(install, "Pavlov", "Saved"));
        return install;
    }

    // ---- Docker ----

    [Fact]
    public void AContainerIsFoundByTheFolderItMountsAsSaved()
    {
        var install = DockerInstall("pavlovserver2");

        var found = PavlovServerDetection.ParseDocker($"/pavlovserver2|{install}/Pavlov/Saved\n");

        var server = Assert.Single(found);
        Assert.Equal("docker:pavlovserver2", server.Unit);
        Assert.Equal(install, server.Install);
    }

    [Theory]
    [InlineData("/somethingelse|")]                                   // no Saved mount: not a game server
    [InlineData("/web|/srv/web/data")]                                // mounts something else
    [InlineData("/--rm|/home/steam/x/Pavlov/Saved")]                  // a name that could be a flag
    [InlineData("garbage")]
    public void AnythingElseIsNotAServer(string line) =>
        Assert.Empty(PavlovServerDetection.ParseDocker(line, _ => true));

    [Fact]
    public void AContainerWhoseInstallIsGoneIsNotAServer() =>
        Assert.Empty(PavlovServerDetection.ParseDocker("/pavlovserver2|/nowhere/pavlovserver2/Pavlov/Saved"));

    [Fact]
    public void SystemdAndDockerServersComeOutInOneServerOrder()
    {
        // Servers 1 and 2 still on systemd, server 3 moved into a container.
        var one = Server("pavlovserver");
        var two = Server("pavlovserver1");
        var three = DockerInstall("pavlovserver2");

        var found = PavlovServerDetection.Detect(_units, () => [new DetectedServer("docker:pavlovserver2", three)]);

        Assert.Equal(["pavlovserver", "pavlovserver1", "docker:pavlovserver2"], found.Select(f => f.Unit));
        Assert.Equal([one, two, three], found.Select(f => f.Install));
    }

    [Fact]
    public void AContainerBeatsALeftoverUnitForTheSameInstall()
    {
        /* Half way through a move: the unit file is still there, the container is what runs.
           Restarting the unit would start a second copy of the server on the same ports. */
        var install = Server("pavlovserver2");

        var found = Assert.Single(PavlovServerDetection.Detect(_units, () => [new DetectedServer("docker:pavlovserver2", install)]));

        Assert.Equal("docker:pavlovserver2", found.Unit);
    }

    [Fact]
    public void BlankSettingsFindADockerOnlyBox()
    {
        var one = DockerInstall("pavlovserver");
        var two = DockerInstall("pavlovserver1");

        var installs = PavlovInstalls.Discover(null, null, unitDirectory: _units,
            docker: () => [new DetectedServer("docker:pavlovserver1", two), new DetectedServer("docker:pavlovserver", one)]);

        Assert.Equal([one, two], installs);
    }

    [Fact]
    public void TheInspectFormatReadsTheMountTheProvisionerMakes() =>
        Assert.Contains("\"/home/steam/pavlovserver/Pavlov/Saved\"", PavlovServerDetection.InspectFormat, StringComparison.Ordinal);

    /// <summary>An install, and the unit /provisionserver would write for it.</summary>
    private string Server(string unit, string? folder = null, bool installed = true)
    {
        var install = Path.Combine(_root, "steam", folder ?? unit);
        if (installed) Directory.CreateDirectory(Path.Combine(install, "Pavlov"));
        File.WriteAllText(Path.Combine(_units, unit + ".service"),
            $"[Unit]\nDescription=Pavlov VR Server\n\n[Service]\nUser=steam\nWorkingDirectory={install}\n" +
            $"ExecStart={install}/PavlovServer.sh -PORT=7777\nRestart=on-failure\n");
        return install;
    }

    [Fact]
    public void EveryPavlovUnitIsFoundInServerOrder()
    {
        var three = Server("pavlovserver2");
        var one = Server("pavlovserver");
        var two = Server("pavlovserver1");

        var found = PavlovServerDetection.FromSystemd(_units);

        Assert.Equal(["pavlovserver", "pavlovserver1", "pavlovserver2"], found.Select(f => f.Unit));
        Assert.Equal([one, two, three], found.Select(f => f.Install));
    }

    [Fact]
    public void TenSortsAfterTwo()
    {
        Server("pavlovserver10");
        Server("pavlovserver2");

        Assert.Equal(["pavlovserver2", "pavlovserver10"], PavlovServerDetection.FromSystemd(_units).Select(f => f.Unit));
    }

    [Fact]
    public void OtherServicesAndDeletedInstallsAreIgnored()
    {
        Server("pavlovserver");
        Server("pavlovserver1", installed: false);   // unit left behind after the install was deleted
        File.WriteAllText(Path.Combine(_units, "nginx.service"), "[Service]\nExecStart=/usr/sbin/nginx\n");

        Assert.Equal(["pavlovserver"], PavlovServerDetection.FromSystemd(_units).Select(f => f.Unit));
    }

    [Fact]
    public void AUnitNamedDifferentlyFromItsFolderKeepsItsOwnName()
    {
        var install = Server("pavlov-fallout", folder: "pavlovserver");

        var found = Assert.Single(PavlovServerDetection.FromSystemd(_units));

        Assert.Equal("pavlov-fallout", found.Unit);
        Assert.Equal(["pavlov-fallout"], PavlovServerDetection.UnitsFor([install], [found]));
    }

    [Fact]
    public void WithNothingConfiguredTheInstallsComeFromSystemd()
    {
        var one = Server("pavlovserver");
        var two = Server("pavlovserver1");

        Assert.Equal([one, two], PavlovInstalls.Discover(null, null, unitDirectory: _units, docker: NoContainers));
    }

    [Fact]
    public void AnExplicitSettingStillWins()
    {
        Server("pavlovserver");

        Assert.Equal(["/srv/a", "/srv/b"], PavlovInstalls.Discover("/srv/a,/srv/b", null, unitDirectory: _units, docker: NoContainers));
    }

    [Fact]
    public void AnInstallWithNoUnitFallsBackToItsFolderName()
    {
        Assert.Equal(["pavlovserver7"], PavlovServerDetection.UnitsFor(["/home/steam/pavlovserver7"], []));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
