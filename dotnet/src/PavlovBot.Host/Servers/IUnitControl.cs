namespace PavlovBot.Host.Servers;

/// <summary>
/// Reading and changing the state of the game server's systemd units.
/// </summary>
/// <remarks>
/// AN INTERFACE SO <see cref="CrashRecovery"/> CAN BE TESTED. The recovery rules - restart on
/// <c>failed</c> but never on <c>inactive</c>, cap the attempts, give up loudly once - are
/// policy worth pinning, and every one of them is about what NOT to do. Against
/// <see cref="ServiceControl"/> directly each of those tests would shell out to a real
/// <c>systemctl</c> and restart a real game server, which is not a test anybody can run.
///
/// Narrow on purpose: three members, all of which <see cref="ServiceControl"/> already had.
/// The privileged parts - unit-name validation, the sudo decision, the argv array - stay in
/// the implementation where they belong. Nothing here widens what can reach a command line.
/// </remarks>
public interface IUnitControl
{
    /// <summary>The configured units, in server order: index 0 is "Server 1".</summary>
    IReadOnlyList<string> Units { get; }

    /// <summary>
    /// What systemd says about a unit.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="UnitState.Unknown"/> when it could not be read, which callers must
    /// treat as "no information" rather than "down".
    /// </remarks>
    Task<UnitState> StateAsync(string unit, CancellationToken ct = default);

    /// <summary>The unit for a 1-based server number, or null when there is no such server.</summary>
    /// <remarks>
    /// A DEFAULT MEMBER, derived from <see cref="Units"/>, so no implementer has to restate
    /// the off-by-one and no two of them can disagree about it. ServiceControl's own method
    /// matches this signature and so implements it, unchanged.
    /// </remarks>
    string? UnitFor(int serverNumber) =>
        serverNumber >= 1 && serverNumber <= Units.Count ? Units[serverNumber - 1] : null;

    /// <summary>Start, stop or restart a unit. Blocks until systemd says it is done.</summary>
    Task<UnitResult> RunAsync(string unit, UnitAction action, CancellationToken ct = default);
}
