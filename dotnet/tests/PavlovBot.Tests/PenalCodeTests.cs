using PavlovBot.Core.Penal;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The charge table and the booking arithmetic.
/// </summary>
/// <remarks>
/// The table was transcribed from the penal code spreadsheet by hand, so unlike the
/// generated version these tests DO check the data - a mistyped bail figure is a player
/// charged the wrong amount, and nobody would notice for weeks.
/// </remarks>
public class PenalCodeTests
{
    [Fact]
    public void TheChargeTableIsComplete()
    {
        Assert.Equal(72, PenalCode.Charges.Count);
        Assert.Equal(8, PenalCode.Sections.Count);
    }

    [Theory]
    [InlineData(100, 8)]
    [InlineData(200, 12)]
    [InlineData(300, 11)]
    [InlineData(400, 6)]
    [InlineData(500, 9)]
    [InlineData(600, 12)]
    [InlineData(700, 8)]
    [InlineData(800, 6)]
    public void EverySectionHasTheRightNumberOfCharges(int section, int count) =>
        Assert.Equal(count, PenalCode.Sections[section].Count);

    [Fact]
    public void NoSectionOverflowsTheChargePicker()
    {
        // Discord caps a select at 25 options. A 26th charge would silently disappear from
        // the menu, and only from the menu - the code would still book fine when typed.
        foreach (var (number, _, count) in PenalCode.SectionList())
            Assert.True(count <= 25, $"section {number} has {count} charges");
    }

    [Theory]
    // One row per section, spot-checked against the sheet.
    [InlineData("PC 100", "Disorderly Conduct", 2, 10)]
    [InlineData("PC 107", "Failure to Obey a Lawful Order", 3, 75)]
    [InlineData("PC 205", "Aggravated Menacing", 6, 500)]
    [InlineData("PC 210", "Homicide", 8, 250)]
    [InlineData("PC 300", "Petty Theft (<$500)", 3, 400)]
    [InlineData("PC 309", "Extortion", 6, 475)]
    [InlineData("PC 403", "Possession of Explosives", 8, 500)]
    [InlineData("PC 506", "Perjury", 6, 325)]
    [InlineData("VC 601", "Reckless Driving", 3, 5)]
    [InlineData("VC 606", "Driving Without a License", 2, 600)]
    public void ChargesMatchTheSheet(string code, string name, int minutes, int bail)
    {
        var charge = PenalCode.Get(code);

        Assert.NotNull(charge);
        Assert.Equal(name, charge!.Name);
        Assert.Equal(minutes, charge.JailMinutes);
        Assert.Equal(bail, charge.Bail);
    }

    [Fact]
    public void CodesAreUniqueAndLookupIsCaseInsensitive()
    {
        // A duplicate code would make one of the two charges unreachable.
        var codes = PenalCode.Charges.Select(c => c.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.NotNull(PenalCode.Get("PC 100"));
        Assert.NotNull(PenalCode.Get("pc 100"));
        Assert.Null(PenalCode.Get("PC 999"));
        Assert.Null(PenalCode.Get(null));
    }

    [Fact]
    public void SectionsAreDerivedFromTheHundredsSeries()
    {
        Assert.Equal(100, PenalCode.SectionOf("PC 104"));
        Assert.Equal(600, PenalCode.SectionOf("VC 604"));
        Assert.Equal(700, PenalCode.SectionOf("PC 707"));
        Assert.Equal(0, PenalCode.SectionOf("no digits here"));
    }

    [Fact]
    public void EverySectionHasATitleAndTheCountsMatch()
    {
        foreach (var (number, title, count) in PenalCode.SectionList())
        {
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.DoesNotContain("Section ", title, StringComparison.Ordinal);   // no fallback titles
            Assert.Equal(PenalCode.Sections[number].Count, count);
        }
    }

    [Fact]
    public void EveryChargeIsInternallyConsistent()
    {
        foreach (var c in PenalCode.Charges)
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.True(c.JailMinutes >= 0);

            /* A figure and a rule cannot disagree. "Fixed with no number" would book at $0
               and read as free; "not bailable, $250" would show a price nobody can pay. */
            if (c.Rule == BailRule.Fixed)
                Assert.True(c.Bail is not null, $"{c.Code} is priced but carries no figure");
            else
                Assert.True(c.Bail is null, $"{c.Code} carries a figure it should not have");

            // Only PC 707 ranges, and a ranging sentence has no fixed minutes to add up.
            if (c.SentenceRanges) Assert.Equal(0, c.JailMinutes);
        }
    }

    // ---- bail rules ----

    [Fact]
    public void NotBailableMeansExactlyThat()
    {
        /* The correction that prompted the rewrite. "N/A" in the bail column was first read
           as "no figure has been chosen, the officer decides" - which would have let
           somebody buy their way out of manslaughter for whatever number was typed. It is
           the opposite: these are the charges you cannot buy your way out of. */
        var booking = PenalCode.Book(["PC 211"]);

        Assert.False(booking.Bailable);
        Assert.Equal("No bail - the sentence must be served", booking.BailLabel());
        Assert.Equal(10, booking.JailMinutes);   // and the sentence is still served
    }

