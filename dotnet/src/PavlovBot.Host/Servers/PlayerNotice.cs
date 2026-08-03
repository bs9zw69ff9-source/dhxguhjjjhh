using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;
using PavlovBot.Host.Rcon;

namespace PavlovBot.Host.Servers;

/// <param name="Delivered">True when the server accepted the broadcast.</param>
/// <param name="Detail">What happened, in words fit to show an admin. Never empty.</param>
public sealed record NoticeResult(bool Delivered, string Detail);

/// <summary>
/// Telling players a server is about to go away, and giving them time to read it.
/// </summary>
/// <remarks>
/// Shared by <c>/rotatemap</c> and <c>/serverswitch</c> so the two cannot drift about how
/// long players get or which server gets told.
///
/// THE SERVER IS FOUND BY NAME, NOT BY POSITION. RCON slots are allowed to have GAPS -
/// <c>RCON_HOST_1</c> and <c>RCON_HOST_3</c> with no <c>_2</c> is a supported configuration -
/// so indexing into the configured list makes "server 2" mean the second CONFIGURED server
/// rather than the second server. On a box with a gap that warns one server while stopping
/// another, which is the exact failure the numbering is supposed to prevent.
/// <c>RCON_HOST_n</c> becomes the name <c>server{n}</c>, so the number maps straight through.
///
/// THE WARNING IS BOUNDED. RconClient retries three times with backoff, so an unresponsive
/// server can absorb ten seconds or more before admitting it - and every one of those is
/// spent with the server still up and the admin watching a spinner. The notice gets one
/// short attempt; the grace period after it is the part that matters, and it is fixed.
/// </remarks>
public sealed class PlayerNotice(RconRegistry rcon, ILogger<PlayerNotice> logger)
{
    /// <summary>
    /// How long players get between being told and the server going away.
    /// </summary>
    /// <remarks>
    /// Five seconds, and it runs whether or not the warning was delivered. Long enough for
    /// the notice to render and be read; short enough that nobody assumes the command did
    /// nothing and runs it again. An action that begins in the same instant as its own
    /// warning is an action with no warning.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the broadcast itself may take before it is given up on.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than the grace. A warning that takes longer to send than players
    /// get to read it has the sequencing backwards, and a server too unwell to answer RCON is
    /// very often exactly the one being restarted.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    /// <summary>The RCON name for a 1-based server number. See <see cref="ServiceControl.RconNameFor"/>.</summary>
    /// <remarks>
    /// Delegated rather than duplicated: one type owns the whole of "what server N is", and
    /// a second copy of the rule is how the notice and the action end up disagreeing about
    /// which server they are talking about.
    /// </remarks>
    public static string RconNameFor(int serverNumber) => ServiceControl.RconNameFor(serverNumber);

    /// <summary>
    /// Broadcast to one server. Never throws - the caller is about to act regardless.
    /// </summary>
    public async Task<NoticeResult> WarnAsync(int serverNumber, string message, CancellationToken ct)
    {
        var name = RconNameFor(serverNumber);

        /* A SERVER WITH NO RCON IS NOT A SERVER THAT FAILED TO ANSWER, and reporting the two
           the same way sends somebody to debug a connection that was never configured. */
        if (rcon.Client(name) is null)
        {
            logger.LogWarning("No RCON configured for {Name} - players on server {Number} were not warned",
                name, serverNumber);
            return new NoticeResult(false,
                $"no RCON is configured for server {serverNumber} (`RCON_HOST_{serverNumber}` is not set), so nobody could be told");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Budget);

            // Sanitize.Message even for a constant: the RCON protocol is line oriented, and
            // every string that reaches it goes through the same door.
            await rcon.SendAsync(name, $"Notify {Sanitize.Message(message)}", cts.Token).ConfigureAwait(false);

            logger.LogInformation("Warned {Name}: {Message}", name, message);
            return new NoticeResult(true, message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Warning to {Name} timed out after {Budget}s", name, Budget.TotalSeconds);
            return new NoticeResult(false, $"the server did not answer within {Budget.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* THE REAL REASON, not a guess at it. "The server was not answering RCON" was
               printed for every failure alike - a wrong password, a closed port and a
               refused command are three different fixes behind one sentence. */
            logger.LogWarning(ex, "Could not warn {Name} before acting on it", name);
            return new NoticeResult(false, Sanitize.Message(ex.Message));
        }
    }

    /// <summary>The pause between the warning and the action. Always the full <see cref="Grace"/>.</summary>
    public static Task WaitAsync(CancellationToken ct) => Task.Delay(Grace, ct);
}
