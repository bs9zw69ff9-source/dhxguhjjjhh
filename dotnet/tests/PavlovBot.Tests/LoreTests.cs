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

            Assert.Equal($"{Lore.Rads} Mojave Authority", plain.Footer!.Value.Text);
            Assert.Equal($"{Lore.Rads} Mojave Authority • Updated 14:02 Eastern", withFooter.Footer!.Value.Text);
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

            // Branded twice: the mark and the name each appear once, not twice.
            Assert.Equal($"{Lore.Rads} Mojave Authority • Updated 14:02 Eastern", embed.Footer!.Value.Text);
        }
        finally
        {
            Theme.BrandName = before;
        }
    }

    [Fact]
    public void NoTwoColoursInThePaletteAreTheSame()
    {
        /* THE BAR IS READ BEFORE THE WORDS ARE. Every embed picks its colour by meaning -
           cleared, warned, banned - so two entries landing on the same hue silently merges
           two meanings, and nothing about the code would show it. Cheap to pin, and the sort
           of thing a theme pass breaks. */
        var palette = new Dictionary<string, Discord.Color>
        {
            ["Green"] = Theme.Green,
            ["Amber"] = Theme.Amber,
            ["Gold"] = Theme.Gold,
            ["Blue"] = Theme.Blue,
            ["Sky"] = Theme.Sky,
            ["BanRed"] = Theme.BanRed,
            ["ErrorRed"] = Theme.ErrorRed,
            ["Grey"] = Theme.Grey,
            ["Purple"] = Theme.Purple,
            ["Pink"] = Theme.Pink,
            ["Teal"] = Theme.Teal,
            ["Orange"] = Theme.Orange,
        };

        var duplicates = palette
            .GroupBy(entry => entry.Value.RawValue)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" = ", group.Select(entry => entry.Key)))
            .ToList();

        Assert.Empty(duplicates);
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
