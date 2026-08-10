using System.Text.Json;
using PavlovBot.Core.Intelligence;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// What a moderator without network access may see of a player profile.
/// </summary>
/// <remarks>
/// AN ADDRESS POSTED TO A CHANNEL CANNOT BE UN-POSTED. Deleting the message does not unsee
/// it, and this is the one failure in the profile system with no recovery - which is why
/// redaction is a pure function over the whole profile rather than a check at each field.
/// A check per field is a check somebody forgets the day they add a field, and nothing fails
/// when they do.
///
/// The central test here is <see cref="ARedactedProfileContainsNoAddressAnywhere"/>, which
/// serialises the whole object and searches it. That is deliberately blunt: it does not care
/// which field an address ended up in, so it catches the field that has not been written yet.
/// </remarks>
public class ProfileRedactionTests
{
    private const string Address = "203.0.113.77";
    private const string OtherAddress = "198.51.100.4";

    private static PlayerProfile Full() => new(
        new ProfileIdentity("Alice", "0002abc", ["OldAlice"]),
        new ProfileActivity(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, 120, false, []),
        new ProfileNetwork([Address], [OtherAddress], Vpn: true, Proxy: false, Asn: "AS15169", Country: "US"),
        new ProfileModeration("griefing", null, true, 2, 5, 3, ["ban by mod"]),
        new ProfileFaction("NYPD", "Sergeant", 123456789012345678UL),
        new ProfileEconomy(5000, 250, 10_000),
        [new ProfileAssociation("Bob", "0002def", "shared a confirmed address", Banned: true)],
        RiskScorer.Assess(
        [
            new RiskSignal(RiskSignalKind.SharedAddressWithBanned,
                $"Shares {Address} with banned player Bob.", Address, Sensitive: true),
            new RiskSignal(RiskSignalKind.ActiveWarnings, "2 active warnings.", null),
        ]));

    /// <summary>
    /// THE PROPERTY THAT MATTERS. No address survives redaction, in any field.
    /// </summary>
    /// <remarks>
    /// Asserted over the SERIALISED profile rather than field by field, so a field added later
    /// that happens to carry an address fails this without anybody remembering to extend the
    /// test. That is the whole reason it is written this way.
    /// </remarks>
    [Fact]
    public void ARedactedProfileContainsNoAddressAnywhere()
    {
        var redacted = ProfileRedaction.For(Full(), ProfileVisibility.Moderation);

        var serialised = JsonSerializer.Serialize(redacted);

        Assert.DoesNotContain(Address, serialised, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherAddress, serialised, StringComparison.Ordinal);
    }

    /// <summary>Associations go entirely, because the link itself is the address.</summary>
    /// <remarks>
    /// "These two accounts are connected" leaks the address relationship even with no address
    /// printed - it says somebody shares a household with a banned player, which is exactly
    /// the inference the access level exists to control.
    /// </remarks>
    [Fact]
    public void AssociationsAreWithheldEntirely()
    {
        var redacted = ProfileRedaction.For(Full(), ProfileVisibility.Moderation);

        Assert.Empty(redacted.Associations);
    }

    /// <summary>
    /// The VPN verdict survives. The address behind it does not.
    /// </summary>
    /// <remarks>
    /// If redaction stripped the verdicts too it would make the feature useless to the people
    /// who use it most, and they would go and ask an admin for the address instead - which is
    /// a worse outcome than showing them the one bit they actually need.
    /// </remarks>
    [Fact]
    public void ModerationRelevantVerdictsSurvive()
    {
        var redacted = ProfileRedaction.For(Full(), ProfileVisibility.Moderation);

        Assert.True(redacted.Network.Vpn);
        Assert.False(redacted.Network.Proxy);
        Assert.Equal("US", redacted.Network.Country);
        Assert.Empty(redacted.Network.ConfirmedIps);
    }

