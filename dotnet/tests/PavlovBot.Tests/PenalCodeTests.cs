using PavlovBot.Core.Penal;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Ported from test/penal.test.js.
///
/// The charge table itself was generated from the running JS rather than retyped, so
/// these tests are about the ARITHMETIC and the edge cases - the parts a generator
/// cannot get right for you.
/// </summary>
public class PenalCodeTests
{
    [Fact]
    public void TheChargeTableIsComplete()
    {
        Assert.Equal(65, PenalCode.Charges.Count);
        Assert.Equal(8, PenalCode.Sections.Count);
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
            // A charge either has a bail figure or a reason it does not.
            Assert.True(c.Bail is not null || c.Special is not null,
                $"{c.Code} has neither a bail figure nor a special case");

            /* Execution and Variable carry no jail timer - one is not a sentence in minutes,
               the other depends entirely on an associated crime. NoFixedBail is different:
               the SENTENCE is fixed and only the price is open, so it keeps its minutes. */
            if (c.Special is ChargeSpecial.Execution or ChargeSpecial.Variable)
                Assert.Equal(0, c.JailMinutes);

            if (c.Special is ChargeSpecial.NoFixedBail)
            {
                Assert.Null(c.Bail);
                Assert.True(c.JailMinutes > 0, $"{c.Code} is unpriced but carries no sentence either");
            }
        }
    }

    [Fact]
    public void BookingSumsJailAndBail()
    {
        var booking = PenalCode.Book(["PC 100", "PC 200"]);   // 2min/$25 + 4min/$75
        Assert.Equal(2, booking.Charges.Count);
        Assert.Equal(6, booking.JailMinutes);
        Assert.Equal(100, booking.Bail);
    }

    [Fact]
    public void UnknownCodesAreSkipped_NeverGuessedAt()
    {
        var booking = PenalCode.Book(["PC 100", "PC 999", "not a code"]);
        Assert.Single(booking.Charges);
        Assert.Equal(25, booking.Bail);
    }

    [Fact]
    public void TheRateScalesBailAndIsRoundedPerCharge()
    {
        /* Per charge, not on the total. Otherwise a booking's bail would not equal the sum
           of the figures shown for its individual charges - a receipt whose lines do not
           add up is worse than no receipt. */
        var single = PenalCode.Book(["PC 101"], 1.5);        // 10 -> 15
        Assert.Equal(15, single.Bail);

        // 25*1.5=37.5 -> 38, and 15*1.5=22.5 -> 23. Summing first would give 60 -> 60.
        var pair = PenalCode.Book(["PC 100", "PC 102"], 1.5);
        Assert.Equal(38 + 23, pair.Bail);
    }

    [Fact]
    public void ARateOfZeroMakesEverythingFree()
    {
        Assert.Equal(0, PenalCode.Book(["PC 100", "PC 200"], 0).Bail);
    }

    [Fact]
    public void AnExecutionChargeHasNoJailAndNoBail()
    {
        var booking = PenalCode.Book(["PC 210"]);
        Assert.True(booking.Execution);
        Assert.Equal("Execution", booking.SentenceLabel());
        Assert.Equal("No bail (execution)", booking.BailLabel());
    }

    [Fact]
    public void ExecutionOverridesEverythingElseInTheBooking()
    {
        // Charged alongside ordinary offences, the death penalty is still the sentence.
        var booking = PenalCode.Book(["PC 100", "PC 210", "PC 200"]);
        Assert.Equal("Execution", booking.SentenceLabel());
        Assert.Equal("No bail (execution)", booking.BailLabel());
        Assert.Equal(6, booking.JailMinutes);   // the underlying sum is still available
    }

    [Fact]
    public void AVariableChargeAnnotatesRatherThanReplaces()
    {
        var booking = PenalCode.Book(["PC 100", "PC 707"]);
        Assert.True(booking.Variable);
        Assert.Equal("2 min + based on the associated charge", booking.SentenceLabel());
        Assert.Equal("Based on the associated charge", booking.BailLabel());
    }

    [Fact]
    public void AVariableChargeAloneHasNoFixedJailTime()
    {
        var booking = PenalCode.Book(["PC 707"]);
        Assert.Equal("based on the associated charge", booking.SentenceLabel());
    }

    [Fact]
    public void AnEmptyBookingIsCoherent()
    {
        var booking = PenalCode.Book([]);
        Assert.Empty(booking.Charges);
        Assert.Equal("No jail time", booking.SentenceLabel());
        Assert.Equal("$0", booking.BailLabel());
    }

    [Fact]
    public void BailIsFormattedWithSeparators()
    {
        // The label is read by a player, so it uses en-US grouping regardless of the host.
        var booking = PenalCode.Book(["PC 210"], 1);
        Assert.Equal("No bail (execution)", booking.BailLabel());

        var big = PenalCode.Book(PenalCode.Charges.Where(c => c.Bail is not null).Select(c => c.Code), 10);
        Assert.Contains(",", big.BailLabel(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChargeBailAtRespectsNullFigures()
    {
        var execution = PenalCode.Get("PC 210")!;
        Assert.Null(execution.BailAt(2.0));

        var ordinary = PenalCode.Get("PC 100")!;
        Assert.Equal(50, ordinary.BailAt(2.0));
    }
}
