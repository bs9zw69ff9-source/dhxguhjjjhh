namespace PavlovBot.Host.Rcon;

/// <summary>
/// Whether a game server is DELIBERATELY down, as opposed to unreachable.
/// </summary>
/// <remarks>
/// WHY THIS IS AN INTERFACE AND NOT A DIRECT CALL. Answering it means asking systemd, and
/// <see cref="RconRegistry"/> has no business knowing that systemd exists - it speaks a game
/// protocol over TCP. An interface here keeps the dependency pointing the right way: the
/// registry states what it needs to know, and the systemd-aware type in
/// <c>PavlovBot.Host.Servers</c> supplies it. Nothing in the RCON layer has to change if the
/// servers are ever run by something other than systemd, and the registry stays constructible
/// in a test without a process launcher.
///
/// OPTIONAL BY DESIGN. A deployment with no systemd - a container, a dev box, somebody
/// running the game by hand - passes nothing and gets exactly the old behaviour, where every
/// unreachable server counts against health. Being unable to ask is not the same as the
/// answer being yes.
/// </remarks>
public interface IServerLifecycle
{
    /// <summary>
    /// True only when the server is known to have been stopped on purpose.
    /// </summary>
    /// <remarks>
    /// False for "running", "starting", and for every case where the question could not be
    /// answered. The caller uses this to EXCUSE an unreachable server, so an uncertain answer
    /// has to fall on the side of still reporting the fault - a bug that silences a real
    /// outage is worse than one that reports an expected one.
    /// </remarks>
    Task<bool> IsIntentionallyStoppedAsync(string rconServerName, CancellationToken ct = default);
}
