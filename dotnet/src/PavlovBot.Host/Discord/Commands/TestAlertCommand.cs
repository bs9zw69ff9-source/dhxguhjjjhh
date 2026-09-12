using Discord;
using Discord.WebSocket;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Moderation;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/testalert</c> - send a sample security alert and say who actually received it.
/// </summary>
/// <remarks>
/// THE FAILURE THIS EXISTS FOR IS SILENT AT DISCORD'S END. A direct message to somebody with
/// DMs closed, or who shares no server with the bot, is ACCEPTED by the API and never
/// delivered. Nothing about the sending side looks wrong, so "it is configured" and "it
/// works" are two different claims, and the only honest way to tell them apart is to send
/// one and look.
///
/// It goes through the SAME sink the detectors use, not a private copy of it. A check that
/// agrees with a separate implementation of the thing it is checking is worth nothing - if
/// the recipient list is empty, or the sink was never attached, this says so instead of
/// quietly proving that a test path works.
/// </remarks>
public sealed class TestAlertCommand(SecurityAlerts alerts, FeatureOptions features, Access access) : ISlashCommand
{
    public string Name => "testalert";

    /// <summary>Ephemeral: it names who the server's security alerts go to.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Send a test security alert and report who received it")
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command)))
                .ConfigureAwait(false);
            return;
        }

        if (!alerts.Enabled || features.SecurityAlertRecipients.Count == 0)
        {
            await Reply(command, Theme.Warning("Nobody is set to receive security alerts",
                "Set `SECURITY_DM_IDS` to the Discord user id(s) that should get them and restart.\n\n" +
                "```\nSECURITY_DM_IDS=307052224087851009\n```\n" +
                "With it unset the owners get them, and with no owners configured either, nothing is sent."))
                .ConfigureAwait(false);
            return;
        }

        var sample = new SecurityAlert(
            SecurityAlertKind.Evasion,
            "TestSubject",
            "This is a test. No player matched anything.",
            "Nothing - `/testalert` sent this to prove the alerts reach you.",
            AccountId: "0002913af4f445df86da1be6a2a01728",
            Ip: "203.0.113.9",
            Server: "server1",
            Alts: ["TestSubject_alt"],
            At: DateTimeOffset.UtcNow);

        var results = await alerts.PostAsync(sample, ct).ConfigureAwait(false);

        var delivered = results.Count(r => r.Delivered);
        var lines = results.Select(r => r.Delivered
            ? $"{Theme.Ok} <@{r.User}> — delivered"
            : $"{Theme.Bad} <@{r.User}> — {r.Problem ?? "not delivered"}");

        var embed = delivered == results.Count
            ? Theme.Success($"Test alert delivered to {delivered} of {results.Count}", string.Join("\n", lines))
            : Theme.Failure($"Test alert reached {delivered} of {results.Count}", string.Join("\n", lines));

        if (delivered < results.Count)
        {
            /* THE TWO CAUSES, because neither is guessable from the failure and both are
               fixed by the recipient rather than by the bot. */
            embed.AddField("Why a DM does not arrive",
                "**Closed DMs** — Privacy Settings → *Direct Messages* must allow messages from " +
                "server members, for a server this bot is in.\n" +
                "**No shared server** — the bot can only DM somebody it shares a server with.");
        }

        await Reply(command, embed).ConfigureAwait(false);
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
