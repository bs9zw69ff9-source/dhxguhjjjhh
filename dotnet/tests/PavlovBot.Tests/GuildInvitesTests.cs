using PavlovBot.Core.Security;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/guildinvites</c>: DMing the super owner an invite to every Discord server the bot is in.
/// </summary>
/// <remarks>
/// The Discord half - walking the guild list, creating an invite, opening a DM - is behind
/// <see cref="IGuildDirectory"/> and only testable against a real gateway. What is pinned here is
/// everything else: who it goes to, how long the invites last, and that a guild the bot cannot
/// invite anyone to is still reported rather than quietly dropped.
/// </remarks>
public class GuildInvitesTests
{
    private static GuildInvite With(string name, string? url, string? problem = null, int members = 42) =>
        new(1234567890UL, name, members, url, problem);

    [Fact]
    public void TheRecipientIsTheCompiledInSuperOwner()
    {
        /* Not an option somebody can type. A recipient taken from the command would turn this
           into a way to mail a working invite to every guild the bot is in to anyone at all, so
           it is the id OwnerGuard already fingerprints and refuses to start without. */
        Assert.Equal(1014251293159731310UL, OwnerGuard.SuperOwnerId);
    }

    [Fact]
    public void InvitesAreShortLivedAndSingleUse()
    {
        // These admit somebody to servers other people administer, so what leaves the bot
        // expires on its own rather than becoming a permanent link sitting in a DM.
        Assert.Equal(TimeSpan.FromHours(24), GuildInvitesCommand.InviteLifetime);
        Assert.Equal(1, GuildInvitesCommand.InviteUses);
    }

    [Fact]
    public void EachGuildIsOneLineWithItsInvite()
    {
        var pages = GuildInvitesCommand.Pages(
            [With("New Vegas RP", "https://discord.gg/abc123"), With("Mojave", "https://discord.gg/def456")],
            TimeSpan.FromHours(24));

        var text = string.Join("\n", pages);
        Assert.Contains("New Vegas RP", text, StringComparison.Ordinal);
        Assert.Contains("https://discord.gg/abc123", text, StringComparison.Ordinal);
        Assert.Contains("Mojave", text, StringComparison.Ordinal);
        Assert.Contains("https://discord.gg/def456", text, StringComparison.Ordinal);

        // The expiry is stated once, on the first page, rather than repeated down a long list.
        Assert.Contains("Single-use, expire in 24h.", pages[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AGuildWithNoInviteIsStillListedWithTheReason()
    {
        /* The bot is IN that server whether or not it may invite anyone to it. Dropping it would
           understate where the bot actually is, which is the one thing this command exists to
           answer. */
        var pages = GuildInvitesCommand.Pages(
            [With("Locked Down", url: null, problem: "no channel here lets the bot create an invite (needs Create Invite)")],
            TimeSpan.FromHours(24));

        var text = string.Join("\n", pages);
        Assert.Contains("Locked Down", text, StringComparison.Ordinal);
        Assert.Contains("needs Create Invite", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberCountsAreGroupedSoALargeGuildIsReadable()
    {
        var pages = GuildInvitesCommand.Pages([With("Big", "https://discord.gg/x", members: 12345)], TimeSpan.FromHours(24));

        Assert.Contains("12,345", string.Join("\n", pages), StringComparison.Ordinal);
    }

    [Fact]
    public void AGuildNameCannotBreakOutOfTheMarkdown()
    {
        // Guild names are set by other people; a backtick in one would otherwise end the code
        // span it lands in and let the rest of the name render as formatting.
        var pages = GuildInvitesCommand.Pages(
            [With("Evil ` Name", "https://discord.gg/x")], TimeSpan.FromHours(24));

        Assert.DoesNotContain("`", string.Join("\n", pages), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongListPaginatesRatherThanLosingTheTail()
    {
        // Discord rejects an over-long embed outright, so a bot in many guilds must split rather
        // than send one message that never arrives.
        var many = Enumerable.Range(1, 400)
            .Select(i => With($"Guild number {i} with a reasonably long name", $"https://discord.gg/invite{i}"))
            .ToList();

        var pages = GuildInvitesCommand.Pages(many, TimeSpan.FromHours(24));

        Assert.True(pages.Count > 1, "400 guilds should not fit in a single embed description");
        Assert.All(pages, p => Assert.True(p.Length <= 4096, "every page must fit Discord's description limit"));
        Assert.Contains("Guild number 400", string.Join("\n", pages), StringComparison.Ordinal);
    }
}
