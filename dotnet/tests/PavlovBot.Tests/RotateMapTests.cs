using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/rotatemap</c>: warn over RCON, then restart the units through systemd.
/// </summary>
/// <remarks>
/// This is the only command that runs a PRIVILEGED SYSTEM COMMAND, so the tests that matter
/// most are the ones about what can reach its argument list. Everywhere else in the bot a
/// bad argument is a wrong ban; here it would be arbitrary execution as root.
/// </remarks>
public class RotateMapTests
{
    private static ServiceControl Control(params string[] units) =>
        new(units.Length > 0 ? units : ServiceControl.DefaultUnits, useSudo: false,
            NullLogger<ServiceControl>.Instance);

    // ---- what may become a unit name ----

    [Fact]
    public void TheServerNumberToUnitMappingIsTheOneOnTheBox()
    {
        /* STATED EXACTLY, because it is off by one and looks like a bug every time somebody
           reads it: server 1 is `pavlovserver` with no suffix, so server N is
           `pavlovserver{N-1}`. Getting this wrong restarts the wrong server, which is the
           kind of mistake that is only noticed by the players on it. */
        var control = Control();

        Assert.Equal("pavlovserver", control.UnitFor(1));
        Assert.Equal("pavlovserver1", control.UnitFor(2));
        Assert.Equal("pavlovserver2", control.UnitFor(3));

        // And the same order the feeds and the player-count channels number servers in.
        Assert.Equal(["pavlovserver", "pavlovserver1", "pavlovserver2"], ServiceControl.DefaultUnits);
    }

    // ---- verbs ----

    [Theory]
    [InlineData(UnitAction.Start, "start")]
    [InlineData(UnitAction.Stop, "stop")]
    [InlineData(UnitAction.Restart, "restart")]
    public void EachActionMapsToItsSystemctlVerb(UnitAction action, string expected)
    {
        // These three strings are the second word of a privileged command line, and they
        // come from an enum rather than from anything a caller can influence.
        Assert.Equal(expected, ServiceControl.Verb(action));
    }

    [Fact]
    public async Task ARefusedUnitNameIsRefusedForEveryAction()
    {
        // The guard belongs to the runner, so no verb can route around it.
        foreach (var action in Enum.GetValues<UnitAction>())
        {
            var result = await Control().RunAsync("--force", action);

            Assert.False(result.Ok);
            Assert.Contains("refused before running anything", result.Detail, StringComparison.Ordinal);
        }
    }

    // ---- warnings ----

    [Fact]
    public void StoppingAndRestartingWarnPlayersDifferently()
    {
        /* A player told "restarting" waits; a player told "shutting down" goes elsewhere.
           Telling them the wrong one is a small lie with a real cost either way. */
        Assert.Equal("Server shutting down...", ServerSwitchCommand.WarningFor(UnitAction.Stop));
        Assert.Equal("Server restarting...", ServerSwitchCommand.WarningFor(UnitAction.Restart));
    }

    [Fact]
    public void StartingWarnsNobody()
    {
        /* THE POINT: a stopped server has nobody on it to warn, and no RCON session to
           reach them through - the process is not running, so the broadcast could not be
           delivered even if somebody were there to receive it.

           Null rather than an empty string, because this is the SINGLE SOURCE for the whole
           decision: the branch that sends the broadcast, the grace period, and the line in
           the reply about whether players were warned all key off it. One rule, one place -
           three copies of it is how a later edit makes them disagree and a starting server
           picks up a five-second pause for an audience of nobody. */
        Assert.Null(ServerSwitchCommand.WarningFor(UnitAction.Start));
    }

    [Fact]
    public void EveryActionThatDropsPlayersWarnsThem()
    {
        // Stated as a rule over the enum rather than case by case, so a fourth action added
        // later has to make a deliberate choice instead of inheriting a fallback.
        foreach (var action in Enum.GetValues<UnitAction>())
        {
            var warning = ServerSwitchCommand.WarningFor(action);

            if (action is UnitAction.Start) Assert.Null(warning);
            else Assert.False(string.IsNullOrWhiteSpace(warning), $"{action} drops players and must warn them");
        }
    }

    [Fact]
    public void EveryWarningSurvivesSanitisingIntact()
    {
        // They go out as `Notify <text>` on a line-oriented protocol. If sanitising altered
        // one, players would see something other than what the command promises.
        foreach (var action in Enum.GetValues<UnitAction>())
        {
            if (ServerSwitchCommand.WarningFor(action) is not { } warning) continue;
            Assert.Equal(warning, PavlovBot.Core.Text.Sanitize.Message(warning));
        }
    }

    // ---- elevation ----

