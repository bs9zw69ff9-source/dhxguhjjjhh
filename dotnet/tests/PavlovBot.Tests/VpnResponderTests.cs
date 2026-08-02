using PavlovBot.Core.Vpn;
using PavlovBot.Host.Moderation;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Acting on a VPN verdict, which nothing used to do.
/// </summary>
/// <remarks>
/// The screening ran on every confirmed connection, merged six providers, computed
/// <c>Actionable</c>, logged <c>action=BAN</c> - and no code read it. The pipeline was
/// complete and produced no consequence, so a VPN user connected and played while the log
/// said a ban had been decided on. The same shape of gap that left ban evasion detected but
/// never enforced.
/// </remarks>
public class VpnResponderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static VpnRecord Record(int screenHits, int screenAnswered, int confirmHits, int confirmAnswered,
        string? provider = null) =>
        new()
        {
            Ip = "45.1.1.1",
            Provider = provider,
            Decision = VpnConsensus.Decide(
                new TierOutcome(screenHits, screenAnswered, 0, []),
                confirmAnswered > 0 ? new TierOutcome(confirmHits, confirmAnswered, 0, []) : null,
                confirmAnswered > 0 ? 1 : 0),
            ScreenHits = screenHits,
            ScreenAnswered = screenAnswered,
            ConfirmHits = confirmHits,
            ConfirmAnswered = confirmAnswered,
            CheckedAt = Now,
        };

    // ---- the ban reason ----

    [Fact]
    public void ABanOverAConfirmersObjectionSaysSo()
    {
        /* THE ONE THAT MATTERS. Two screeners agreeing now bans even when the confirmer
           disagreed, so the reason must not claim the confirmation supported it - a ban
           record that misstates its own evidence is the thing an appeal turns on. */
        var label = VpnResponder.Label(Record(2, 4, 0, 1));

        Assert.Contains("the final confirmation disagreed", label, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmation(s) agree", label, StringComparison.Ordinal);
        Assert.Contains("2/5", label, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfirmedBanCitesTheConfirmation()
    {
        var label = VpnResponder.Label(Record(2, 4, 1, 1));

        Assert.Contains("confirmed", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1/1 final confirmation(s) agree", label, StringComparison.Ordinal);
    }

    [Fact]
    public void AConsensusBanWithNoConfirmerSaysThereWasNone()
    {
        var label = VpnResponder.Label(Record(3, 4, 0, 0));

        Assert.Contains("3/4 detectors agree", label, StringComparison.Ordinal);
        Assert.Contains("no confirmation available", label, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProviderBrandIsNamedWhenKnown()
    {
        // "NordVPN" is what a player disputes; "2/5 detectors" is what staff verify.
        Assert.Contains("(NordVPN)", VpnResponder.Label(Record(2, 4, 0, 0, provider: "NordVPN")), StringComparison.Ordinal);
    }

    [Fact]
    public void AProviderNameCannotForgeTheBanReason()
    {
        /* The provider string comes from a third-party API and lands in a ban record and a
           plain-text feed line. A newline in it would let it forge an entire extra line. */
        var label = VpnResponder.Label(Record(2, 4, 0, 0, provider: "Eve\n[VPN BAN] SomebodyElse"));

        Assert.DoesNotContain("\n", label, StringComparison.Ordinal);
    }

    // ---- the shared-exit-node debounce ----

    [Fact]
    public void OneAddressIsNotActionedTwiceInTheWindow()
    {
        /* A VPN exit node is SHARED. Without this, five players behind one commercial VPN
           produce five bans in a minute from one verdict, and a reconnect loop produces
           them indefinitely. */
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var window = TimeSpan.FromHours(1);

        Assert.True(VpnResponder.ShouldAction(seen, "45.1.1.1", Now, window));
        Assert.False(VpnResponder.ShouldAction(seen, "45.1.1.1", Now.AddMinutes(1), window));
        Assert.False(VpnResponder.ShouldAction(seen, "45.1.1.1", Now.AddMinutes(59), window));
    }

    [Fact]
    public void ADifferentAddressIsActionedImmediately()
    {
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var window = TimeSpan.FromHours(1);

        Assert.True(VpnResponder.ShouldAction(seen, "45.1.1.1", Now, window));
        Assert.True(VpnResponder.ShouldAction(seen, "45.1.1.2", Now, window));
    }

    [Fact]
    public void TheSameAddressIsActionableAgainAfterTheWindow()
    {
        // A genuinely different player on that address tomorrow must still be caught.
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var window = TimeSpan.FromHours(1);

        Assert.True(VpnResponder.ShouldAction(seen, "45.1.1.1", Now, window));
        Assert.True(VpnResponder.ShouldAction(seen, "45.1.1.1", Now.AddHours(2), window));
    }

    // ---- what actually reaches a ban ----

    [Fact]
    public void TwoDetectorsMakeAVerdictActionable()
    {
        // The rule the responder acts on, asserted at the point of action.
        Assert.True(Record(2, 4, 0, 0).Decision.Actionable);
        Assert.True(Record(1, 4, 1, 1).Decision.Actionable);
        Assert.True(Record(2, 4, 0, 1).Decision.Actionable);   // confirmer dissented
    }

    [Fact]
    public void OneDetectorNeverIs()
    {
        Assert.False(Record(1, 4, 0, 0).Decision.Actionable);
        Assert.False(Record(1, 4, 0, 1).Decision.Actionable);
        Assert.False(Record(0, 4, 0, 0).Decision.Actionable);
    }
}
