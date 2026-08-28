using PavlovBot.Core.Provisioning;

namespace PavlovBot.Host.Servers;

/// <summary>Where a step in a provision run ended up.</summary>
public enum ProvisionStatus
{
    /// <summary>Not started yet. The whole plan is shown up front so the checklist reads as one.</summary>
    Pending,

    /// <summary>In progress. A slow step (SteamCMD) reports this before it reports its result.</summary>
    Running,

    /// <summary>Done and good.</summary>
    Ok,

    /// <summary>Attempted and failed. A failure of a critical step stops the run.</summary>
    Failed,

    /// <summary>Not attempted, because an earlier critical step failed.</summary>
    Skipped,
}

/// <param name="Name">A short label for the step, e.g. "SteamCMD install".</param>
/// <param name="Status">Where it is now.</param>
/// <param name="Detail">One line of context - the reason for a failure, or a note on success.</param>
public sealed record ProvisionStep(string Name, ProvisionStatus Status, string Detail);

/// <summary>The result of a whole provision run.</summary>
/// <param name="Steps">Every step, in order, with its final status.</param>
/// <param name="RestartQueued">True when the bot's own restart was actually triggered.</param>
public sealed record ProvisionOutcome(IReadOnlyList<ProvisionStep> Steps, bool RestartQueued)
{
    /// <summary>Whether every step that ran succeeded (skipped steps do not count against it).</summary>
    public bool Ok => Steps.All(s => s.Status is ProvisionStatus.Ok or ProvisionStatus.Skipped);
}

/// <summary>Everything the provisioner needs to stand up one server and wire it in.</summary>
/// <param name="Spec">The validated server request.</param>
/// <param name="FinalPavlovUnits">The full, aligned <c>PAVLOV_UNITS</c> list to write.</param>
/// <param name="FinalPavlovBases">The full, aligned <c>PAVLOV_BASES</c> list to write.</param>
/// <param name="FinalPlayerCountChannels">The full player-count channel list, or null to leave it.</param>
/// <param name="EnvPath">The path to the bot's <c>.env</c> that gets the override block.</param>
/// <param name="SteamUserPassword">
/// The password to set IF the <c>steam</c> OS account needs to be created.
/// </param>
/// <param name="CopyFromInstallDir">
/// An existing install to copy instead of downloading. Null runs SteamCMD as usual; a path copies
/// that directory's contents into the new install, which is far quicker than fetching several GB
/// again and starts from a build already known to work on this box.
/// </param>
/// <param name="RebuildingExistingSlot">
/// True when this slot is already configured and is being filled in rather than added. Its unit
/// file may well already be there, and replacing it is the whole point - refusing to overwrite it
/// would block exactly the repair being asked for.
/// </param>
public sealed record ProvisionRequest(
    ServerProvisionSpec Spec,
    IReadOnlyList<string> FinalPavlovUnits,
    IReadOnlyList<string> FinalPavlovBases,
    IReadOnlyList<string>? FinalPlayerCountChannels,
    string EnvPath,
    string SteamUserPassword,
    string? CopyFromInstallDir = null,
    bool RebuildingExistingSlot = false);

/// <summary>
/// Standing up a new Pavlov dedicated server on this box and wiring it into the bot.
/// </summary>
/// <remarks>
/// A SEAM, for the same reason <see cref="IUnitControl"/> is one: the real work is SteamCMD,
/// writing a systemd unit, <c>enable --now</c> and <c>ufw</c>, none of which a test may run.
/// The command depends on this interface and is tested against a stub; the real implementation
/// is exercised only on a live root box. Reporting is a callback rather than a return value
/// because the run outlives the Discord interaction token - progress goes to a channel, live.
/// </remarks>
public interface IServerProvisioner
{
    /// <summary>
    /// Run the whole provision. Long-running: pass a host-lifetime token, never a command budget.
    /// </summary>
    /// <param name="request">What to build and how to wire it in.</param>
    /// <param name="onProgress">Called with the full step list whenever a step changes.</param>
    /// <param name="ct">Cancelled only when the host is shutting down.</param>
    Task<ProvisionOutcome> ProvisionAsync(
        ProvisionRequest request,
        Func<IReadOnlyList<ProvisionStep>, Task> onProgress,
        CancellationToken ct);

    /// <summary>
    /// Take a server back off the box: stop it, remove its unit, delete its install, unwire it.
    /// </summary>
    /// <remarks>
    /// IRREVERSIBLE. The install directory goes with it, including that server's own whitelist,
    /// bans and logs - so the caller is responsible for being sure, and for not offering this
    /// where a mis-click reaches it.
    /// </remarks>
    Task<ProvisionOutcome> DeleteAsync(
        DeleteRequest request,
        Func<IReadOnlyList<ProvisionStep>, Task> onProgress,
        CancellationToken ct);
}

/// <param name="Slot">The 1-based server number being removed.</param>
/// <param name="UnitName">Its systemd unit.</param>
/// <param name="InstallDir">Its install directory, which is deleted outright.</param>
/// <param name="FinalPavlovUnits">The unit list WITHOUT it.</param>
/// <param name="FinalPavlovBases">The base list WITHOUT it.</param>
/// <param name="EnvPath">The bot's <c>.env</c>, which gets the removal block.</param>
public sealed record DeleteRequest(
    int Slot,
    string UnitName,
    string InstallDir,
    IReadOnlyList<string> FinalPavlovUnits,
    IReadOnlyList<string> FinalPavlovBases,
    string EnvPath);
