using PavlovBot.Core.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// A second faction that does not exist in the registry.
/// </summary>
/// <remarks>
/// These tests are about the RULES - rank caps, one-faction-per-player, subclasses - not
/// about which factions the server happens to run. They used to reach for Gambino, so
/// removing it broke seven of them for no reason connected to what they assert. A local
/// definition keeps them testing the rule and stops the next roster change from breaking
/// them again.
/// </remarks>
internal static class TestFactions
{
    public static readonly FactionDefinition Other = new()
    {
        Name = "Other",
        Order = ["Associate", "Soldier", "Capo", "Consigliere", "Underboss", "Boss"],
        Default = "Associate",
        RankFiles = new Dictionary<string, string>
        {
            ["Associate"] = "otherassociate.txt", ["Soldier"] = "othersoldier.txt",
            ["Capo"] = "othercapo.txt", ["Consigliere"] = "otherconsigliere.txt",
            ["Underboss"] = "otherunderboss.txt", ["Boss"] = "otherboss.txt",
        },
        // Consigliere is deliberately absent: it is the one uncapped rank here.
        RankCaps = new Dictionary<string, int>
        {
            ["Associate"] = 18, ["Soldier"] = 12, ["Capo"] = 3, ["Underboss"] = 1, ["Boss"] = 1,
        },
    };
}


/// <summary>
/// Ported from test/ranks.test.js and test/rank-flow.test.js.
///
/// These rules exist because the storage cannot express them: the roster files are plain
/// text the game reads live, and nothing stops a name appearing in six of them at once.
/// Every assertion here is a constraint that has no other enforcement point.
/// </summary>
public class FactionRegistryTests
{
    [Fact]
    public void EveryFactionIsInternallyConsistent()
    {
        /* The invariant that keeps the "ranks are data" design honest: every rank needs a
           roster file, the default must be a real rank, and a cap must not name a rank
           that does not exist. A typo here would fail silently at write time. */
        foreach (var faction in FactionRegistry.All.Values)
        {
            Assert.NotEmpty(faction.Order);
            Assert.Contains(faction.Default, faction.Order);

            foreach (var rank in faction.Order)
                Assert.True(faction.RankFiles.ContainsKey(rank), $"{faction.Name}/{rank} has no roster file");

            foreach (var capped in faction.RankCaps.Keys)
                Assert.Contains(capped, faction.Order);
        }
    }

    [Fact]
    public void RosterFilenamesAreGloballyUnique()
    {
        // Two ranks sharing a file would silently merge their rosters in-game.
        var files = FactionRegistry.All.Values
            .SelectMany(f => f.RankFiles.Values.Concat(f.Subclasses.Values))
            .ToList();

        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void OrderIsLowestToHighest()
    {
        var nypd = FactionRegistry.Get("NYPD")!;
        Assert.Equal("Cadet", nypd.Lowest);
        Assert.Equal("Chief of Police", nypd.Highest);
        Assert.True(nypd.IndexOf("Sergeant") > nypd.IndexOf("Patrolman"));
    }

    [Fact]
    public void CapsMatchTheConfiguredLimits()
    {
        var nypd = FactionRegistry.Get("NYPD")!;
        Assert.Equal(1, nypd.CapFor("Chief of Police"));
        Assert.Equal(4, nypd.CapFor("Captain"));
        Assert.Equal(50, nypd.CapFor("Cadet"));

        var gambino = TestFactions.Other;
        Assert.Equal(1, gambino.CapFor("Boss"));
        Assert.Equal(3, gambino.CapFor("Capo"));
        // Consigliere is deliberately uncapped.
        Assert.Equal(int.MaxValue, gambino.CapFor("Consigliere"));
    }

    [Fact]
    public void FactionLookupIsCaseInsensitive()
    {
        Assert.NotNull(FactionRegistry.Get("nypd"));
        Assert.NotNull(FactionRegistry.Get("NYPD"));
        Assert.Null(FactionRegistry.Get("Yakuza"));
        Assert.Null(FactionRegistry.Get(null));
    }

    [Fact]
    public void OnlyNypdHasSubclasses()
    {
        Assert.True(FactionRegistry.Get("NYPD")!.HasSubclass("Tactical Response Unit"));
        Assert.Empty(TestFactions.Other.Subclasses);
    }
}

public class MembershipRulesTests
{
    private static readonly FactionDefinition Nypd = FactionRegistry.Get("NYPD")!;
    private static readonly FactionDefinition Gambino = TestFactions.Other;
    private static Func<string, int> Empty => _ => 0;
    private static Func<string, int> Full(string rank, int n) => r => r == rank ? n : 0;

    [Fact]
    public void PromotionMovesOneStepUp()
    {
        var d = MembershipRules.ChangeRank(Nypd, "Patrolman", +1, Empty);
        Assert.True(d.IsAllowed);
        Assert.Equal("Corporal", d.Rank);
    }

    [Fact]
    public void DemotionMovesOneStepDown()
    {
        var d = MembershipRules.ChangeRank(Nypd, "Corporal", -1, Empty);
        Assert.True(d.IsAllowed);
        Assert.Equal("Patrolman", d.Rank);
    }

    [Fact]
    public void TheLadderEndsAreRefusedRatherThanWrappingAround()
    {
        Assert.Equal(MembershipOutcome.AlreadyLowest, MembershipRules.ChangeRank(Nypd, "Cadet", -1, Empty).Outcome);
        Assert.Equal(MembershipOutcome.AlreadyHighest, MembershipRules.ChangeRank(Nypd, "Chief of Police", +1, Empty).Outcome);
    }

