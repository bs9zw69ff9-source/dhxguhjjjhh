using PavlovBot.Host.Servers;

namespace PavlovBot.Tests;

/// <summary>
/// A provisioner that does no OS work, so a command can be tested without a live root box.
/// </summary>
/// <remarks>
/// The same idea as <see cref="StubUnitControl"/>: the real provisioner runs SteamCMD, writes a
/// systemd unit and restarts the bot, none of which a test may do. This records the request it
/// was handed, drives the progress callback with a canned checklist, and returns a fixed outcome.
/// </remarks>
internal sealed class StubServerProvisioner(bool restartQueued = true) : IServerProvisioner
{
    public ProvisionRequest? Received { get; private set; }
    public int ProgressCallbacks { get; private set; }

    public async Task<ProvisionOutcome> ProvisionAsync(
        ProvisionRequest request,
        Func<IReadOnlyList<ProvisionStep>, Task> onProgress,
        CancellationToken ct)
    {
        Received = request;

        var steps = new List<ProvisionStep>
        {
            new("Pre-flight checks", ProvisionStatus.Ok, "ok"),
            new("SteamCMD install", ProvisionStatus.Ok, "installed"),
        };

        await onProgress(steps).ConfigureAwait(false);
        ProgressCallbacks++;

        return new ProvisionOutcome(steps, restartQueued);
    }

    public DeleteRequest? Deleted { get; private set; }

    public async Task<ProvisionOutcome> DeleteAsync(
        DeleteRequest request,
        Func<IReadOnlyList<ProvisionStep>, Task> onProgress,
        CancellationToken ct)
    {
        Deleted = request;

        var steps = new List<ProvisionStep>
        {
            new("Pre-flight checks", ProvisionStatus.Ok, "ok"),
            new("Delete the install directory", ProvisionStatus.Ok, "deleted"),
        };

        await onProgress(steps).ConfigureAwait(false);
        ProgressCallbacks++;

        return new ProvisionOutcome(steps, restartQueued);
    }
}
