using System.Collections.Frozen;

namespace PavlovBot.Core.Factions;

/// <summary>
/// One faction's rank ladder and limits. Pure data - no logic, no state.
/// </summary>
/// <remarks>
/// Ranks are a LIST, lowest to highest, because that makes promotion "move one index up"
/// and demotion "move one index down" - one piece of logic walking a shape, rather than a
/// method or a switch arm per rank. Adding a rank is a line of data; code that was never
/// written cannot have bugs in it.
/// </remarks>
public sealed record FactionDefinition
{
    public required string Name { get; init; }

    /// <summary>Lowest to highest. Order IS the hierarchy.</summary>
    public required IReadOnlyList<string> Order { get; init; }

    /// <summary>Rank a member starts at when first whitelisted.</summary>
    public required string Default { get; init; }

    /// <summary>Rank -> the Config/*.txt roster the game reads for it.</summary>
    public required IReadOnlyDictionary<string, string> RankFiles { get; init; }

    /// <summary>Rank -> member limit. A rank absent from this map is uncapped.</summary>
    public IReadOnlyDictionary<string, int> RankCaps { get; init; } =
        new Dictionary<string, int>();

    /// <summary>
    /// Sub-classes are NOT ranks. A member keeps their rank and may additionally hold one
    /// of these, each with its own roster file.
    /// </summary>
    public IReadOnlyDictionary<string, string> Subclasses { get; init; } =
        new Dictionary<string, string>();

    public string Lowest => Order[0];
    public string Highest => Order[^1];

    /// <summary>Position in the ladder, or -1 when the rank is not part of this faction.</summary>
    public int IndexOf(string? rank) =>
        rank is null ? -1 : Order.ToList().FindIndex(r => string.Equals(r, rank, StringComparison.OrdinalIgnoreCase));

    /// <summary>Member limit for a rank, or <see cref="int.MaxValue"/> when uncapped.</summary>
    public int CapFor(string rank) =>
        RankCaps.TryGetValue(rank, out var cap) ? cap : int.MaxValue;

    public bool HasSubclass(string subclass) => Subclasses.ContainsKey(subclass);
}

/// <summary>The factions this server runs. Ported verbatim from factions/ranks.js.</summary>
public static class FactionRegistry
{
    private static readonly string[] MafiaOrder =
        ["Associate", "Soldier", "Capo", "Consigliere", "Underboss", "Boss"];

    // Consigliere is deliberately absent from the caps: it is the one uncapped mafia rank.
    private static Dictionary<string, int> MafiaCaps() => new()
    {
        ["Associate"] = 18, ["Soldier"] = 12, ["Capo"] = 3, ["Underboss"] = 1, ["Boss"] = 1,
    };

    private static Dictionary<string, string> MafiaFiles(string prefix) => new()
    {
        ["Associate"] = $"{prefix}associate.txt",
        ["Soldier"] = $"{prefix}soldier.txt",
        ["Capo"] = $"{prefix}capo.txt",
        ["Consigliere"] = $"{prefix}consigliere.txt",
        ["Underboss"] = $"{prefix}underboss.txt",
        ["Boss"] = $"{prefix}boss.txt",
    };

    public static readonly FrozenDictionary<string, FactionDefinition> All =
        new Dictionary<string, FactionDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gambino"] = new()
            {
                Name = "Gambino",
                Order = MafiaOrder,
                Default = "Associate",
                RankFiles = MafiaFiles("gambino"),
                RankCaps = MafiaCaps(),
            },
            ["Colombo"] = new()
            {
                Name = "Colombo",
                Order = MafiaOrder,
                Default = "Associate",
                RankFiles = MafiaFiles("colombo"),
                RankCaps = MafiaCaps(),
            },
            ["NYPD"] = new()
            {
                Name = "NYPD",
                Order = ["Cadet", "Patrolman", "Corporal", "Sergeant", "Lieutenant", "Captain", "Deputy Chief", "Chief of Police"],
                Default = "Cadet",
                RankFiles = new Dictionary<string, string>
                {
                    ["Cadet"] = "policecadet.txt",
                    ["Patrolman"] = "policepatrolman.txt",
                    ["Corporal"] = "policecorporal.txt",
                    ["Sergeant"] = "policesergeant.txt",
                    ["Lieutenant"] = "policelieutenant.txt",
                    ["Captain"] = "policecaptain.txt",
                    ["Deputy Chief"] = "policedeputychief.txt",
                    ["Chief of Police"] = "policechief.txt",
                },
                // Every NYPD rank is capped, unlike the mafia ladders.
                RankCaps = new Dictionary<string, int>
                {
                    ["Cadet"] = 50, ["Patrolman"] = 20, ["Corporal"] = 15, ["Sergeant"] = 20,
                    ["Lieutenant"] = 8, ["Captain"] = 4, ["Deputy Chief"] = 1, ["Chief of Police"] = 1,
                },
                Subclasses = new Dictionary<string, string>
                {
                    ["Vice Officer"] = "policevice.txt",
                    ["Detective"] = "policedetective.txt",
                    ["Tactical Response Unit"] = "policetacticalresponse.txt",
                },
            },
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FactionDefinition? Get(string? faction) =>
        faction is not null && All.TryGetValue(faction, out var def) ? def : null;

    public static IReadOnlyCollection<string> Names => All.Keys;
}