    [Fact]
    public void PromotionIntoAFullRankIsRefused()
    {
        // There is one Chief. The rule is the only thing preventing a second.
        var d = MembershipRules.ChangeRank(Nypd, "Deputy Chief", +1, Full("Chief of Police", 1));
        Assert.Equal(MembershipOutcome.RankFull, d.Outcome);
        Assert.Equal("Chief of Police", d.Rank);
        Assert.Equal(1, d.Cap);
    }

    [Fact]
    public void DemotionIntoAFullRankIsAlsoRefused()
    {
        /* Easy to miss: a demotion into a full rank overflows it exactly as a promotion
           would. Checking only the promote direction is a real bug in this shape of code. */
        var d = MembershipRules.ChangeRank(Nypd, "Sergeant", -1, Full("Corporal", 15));
        Assert.Equal(MembershipOutcome.RankFull, d.Outcome);
        Assert.Equal("Corporal", d.Rank);
    }

    [Fact]
    public void AnUncappedRankNeverFills()
    {
        var d = MembershipRules.ChangeRank(Gambino, "Capo", +1, Full("Consigliere", 9999));
        Assert.True(d.IsAllowed);
        Assert.Equal("Consigliere", d.Rank);
    }

    [Fact]
    public void AnUnknownCurrentRankIsTreatedAsTheLowest()
    {
        /* A member can end up off-ladder if a rank was renamed underneath them. Letting a
           promotion fix that is more useful than stranding them. */
        var d = MembershipRules.ChangeRank(Nypd, "Traffic Warden", +1, Empty);
        Assert.True(d.IsAllowed);
        Assert.Equal("Patrolman", d.Rank);   // index 0 -> 1
    }

    [Fact]
    public void ANullRankBehavesTheSameWay()
    {
        var d = MembershipRules.ChangeRank(Nypd, null, +1, Empty);
        Assert.True(d.IsAllowed);
        Assert.Equal("Patrolman", d.Rank);
    }

    [Fact]
    public void JoiningStartsAtTheDefaultRank()
    {
        var d = MembershipRules.Join(Nypd, [], Empty);
        Assert.True(d.IsAllowed);
        Assert.Equal("Cadet", d.Rank);
    }

    [Fact]
    public void APlayerCannotBeInTwoFactions()
    {
        // Factions are mutually exclusive by design.
        var d = MembershipRules.Join(Nypd, ["Other"], Empty);
        Assert.Equal(MembershipOutcome.AlreadyInAnotherFaction, d.Outcome);
        Assert.Equal("Other", d.Conflict);
    }

    [Fact]
    public void RejoiningTheSameFactionIsANoChange()
    {
        var d = MembershipRules.Join(Nypd, ["NYPD"], Empty);
        Assert.Equal(MembershipOutcome.NoChange, d.Outcome);
    }

    [Fact]
    public void JoiningIsRefusedWhenTheEntryRankIsFull()
    {
        var d = MembershipRules.Join(Nypd, [], Full("Cadet", 50));
        Assert.Equal(MembershipOutcome.RankFull, d.Outcome);
        Assert.Equal(50, d.Cap);
    }

    [Fact]
    public void ASubclassCanBeAssignedAlongsideARank()
    {
        // Sub-classes are additive: the member keeps their rank.
        var d = MembershipRules.ChangeSubclass(Nypd, "Detective", [], removing: false);
        Assert.True(d.IsAllowed);
    }

    [Fact]
    public void OnlyOneSubclassAtATime()
    {
        var d = MembershipRules.ChangeSubclass(Nypd, "Detective", ["Vice Officer"], removing: false);
        Assert.Equal(MembershipOutcome.AlreadyHasSubclass, d.Outcome);
        Assert.Equal("Vice Officer", d.Conflict);
    }

    [Fact]
    public void AssigningOneAlreadyHeldIsANoChange()
    {
        var d = MembershipRules.ChangeSubclass(Nypd, "Detective", ["Detective"], removing: false);
        Assert.Equal(MembershipOutcome.NoChange, d.Outcome);
    }

    [Fact]
    public void RemovingRequiresHoldingIt()
    {
        Assert.True(MembershipRules.ChangeSubclass(Nypd, "Detective", ["Detective"], removing: true).IsAllowed);
        Assert.Equal(MembershipOutcome.NoChange,
            MembershipRules.ChangeSubclass(Nypd, "Detective", [], removing: true).Outcome);
    }

    [Fact]
    public void AFactionWithoutSubclassesRefusesThemAll()
    {
        var d = MembershipRules.ChangeSubclass(Gambino, "Detective", [], removing: false);
        Assert.Equal(MembershipOutcome.NoSuchSubclass, d.Outcome);
    }

    [Fact]
    public void AnUnknownFactionIsRefusedEverywhere()
    {
        Assert.Equal(MembershipOutcome.UnknownFaction, MembershipRules.ChangeRank(null, "Cadet", +1, Empty).Outcome);
        Assert.Equal(MembershipOutcome.UnknownFaction, MembershipRules.Join(null, [], Empty).Outcome);
        Assert.Equal(MembershipOutcome.UnknownFaction, MembershipRules.ChangeSubclass(null, "x", [], false).Outcome);
    }
}
