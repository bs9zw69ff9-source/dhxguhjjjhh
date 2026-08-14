using Microsoft.Extensions.Configuration;
using PavlovBot.Host.Configuration;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>USER_APP</c>: register as a user-installable app rather than into one guild.
/// </summary>
/// <remarks>
/// WHAT IS AND IS NOT COVERED HERE, because the gap matters more than the coverage.
///
/// These test the SWITCH. The registration itself - setting the integration types and
/// contexts on each command and sending them globally - lives in <c>DiscordGateway</c>, which
/// constructs a real <c>DiscordSocketClient</c> in its constructor and cannot be built in a
/// test without a network client. There is no seam to fake and adding one is a refactor of
/// its own, so that half is verified by the startup log on a live bot and nothing here should
/// be read as covering it.
///
/// The switch is still worth pinning. It is the thing an operator sets, its spelling is the
/// thing they get wrong, and it silently changes where every command in the bot is
/// registered.
/// </remarks>
public class UserAppTests
{
    private static BotOptions Bind(params (string Key, string Value)[] settings) =>
        BotOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings
                .Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))
                .Append(new KeyValuePair<string, string?>("DISCORD_TOKEN", "main-token")))
            .Build());

    [Fact]
    public void ItIsOffByDefault()
    {
        /* Off must be the default. Turning it on moves every command from guild-scoped to
           global, which trades instant registration for up to an hour of propagation - not
           something an install should inherit without asking for it. */
        Assert.False(Bind().UserApp);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData(" 1 ")]
    public void TheUsualWaysOfSayingYesAllWork(string value)
    {
        // Whatever somebody types in .env, they meant on. The spellings match the other
        // flags in this file rather than inventing a new convention for this one.
        Assert.True(Bind(("USER_APP", value)).UserApp);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("")]
    public void AnythingElseIsOff(string value)
    {
        Assert.False(Bind(("USER_APP", value)).UserApp);
    }

    /// <summary>
    /// GUILD_ID and USER_APP both bind. The conflict is resolved at registration, not here.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT made mutually exclusive in configuration. Discord cannot combine
    /// them - integration types exist only on global commands - but refusing to start on the
    /// combination would mean an operator trying user-install has to also remember to delete
    /// a setting that is about to be ignored anyway.
    ///
    /// So both are read, USER_APP wins, and the startup log says GUILD_ID was ignored and
    /// why. Turning USER_APP back off restores guild registration with nothing to undo.
    /// </remarks>
    [Fact]
    public void GuildIdIsStillReadWhenUserAppIsOn()
    {
        var options = Bind(("USER_APP", "1"), ("GUILD_ID", "123456789012345678"));

        Assert.True(options.UserApp);
        Assert.Equal(123456789012345678UL, options.GuildId);
    }
}
