using Discord;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Deferring ephemerally where Discord requires it.
/// </summary>
/// <remarks>
/// THE BUG: a user-installed app may only speak EPHEMERALLY in a server it has not been
/// added to. The gateway deferred with whatever the command preferred, so every public
/// command run that way had its deferral rejected - and a rejected deferral is not a bot
/// error message, it is Discord's own "the application did not respond" with nothing in the
/// log to connect it to anything.
///
/// /server lookup is where it showed up first because it is public and reads nothing from
/// the guild: there is no permission refusal to hide behind, so it simply broke.
/// </remarks>
public class UserAppEphemeralTests
{
    private static IReadOnlyDictionary<ApplicationIntegrationType, ulong> Owners(
        params ApplicationIntegrationType[] kinds) =>
        kinds.ToDictionary(k => k, _ => 1234UL);

    /// <summary>Fails the test if the fallback is consulted when it should not be.</summary>
    private static bool Unreachable() => throw new InvalidOperationException(
        "the owners were authoritative; the guild cache should not have been asked");

    [Fact]
    public void AUserInstalledAppInSomebodyElsesServerIsForcedEphemeral()
    {
        // THE REGRESSION. Only the user authorised this, so the app is not installed here.
        Assert.True(DiscordGateway.RequiresEphemeral(
            guildId: 999, Owners(ApplicationIntegrationType.UserInstall), Unreachable));
    }

    [Fact]
    public void AGuildTheBotIsInstalledInIsLeftAlone()
    {
        /* The ordinary case, and it must stay non-ephemeral: a board or an announcement
           posted only to the person who ran it is a different bug with the same shape. */
        Assert.False(DiscordGateway.RequiresEphemeral(
            guildId: 999, Owners(ApplicationIntegrationType.GuildInstall), Unreachable));
    }

    [Fact]
    public void BothInstallationsAtOnceIsStillAGuildInstall()
    {
        // Installed to the server AND carried by the user. The guild install is what counts.
        Assert.False(DiscordGateway.RequiresEphemeral(
            guildId: 999,
            Owners(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall),
            Unreachable));
    }

    [Fact]
    public void ADirectMessageIsNeverForced()
    {
        /* No guild, nothing to be outside of, and the deferral is unrestricted there. Forcing
           it would make every DM reply ephemeral for no reason. */
        Assert.False(DiscordGateway.RequiresEphemeral(guildId: null, owners: null, Unreachable));
        Assert.False(DiscordGateway.RequiresEphemeral(
            guildId: null, Owners(ApplicationIntegrationType.UserInstall), Unreachable));
    }

    [Fact]
    public void WithNoOwnersTheGuildCacheDecides()
    {
        /* Older payloads carry no owners at all. A guild this bot is a member of is one the
           gateway has; one it has never heard of is one it is not in. */
        Assert.True(DiscordGateway.RequiresEphemeral(guildId: 999, owners: null, () => false));
        Assert.False(DiscordGateway.RequiresEphemeral(guildId: 999, owners: null, () => true));
    }

    [Fact]
    public void AnEmptyOwnerListFallsBackRatherThanAssuming()
    {
        /* Present but empty says nothing either way, and reading it as "no guild install"
           would force every reply ephemeral on a payload shape that means nothing of the
           sort. */
        Assert.False(DiscordGateway.RequiresEphemeral(guildId: 999, Owners(), () => true));
        Assert.True(DiscordGateway.RequiresEphemeral(guildId: 999, Owners(), () => false));
    }
}
