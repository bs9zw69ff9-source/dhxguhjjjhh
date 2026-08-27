using PavlovBot.Core.Provisioning;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/provisionserver</c>: standing up a new Pavlov server and wiring it into the bot.
/// </summary>
/// <remarks>
/// The dangerous parts of this feature are text and alignment, so that is what is pinned here:
/// the config files must match what the game reads (golden strings), the RCON password must
/// survive being written unquoted into two different file formats, and the slot arithmetic must
/// REFUSE a layout it cannot align rather than bind a unit to the wrong server. The privileged
/// OS work is behind an interface and is not run in tests; only the exact command lines it would
/// build are asserted.
/// </remarks>
public class ProvisioningTests
{
    private static ServerProvisionSpec Spec(
        int slot = 3,
        string unit = "pavlovserver2",
        string dir = "/home/steam/pavlovserver2",
        string name = "Mojave Authority",
        int game = 7787,
        int query = 8187,
        int rcon = 9102,
        string password = "Ab1_-.!xyzZ",
        int maxPlayers = 24,
        string? gamePassword = null,
        IReadOnlyList<MapEntry>? maps = null) =>
        new()
        {
            Slot = slot,
            UnitName = unit,
            InstallDir = dir,
            ServerName = name,
            GamePort = game,
            QueryPort = query,
            RconPort = rcon,
            RconPassword = password,
            MaxPlayers = maxPlayers,
            Maps = maps ?? [new MapEntry("datacenter", "SND")],
            GamePassword = gamePassword,
        };

    // ---- config file text ----

    [Fact]
    public void RconSettingsIsTheTwoLinesTheGameReads()
    {
        Assert.Equal("Password=hunter2secret\nPort=9102\n", ProvisionText.RconSettings("hunter2secret", 9102));
    }

    [Fact]
    public void GameIniHasTheSectionNameCapacityAndOneMapPerLine()
    {
        var spec = Spec(name: "Test Server", maxPlayers: 16, maps:
            [new MapEntry("UGC1758245796", "GUN"), new MapEntry("datacenter", "SND")]);

        var expected =
            "[/Script/Pavlov.DedicatedServer]\n" +
            "bEnabled=true\n" +
            "ServerName=\"Test Server\"\n" +
            "MaxPlayers=16\n" +
            "MapRotation=(MapId=\"UGC1758245796\", GameMode=\"GUN\")\n" +
            "MapRotation=(MapId=\"datacenter\", GameMode=\"SND\")\n";

        Assert.Equal(expected, ProvisionText.GameIni(spec));
    }

    [Fact]
    public void GameIniWritesAPasswordLineOnlyWhenOneIsSet()
    {
        Assert.DoesNotContain("Password=", ProvisionText.GameIni(Spec(gamePassword: null)));
        Assert.Contains("Password=0451\n", ProvisionText.GameIni(Spec(gamePassword: "0451")));
    }

    [Fact]
    public void GameIniStripsQuotesAndNewlinesFromTheServerName()
    {
        var text = ProvisionText.GameIni(Spec(name: "Bad\"Name\nHere"));
        // The value stays on one line and cannot close early.
        Assert.Contains("ServerName=\"BadName Here\"\n", text);
    }

    [Fact]
    public void SystemdUnitRunsAsSteamFromTheInstallDirWithTheGamePort()
    {
        var expected =
            "[Unit]\n" +
            "Description=Pavlov VR Dedicated Server (pavlovserver2)\n" +
            "After=network-online.target\n" +
            "Wants=network-online.target\n" +
            "\n" +
            "[Service]\n" +
            "Type=simple\n" +
            "User=steam\n" +
            "WorkingDirectory=/home/steam/pavlovserver2\n" +
            "ExecStart=/home/steam/pavlovserver2/PavlovServer.sh -PORT=7787\n" +
            "Restart=on-failure\n" +
            "RestartSec=5\n" +
            "\n" +
            "[Install]\n" +
            "WantedBy=multi-user.target\n";

        Assert.Equal(expected, ProvisionText.SystemdUnit(Spec()));
    }

