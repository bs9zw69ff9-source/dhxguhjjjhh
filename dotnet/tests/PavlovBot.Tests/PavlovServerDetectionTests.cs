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

        Assert.Equal([one, two], PavlovInstalls.Discover(null, null, unitDirectory: _units));
    }

    [Fact]
    public void AnExplicitSettingStillWins()
    {
        Server("pavlovserver");

        Assert.Equal(["/srv/a", "/srv/b"], PavlovInstalls.Discover("/srv/a,/srv/b", null, unitDirectory: _units));
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
