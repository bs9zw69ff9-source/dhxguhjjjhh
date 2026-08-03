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

        embed.AddField("Note",
            "The timer runs this every 5 minutes. Discord allows two renames per 10 minutes " +
            "*per channel*, so running this by hand spends part of that allowance.");

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