    // ---- .env override block, and its round trip through the real parser ----

    [Fact]
    public void EnvBlockAddsAnIndexedRconTripleAndRewritesThePositionalLists()
    {
        var spec = Spec(slot: 3, rcon: 9102, password: "Ab1_-.!@%^*+=z");
        var block = ProvisionText.EnvOverrideBlock(
            spec,
            ["pavlovserver", "pavlovserver1", "pavlovserver2"],
            ["/home/steam/pavlovserver", "/home/steam/pavlovserver1", "/home/steam/pavlovserver2"],
            null,
            new DateOnly(2026, 8, 27));

        Assert.Contains("RCON_HOST_3=127.0.0.1\n", block);
        Assert.Contains("RCON_PORT_3=9102\n", block);
        Assert.Contains("RCON_PASSWORD_3=Ab1_-.!@%^*+=z\n", block);
        Assert.Contains("PAVLOV_UNITS=pavlovserver,pavlovserver1,pavlovserver2\n", block);
        Assert.Contains("PAVLOV_BASES=/home/steam/pavlovserver,/home/steam/pavlovserver1,/home/steam/pavlovserver2\n", block);
    }

    [Fact]
    public void AppendedBlockParsesBackToTheNewValuesUnderLastKeyWins()
    {
        // An existing .env with a one-server layout; the block appends a second, and the parser
        // the bot actually uses must read the LATER values, password intact and unquoted.
        const string existing =
            "PAVLOV_UNITS=pavlovserver\n" +
            "PAVLOV_BASES=/home/steam/pavlovserver\n";

        var spec = Spec(slot: 2, rcon: 9101, password: "S3cret_-.!@%^*+=");
        var block = ProvisionText.EnvOverrideBlock(
            spec,
            ["pavlovserver", "pavlovserver1"],
            ["/home/steam/pavlovserver", "/home/steam/pavlovserver1"],
            null,
            new DateOnly(2026, 8, 27));

        var parsed = DotEnvConfigurationProvider.Parse(existing + block);

        Assert.Equal("S3cret_-.!@%^*+=", parsed["RCON_PASSWORD_2"]);
        Assert.Equal("9101", parsed["RCON_PORT_2"]);
        Assert.Equal("pavlovserver,pavlovserver1", parsed["PAVLOV_UNITS"]);
        Assert.Equal("/home/steam/pavlovserver,/home/steam/pavlovserver1", parsed["PAVLOV_BASES"]);
    }

    // ---- password charset ----

    [Theory]
    [InlineData("Abcdef12", true)]              // exactly 8, mixed
    [InlineData("A1_-.!@%^*+=zZ", true)]        // every allowed symbol
    [InlineData("short1", false)]               // under 8
    [InlineData("has space1", false)]           // space
    [InlineData("has#hash1", false)]            // '#' would start a comment in .env
    [InlineData("has\"quote1", false)]          // quote
    [InlineData("has`tick1", false)]            // backtick
    [InlineData("has$dollar1", false)]          // '$'
    public void PasswordCharsetIsWhatSurvivesBothFileFormats(string password, bool valid)
    {
        Assert.Equal(valid, ProvisionValidation.IsValidPassword(password));
    }

    // ---- validation ----

    [Fact]
    public void AGoodSpecHasNoProblems()
    {
        Assert.Empty(ProvisionValidation.Check(Spec(), [9100, 9101], ["pavlovserver", "pavlovserver1"], ["/a", "/b"]));
    }

