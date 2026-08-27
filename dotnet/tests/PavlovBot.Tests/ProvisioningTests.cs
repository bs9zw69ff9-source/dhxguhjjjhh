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

    [Theory]
    [InlineData(1, true)]
    [InlineData(24, true)]    // Shack's hard cap, and the default
    [InlineData(25, false)]   // one past it
    [InlineData(50, false)]   // the PC build's limit, which is not this one
    [InlineData(0, false)]
    public void CapacityStopsAtShacksHardLimitRatherThanThePcOne(int maxPlayers, bool allowed)
    {
        // 24 is a limit the game enforces, not a preference - a higher number is not a bigger
        // server, it is a setting Shack will not honour.
        var problems = ProvisionValidation.Check(Spec(maxPlayers: maxPlayers), [], [], []);

        Assert.Equal(allowed, !problems.Any(p => p.Contains("Max players", StringComparison.Ordinal)));
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
        /* The wiki's line, argument for argument - and "-beta shack", NOT "-beta default". Those
           are two different builds: default is PC, shack is the Quest/standalone one this bot is
           built around throughout. Installing the wrong branch yields a server the rest of the bot
           can never see, and nothing about that failure would name the branch as the reason. */
        Assert.Equal(
            ["+force_install_dir", "/home/steam/pavlovserver2", "+login", "anonymous",
             "+app_update", "622970", "-beta", "shack", "+exit"],
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

    // ---- per-slot port defaults ----

    [Theory]
    [InlineData(1, 7777, 8177, 9100)]
    [InlineData(2, 7778, 8178, 9200)]
    [InlineData(3, 7779, 8179, 9300)]
    [InlineData(9, 7785, 8185, 9900)]
    public void DefaultPortsStepPerSlotSoASecondServerIsNotACopyOfTheFirst(
        int slot, int game, int query, int rcon)
    {
        /* THE REGRESSION THIS EXISTS FOR. These were three fixed constants, so provisioning
           server 2 proposed server 1's exact ports: the RCON clash was caught and the command
           refused outright, and working around that by naming an RCON port left the game and
           query ports colliding silently - which does not fail cleanly, the second server just
           loses the bind and never appears. */
        Assert.Equal(game, ServerPortDefaults.GamePort(slot));
        Assert.Equal(query, ServerPortDefaults.QueryPortFor(ServerPortDefaults.GamePort(slot)));
        Assert.Equal(rcon, ServerPortDefaults.RconPort(slot));
    }

    [Fact]
    public void TheQueryPortFollowsTheGamePortEvenWhenItIsChosenByHand()
    {
        // Derived from the game port, not the slot, so a hand-picked game port keeps Pavlov's
        // own +400 pairing instead of drifting away from it.
        Assert.Equal(8200, ServerPortDefaults.QueryPortFor(7800));
        Assert.Equal(ServerPortDefaults.QueryPortOffset, 8177 - 7777);
    }

    [Fact]
    public void EveryDefaultPortStaysInsideTheValidRange()
    {
        // The slot ceiling is 9; nothing in that range may produce a port validation rejects.
        for (var slot = 1; slot <= ProvisionValidation.MaxServers; slot++)
        {
            var spec = Spec(
                slot: slot,
                game: ServerPortDefaults.GamePort(slot),
                query: ServerPortDefaults.QueryPortFor(ServerPortDefaults.GamePort(slot)),
                rcon: ServerPortDefaults.RconPort(slot));

            Assert.Empty(ProvisionValidation.Check(spec, [], [], []));
        }
    }

    // ---- slot to unit name to install dir to unit file ----

    [Theory]
    [InlineData(1, "pavlovserver", "/home/steam/pavlovserver", 7777)]
    [InlineData(2, "pavlovserver1", "/home/steam/pavlovserver1", 7778)]
    [InlineData(3, "pavlovserver2", "/home/steam/pavlovserver2", 7779)]
    public void TheUnitFileGetsThisServersOwnDirectoryAndPort(
        int slot, string expectedUnit, string expectedDir, int expectedGamePort)
    {
        /* THE WHOLE CHAIN, pinned end to end: the slot picks the unit name (off by one - server 1
           is "pavlovserver" with no suffix), the unit name picks the install directory beside its
           siblings, and the unit file has to carry THAT directory and THAT server's game port.
           A unit pointing at another server's tree runs the wrong install; one carrying another
           server's port fights it for the socket. */
        var unit = $"pavlovserver{ProvisionServerCommand.Off(slot)}";
        Assert.Equal(expectedUnit, unit);

        var installDir = Path.Combine("/home/steam", unit);
        Assert.Equal(expectedDir, installDir);

        var text = ProvisionText.SystemdUnit(Spec(
            slot: slot, unit: unit, dir: installDir, game: ServerPortDefaults.GamePort(slot)));

        Assert.Contains($"WorkingDirectory={expectedDir}\n", text, StringComparison.Ordinal);
        Assert.Contains($"ExecStart={expectedDir}/PavlovServer.sh -PORT={expectedGamePort}\n", text, StringComparison.Ordinal);
        Assert.Contains($"Description=Pavlov VR Dedicated Server ({expectedUnit})\n", text, StringComparison.Ordinal);
    }

    // ---- finding SteamCMD ----

    [Fact]
    public void TheTarballInstallLocationIsAmongTheCandidates()
    {
        /* THE REGRESSION. This used to invoke "steamcmd" and trust PATH, and a provision died on
           "sudo: steamcmd: command not found" AFTER creating the steam account and granting it
           sudo. The documented Linux install is a tarball unpacked into ~/Steam, which puts
           nothing on PATH - so the lookup found nothing even where SteamCMD was present. */
        var candidates = ServerProvisioner.SteamCmdCandidates("/home/steam");

        Assert.Contains("/home/steam/Steam/steamcmd.sh", candidates);

        // A distro package wins over the tarball when the box has one.
        Assert.True(
            candidates.ToList().IndexOf("/usr/games/steamcmd") < candidates.ToList().IndexOf("/home/steam/Steam/steamcmd.sh"),
            "a packaged steamcmd should be preferred over an unpacked tarball");

        // Every candidate is absolute - a bare name would be a PATH lookup again.
        Assert.All(candidates, c => Assert.StartsWith("/", c, StringComparison.Ordinal));
    }

    [Fact]
    public void ATrailingSlashOnTheHomeDoesNotDoubleUp()
    {
        Assert.Contains("/home/steam/Steam/steamcmd.sh", ServerProvisioner.SteamCmdCandidates("/home/steam/"));
    }

    [Theory]
    [InlineData("steam:x:1001:1001::/home/steam:/bin/bash\n", "/home/steam")]
    [InlineData("steam:x:1001:1001::/srv/steam:/bin/bash", "/srv/steam")]
    [InlineData("", null)]                                   // getent said nothing
    [InlineData("garbage", null)]                            // not a passwd line
    [InlineData("steam:x:1:1::relative:/bin/sh", null)]      // a home that is not absolute
    public void TheSteamHomeIsParsedOutOfGetentRatherThanAssumed(string output, string? expected)
    {
        Assert.Equal(expected, ServerProvisioner.HomeFromGetent(output));
    }

    [Fact]
    public void TheBootstrapDownloadsToAFileRatherThanThroughAShellPipe()
    {
        // The documented one-liner pipes curl into tar, which needs a shell. Its curl flags and
        // URL are kept exactly; only the pipe becomes a download plus a separate tar.
        var download = ServerProvisioner.SteamCmdDownloadArgv("/home/steam/Steam/steamcmd_linux.tar.gz");

        Assert.Contains("https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz", download);
        Assert.Contains("-sqL", download);     // the documented flags, verbatim
        Assert.Contains("-o", download);
        Assert.Contains("/home/steam/Steam/steamcmd_linux.tar.gz", download);
        Assert.DoesNotContain(download, a => a.Contains('|', StringComparison.Ordinal));

        Assert.Equal(
            ["-xzf", "/home/steam/Steam/steamcmd_linux.tar.gz", "-C", "/home/steam/Steam"],
            ServerProvisioner.SteamCmdExtractArgv("/home/steam/Steam/steamcmd_linux.tar.gz", "/home/steam/Steam"));
    }

    // ---- reporting a failure ----

    [Fact]
    public void FailureOutputKeepsTheENDWhereTheReasonIs()
    {
        /* THE REGRESSION. This kept the FIRST 300 characters, so a SteamCMD failure was reported
           as three lines of startup banner and a progress bar cut off mid-word, with the actual
           error trimmed away entirely - the operator was shown everything except the part that
           mattered. Tools put the reason last. */
        var output = new string('x', 900) + "\nERROR! the part that actually matters";

        var excerpt = ServerProvisioner.Tail(output);

        Assert.Contains("ERROR! the part that actually matters", excerpt, StringComparison.Ordinal);
        Assert.StartsWith("…", excerpt, StringComparison.Ordinal);
        Assert.True(excerpt.Length <= 601, "the excerpt has to stay well inside Discord's embed limits");
    }

    [Fact]
    public void ShortOutputIsPassedThroughWhole()
    {
        Assert.Equal("useradd: user 'steam' already exists", ServerProvisioner.Tail("useradd: user 'steam' already exists"));
        Assert.Equal("", ServerProvisioner.Tail(null));
        Assert.Equal("trimmed", ServerProvisioner.Tail("  trimmed\n"));
    }

    // ---- steam's sudo grant ----

    [Fact]
    public void SudoersFullAccessLineIsUnrestrictedAndPasswordless()
    {
        Assert.Equal("steam ALL=(ALL) NOPASSWD: ALL\n", ServerProvisioner.SudoersFullAccessLine("steam"));
    }

    // ---- the checklist itself: real step list, real cursor ----

    [Fact]
    public void TheRealStepListHasSteamAccountAndSudoRightAfterPreflight()
    {
        // Pinned in order: this is the exact list ProvisionAsync drives, and its position matters
        // - the sudo grant has to land after the account exists and before anything that could
        // need it (SteamCMD, running as steam).
        Assert.Equal(
        [
            "Pre-flight checks",
            "Steam user account",
            "Steam sudo access (full, NOPASSWD)",
            "SteamCMD (locate or bootstrap)",
            "SteamCMD install",
            "Server config (RconSettings.txt, Game.ini)",
            "systemd unit (write, daemon-reload, enable --now)",
            "Firewall (ufw)",
            "RCON reachability",
            "Wire into the bot (.env)",
            "Restart the bot",
        ], ServerProvisioner.StepNames);
    }

    private static ServerProvisioner.Run NewRun(out List<IReadOnlyList<ProvisionStep>> snapshots)
    {
        var captured = new List<IReadOnlyList<ProvisionStep>>();
        snapshots = captured;
        return new ServerProvisioner.Run(["one", "two", "three"], steps =>
        {
            captured.Add(steps);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task StartAdvancesToTheNextStepWithoutAnIndex()
    {
        // The whole point of the cursor: nothing here says which step it is. Getting the ORDER
        // of these calls wrong is still a real mistake, but there is no number left to go stale
        // the way RestartAsync's did when a step was inserted before it.
        var run = NewRun(out var snapshots);

        await run.Start("first");
        await run.Ok("first done");
        await run.Start("second");
        await run.Fail("second failed");

        var last = snapshots[^1];
        Assert.Equal(ProvisionStatus.Ok, last[0].Status);
        Assert.Equal("first done", last[0].Detail);
        Assert.Equal(ProvisionStatus.Failed, last[1].Status);
        Assert.Equal("second failed", last[1].Detail);
        Assert.Equal(ProvisionStatus.Pending, last[2].Status);
    }

    [Fact]
    public async Task AbortSkipsEverythingAfterTheCurrentStepAndLeavesTheFailureAlone()
    {
        var run = NewRun(out _);

        await run.Start("first");
        await run.Ok("fine");
        await run.Start("second");
        await run.Fail("boom");
        var outcome = await run.Abort();

        Assert.Equal(ProvisionStatus.Ok, outcome.Steps[0].Status);
        Assert.Equal(ProvisionStatus.Failed, outcome.Steps[1].Status);       // NOT overwritten to Skipped
        Assert.Equal(ProvisionStatus.Skipped, outcome.Steps[2].Status);
        Assert.False(outcome.Ok);
        Assert.False(outcome.RestartQueued);
    }

    [Fact]
    public async Task EveryChangeEmitsASnapshotOfAllSteps()
    {
        var run = NewRun(out var snapshots);

        await run.Start("go");

        Assert.Single(snapshots);
        Assert.Equal(3, snapshots[0].Count);
        Assert.Equal(ProvisionStatus.Running, snapshots[0][0].Status);
    }
}
