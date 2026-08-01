using PavlovBot.Core.Penal;
using PavlovBot.Host.Discord.Commands;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The sentence cap, and an officer's right to set the time themselves.
/// </summary>
/// <remarks>
/// The rule: BAIL STACKS WITHOUT LIMIT, JAIL DOES NOT. Stacking eight charges should cost
/// eight charges' worth of money - that is the deterrent - but should not put somebody in a
/// cell for half an hour, because a sentence nobody sits through is one they disconnect
/// through instead.
/// </remarks>
public class SentenceCapTests
{
    // 8 + 6 + 5 + 3 = 22 minutes uncapped.
    private static readonly string[] Stack = ["PC 403", "PC 302", "PC 301", "PC 300"];

    [Fact]
    public void WithNoCapNothingChanges()
    {
        // Zero is the default, so existing servers see no change to any sentence.
        var booking = PenalCode.Book(Stack, capMinutes: 0);

        Assert.Equal(22, booking.JailMinutes);
        Assert.False(booking.Capped);
    }

    [Fact]
    public void StackedJailIsClampedToTheCap()
    {
        var booking = PenalCode.Book(Stack, capMinutes: 15);

        Assert.Equal(15, booking.JailMinutes);
        Assert.True(booking.Capped);
        Assert.Equal(15, booking.CapMinutes);
    }

    [Fact]
    public void BailIsNeverCapped()
    {
        /* The whole point of the split. If bail were capped too, stacking charges would
           cost nothing extra and there would be no deterrent left. */
        var uncapped = PenalCode.Book(Stack, capMinutes: 0);
        var capped = PenalCode.Book(Stack, capMinutes: 2);

        Assert.Equal(uncapped.Bail, capped.Bail);
        Assert.True(capped.Bail > 0);
        Assert.Equal(2, capped.JailMinutes);
    }

    [Fact]
    public void ABookingUnderTheCapIsNotMarkedCapped()
    {
        // "(capped)" on a sentence the cap did not touch would be a lie on the receipt.
        var booking = PenalCode.Book(["PC 100"], capMinutes: 60);

        Assert.False(booking.Capped);
        Assert.Null(booking.CapMinutes);
        Assert.DoesNotContain("capped", booking.SentenceLabel(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSentenceLabelSaysWhenItWasCapped() =>
        Assert.Contains("capped", PenalCode.Book(Stack, capMinutes: 15).SentenceLabel(), StringComparison.Ordinal);

    [Fact]
    public void CappingDoesNotMakeANonBailableBookingBailable()
    {
        /* The two rules are independent, and a shorter sentence is not a decision that the
           charge has become payable. */
        var booking = PenalCode.Book(["PC 211", "PC 403"], capMinutes: 5);

        Assert.Equal(5, booking.JailMinutes);
        Assert.False(booking.Bailable);
    }

    // ---- the officer's override ----

    [Fact]
    public void AnOfficersTimeReplacesTheComputedOne() =>
        Assert.Equal(4, ArrestCommand.Override(PenalCode.Book(Stack), minutes: 4, capMinutes: 0).JailMinutes);

    [Fact]
    public void TheOverrideLeavesBailAlone()
    {
        // "Bail can automatically stack" - choosing a time must not discount the fine.
        var computed = PenalCode.Book(Stack);
        var overridden = ArrestCommand.Override(computed, minutes: 1, capMinutes: 0);

        Assert.Equal(computed.Bail, overridden.Bail);
        Assert.Equal(computed.Charges.Count, overridden.Charges.Count);
    }

    [Fact]
    public void TheOverrideDoesNotMakeANonBailableBookingBailable()
    {
        var overridden = ArrestCommand.Override(PenalCode.Book(["PC 801"]), minutes: 1, capMinutes: 0);

        Assert.False(overridden.Bailable);
        Assert.Equal("No bail - the sentence must be served", overridden.BailLabel());
    }

    [Fact]
    public void AnOverrideIsItselfHeldToTheCap()
    {
        /* The cap is a maximum sentence, not a tie-breaker between two ways of arriving at
           one. An officer wanting longer needs the cap raised, which is a deliberate act by
           somebody who can change policy. */
        var booking = ArrestCommand.Override(PenalCode.Book(Stack, capMinutes: 15), minutes: 99, capMinutes: 15);

        Assert.Equal(15, booking.JailMinutes);
        Assert.True(booking.Capped);
    }

    [Fact]
    public void ANegativeOverrideIsFlooredAtZero() =>
        Assert.Equal(0, ArrestCommand.Override(PenalCode.Book(Stack), minutes: -5, capMinutes: 0).JailMinutes);

    // ---- controlled substances ----

    [Fact]
    public void TheControlledSubstancesSectionExists()
    {
        Assert.True(PenalCode.Sections.ContainsKey(800));
        Assert.Equal(6, PenalCode.Sections[800].Count);
        Assert.Equal("Controlled Substances", PenalCode.SectionTitles[800]);
    }

    [Theory]
    [InlineData("PC 801", 3)]
    [InlineData("PC 802", 5)]
    [InlineData("PC 803", 6)]
    [InlineData("PC 804", 8)]
    [InlineData("PC 805", 3)]
    [InlineData("PC 806", 4)]
    public void EveryControlledSubstanceChargeIsServedNotPaid(string code, int minutes)
    {
        /* No bail anywhere in this section, and a real sentence in every row - so the time
           must survive both the bail rule and the cap logic. */
        var charge = PenalCode.Get(code);

        Assert.NotNull(charge);
        Assert.Equal(minutes, charge!.JailMinutes);
        Assert.Equal(BailRule.None, charge.Rule);
        Assert.Null(charge.BailAt(1.0));
    }

    [Fact]
    public void ADrugStackStillServesItsTime()
    {
        var booking = PenalCode.Book(["PC 804", "PC 803"]);

        Assert.Equal(14, booking.JailMinutes);
        Assert.Equal(0, booking.Bail);
        Assert.False(booking.Bailable);
    }

    // ---- the receipt ----

    [Fact]
    public void TheReceiptNamesThePriceOrSaysThereIsNone()
    {
        /* A blank price column is indistinguishable from a charge that costs nothing, and
           the difference between "free" and "you cannot pay this" is the whole point. */
        Assert.Equal("2 min, $10", ArrestCommand.ChargeLine(PenalCode.Get("PC 100")!, 1.0));
        Assert.Equal("3 min, no bail", ArrestCommand.ChargeLine(PenalCode.Get("PC 801")!, 1.0));
        Assert.Equal("no jail, $50", ArrestCommand.ChargeLine(PenalCode.Get("VC 600")!, 1.0));
        Assert.Equal("3 min, bail from the associated charge", ArrestCommand.ChargeLine(PenalCode.Get("PC 700")!, 1.0));
        Assert.Equal("ranges with the associated charge, no bail", ArrestCommand.ChargeLine(PenalCode.Get("PC 707")!, 1.0));
    }

    [Fact]
    public void TheReceiptShowsTheScaledPrice() =>
        Assert.Equal("2 min, $20", ArrestCommand.ChargeLine(PenalCode.Get("PC 100")!, 2.0));
}
