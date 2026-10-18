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
        SpawnFile = "otherspawn.txt",
        RankFiles = new Dictionary<string, string>
        {
            ["Associate"] = "otherassociate.txt", ["Soldier"] = "othersoldier.txt",
            ["Capo"] = "othercapo.txt", ["Consigliere"] = "otherconsigliere.txt",
            ["Underboss"] = "otherunderboss.txt", ["Boss"] = "otherboss.txt",
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
           roster file and the default must be a real rank. A typo here would fail silently
           at write time. */
        foreach (var faction in FactionRegistry.All.Values)
        {
            Assert.NotEmpty(faction.Order);
            Assert.Contains(faction.Default, faction.Order);

            foreach (var rank in faction.Order)
                Assert.True(faction.RankFiles.ContainsKey(rank), $"{faction.Name}/{rank} has no roster file");

            Assert.False(string.IsNullOrWhiteSpace(faction.SpawnFile), $"{faction.Name} has no spawn file");
        }
    }

    /// <summary>
    /// The roster file names are the ones a live install actually has.
    /// </summary>
    /// <remarks>
    /// THE BUG THIS CATCHES, and it shipped. The mafia rosters were named gambino.txt and
    /// colombo.txt. The install has gambinospawn.txt and colombospawn.txt, so every mafia
    /// whitelist created a file the game never opens: the write succeeded, the bot reported
    /// success, /whitelist list showed the member, and in game they could not spawn.
    ///
    /// Nothing else could have caught it. Every other test here uses whatever names the
    /// registry declares, so the whole suite passed against names that were fiction. This is
    /// the one assertion that pins them to the server, and the names are spelled out rather
    /// than derived for exactly that reason - a rule computed from the registry would have
    /// agreed with the registry.
    /// </remarks>
    [Fact]
    public void RosterFilenamesMatchTheOnesTheGameInstallHas()
    {
        Assert.Equal("gambinospawn.txt", FactionRegistry.Get("Gambino")!.SpawnFile);
        Assert.Equal("colombospawn.txt", FactionRegistry.Get("Colombo")!.SpawnFile);
        Assert.Equal("policespawn.txt", FactionRegistry.Get("NYPD")!.SpawnFile);

        // A ladderless faction's single roster IS its spawn file: there is no separate
        // membership list to be a member of.
        foreach (var faction in FactionRegistry.All.Values.Where(f => !f.HasRanks))
            Assert.Equal(faction.SpawnFile, faction.RankFiles[faction.Default]);

        // A faction WITH a ladder keeps them separate: the rank files say what you are, the
        // spawn file says whether you are in at all.
        var nypd = FactionRegistry.Get("NYPD")!;
        Assert.DoesNotContain(nypd.SpawnFile, nypd.RankFiles.Values, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(nypd.SpawnFile, nypd.Subclasses.Values, StringComparer.OrdinalIgnoreCase);
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

    [Fact]
    public void PromotionMovesOneStepUp()
    {
        var d = MembershipRules.ChangeRank(Nypd, "Patrolman", +1);
        Assert.True(d.IsAllowed);
        Assert.Equal("Corporal", d.Rank);
    }

    [Fact]
    public void DemotionMovesOneStepDown()
    {
        var d = MembershipRules.ChangeRank(Nypd, "Corporal", -1);
        Assert.True(d.IsAllowed);
        Assert.Equal("Patrolman", d.Rank);
    }

    // ---- setting a rank outright ----

    [Fact]
    public void ARankCanBeSetSeveralPlacesAwayInOneStep()
    {
        /* WHAT /promotion COULD NOT DO. Moving a Cadet to Sergeant took four runs, rewriting
           the roster files on each one, with the member briefly holding every rank between. */
        var d = MembershipRules.SetRank(Nypd, "Cadet", "Sergeant");

        Assert.True(d.IsAllowed);
        Assert.Equal("Sergeant", d.Rank);
    }

    [Fact]
    public void SettingARankWorksDownwardsToo()
    {
        var d = MembershipRules.SetRank(Nypd, "Chief of Police", "Patrolman");

        Assert.True(d.IsAllowed);
        Assert.Equal("Patrolman", d.Rank);
    }

    [Fact]
    public void TheTargetIsAbsolute_NotRelativeToWhereTheyWere()
    {
        // The same target from opposite directions lands in the same place, which is the
        // whole difference from a promotion.
        Assert.Equal("Sergeant", MembershipRules.SetRank(Nypd, "Cadet", "Sergeant").Rank);
        Assert.Equal("Sergeant", MembershipRules.SetRank(Nypd, "Chief of Police", "Sergeant").Rank);
        Assert.Equal("Sergeant", MembershipRules.SetRank(Nypd, null, "Sergeant").Rank);
    }

    [Fact]
    public void ARankNameIsMatchedCaseInsensitivelyAndResolvedToTheRegistrysSpelling()
    {
        /* The name arrives from an autocomplete the moderator may have typed over, and the
           rank FILES are keyed on the registry's casing - so the resolved spelling is what
           has to come back or the write lands nowhere. */
        var d = MembershipRules.SetRank(Nypd, "Cadet", "  sErGeAnT  ");

        Assert.True(d.IsAllowed);
        Assert.Equal("Sergeant", d.Rank);
    }

    [Fact]
    public void ARankTheFactionDoesNotHaveIsRefused()
    {
        Assert.Equal(MembershipOutcome.NoSuchRank, MembershipRules.SetRank(Nypd, "Cadet", "Centurion").Outcome);
        Assert.Equal(MembershipOutcome.NoSuchRank, MembershipRules.SetRank(Nypd, "Cadet", "").Outcome);
        Assert.Equal(MembershipOutcome.NoSuchRank, MembershipRules.SetRank(Nypd, "Cadet", null).Outcome);
    }

    [Fact]
    public void ASubclassIsNotARankAndCannotBeSetAsOne()
    {
        /* TWO DIFFERENT THINGS THAT BOTH READ AS "WHAT THEY ARE". A rank is a position on the
           ladder and a member holds exactly one; a sub-class is ADDITIVE to whatever rank they
           hold, and it lives in a separate dictionary with its own file. Order and Subclasses
           never overlap.

           Worth pinning rather than assuming: they are easy to confuse from the outside - the
           author of this feature demonstrated it with "Veteran Ranger", which is an NCR
           SUB-CLASS - and nothing else fails if a rank lookup started reaching into the
           sub-class list. It would just quietly let /whitelist setrank strip somebody's rank
           and file them under a loadout instead. */
        foreach (var subclass in Nypd.Subclasses.Keys)
        {
            Assert.DoesNotContain(subclass, Nypd.Order);
            Assert.Equal(MembershipOutcome.NoSuchRank, MembershipRules.SetRank(Nypd, "Cadet", subclass).Outcome);
        }
    }

    [Fact]
    public void SettingTheRankTheyAlreadyHoldWritesNothing()
    {
        // Re-running it is the natural thing to do when somebody is unsure it took, and a
        // no-op write would churn every rank file for nothing.
        var d = MembershipRules.SetRank(Nypd, "Sergeant", "Sergeant");

        Assert.False(d.IsAllowed);
        Assert.Equal(MembershipOutcome.NoChange, d.Outcome);
        Assert.Equal("Sergeant", d.Rank);
    }

    [Fact]
    public void SettingARankOnAnUnknownFactionIsRefused()
    {
        Assert.Equal(MembershipOutcome.UnknownFaction, MembershipRules.SetRank(null, "Cadet", "Sergeant").Outcome);
    }

    [Fact]
    public void TheLadderEndsAreRefusedRatherThanWrappingAround()
    {
        Assert.Equal(MembershipOutcome.AlreadyLowest, MembershipRules.ChangeRank(Nypd, "Cadet", -1).Outcome);
        Assert.Equal(MembershipOutcome.AlreadyHighest, MembershipRules.ChangeRank(Nypd, "Chief of Police", +1).Outcome);
    }

    [Fact]
    public void APromotionIntoABusyRankIsAllowed()
    {
        /* RANK CAPS ARE GONE. There used to be exactly one Chief of Police and this rule was
           the only thing preventing a second; a promotion into a rank at its old limit is now
           an ordinary promotion. */
        var d = MembershipRules.ChangeRank(Nypd, "Deputy Chief", +1);
        Assert.True(d.IsAllowed);
        Assert.Equal("Chief of Police", d.Rank);
    }

    [Fact]
    public void ADemotionIntoABusyRankIsAllowedToo()
    {
        // Both directions, because the refusal used to apply to both.
        var d = MembershipRules.ChangeRank(Nypd, "Sergeant", -1);
        Assert.True(d.IsAllowed);
        Assert.Equal("Corporal", d.Rank);
    }

    [Fact]
    public void NoRankSizeIsConsultedAtAll()
    {
        /* THE PROPERTY, rather than one example of it: the rules take no member count, so
           there is no input by which a decision could depend on how full a rank is. A
           reintroduced cap would have to change this signature to work, which is what makes
           this test worth more than another allowed-case. */
        Assert.Equal(3, typeof(MembershipRules)
            .GetMethod(nameof(MembershipRules.ChangeRank))!.GetParameters().Length);
        Assert.Equal(2, typeof(MembershipRules)
            .GetMethod(nameof(MembershipRules.Join))!.GetParameters().Length);
    }

    [Fact]
    public void AnUnknownCurrentRankIsTreatedAsTheLowest()
    {
        /* A member can end up off-ladder if a rank was renamed underneath them. Letting a
           promotion fix that is more useful than stranding them. */
        var d = MembershipRules.ChangeRank(Nypd, "Traffic Warden", +1);
        Assert.True(d.IsAllowed);
        Assert.Equal("Patrolman", d.Rank);   // index 0 -> 1
    }

    [Fact]
    public void ANullRankBehavesTheSameWay()
    {
        var d = MembershipRules.ChangeRank(Nypd, null, +1);
        Assert.True(d.IsAllowed);
        Assert.Equal("Patrolman", d.Rank);
    }

    [Fact]
    public void JoiningStartsAtTheDefaultRank()
    {
        var d = MembershipRules.Join(Nypd, []);
        Assert.True(d.IsAllowed);
        Assert.Equal("Cadet", d.Rank);
    }

    [Fact]
    public void APlayerCannotBeInTwoFactions()
    {
        // Factions are mutually exclusive by design.
        var d = MembershipRules.Join(Nypd, ["Other"]);
        Assert.Equal(MembershipOutcome.AlreadyInAnotherFaction, d.Outcome);
        Assert.Equal("Other", d.Conflict);
    }

    [Fact]
    public void RejoiningTheSameFactionIsANoChange()
    {
        var d = MembershipRules.Join(Nypd, [Nypd.Name]);
        Assert.Equal(MembershipOutcome.NoChange, d.Outcome);
    }

    [Fact]
    public void JoiningABusyFactionIsAllowed()
    {
        // The entry rank had a cap of 50 and a faction has never had a total limit. Neither
        // is consulted now, so a faction takes as many members as it is given.
        var d = MembershipRules.Join(Nypd, []);
        Assert.True(d.IsAllowed);
        Assert.Equal("Cadet", d.Rank);
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
        Assert.Equal(MembershipOutcome.UnknownFaction, MembershipRules.ChangeRank(null, "Cadet", +1).Outcome);
        Assert.Equal(MembershipOutcome.UnknownFaction, MembershipRules.Join(null, []).Outcome);
        Assert.Equal(MembershipOutcome.UnknownFaction, MembershipRules.ChangeSubclass(null, "x", [], false).Outcome);
    }
}
