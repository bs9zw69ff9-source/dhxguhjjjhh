using Microsoft.Extensions.Configuration;
using PavlovBot.Host.Configuration;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The whitelist bot: a second Discord application owning the whitelist commands.
/// </summary>
/// <remarks>
/// Unported until now. The Node bot runs a second client in the same process when
/// FACTION_BOT_TOKEN and FACTION_CLIENT_ID are set, and the C# bot read neither - so after
/// the cutover that application never logged in and simply showed offline, while its four
/// commands silently moved to the main bot.
/// </remarks>
public class FactionBotTests
{
    private static BotOptions Bind(params (string Key, string Value)[] settings) =>
        BotOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings
                .Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))
                .Append(new KeyValuePair<string, string?>("DISCORD_TOKEN", "main-token")))
            .Build());

    [Fact]
    public void BothHalvesAreNeeded()
    {
        /* A token with no client id cannot register commands, and a client id with no token
           cannot log in. Treating either alone as "enabled" would start a bot that connects
           and then does nothing, which looks like the bot being broken rather than
           misconfigured. */
        Assert.False(Bind(("FACTION_BOT_TOKEN", "t")).FactionBotEnabled);
        Assert.False(Bind(("FACTION_CLIENT_ID", "123456789012345678")).FactionBotEnabled);
        Assert.True(Bind(("FACTION_BOT_TOKEN", "t"), ("FACTION_CLIENT_ID", "123456789012345678")).FactionBotEnabled);
    }

    [Fact]
    public void ItIsOffByDefault()
    {
        // Most servers run one bot. Off must be the default, and off must be silent.
        var options = Bind();

        Assert.False(options.FactionBotEnabled);
        Assert.Null(options.FactionToken);
        Assert.Null(options.FactionClientId);
    }

    [Fact]
    public void ABlankTokenIsNotAToken()
    {
        // An empty FACTION_BOT_TOKEN= line in .env is how somebody turns this off, and it
        // must not read as configured.
        Assert.False(Bind(("FACTION_BOT_TOKEN", "   "), ("FACTION_CLIENT_ID", "123456789012345678")).FactionBotEnabled);
    }

    [Fact]
    public void AMalformedClientIdIsNotEnabled()
    {
        // Rather than parsing to zero and registering commands against application 0.
        Assert.False(Bind(("FACTION_BOT_TOKEN", "t"), ("FACTION_CLIENT_ID", "not-a-snowflake")).FactionBotEnabled);
    }

    [Fact]
    public void TheFactionTokenIsNotTheMainToken()
    {
        /* They are separate Discord applications. Logging the second client in with the
           main token would connect two gateways to ONE application, which answers every
           command twice - the exact failure the two-bots-at-once guard exists to prevent. */
        var options = Bind(("FACTION_BOT_TOKEN", "faction-token"), ("FACTION_CLIENT_ID", "123456789012345678"));

        Assert.Equal("main-token", options.DiscordToken);
        Assert.Equal("faction-token", options.FactionToken);
    }
}

/// <summary>
/// Where the ban-message file lives.
/// </summary>
/// <remarks>
/// The C# bot built it as &lt;MODSAVE_PATH&gt;/ModSave/banlist.txt. MODSAVE_PATH already
/// points AT the ModSave directory, so that was a doubled path that does not exist - the
/// file a banned player's message is written to went somewhere the game never reads, and
/// MODSAVE_BLACKLIST_PATH was ignored entirely.
/// </remarks>
public class ModsaveBanlistPathTests
{
    private static FeatureOptions Bind(params (string Key, string Value)[] settings) =>
        FeatureOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void AnExplicitOverrideWins()
    {
        var options = Bind(
            ("MODSAVE_BLACKLIST_PATH", "/custom/banlist.txt"),
            ("PAVLOV_BASE_1", "/home/steam/pavlovserver"),
            ("MODSAVE_PATH", "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave"));

        Assert.Equal("/custom/banlist.txt", options.ModsaveBanlistPath);
    }

    [Fact]
    public void ItIsDerivedFromTheInstallRoot_NotFromTheLedgerDirectory()
    {
        // The ledger directory IS .../Config/ModSave, so appending ModSave again produced
        // .../Config/ModSave/ModSave/banlist.txt.
        var options = Bind(
            ("PAVLOV_BASE_1", "/home/steam/pavlovserver"),
            ("MODSAVE_PATH", "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave"));

        Assert.Equal("/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/banlist.txt",
            options.ModsaveBanlistPath);
        Assert.DoesNotContain("ModSave/ModSave", options.ModsaveBanlistPath, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultRootMatchesTheNodeBot()
    {
        // index.js: PAVLOV_BASE_1 || "/home/steam/pavlovserver"
        Assert.Equal("/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/banlist.txt", Bind().ModsaveBanlistPath);
    }
}
