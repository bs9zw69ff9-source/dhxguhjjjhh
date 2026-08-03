using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Time;
using PavlovBot.Host.Configuration;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/feeds</c> - what each webhook is doing, and a button to prove it.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS: <see cref="FeedWebhooks"/> tracked a status string per feed from the
/// day it was written, and nothing ever displayed it. A feed that was not delivering was
/// therefore indistinguishable from a quiet server - which cost days across several
/// separate debugging sessions, every one of them starting from "the webhook doesn't work"
/// with no way to find out why from inside Discord.
///
/// The three states need three different fixes, and the difference is not visible from the
/// channel: no URL set, a URL that Discord rejects, and a URL that works but has nothing to
/// say yet.
/// </remarks>
public sealed class FeedsCommand(FeedWebhooks feeds, FeatureOptions features, Access access) : ISlashCommand
{
    public string Name => "feeds";

    /// <summary>Ephemeral: a webhook URL is a credential, and this discusses them.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Check whether the join, connect, kill and money webhooks are working")
            .AddOption("test", ApplicationCommandOptionType.Boolean,
                "Post a test line to every configured feed", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var test = command.Data.Options.FirstOrDefault(o => o.Name == "test")?.Value as bool? ?? false;

        if (test)
        {
            /* A real post is the only thing that actually answers the question. A URL can
               be present, well-formed and belong to a deleted channel, and nothing short of
               sending to it can tell you. */
            var stamp = EasternTime.Stamp(DateTimeOffset.UtcNow);
            foreach (var label in Feeds.Select(f => f.Label))
                await feeds.PostAsync(label, $"[{stamp}] Feed test from /feeds - if you can read this, {label} works", ct)
                    .ConfigureAwait(false);

            /* AND AN EMBED, to the connect feed only. The connection card goes out through a
               different method than every other feed line - PostEmbedAsync rather than
               PostAsync - so a text-only test would report the connect webhook healthy while
               the thing that actually uses it was failing. Those need separate fixes:
               a dead webhook is a URL in .env, a rejected embed is this code. */
            await feeds.PostEmbedAsync(FeedWebhooks.Connect, SampleCard(), ct).ConfigureAwait(false);
        }

        var lines = Feeds.Select(f =>
        {
            var status = feeds.Status.TryGetValue(f.Label, out var s) ? s : "not registered";
            var mark = status.StartsWith("delivering", StringComparison.Ordinal) ? Theme.Ok
                : status.StartsWith("not configured", StringComparison.Ordinal) ? Theme.Dot
                : status.StartsWith("configured", StringComparison.Ordinal) || status.StartsWith("open", StringComparison.Ordinal) ? Theme.Warn
                : Theme.Bad;

            return $"{mark} **{f.Label}** — `{f.Variable}`\n" +
                   $"{Theme.Dot} {status}\n" +
                   $"{Theme.Dot} *{f.Carries}*";
        });

        var embed = Theme.Notice("Feed webhooks", string.Join("\n\n", lines))
            .AddField("Reading this",
                $"{Theme.Ok} delivering · {Theme.Warn} configured but nothing sent yet · " +
                $"{Theme.Bad} failing · {Theme.Dot} no URL set\n\n" +
                (test
                    ? "A test line went to every configured feed, plus a sample **connection card** " +
                      "to `connect` — the cards use a different code path than the plain lines, so " +
                      "seeing the line but not the card narrows it to the card itself.\n" +
                      "A feed still showing *configured* after this did not accept it."
                    : "Run `/feeds test:true` to post a line to each one, and a sample card to `connect`."));

        await Reply(command, embed).ConfigureAwait(false);
        _ = features;
    }

    /// <summary>
    /// A connection card with nobody real on it, shaped exactly like the live one.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="ConnectCard"/> rather than as a hand-made embed, so that a
    /// card Discord would reject - an over-long field, a bad colour - fails here too. A test
    /// that passes because it sends something simpler than the real thing is worse than no
    /// test: it says the feed works and it is the feed that does not.
    /// </remarks>
    internal static Embed SampleCard() =>
        ConnectCard.Build(
            name: "feed-test", accountId: "0000000000000000", ip: "203.0.113.1", confidentIp: true,
            server: "test", account: null, alts: [], vpn: null,
            flagged: false, master: false, at: DateTimeOffset.UtcNow);

    /// <param name="Carries">What goes to this feed, so the right URL ends up in the right one.</param>
    private sealed record Feed(string Label, string Variable, string Carries);

    /* The Join/Connect split is called out because getting it backwards is the mistake with
       consequences: connect carries player addresses, and pointing it at a public channel
       publishes them. */
    private static readonly Feed[] Feeds =
    [
        new(FeedWebhooks.Join, "JOIN_WEBHOOK_URL", "Public join and leave lines. No addresses - safe in a public channel."),
        new(FeedWebhooks.Connect, "CONNECT_WEBHOOK_URL", "Connection cards WITH IP ADDRESSES, plus auto-ban notices. Private channel only."),
        new(FeedWebhooks.Kill, "KILL_WEBHOOK_URL", "Kill lines."),
        new(FeedWebhooks.Money, "MONEY_WEBHOOK_URL", "Balance changes."),
        new(FeedWebhooks.Staff, "STAFF_WEBHOOK_URL",
            "Every staff action as it happens - bans, kicks, menu grants, /serverswitch. Private channel only."),
    ];

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
