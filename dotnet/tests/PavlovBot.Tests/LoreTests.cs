using Discord;
using Microsoft.Extensions.Configuration;
using PavlovBot.Core.Penal;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The theme: one vocabulary, one stamp, and the currency the server actually uses.
/// </summary>
/// <remarks>
/// These pin the two things a themed bot gets wrong by drifting rather than by breaking -
/// money quoted in two different units on the same screen, and a skin that is documented but
/// never applied.
/// </remarks>
public class LoreTests
{
    private static FeatureOptions Bind(params (string Key, string Value)[] settings) =>
        FeatureOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void MoneyIsCountedInCaps() => Assert.Equal("1,250 caps", Lore.Amount(1250));

    [Fact]
    public void TheGameAndTheBotQuotePricesTheSameWay()
    {
        /* Core cannot see Host, so the bail label formats its own money. Two formatters for
           one currency is how a booking ends up quoting dollars next to a balance in caps. */
        var booking = PenalCode.Book(["PC 100"], 1.0);

        Assert.Equal(Lore.Amount(booking.Bail), booking.BailLabel());
    }

    [Fact]
    public void EveryEmbedCarriesTheBrand()
    {
        var before = Theme.BrandName;
        try
        {
            Theme.BrandName = "Mojave Authority";

            var plain = new EmbedBuilder().WithTitle("x").Brand().Build();
            var withFooter = new EmbedBuilder().WithTitle("x").Brand("Updated 14:02 Eastern").Build();

            Assert.Equal("Mojave Authority", plain.Footer!.Value.Text);
            Assert.Equal("Mojave Authority • Updated 14:02 Eastern", withFooter.Footer!.Value.Text);
        }
        finally
        {
            Theme.BrandName = before;
        }
    }

    [Fact]
    public void BrandingTwiceDoesNotStackTheNameUp()
    {
        // Theme.Base() brands, and most call sites brand again with a footer of their own.
        var before = Theme.BrandName;
        try
        {
            Theme.BrandName = "Mojave Authority";

            var embed = Theme.Notice("Title", "body").Brand("Updated 14:02 Eastern").Build();

            Assert.Equal("Mojave Authority • Updated 14:02 Eastern", embed.Footer!.Value.Text);
        }
        finally
        {
            Theme.BrandName = before;
        }
    }

    [Fact]
    public void TheSkinIsReadFromTheEnvironment()
    {
        /* BOT_NAME has been documented as "stamped on every embed" since the port and was
           read by nothing at all - a skin that silently did nothing. */
        Assert.Equal("Vault 21 Security", Bind(("BOT_NAME", "Vault 21 Security")).BotName);
        Assert.Null(Bind().BotName);
    }
}
