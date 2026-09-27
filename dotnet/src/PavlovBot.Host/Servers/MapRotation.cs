using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;

namespace PavlovBot.Host.Servers;

/// <summary>How a rotation went.</summary>
public enum RotationOutcome
{
    /// <summary>The server accepted <c>RotateMap</c>, or Pavlov.log showed it running.</summary>
    Rotated,

    /// <summary>Sent, but the reply was lost: the map may or may not have changed.</summary>
    Unconfirmed,

    /// <summary>Never reached the server, or the server said no.</summary>
    Failed,
}

/// <param name="Number">The 1-based server number.</param>
/// <param name="Detail">What happened, in words fit to show an admin. Never empty.</param>
public sealed record RotationResult(int Number, RotationOutcome Outcome, string Detail)
{
    public bool Ok => Outcome is RotationOutcome.Rotated;
}

/// <summary>
/// <c>RotateMap</c> over RCON: the game moves to the next map in its rotation, in the same process.
/// </summary>
/// <remarks>
/// <c>/rotatemap</c> used to do this with <c>systemctl restart</c>, which replaced the process
/// instead. That is still available as <c>/serverswitch restart</c> for a server that has gone bad
/// in a way a map change does not fix; this is the lighter action the command's name promises.
///
/// Servers are addressed through <see cref="ServiceControl.RconNameFor"/>, the same rule the
/// warning uses, so the server that is told and the server that rotates cannot differ.
/// </remarks>
public sealed class MapRotation(RconRegistry rcon, ILogger<MapRotation> logger)
{
    /// <summary>The verb. Takes no arguments: the server picks the next map from its own rotation.</summary>
    public const string Command = "RotateMap";

    /// <summary>Rotate one server. Never throws except on cancellation - the caller reports every server.</summary>
    public async Task<RotationResult> RotateAsync(int serverNumber, CancellationToken ct)
    {
        var name = ServiceControl.RconNameFor(serverNumber);

        if (rcon.Client(name) is null)
        {
            return new RotationResult(serverNumber, RotationOutcome.Failed,
                $"no RCON is configured for server {serverNumber} (`RCON_HOST_{serverNumber}` is not set)");
        }

        try
        {
            // Verified: "Successful": false is a refusal, not a rotation.
            await rcon.SendVerifiedAsync(name, Command, ct).ConfigureAwait(false);
            logger.LogInformation("Rotated the map on {Name}", name);
            return new RotationResult(serverNumber, RotationOutcome.Rotated, "map rotated");
        }
        catch (RconUnconfirmedException ex)
        {
            /* NOT A FAILURE TO REPORT AS ONE. The command went out and the reply did not come
               back; a server busy loading the next map is exactly when that happens. Calling
               it failed invites a second run, which skips a map. */
            logger.LogWarning(ex, "RotateMap on {Name} was sent but not confirmed", name);
            return new RotationResult(serverNumber, RotationOutcome.Unconfirmed,
                "sent, but the server did not confirm it - check in-game before running this again");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RotateMap on {Name} failed", name);
            return new RotationResult(serverNumber, RotationOutcome.Failed, Sanitize.Message(ex.Message));
        }
    }
}
