using Discord;
using Discord.WebSocket;
using PavlovBot.Host.Configuration;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/counts</c> - run the player-count channel update now, and say what happened.
/// </summary>
/// <remarks>
/// The tick is five minutes apart, which makes every attempt at diagnosing it a five-minute
/// wait - and the causes all look identical from the channel list: no ids configured, an id
/// the bot cannot see, a missing Manage Channels permission, a roster too stale to publish,
/// or simply nothing to change since last time.
///
/// This runs the same code the timer runs. Not a simulation of it: a check that agrees with
/// a separate implementation of the thing it is checking is worth nothing.
/// </remarks>
public sealed class CountsCommand(PlayerCountChannels counts, FeatureOptions features, Access access) : ISlashCommand
{
    public string Name => "counts";

    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Update the player-count voice channels now")
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var targets = new PlayerCountChannels.Targets(features.PlayerCountChannels, features.ShackTotalChannel);

        if (targets.ServerChannels.Count == 0 && targets.TotalChannel is null)
        {
            /* The commonest cause by a distance, and the one the channel list cannot show:
               the feature is not configured at all, so the timer is not even registered. */
            await Reply(command, Theme.Warning("No count channels are configured",
                "Set `PLAYER_COUNT_CHANNELS` to the voice channel ids in server order, and " +
                "`SHACK_TOTAL_CHANNEL` to the platform-total channel, then restart.\n\n" +
                "```\nPLAYER_COUNT_CHANNELS=111,222,333\nSHACK_TOTAL_CHANNEL=444\n```")).ConfigureAwait(false);
            return;
        }

        var report = await counts.RunAsync(targets, ct).ConfigureAwait(false);
        var failed = report.Count(r => r.Contains("FAILED", StringComparison.Ordinal));

        var embed = failed > 0
            ? Theme.Failure($"{failed} of {report.Count} channel(s) could not be updated",
                string.Join("\n", report.Select(r => $"{Theme.Dot} {r}")))
            : Theme.Success("Player-count channels updated",
                string.Join("\n", report.Select(r => $"{Theme.Dot} {r}")));

        if (failed > 0)
        {
            embed.AddField("Most likely",
                "**needs Manage Channels** — the bot must have that permission *on each voice channel*. " +
                "A bot invited for messaging alone does not have it, and this is the usual answer.\n" +
                "**not visible** — the id is wrong, or the bot cannot see that channel.");
        }

        /* HALF-CONFIGURED IS THE CASE THIS MISSES. The guard above only fires when BOTH
           settings are empty, so setting one and not the other produces a green tick and a
           report that quietly covers half the feature - and the missing half is invisible,
           because a channel that was never configured cannot appear in a per-channel report.

           The symptom is somebody watching servers go down and no channel ever saying so. */
        if (MissingHalf(targets) is { } missing) embed.AddField(missing.Title, missing.Body);

        embed.AddField("Note",
            "The timer runs this every 5 minutes. Discord allows two renames per 10 minutes " +
            "*per channel*, so running this by hand spends part of that allowance.");

        await Reply(command, embed).ConfigureAwait(false);
    }

    /// <summary>
    /// The half of the feature that is not configured, or null when both halves are.
    /// </summary>
    /// <remarks>
    /// A REPORT CANNOT MENTION A CHANNEL THAT WAS NEVER CONFIGURED. Every other diagnostic in
    /// this command is per-channel, so the one state it could not describe was the state of
    /// having no channels - and setting one of the two settings and not the other produced a
    /// green tick over a report covering half the feature.
    ///
    /// The way that presents is somebody watching servers go down and no channel ever saying
    /// so, which reads as the offline detection being broken rather than as a missing line
    /// in .env.
    ///
    /// Stated as a note on a successful run rather than an error: half the feature genuinely
    /// is working, and the other half is a setting rather than a fault.
    /// </remarks>
    internal static (string Title, string Body)? MissingHalf(PlayerCountChannels.Targets targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.ServerChannels.Count == 0)
        {
            return ("Per-server channels are not configured",
                "`PLAYER_COUNT_CHANNELS` is empty, so nothing above reports a per-server count - " +
                "including when a server is down. Only the platform total is being updated.\n\n" +
                "Set it to one voice channel id per server, **in server order** so the first id is " +
                "the server behind `RCON_HOST_1`, then restart:\n" +
                "```\nPLAYER_COUNT_CHANNELS=111,222,333\n```");
        }

        if (targets.TotalChannel is null)
        {
            return ("The platform total is not configured",
                "`SHACK_TOTAL_CHANNEL` is empty, so the Pavlov Shack total is not being updated. " +
                "The per-server channels above are unaffected.");
        }

        return null;
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
