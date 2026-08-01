using PavlovBot.Core.Verification;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Verification;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The verification flow's decisions, minus Discord.
/// </summary>
/// <remarks>
/// The gateway parts - showing a modal, granting a role - cannot be tested without a live
/// connection. What CAN be pinned is the custom-id encoding the whole flow routes on, and
/// the conflict re-check at decision time, which is the part with a race in it.
/// </remarks>
public class VerificationServiceTests
{
    [Fact]
    public void EveryStepOfTheFlowRoundTripsItsCustomId()
    {
        /* If any of these stops parsing, the click goes nowhere and the member sees a
           button that does nothing - with no error anywhere, because an unrouted component
           is not an exception. */
        var start = ComponentId.Parse(ComponentId.Encode(VerificationService.Id, "start"));
        Assert.Equal(VerificationService.Id, start.Prefix);
        Assert.Equal("start", start.Argument(0));

        var submit = ComponentId.Parse(ComponentId.Encode(VerificationService.Id, "submit"));
        Assert.Equal("submit", submit.Argument(0));

        var accept = ComponentId.Parse(ComponentId.Encode(VerificationService.Id, "ok", "a1b2c3d4e5f6a7b8"));
        Assert.Equal("ok", accept.Argument(0));
        Assert.Equal("a1b2c3d4e5f6a7b8", accept.Argument(1));
    }

    [Fact]
    public void ADecisionIdFitsInsideDiscordsLimit()
    {
        /* The reason pending requests are keyed by a TOKEN rather than carrying the name:
           a 64-character Pavlov name plus the prefix would exceed 100 and be truncated,
           routing the accept button to nothing. */
        var id = ComponentId.Encode(VerificationService.Id, "ok", "a1b2c3d4e5f6a7b8");

        Assert.True(id.Length <= ComponentId.MaxLength, $"decision id is {id.Length} characters");
    }

    [Fact]
    public void CarryingTheNameAndTheRequesterWouldNotFit()
    {
        /* The constraint the token design exists for, demonstrated rather than asserted in
           a comment. A 64-character Pavlov name plus the requester's Discord id plus the
           prefix is 111 characters - past Discord's 100 - and ComponentId refuses rather
           than letting it truncate into an id that routes nowhere. */
        var longName = new string('x', 64);

        Assert.Throws<ArgumentException>(() =>
            ComponentId.Encode(VerificationService.Id, "ok", longName, "76561198000000001", "123456789012345678"));

        // The token form of the same decision is comfortably inside the limit.
        Assert.True(ComponentId.Encode(VerificationService.Id, "ok", "a1b2c3d4e5f6a7b8").Length < 30);
    }

    [Fact]
    public void AnAlreadyVerifiedAccountIsRefusedBeforeStaffSeeIt()
    {
        var store = new Dictionary<string, VerificationRecord>(StringComparer.Ordinal)
        {
            ["111"] = new("Alice", ["203.0.113.9"]),
        };

        var verdict = VerificationRules.Check(store, "111", "Bob", []);

        Assert.Equal(VerificationConflict.AlreadyVerified, verdict.Conflict);
    }

    [Fact]
    public void TheNameIsRecheckedAtDecisionTime()
    {
        /* The race the re-check exists for: two members request the same name, staff
           approve the first, and the second's card is still sitting there with an Accept
           button. Without re-checking at decision time the second approval would overwrite
           the first. */
        var atRequestTime = new Dictionary<string, VerificationRecord>(StringComparer.Ordinal);
        Assert.True(VerificationRules.Check(atRequestTime, "222", "Alice", []).IsAllowed);

        var atDecisionTime = new Dictionary<string, VerificationRecord>(StringComparer.Ordinal)
        {
            ["111"] = new("Alice", ["203.0.113.9"]),
        };
        Assert.Equal(VerificationConflict.NameTaken,
            VerificationRules.Check(atDecisionTime, "222", "Alice", []).Conflict);
    }

    [Fact]
    public void AMemberWithNoConfirmedAddressIsAllowedThrough()
    {
        /* Somebody who joined Discord before ever playing has no address on record. That is
           the normal first-time case, not a conflict - the request goes to staff with "none
           on record" said plainly on the card. */
        var verdict = VerificationRules.Check(
            new Dictionary<string, VerificationRecord>(StringComparer.Ordinal), "333", "NewPlayer", []);

        Assert.True(verdict.IsAllowed);
    }
}
