using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;
using PavlovBot.Core.Penal;
using PavlovBot.Core.Text;
using PavlovBot.Host;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Monitoring;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The skin is process-wide, so the tests that switch it must never overlap anything that
/// reads it. A collection that disables parallelisation runs on its own, after the rest.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SkinCollection
{
    public const string Name = "Process-wide skin";
}

/// <summary>THEME: the police bot says police things, and the Fallout bot says what it always did.</summary>
[Collection(SkinCollection.Name)]
public sealed class SkinTests : IDisposable
{
    private readonly Skin _skin = Theme.Skin;
    private readonly string _brand = Theme.BrandName;

    public void Dispose()
    {
        Theme.Use(_skin);
        Theme.BrandName = _brand;
    }

    private static FeatureOptions Bind(params (string Key, string Value)[] settings) =>
        FeatureOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    private static Boards Boards() => new(
        new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec()),
        new RconRegistry(new BotOptions
        {
            DiscordToken = "t",
            Servers = [],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        }, new MetricsRegistry(), NullLogger<RconRegistry>.Instance));

    [Fact]
    public void FalloutIsTheDefault() => Assert.Same(Skins.Fallout, Theme.Skin);

    [Fact]
    public void TheFalloutSkinSaysWhatItAlwaysSaid()
    {
        // Pinned, because the Fallout bot must not notice that skins exist.
        Theme.Use(Skins.Fallout);

        Assert.Equal("Mojave Authority", Theme.BrandName);
        Assert.Equal("1,250 caps", Lore.Amount(1250));
        Assert.Equal("☢️ Mojave Authority", Theme.Notice("x").Build().Footer!.Value.Text);
        Assert.Equal("12 wastelanders", Lore.Wastelanders(12));
        Assert.Equal("courier roaming the Mojave right now.", Lore.Roaming(1));
        Assert.Equal("The 10 richest in the wasteland", Lore.RichestHeading(10));
        Assert.Equal("✅ 📜 No open bounties", Boards().BuildWarrantBoard()!.Title);
        Assert.Equal("Nobody is wanted in the Mojave right now.", Boards().BuildWarrantBoard()!.Description);
        Assert.Equal("ℹ️ ⛓️ Most wanted in the Mojave", Boards().BuildArrestBoard()!.Title);
        Assert.Equal("Bounties", Lore.WarrantsHeading);
        Assert.Equal(new Discord.Color(0x1F, 0x8A, 0x4C), Theme.Green);
    }

    [Fact]
    public void ThePoliceSkinIsTheGwpd()
    {
        Theme.Use(Skins.Police);

        Assert.Equal("GWPD", Theme.BrandName);
        Assert.Equal("🚔 GWPD", Theme.Notice("x").Build().Footer!.Value.Text);
        Assert.Equal("$1,250", Lore.Amount(1250));
        Assert.Equal("12 players", Lore.Wastelanders(12));
        Assert.Equal("Top 10 richest players", Lore.RichestHeading(10));
        Assert.Equal("✅ 🚨 No active warrants", Boards().BuildWarrantBoard()!.Title);
        Assert.Equal("Nobody is currently wanted.", Boards().BuildWarrantBoard()!.Description);
        Assert.Equal("ℹ️ 🔒 Most wanted", Boards().BuildArrestBoard()!.Title);
        Assert.Equal("Warrants", Lore.WarrantsHeading);
        Assert.Equal(new Discord.Color(0x60, 0xA5, 0xFA), Theme.Blue);
    }

    [Fact]
    public void NoFalloutWordReachesThePoliceSkin()
    {
        /* The failure this guards is a phrase copied across from the Fallout skin and never
           reworded - a police bot quoting caps, or telling somebody they are exiled. */
        string[] fallout = ["Mojave", "wasteland", "caps", "Caps", "courier", "bount", "Bount", "xile", "Dossier", "gate", "Terminal"];
        var words = Skins.Police.Words;

        var phrases = typeof(Vocabulary).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (p.Name, Text: (string)p.GetValue(words)!));

        var leaks = phrases.Where(p => fallout.Any(f => p.Text.Contains(f, StringComparison.Ordinal))).ToList();

        Assert.Empty(leaks);
        Assert.Equal(Currency.Dollars, words.Money);
    }

    [Fact]
    public void BailIsQuotedInTheSameMoneyAsABalance()
    {
        Theme.Use(Skins.Police);
        var booking = PenalCode.Book(["PC 100"], 1.0);

        Assert.Equal(Lore.Amount(booking.Bail), booking.BailLabel(Lore.Money));
        Assert.StartsWith("$", booking.BailLabel(Lore.Money), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCashTableIsHeadedInTheSkinsWords()
    {
        Theme.Use(Skins.Police);
        Assert.Contains("Balance", PavlovBot.Host.Discord.Boards.CashTable([("Alice", 5)], 1000).Block, StringComparison.Ordinal);

        Theme.Use(Skins.Fallout);
        Assert.Contains("Caps", PavlovBot.Host.Discord.Boards.CashTable([("Alice", 5)], 1000).Block, StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedBotNameStillWins()
    {
        // Program applies the skin, then BOT_NAME - so an explicit name beats the skin's own.
        Theme.Use(Skins.Police);
        Theme.BrandName = "Vault 21 Security";

        Assert.Equal("🚔 Vault 21 Security", Theme.Notice("x").Build().Footer!.Value.Text);
    }

    [Theory]
    [InlineData("police", "police")]
    [InlineData(" POLICE ", "police")]
    [InlineData("fallout", "fallout")]
    public void ASkinIsChosenByName(string setting, string expected) =>
        Assert.Equal(expected, Skins.Named(setting)!.Name);

    [Theory]
    [InlineData("polcie")]
    [InlineData("")]
    [InlineData(null)]
    public void ANameThatIsNotASkinIsRefused(string? setting) => Assert.Null(Skins.Named(setting));

    [Fact]
    public void ThemeIsReadFromTheEnvironment()
    {
        Assert.Equal("police", Bind(("THEME", "police")).ThemeName);
        Assert.Null(Bind().ThemeName);
    }

    [Fact]
    public void ASkinFromTheOtherServerIsCaught()
    {
        Assert.True(Program.SkinAndFactionsDisagree(Skins.Police, FactionRegistry.FalloutSet));
        Assert.True(Program.SkinAndFactionsDisagree(Skins.Fallout, FactionRegistry.Police));
        Assert.False(Program.SkinAndFactionsDisagree(Skins.Police, FactionRegistry.Police));
        Assert.False(Program.SkinAndFactionsDisagree(Skins.Fallout, FactionRegistry.FalloutSet));
    }
}
