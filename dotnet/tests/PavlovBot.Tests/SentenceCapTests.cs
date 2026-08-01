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
        var uncapped = PenalCode.Book(["PC 100", "PC 200", "PC 300", "PC 400"], capMinutes: 0);
        var capped = PenalCode.Book(["PC 100", "PC 200", "PC 300", "PC 400"], capMinutes: 2);

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
    public void ExecutionIsNotCapped()
    {
        /* It is not a number of minutes, so there is nothing to clamp - capping it would
           silently turn a capital charge into a short sentence. */
        var booking = PenalCode.Book(["PC 210"], capMinutes: 1);

        Assert.True(booking.Execution);
        Assert.False(booking.Capped);
        Assert.Equal("Execution", booking.SentenceLabel());
    }

    [Fact]
    public void TheSentenceLabelSaysWhenItWasCapped()
    {
        Assert.Contains("capped", PenalCode.Book(Stack, capMinutes: 15).SentenceLabel(), StringComparison.Ordinal);
    }

    // ---- the officer's override ----

    [Fact]
    public void AnOfficersTimeReplacesTheComputedOne()
    {
        var booking = ArrestCommand.Override(PenalCode.Book(Stack), minutes: 4, capMinutes: 0);

        Assert.Equal(4, booking.JailMinutes);
    }

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
    public void OverridingAnExecutionDoesNothing()
    {
        // Otherwise a number typed into an unrelated field downgrades a capital charge.
        var booking = ArrestCommand.Override(PenalCode.Book(["PC 210"]), minutes: 2, capMinutes: 0);

        Assert.True(booking.Execution);
        Assert.Equal("Execution", booking.SentenceLabel());
    }

    [Fact]
    public void ANegativeOverrideIsFlooredAtZero()
    {
        var booking = ArrestCommand.Override(PenalCode.Book(Stack), minutes: -5, capMinutes: 0);

        Assert.Equal(0, booking.JailMinutes);
    }
}