    [Fact]
    public void ThreePortsMustAllDiffer()
    {
        var problems = ProvisionValidation.Check(Spec(game: 7777, query: 7777, rcon: 9102), [], [], []);
        Assert.Contains(problems, p => p.Contains("different from each other", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewPortCollidingWithAnExistingRconPortIsRejected()
    {
        var problems = ProvisionValidation.Check(Spec(rcon: 9100), [9100], [], []);
        Assert.Contains(problems, p => p.Contains("already an RCON port", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfRangePortsAreRejected()
    {
        var problems = ProvisionValidation.Check(Spec(game: 0, query: 70000, rcon: -1), [], [], []);
        Assert.Equal(3, problems.Count(p => p.Contains("valid port", StringComparison.Ordinal)));
    }

    [Fact]
    public void AUnitOrDirAlreadyInUseIsRejected()
    {
        var problems = ProvisionValidation.Check(
            Spec(unit: "pavlovserver1", dir: "/home/steam/pavlovserver1"),
            [], ["pavlovserver1"], ["/home/steam/pavlovserver1"]);

        Assert.Contains(problems, p => p.Contains("already configured", StringComparison.Ordinal) && p.Contains("unit", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("already configured", StringComparison.Ordinal) && p.Contains("directory", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/home/steam/pavlovserver2", true)]
    [InlineData("relative/path", false)]          // not absolute
    [InlineData("-rf", false)]                     // would be read as an option by mkdir/chown
    [InlineData("/home/steam/../../etc/x", false)] // traversal
    [InlineData("/home/steam/bad name", false)]    // space
    public void InstallDirMustBeAnAbsoluteTraversalFreePath(string dir, bool ok)
    {
        var problems = ProvisionValidation.Check(Spec(dir: dir), [], [], []);
        Assert.Equal(ok, !problems.Any(p => p.Contains("install directory", StringComparison.Ordinal)));
    }

    [Fact]
    public void BadMapTokensAndCapacityAndEmptyNameAreRejected()
    {
        var spec = Spec(name: "   ", maxPlayers: 500, maps: [new MapEntry("has space", "GUN\"")]);
        var problems = ProvisionValidation.Check(spec, [], [], []);

        Assert.Contains(problems, p => p.Contains("Max players", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("empty after cleaning", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Map id", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Game mode", StringComparison.Ordinal));
    }

    // ---- slot allocation and alignment ----

    [Fact]
    public void AContiguousLayoutTakesTheNextSlotAndAppendsToBothLists()
    {
        var (plan, problems) = ServerSlotPlanner.Plan(
            new ServerLayout([1, 2], ["pavlovserver", "pavlovserver1"], ["/a", "/b"]),
            "pavlovserver2", "/c");

        Assert.Empty(problems);
        Assert.NotNull(plan);
        Assert.Equal(3, plan!.Slot);
        Assert.Equal(["pavlovserver", "pavlovserver1", "pavlovserver2"], plan.FinalUnits);
        Assert.Equal(["/a", "/b", "/c"], plan.FinalBases);
    }

    [Fact]
    public void AGappedRconLayoutIsRefused()
    {
        var (plan, problems) = ServerSlotPlanner.Plan(
            new ServerLayout([1, 3], ["a", "b"], ["/a", "/b"]), "u", "/d");

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Contains("gapless", StringComparison.Ordinal));
    }

    [Fact]
    public void ListsThatDoNotMatchTheRconCountAreRefused()
    {
        // Two RCON servers but only one unit listed: a positional append would misalign.
        var (plan, problems) = ServerSlotPlanner.Plan(
            new ServerLayout([1, 2], ["only-one"], ["/a", "/b"]), "u", "/d");

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Contains("PAVLOV_UNITS must list exactly 2", StringComparison.Ordinal));
    }

    [Fact]
    public void NoServersYetIsRefusedBecauseServerOneMustExistFirst()
    {
        var (plan, problems) = ServerSlotPlanner.Plan(new ServerLayout([], [], []), "u", "/d");

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Contains("No RCON servers are configured", StringComparison.Ordinal));
    }

    [Fact]
    public void RunningOutOfSlotsIsRefused()
    {
        var indices = Enumerable.Range(1, ProvisionValidation.MaxServers).ToList();
        var names = indices.Select(i => $"u{i}").ToList();
        var bases = indices.Select(i => $"/b{i}").ToList();

        var (plan, problems) = ServerSlotPlanner.Plan(new ServerLayout(indices, names, bases), "u10", "/b10");

        Assert.Null(plan);
        Assert.Contains(problems, p => p.Contains("no room", StringComparison.Ordinal));
    }

    // ---- the privileged command lines (pinned, never run here) ----

    [Fact]
    public void SteamCmdArgvUsesThePavlovAppIdInOrder()
    {
        Assert.Equal(
            ["+force_install_dir", "/home/steam/pavlovserver2", "+login", "anonymous",
             "+app_update", "622970", "-beta", "default", "+quit"],
            ServerProvisioner.SteamCmdArgv("/home/steam/pavlovserver2"));
    }

    [Fact]
    public void EnableArgvAndUfwRuleAreExact()
    {
        Assert.Equal(["enable", "--now", "pavlovserver2"], ServerProvisioner.EnableArgv("pavlovserver2"));
        Assert.Equal("7777/udp", ServerProvisioner.UfwRule(7777, "udp"));
        Assert.Equal("9100/tcp", ServerProvisioner.UfwRule(9100, "tcp"));
    }

    [Fact]
    public void UseraddArgvGivesTheSteamAccountARealHomeAndShell()
    {
        Assert.Equal(["-m", "-s", "/bin/bash", "steam"], ServerProvisioner.UseraddArgv("steam"));
    }

    [Fact]
    public void ChpasswdStdinIsTheColonFormatWithATrailingNewline()
    {
        // The exact wire format chpasswd documents: "user:password\n". Pinned because getting
        // this wrong either fails silently or, with no newline, leaves chpasswd waiting on more
        // input that never comes.
        Assert.Equal("steam:Ab1_-.!xyzZ\n", ServerProvisioner.ChpasswdStdin("steam", "Ab1_-.!xyzZ"));
    }

    // ---- command helpers ----

    [Theory]
    [InlineData(1, "")]
    [InlineData(2, "1")]
    [InlineData(3, "2")]
    public void UnitNumberingIsOffByOneLikeTheBox(int slot, string suffix)
    {
        Assert.Equal($"pavlovserver{suffix}", $"pavlovserver{ProvisionServerCommand.Off(slot)}");
    }

    [Fact]
    public void ParseMapsDefaultsAndSplitsModes()
    {
        Assert.Equal([new MapEntry("datacenter", "SND")], ProvisionServerCommand.ParseMaps(null));
        Assert.Equal([new MapEntry("UGC123", "GUN")], ProvisionServerCommand.ParseMaps("UGC123:GUN"));
        Assert.Equal(
            [new MapEntry("UGC123", "GUN"), new MapEntry("datacenter", "SND")],
            ProvisionServerCommand.ParseMaps("UGC123:GUN, datacenter:SND"));
        // A bare map with no mode defaults to SND.
        Assert.Equal([new MapEntry("sand", "SND")], ProvisionServerCommand.ParseMaps("sand"));
    }

    // ---- atomic write ----

    [Fact]
    public async Task AtomicWriteReplacesAnExistingFileWhole()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pavlov-atomic-{Guid.NewGuid():N}");
        try
        {
            await AtomicFile.WriteAsync(path, "first\n");
            await AtomicFile.WriteAsync(path, "second\n");
            Assert.Equal("second\n", await File.ReadAllTextAsync(path));
            Assert.False(File.Exists($"{path}.bot.tmp"), "the temp file should have been renamed away");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- the seam ----

    [Fact]
    public async Task TheProvisionerSeamReportsProgressAndReturnsAnOutcome()
    {
        var stub = new StubServerProvisioner();
        var request = new ProvisionRequest(Spec(), ["a", "b", "c"], ["/a", "/b", "/c"], null, "/tmp/.env", "SteamAcctPass1");

        var seen = 0;
        var outcome = await stub.ProvisionAsync(request, _ => { seen++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Same(request, stub.Received);
        Assert.True(seen > 0);
        Assert.True(outcome.Ok);
        Assert.True(outcome.RestartQueued);
    }
}
