using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord.Commands;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The /subclass picker, grouped by the faction that owns each sub-class.
/// </summary>
/// <remarks>
/// It was a flat list in registry order - which is insertion order, and means nothing to
/// whoever is reading it. On a multi-faction server that is a wall of names with no clue
/// which faction each belongs to.
///
/// THE DUPLICATE IS THE DANGEROUS PART. Two factions may define a sub-class with the same
/// name, and Discord rejects a duplicate choice by rejecting the WHOLE registration - which
/// takes every command in the bot off the picker, not just this one. That has happened here
/// before, over duplicated subcommands, and it cost nine hours. Merging them into one choice
/// hid the second faction's (Enclave Recon under "BoS/Enclave - Recon"), so each faction gets
/// its own choice and the faction travels in the value.
/// </remarks>
public class SubclassChoiceTests
{
    private static FactionDefinition Faction(string name, params string[] subclasses) => new()
    {
        Name = name,
        SpawnFile = $"{name.ToLowerInvariant()}spawn.txt",
        RankFiles = new Dictionary<string, string> { ["Member"] = $"{name.ToLowerInvariant()}spawn.txt" },
        Order = ["Member"],
        Default = "Member",
        Subclasses = subclasses.ToDictionary(s => s, s => $"{name.ToLowerInvariant()}{s}.txt"),
    };

    [Fact]
    public void SubclassesComeOutGroupedByFactionInFactionOrder()
    {
        var set = FactionSet.Of([
            Faction("NCR", "Veteran Ranger", "Patrol Ranger"),
            Faction("Legion", "Frumentarius"),
        ]);

        var choices = SubclassCommand.SubclassChoices(set);

        Assert.Equal(["Patrol Ranger", "Veteran Ranger", "Frumentarius"], choices.Select(c => c.Name));
        Assert.Equal("NCR - Patrol Ranger", choices[0].Label);
        Assert.Equal("Legion - Frumentarius", choices[2].Label);
    }

    [Fact]
    public void WithinAFactionTheyAreSortedByName()
    {
        // Registry order is insertion order. Alphabetical within a faction is at least a rule.
        var set = FactionSet.Of([Faction("NCR", "Veteran Ranger", "Heavy Trooper", "Patrol Ranger")]);

        Assert.Equal(["Heavy Trooper", "Patrol Ranger", "Veteran Ranger"],
            SubclassCommand.SubclassChoices(set).Select(c => c.Name));
    }

    [Fact]
    public void ANameTwoFactionsShareGetsAChoicePerFactionWithUniqueLabelsAndValues()
    {
        var set = FactionSet.Of([Faction("NCR", "Scout"), Faction("Legion", "Scout")]);

        var choices = SubclassCommand.SubclassChoices(set);

        Assert.Equal(["NCR - Scout", "Legion - Scout"], choices.Select(c => c.Label));
        Assert.Equal(["NCR:Scout", "Legion:Scout"], choices.Select(c => c.Value));
    }

    [Fact]
    public void AValueParsesBackToItsFactionAndName()
    {
        Assert.Equal(("Enclave", "Recon"), SubclassCommand.ParseValue("Enclave:Recon"));
        Assert.Equal(("NCR", "Veteran Ranger"), SubclassCommand.ParseValue("NCR:Veteran Ranger"));
    }

    [Fact]
    public void ABareNameFromAnOldRegistrationStillParses()
    {
        Assert.Equal(((string?)null, "Hellfire"), SubclassCommand.ParseValue("Hellfire"));
    }

    [Fact]
    public void EveryChoiceFitsDiscordsLimits()
    {
        var choices = SubclassCommand.SubclassChoices(FactionRegistry.Default);

        Assert.InRange(choices.Count, 1, SubclassCommand.MaxChoices);
        Assert.All(choices, c => Assert.InRange(c.Label.Length, 1, 100));
        Assert.All(choices, c => Assert.InRange(c.Value.Length, 1, 100));
        Assert.Equal(choices.Count, choices.Select(c => c.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(choices.Count, choices.Select(c => c.Value).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AFactionWithNoSubclassesContributesNothing()
    {
        var set = FactionSet.Of([Faction("NCR", "Veteran Ranger"), Faction("Enclave")]);

        Assert.Equal(["Veteran Ranger"], SubclassChoicesNames(set));
    }

    [Fact]
    public void NoSubclassesAnywhereIsAnEmptyListRatherThanAThrow()
    {
        // A spawn-only server is a perfectly good configuration.
        Assert.Empty(SubclassCommand.SubclassChoices(FactionSet.Of([Faction("NCR")])));
    }

    private static IEnumerable<string> SubclassChoicesNames(FactionSet set) =>
        SubclassCommand.SubclassChoices(set).Select(c => c.Name);
}
