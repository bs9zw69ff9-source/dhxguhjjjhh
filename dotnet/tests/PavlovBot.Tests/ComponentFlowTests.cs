using PavlovBot.Core.Moderation;
using PavlovBot.Core.Penal;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The two component flows the port left behind: arrest booking and the menu panel.
/// </summary>
/// <remarks>
/// Their Discord halves need a live gateway. What is testable here is the routing every
/// click depends on, and the decisions underneath - which is where the rules that matter
/// actually live.
/// </remarks>
public class ComponentFlowTests
{
    [Fact]
    public void EveryArrestControlRoundTripsItsCustomId()
    {
        foreach (var step in new[] { "section", "charge", "confirm", "cancel" })
        {
            var id = ComponentId.Parse(ComponentId.Encode(ArrestBooking.Id, step));
            Assert.Equal(ArrestBooking.Id, id.Prefix);
            Assert.Equal(step, id.Argument(0));
        }
    }

    [Fact]
    public void TheArrestAndMenuPrefixesDoNotCollide()
    {
        /* Dispatch is by prefix. Two handlers answering to one prefix would route a click
           to whichever the container resolved first - a menu claim landing in the arrest
           booking, silently. */
        Assert.NotEqual(ArrestBooking.Id, MenuPanel.Id);
        Assert.NotEqual(ArrestBooking.Id, Paged.Id);
        Assert.NotEqual(MenuPanel.Id, Paged.Id);
        Assert.NotEqual(MenuPanel.Id, ConfigPanel.Id);
    }

    [Fact]
    public void EverySectionFitsInsideASelectMenu()
    {
        /* Discord allows 25 options per select. A section that outgrew that would render a
           truncated charge list, so an officer would simply never see the last charges -
           with nothing anywhere reporting it. */
        foreach (var (number, _, count) in PenalCode.SectionList())
            Assert.True(count <= 25, $"section {number} has {count} charges");
    }

    [Fact]
    public void EveryChargeLabelFitsDiscordsLimit()
    {
        // A select option label is capped at 100 characters; over that, Discord rejects the
        // whole message and the picker never appears.
        foreach (var charge in PenalCode.Charges)
        {
            Assert.True($"{charge.Code} {charge.Name}".Length <= 100, charge.Code);
            Assert.True(charge.Code.Length <= 100, charge.Code);
        }
    }

    [Fact]
    public void BookingTheSameChargeTwiceIsNotDoubleJail()
    {
        /* The picker deduplicates, because a select does not remember what is already on the
           booking - re-opening a section and picking the same charge is the normal way this
           happens, and it must not double somebody's sentence. */
        var once = PenalCode.Book(["PC 100"]);
        var twice = PenalCode.Book(["PC 100", "PC 100"]);

        Assert.Equal(once.JailMinutes * 2, twice.JailMinutes);   // the raw call DOES double
        Assert.Single(once.Charges);

        // ... which is exactly why ArrestBooking filters before it ever calls Book.
        var deduped = new List<string>();
        foreach (var code in new[] { "PC 100", "PC 100" })
            if (!deduped.Contains(code, StringComparer.Ordinal)) deduped.Add(code);

        Assert.Equal(once.JailMinutes, PenalCode.Book(deduped).JailMinutes);
    }

    [Fact]
    public void AnEmptyBookingCostsNothing()
    {
        // The confirm button is disabled at zero charges; this is the belt to that brace.
        var booking = PenalCode.Book([]);

        Assert.Empty(booking.Charges);
        Assert.Equal(0, booking.JailMinutes);
        Assert.Equal(0, booking.Bail);
    }

    [Fact]
    public void ReleasingAMenuKeepsTheNameBinding()
    {
        /* The rule the whole self-serve panel rests on. If releasing freed the name too,
           somebody could strip their menu and re-claim it under an alt - which is the exact
           thing the binding exists to prevent. */
        var release = MenuLink.Decide("Alice", "Alice", ownerId: "111", selfId: "111", holdsMenu: true);
        Assert.Equal(MenuClaimAction.Release, release.Action);

        // Still bound afterwards: a different name is refused whether or not they hold one.
        var afterRelease = MenuLink.Decide("Alice", "AliceAlt", ownerId: null, selfId: "111", holdsMenu: false);
        Assert.Equal(MenuClaimAction.Locked, afterRelease.Action);
    }

    [Fact]
    public void ANameOwnedBySomebodyElseIsRefused()
    {
        var decision = MenuLink.Decide(boundName: null, "Alice", ownerId: "999", selfId: "111", holdsMenu: false);

        Assert.Equal(MenuClaimAction.Taken, decision.Action);
        Assert.Equal("999", decision.OwnerId);
    }

    [Fact]
    public void AFirstClaimIsGranted()
    {
        var decision = MenuLink.Decide(boundName: null, "Alice", ownerId: null, selfId: "111", holdsMenu: false);

        Assert.Equal(MenuClaimAction.Grant, decision.Action);
    }
}