    [Fact]
    public void OneNonBailableChargeMakesTheWholeBookingNonBailable()
    {
        /* Otherwise a player charged with manslaughter and a parking ticket pays the parking
           ticket and walks out of the manslaughter. */
        var booking = PenalCode.Book(["VC 607", "PC 801"]);

        Assert.False(booking.Bailable);
        Assert.Equal("No bail - the sentence must be served", booking.BailLabel());
    }

    [Fact]
    public void ABailableStackStillAddsUp()
    {
        var booking = PenalCode.Book(["PC 100", "PC 200"]);   // 2min/$10 + 4min/$75

        Assert.True(booking.Bailable);
        Assert.Equal(2, booking.Charges.Count);
        Assert.Equal(6, booking.JailMinutes);
        Assert.Equal(85, booking.Bail);
        Assert.Equal("$85", booking.BailLabel());
    }

    [Fact]
    public void BailFromTheAssociatedCrimeSaysSo()
    {
        // PC 700 is the only charge whose price is set by whatever it attaches to.
        var alone = PenalCode.Book(["PC 700"]);
        Assert.True(alone.Bailable);
        Assert.Equal("Based on the associated charge", alone.BailLabel());
        Assert.Equal(3, alone.JailMinutes);      // the sentence is fixed even so

        var withOthers = PenalCode.Book(["PC 100", "PC 700"]);
        Assert.Equal("$10 + based on the associated charge", withOthers.BailLabel());
    }

    [Fact]
    public void ARangingSentenceIsAnnotatedRatherThanGuessed()
    {
        // PC 707 is the charge both axes exist for: the sentence ranges AND there is no bail.
        var booking = PenalCode.Book(["PC 100", "PC 707"]);

        Assert.True(booking.Ranges);
        Assert.False(booking.Bailable);
        Assert.Equal("2 min + ranges with the associated charge", booking.SentenceLabel());
    }

    [Fact]
    public void ARangingChargeAloneHasNoFixedJailTime() =>
        Assert.Equal("ranges with the associated charge", PenalCode.Book(["PC 707"]).SentenceLabel());

    [Fact]
    public void AnInfractionWithNoJailIsStillARealCharge()
    {
        // The fine is the whole penalty, so a $0 total here would make speeding free.
        var booking = PenalCode.Book(["VC 600"]);

        Assert.Equal(0, booking.JailMinutes);
        Assert.Equal(50, booking.Bail);
        Assert.Equal("No jail time", booking.SentenceLabel());
    }

    // ---- arithmetic ----

    [Fact]
    public void UnknownCodesAreSkipped_NeverGuessedAt()
    {
        var booking = PenalCode.Book(["PC 100", "PC 999", "not a code"]);

        Assert.Single(booking.Charges);
        Assert.Equal(10, booking.Bail);
    }

    [Fact]
    public void TheRateScalesBailAndIsRoundedPerCharge()
    {
        /* Per charge, not on the total. Otherwise a booking's bail would not equal the sum
           of the figures shown for its individual charges - a receipt whose lines do not
           add up is worse than no receipt.

           Both charges are $15: per charge that is 23 + 23 = 46, where summing first would
           give 30 * 1.5 = 45. */
        Assert.Equal(46, PenalCode.Book(["PC 101", "PC 102"], 1.5).Bail);
        Assert.Equal(23, PenalCode.Book(["PC 101"], 1.5).Bail);
    }

    [Fact]
    public void ARateOfZeroMakesEverythingFree() =>
        Assert.Equal(0, PenalCode.Book(["PC 100", "PC 200"], 0).Bail);

    [Fact]
    public void AnEmptyBookingIsCoherent()
    {
        var booking = PenalCode.Book([]);

        Assert.Empty(booking.Charges);
        Assert.True(booking.Bailable);
        Assert.Equal("No jail time", booking.SentenceLabel());
        Assert.Equal("$0", booking.BailLabel());
    }

    [Fact]
    public void BailIsFormattedWithSeparators()
    {
        // The label is read by a player, so it uses en-US grouping regardless of the host.
        var big = PenalCode.Book(PenalCode.Charges.Where(c => c.Rule == BailRule.Fixed).Select(c => c.Code), 10);

        Assert.True(big.Bailable);
        Assert.Contains(",", big.BailLabel(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChargeBailAtRespectsNullFigures()
    {
        Assert.Null(PenalCode.Get("PC 801")!.BailAt(2.0));
        Assert.False(PenalCode.Get("PC 801")!.Bailable);

        Assert.Equal(20, PenalCode.Get("PC 100")!.BailAt(2.0));
        Assert.True(PenalCode.Get("PC 100")!.Bailable);
    }
}
