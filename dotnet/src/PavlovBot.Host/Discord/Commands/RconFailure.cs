using Discord;
using PavlovBot.Core.Text;
using PavlovBot.Host.Rcon;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// The reply for "the command reached no server", with the reason attached.
/// </summary>
/// <remarks>
/// "No server accepted the command" NAMES THE SYMPTOM AND NOTHING ELSE, and it is the answer
/// somebody gets while standing in front of a server they cannot administer. The reasons it
/// covers are not variations on a theme - a refused connection, a wrong password, a server
/// somebody stopped and a command RCON+ does not implement need four different actions, and
/// the bot knows which it was at the moment it gives up.
///
/// It was thrown away: the exception was logged at Warning, where an operator would have to
/// go and look for it, and the reply said nothing. This puts it on the reply.
/// </remarks>
internal static class RconFailure
{
    /// <param name="attempts">Each server tried, and the error it gave, or null if it simply refused.</param>
    public static EmbedBuilder Explain(
        string title, string body, RconRegistry rcon, IReadOnlyList<(string Server, string? Problem)> attempts)
    {
        ArgumentNullException.ThrowIfNull(rcon);
        ArgumentNullException.ThrowIfNull(attempts);

        var embed = Theme.Failure(title, body);

        if (attempts.Count == 0)
        {
            /* No servers configured at all is a different fault from every server refusing,
               and it is one nobody diagnoses from a message about servers refusing. */
            embed.AddField("Why", "No RCON servers are configured. Set `RCON_HOST_1`, `RCON_PORT_1` and `RCON_PASSWORD_1`.");
            return embed;
        }

        var lines = attempts.Select(a =>
        {
            /* THE SEND ERROR FIRST, then the last health probe. The send is what just
               happened; the probe is a minute old at worst and still says more than
               "refused" - a server that is down fails both, and the probe is the one that
               words it as a connection rather than as a timeout. */
            var reason = a.Problem
                ?? rcon.LastError(a.Server)
                ?? "refused the command without an error - RCON+ may not implement it";

            return $"{Theme.Dot} **{Sanitize.Markdown(a.Server)}** — {Sanitize.Message(reason)}";
        });

        embed.AddField("Why", string.Join("\n", lines));

        // The commonest causes, in the order they actually occur, and each one actionable.
        embed.AddField("Usually",
            "**Connection refused** — the server is not running, or its RCON port is not the one configured.\n" +
            "**Authentication failed** — `RCON_PASSWORD_n` does not match the server's `RconPassword`.\n" +
            "**Timed out** — the server is up but wedged; `/serverinfo` says whether anything answers.");

        return embed;
    }
}