    /// <summary>
    /// The score is the same number for both viewers.
    /// </summary>
    /// <remarks>
    /// Dropping sensitive signals rather than narrowing them would give two moderators
    /// different scores for one player, and neither would know why theirs disagreed. The
    /// reason stays visible; only the evidence string is replaced.
    /// </remarks>
    [Fact]
    public void TheScoreDoesNotChangeWithTheViewer()
    {
        var full = Full();
        var redacted = ProfileRedaction.For(full, ProfileVisibility.Moderation);

        Assert.Equal(full.Risk.Score, redacted.Risk.Score);
        Assert.Equal(full.Risk.Confidence, redacted.Risk.Confidence);
        Assert.Equal(full.Risk.Signals.Count, redacted.Risk.Signals.Count);
    }

    /// <summary>A sensitive signal keeps its claim and loses its evidence.</summary>
    [Fact]
    public void ASensitiveSignalKeepsItsReasonAndLosesItsEvidence()
    {
        var redacted = ProfileRedaction.For(Full(), ProfileVisibility.Moderation);

        var signal = redacted.Risk.Signals.First(s => s.Sensitive);

        /* THE SENTENCE SURVIVES, THE ADDRESS DOES NOT. The fixture deliberately embeds an
           address in the SUMMARY rather than only in the evidence field, because that is the
           leak the first version of this had: it cleared Evidence and printed the summary
           verbatim. */
        Assert.Contains("banned player Bob", signal.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Address, signal.Summary, StringComparison.Ordinal);
        Assert.Contains(Addresses.Placeholder, signal.Summary, StringComparison.Ordinal);
        Assert.Equal("withheld - needs network access", signal.Evidence);
    }

    /// <summary>
    /// The viewer is TOLD something was withheld.
    /// </summary>
    /// <remarks>
    /// A silently emptied network section reads as "clean", which is the opposite of what it
    /// means. The flag is what lets the reply say "this exists and you cannot see it".
    /// </remarks>
    [Fact]
    public void WithholdingIsDeclaredRatherThanSilent()
    {
        Assert.True(ProfileRedaction.For(Full(), ProfileVisibility.Moderation).Redacted);
    }

    /// <summary>A player with no network data at all does not claim anything was withheld.</summary>
    /// <remarks>
    /// The control. Flagging every profile as redacted would satisfy the test above and make
    /// the notice meaningless - staff would learn to ignore it, which is worse than not
    /// showing it.
    /// </remarks>
    [Fact]
    public void NothingToWithholdIsNotReportedAsWithheld()
    {
        var bare = Full() with
        {
            Network = ProfileNetwork.Empty,
            Associations = [],
            Risk = RiskScorer.Assess([new RiskSignal(RiskSignalKind.ActiveWarnings, "2 warnings.", null)]),
        };

        Assert.False(ProfileRedaction.For(bare, ProfileVisibility.Moderation).Redacted);
    }

    /// <summary>Full access is untouched, field for field.</summary>
    [Fact]
    public void NetworkAccessSeesEverything()
    {
        var full = Full();

        Assert.Same(full, ProfileRedaction.For(full, ProfileVisibility.Network));
    }

    [Fact]
    public void VisibilityFollowsTheAccessCheck()
    {
        Assert.Equal(ProfileVisibility.Network, ProfileRedaction.VisibilityFor(hasNetworkAccess: true));
        Assert.Equal(ProfileVisibility.Moderation, ProfileRedaction.VisibilityFor(hasNetworkAccess: false));
    }

    /// <summary>Moderation history is NOT network data and survives redaction.</summary>
    /// <remarks>
    /// The other half of getting the split right: redaction that also hid bans and warnings
    /// would take the profile away from the people it was built for.
    /// </remarks>
    [Fact]
    public void ModerationAndFactionAndEconomyAreNotRedacted()
    {
        var full = Full();
        var redacted = ProfileRedaction.For(full, ProfileVisibility.Moderation);

        Assert.Equal(full.Moderation, redacted.Moderation);
        Assert.Equal(full.Faction, redacted.Faction);
        Assert.Equal(full.Economy, redacted.Economy);
        Assert.Equal(full.Identity, redacted.Identity);
    }
}