    [Fact]
    public void SudoIsUsedWhenTheBotIsNotRoot()
    {
        /* systemctl HAS to run as root. Left undeclared, the bot works out which of the two
           routes it needs rather than making the operator assert one - and a wrong assertion
           here is a command that refuses everything for a reason nobody can see. */
        var detected = new ServiceControl(ServiceControl.DefaultUnits, useSudo: null,
            NullLogger<ServiceControl>.Instance);

        Assert.Equal(Environment.IsPrivilegedProcess, !detected.Elevation.Contains("sudo", StringComparison.Ordinal));
    }

    [Fact]
    public void ForcingSudoOffWhileNotRootSaysThatItWillFail()
    {
        // An override that cannot work should say so where somebody will read it, rather
        // than presenting as a broken command later.
        if (Environment.IsPrivilegedProcess) return;   // the assertion is meaningless as root

        var forced = new ServiceControl(ServiceControl.DefaultUnits, useSudo: false,
            NullLogger<ServiceControl>.Instance);

        Assert.Contains("NOT root", forced.Elevation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pavlovserver")]
    [InlineData("pavlovserver1")]
    [InlineData("pavlov-ttt")]
    [InlineData("pavlov_server.service")]
    [InlineData("pavlov@1.service")]
    public void RealUnitNamesAreAccepted(string name) =>
        Assert.True(ServiceControl.IsPlausibleUnitName(name));

    [Theory]
    [InlineData("pavlovserver; rm -rf /")]
    [InlineData("pavlovserver && reboot")]
    [InlineData("pavlovserver\nreboot")]
    [InlineData("$(reboot)")]
    [InlineData("`reboot`")]
    [InlineData("pavlov server")]          // a space is two arguments
    [InlineData("--force")]                 // an option, not a unit
    [InlineData("-pavlovserver")]           // ditto, and it looks much more innocent
    [InlineData("@@@")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void AnythingThatIsNotAUnitNameIsRefused(string name)
    {
        /* Belt to the argv brace. There is no shell here - UseShellExecute is false and the
           arguments go in as an array - so none of these could execute anything even if they
           got through. They still do not get through, because the cost of being wrong about
           that reasoning exactly once is root. */
        Assert.False(ServiceControl.IsPlausibleUnitName(name));
    }

    [Fact]
    public async Task ARefusedUnitNameNeverStartsAProcess()
    {
        // The check has to be in the runner, not only in the parser: a caller reaching
        // RestartAsync directly must not be able to skip it.
        var result = await Control().RestartAsync("pavlovserver; reboot");

        Assert.False(result.Ok);
        Assert.Contains("refused before running anything", result.Detail, StringComparison.Ordinal);
    }

    // ---- configuration ----

    [Fact]
    public void UnsetUnitsFallBackToTheDefaults()
    {
        Assert.Equal(ServiceControl.DefaultUnits, ServiceControl.ParseUnits(null));
        Assert.Equal(ServiceControl.DefaultUnits, ServiceControl.ParseUnits("   "));
    }

    [Fact]
    public void ConfiguredUnitsAreUsedInOrder()
    {
        // Order IS the server numbering, so it is preserved rather than sorted.
        Assert.Equal(["pavlov-c", "pavlov-a", "pavlov-b"],
            ServiceControl.ParseUnits("pavlov-c, pavlov-a, pavlov-b"));
    }

    [Fact]
    public void AGarbledUnitInTheListIsDroppedRatherThanTakingTheRestWithIt()
    {
        Assert.Equal(["pavlovserver", "pavlovserver2"],
            ServiceControl.ParseUnits("pavlovserver, bad name here, pavlovserver2"));
    }

    [Fact]
    public void AListWithNothingUsableInItFallsBackRatherThanDisablingTheCommand()
    {
        /* An empty unit list would make /rotatemap a command that reports success for
           restarting nothing, which is the worst of the available outcomes. */
        Assert.Equal(ServiceControl.DefaultUnits, ServiceControl.ParseUnits("!!!, @@@"));
    }

    [Fact]
    public void DuplicatesAreCollapsed()
    {
        // Otherwise a copy-pasted .env restarts one server twice and reports it as two.
        Assert.Equal(["pavlovserver"], ServiceControl.ParseUnits("pavlovserver, pavlovserver"));
    }

    // ---- server number to unit ----

    [Fact]
    public void ServerNumbersAreOneBasedAndMapToTheConfiguredOrder()
    {
        var control = Control("unit-a", "unit-b", "unit-c");

        Assert.Equal("unit-a", control.UnitFor(1));
        Assert.Equal("unit-b", control.UnitFor(2));
        Assert.Equal("unit-c", control.UnitFor(3));
    }

    [Fact]
    public void AServerNumberOutsideTheConfiguredRangeResolvesToNothing()
    {
        // The command turns this into "no such server" rather than restarting something else.
        var control = Control("unit-a");

        Assert.Null(control.UnitFor(0));
        Assert.Null(control.UnitFor(2));
        Assert.Null(control.UnitFor(-1));
        Assert.Null(control.UnitFor(int.MaxValue));
    }

    // ---- the warning ----

    [Fact]
    public void TheBroadcastIsTheExactLineAsked()
    {
        Assert.Equal("All Server Rotating...", RotateMapCommand.Warning);
    }

    [Fact]
    public void TheBroadcastSurvivesSanitisingIntact()
    {
        /* It goes out as `Notify <text>` on a line-oriented protocol, so it passes through
           Sanitize.Message like every other string that reaches RCON. If sanitising altered
           it, players would see something other than what this command promises. */
        Assert.Equal(RotateMapCommand.Warning,
            PavlovBot.Core.Text.Sanitize.Message(RotateMapCommand.Warning));
    }

    // ---- the failure that will actually happen ----

    [Fact]
    public void APermissionFailureExplainsHowToGrantIt()
    {
        /* THE EXPECTED FAILURE, not an exotic one: the bot runs as an unprivileged user and
           systemd is right to refuse it. Without this the refusal presents as the command
           being broken, which is an evening. */
        var advice = Control().Advice([
            new UnitResult(false, "pavlovserver", "Failed to restart pavlovserver.service: Access denied"),
        ]);

        Assert.NotNull(advice);
        Assert.Contains("visudo", advice, StringComparison.Ordinal);

        // Every verb, for every unit - a sudoers line that only permits restart makes
        // /serverswitch stop fail with the same error the advice was meant to fix.
        foreach (var unit in ServiceControl.DefaultUnits)
            foreach (var verb in new[] { "start", "stop", "restart" })
                Assert.Contains($"/bin/systemctl {verb} {unit}", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void PolkitsWordingIsRecognisedToo()
    {
        // systemd says this instead of "access denied" when polkit is in the path.
        var advice = Control().Advice([
            new UnitResult(false, "pavlovserver", "Interactive authentication required."),
        ]);

        Assert.Contains("visudo", advice!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownUnitPointsAtTheConfigurationRatherThanPermissions()
    {
        // Two different fixes, and they are indistinguishable from "it didn't work".
        var advice = Control().Advice([
            new UnitResult(false, "pavlovserver", "Unit pavlovserver.service not found."),
        ]);

        Assert.NotNull(advice);
        Assert.Contains("PAVLOV_UNITS", advice, StringComparison.Ordinal);
        Assert.DoesNotContain("visudo", advice, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessOffersNoAdvice()
    {
        Assert.Null(Control().Advice([new UnitResult(true, "pavlovserver", "restarted")]));
    }

    [Fact]
    public void AnUnrecognisedFailureSaysNothingRatherThanGuessing()
    {
        // Confident wrong advice is worse than none - it sends somebody to edit sudoers for
        // a problem that has nothing to do with permissions.
        Assert.Null(Control().Advice([
            new UnitResult(false, "pavlovserver", "Job for pavlovserver.service failed"),
        ]));
    }
}

/// <summary>
/// Warning players before a server goes away, and giving them time to read it.
/// </summary>
public class PlayerNoticeTests
{
    [Fact]
    public void TheGraceIsFiveSecondsAndRunsBeforeTheSystemCommand()
    {
        /* The whole reason the pause exists: the broadcast has to reach the client and
           render before the process it warns about disappears. Sent and acted on in the same
           instant, the player sees nothing and is simply dropped. */
        Assert.Equal(TimeSpan.FromSeconds(5), PlayerNotice.Grace);
    }

    [Fact]
    public async Task TheGraceIsActuallyWaited()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        await PlayerNotice.WaitAsync(CancellationToken.None);

        // Timer slack cuts both ways; the assertion is that it is a real wait, not the exact
        // millisecond count.
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(4.5), $"waited only {started.Elapsed}");
    }

    [Fact]
    public void ServerNumbersMapToRconSlotsByNameRatherThanByPosition()
    {
        /* RCON SLOTS ARE ALLOWED TO HAVE GAPS - RCON_HOST_1 and RCON_HOST_3 with no _2 is a
           supported configuration. Indexing into the configured list makes "server 2" mean
           the second CONFIGURED server, so on a box with a gap the bot warns one server and
           stops another. That is precisely the mistake the numbering exists to prevent, and
           only the players on the wrong server would ever find out. */
        Assert.Equal("server1", PlayerNotice.RconNameFor(1));
        Assert.Equal("server2", PlayerNotice.RconNameFor(2));
        Assert.Equal("server3", PlayerNotice.RconNameFor(3));
    }
}
