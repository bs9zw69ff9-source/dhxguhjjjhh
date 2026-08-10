using PavlovBot.Core.Events;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Turning health readings into incidents, and mostly into nothing.
/// </summary>
/// <remarks>
/// AN ALERT THAT FIRES ON NORMAL OPERATION IS WORSE THAN NO ALERT, because the response to it
/// is learned within a week and then the real one is ignored too. Most of these are about the
/// detector staying quiet: an empty server at 4am, a squad leaving together, a busy game
/// thread. The one thing worth waking somebody for is a POPULATED server emptying while the
/// process stays up.
/// </remarks>
public class IncidentDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 3, 0, 0, TimeSpan.Zero);

    private static ServerHealth Health(
        bool reachable = true,
        double latencySeconds = 0.05,
        int players = 10,
        int previousPlayers = 10,
        bool unitFailed = false,
        int restarts = 0) =>
        new("server1", reachable, TimeSpan.FromSeconds(latencySeconds), players, previousPlayers, unitFailed, restarts);

    [Fact]
    public void AHealthyServerRaisesNothing()
    {
        Assert.Null(IncidentDetector.Detect(Health(), Now));
    }

    /// <summary>
    /// AN EMPTY SERVER IS NOT AN INCIDENT. The most important false positive to avoid.
    /// </summary>
    /// <remarks>
    /// Servers empty out overnight, every night. A detector that alerts on zero players would
    /// fire on every server in the fleet at 4am and be switched off by the end of the week.
    /// </remarks>
    [Fact]
    public void AnEmptyServerThatWasAlreadyEmptyIsFine()
    {
        Assert.Null(IncidentDetector.Detect(Health(players: 0, previousPlayers: 0), Now));
        Assert.Null(IncidentDetector.Detect(Health(players: 0, previousPlayers: 1), Now));
    }

    /// <summary>A squad leaving together is not a crash.</summary>
    [Fact]
    public void ASmallGroupLeavingIsNotAnIncident()
    {
        Assert.Null(IncidentDetector.Detect(Health(players: 0, previousPlayers: 3), Now));
    }

    /// <summary>Ordinary churn on a busy server is not an incident.</summary>
    [Fact]
    public void PartialChurnOnABusyServerIsNotAnIncident()
    {
        Assert.Null(IncidentDetector.Detect(Health(players: 14, previousPlayers: 20), Now));
    }

    /// <summary>
    /// A populated server emptying while the process stays up IS an incident.
    /// </summary>
    /// <remarks>
    /// The shape worth waking somebody for: twenty players gone, RCON still answering, systemd
    /// still happy. Nobody chose to leave - they could not play.
    /// </remarks>
    [Fact]
    public void APopulatedServerEmptyingIsFlagged()
    {
        var incident = IncidentDetector.Detect(Health(players: 2, previousPlayers: 20), Now);

        Assert.NotNull(incident);
        Assert.Equal(IncidentKind.PlayersVanished, incident.Kind);
        Assert.Equal(IncidentSeverity.High, incident.Severity);
        Assert.Contains(incident.Evidence, e => e.Contains("20 -> 2", StringComparison.Ordinal));
    }

    /// <summary>Emptying AND slow is worse than emptying alone.</summary>
    [Fact]
    public void EmptyingWithSlowRconIsCritical()
    {
        var incident = IncidentDetector.Detect(Health(players: 1, previousPlayers: 24, latencySeconds: 2.8), Now);

        Assert.Equal(IncidentSeverity.Critical, incident!.Severity);
    }

    [Fact]
    public void AFailedUnitIsCritical()
    {
        var incident = IncidentDetector.Detect(Health(unitFailed: true, reachable: false), Now);

        Assert.Equal(IncidentKind.ServiceFailed, incident!.Kind);
        Assert.Equal(IncidentSeverity.Critical, incident.Severity);
    }

    /// <summary>
    /// Unreachable RCON with a healthy unit points at the password, not the process.
    /// </summary>
    /// <remarks>
    /// The recommendation matters more than the detection here. A wrong RCON password looks
    /// exactly like a dead server from the bot's side, and "restart it" is the wrong first
    /// move - it wastes an outage and does not fix anything.
    /// </remarks>
    [Fact]
    public void UnreachableRconWithAHealthyUnitSuggestsCheckingTheCredentials()
    {
        var incident = IncidentDetector.Detect(Health(reachable: false), Now);

        Assert.Equal(IncidentKind.RconDown, incident!.Kind);
        Assert.Contains("password", incident.Recommended, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SlowRconAloneIsOnlyMedium()
    {
        var incident = IncidentDetector.Detect(Health(latencySeconds: 3), Now);

        Assert.Equal(IncidentKind.RconSlow, incident!.Kind);
        Assert.Equal(IncidentSeverity.Medium, incident.Severity);
    }

    /// <summary>A restart loop outranks everything, because nothing else will fix it.</summary>
    [Fact]
    public void ARestartLoopIsReportedAheadOfItsSymptoms()
    {
        var incident = IncidentDetector.Detect(Health(reachable: false, unitFailed: true, restarts: 4), Now);

        Assert.Equal(IncidentKind.RestartLoop, incident!.Kind);
        Assert.Contains("person", incident.Recommended, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One problem produces one incident, not one per symptom.</summary>
    /// <remarks>
    /// A failed unit whose RCON is also unreachable is one problem seen twice. Reporting both
    /// doubles every alert and buries the one that says what to do.
    /// </remarks>
    [Fact]
    public void OneReadingProducesAtMostOneIncident()
    {
        var incident = IncidentDetector.Detect(
            Health(reachable: false, unitFailed: true, latencySeconds: 5, players: 0, previousPlayers: 20), Now);

        Assert.NotNull(incident);
        Assert.Equal(IncidentKind.ServiceFailed, incident.Kind);
    }

    /// <summary>The key is stable, so one ongoing problem is not re-announced every tick.</summary>
    [Fact]
    public void TheKeyIdentifiesOneOngoingProblem()
    {
        var first = IncidentDetector.Detect(Health(reachable: false), Now);
        var later = IncidentDetector.Detect(Health(reachable: false), Now.AddMinutes(5));

        Assert.Equal(first!.Key, later!.Key);
    }
}
