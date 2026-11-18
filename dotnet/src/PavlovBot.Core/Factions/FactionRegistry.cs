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

    /// <summary>
    /// The file that grants SPAWN ACCESS to the faction, holding every member regardless of
    /// rank.
    /// </summary>
    /// <remarks>
    /// THIS IS THE FILE THAT DECIDES WHETHER SOMEBODY CAN PLAY AS THE FACTION. The rank files
    /// decide what they get once they are in; this decides whether they are in at all. A
    /// member written to <c>policecadet.txt</c> but not to <c>policespawn.txt</c> is on the
    /// roster as far as the bot is concerned and cannot spawn as far as the game is.
    ///
    /// The port dropped it. The Node bot wrote both, which is visible in a live install: the
    /// spawn file is byte-for-byte the union of the rank files, written moments before them.
    /// Nothing announced the omission, because every command still reported success.
    ///
    /// For a faction with no ladder this is the same file as its single rank file, and the
    /// writer is idempotent, so that costs a read and no write.
    /// </remarks>
    public required string SpawnFile { get; init; }

    /// <summary>
    /// Sub-classes are NOT ranks. A member keeps their rank and may additionally hold one
    /// of these, each with its own roster file.
    /// </summary>
    public IReadOnlyDictionary<string, string> Subclasses { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// What this faction used to be called. Looked up as this faction, never listed as one.
    /// </summary>
    /// <remarks>
    /// A FACTION'S NAME IS STORED DATA, not just a label. It is the key a membership record,
    /// a rank suspension and a delegated roster role are filed under, and an operator may
    /// still have the old name in PAYROLL_FACTION. Renaming the faction without this would
    /// leave every one of those pointing at a faction that no longer exists - nobody loses
    /// game access, because the roster FILES keep their names, but /promotion by user,
    /// suspensions and delegated roles quietly stop finding anybody.
    /// </remarks>
    public IReadOnlyList<string> FormerNames { get; init; } = [];

    public string Lowest => Order[0];
    public string Highest => Order[^1];

    /// <summary>
    /// Whether this faction has a ladder at all.
    /// </summary>
    /// <remarks>
    /// DERIVED FROM THE DATA, not a separate flag that could disagree with it. A faction with
    /// one entry in <see cref="Order"/> has nowhere to be promoted TO, so promotion and
    /// demotion are meaningless rather than merely unavailable - a spawn-only faction loaded
    /// from FACTIONS_PATH is spawn access and nothing else.
    ///
    /// A flag would let somebody set HasRanks = true on a one-rank faction and get a
    /// promotion command that silently does nothing.
    /// </remarks>
    public bool HasRanks => Order.Count > 1;

    /// <summary>Position in the ladder, or -1 when the rank is not part of this faction.</summary>
    public int IndexOf(string? rank) =>
        rank is null ? -1 : Order.ToList().FindIndex(r => string.Equals(r, rank, StringComparison.OrdinalIgnoreCase));

    /// <summary>Member limit for a rank, or <see cref="int.MaxValue"/> when uncapped.</summary>
    public bool HasSubclass(string subclass) => Subclasses.ContainsKey(subclass);
}

/// <summary>The factions this server runs. Ported verbatim from factions/ranks.js.</summary>
public static class FactionRegistry
{
    /// <summary>The police faction's name in the police set.</summary>
    public const string PoliceName = "GWPD";

    public static readonly FrozenDictionary<string, FactionDefinition> All =
        new Dictionary<string, FactionDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            /* GWPD, AND IT WAS NYPD. Only the name moved: every roster file below is the one
               the game already reads, because renaming a faction must never repoint a file -
               everybody listed in the old one would silently lose the rank in game. The old
               name is kept as a former name so records filed under it still resolve here. */
            [PoliceName] = new()
            {
                Name = PoliceName,
                FormerNames = ["NYPD"],
                Order = ["Cadet", "Patrolman", "Corporal", "Sergeant", "Lieutenant", "Captain", "Deputy Chief", "Chief of Police"],
                Default = "Cadet",
                SpawnFile = "policespawn.txt",
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
                /* SUB-CLASSES ARE NOT RANKS. A member keeps their rank and may additionally
                   hold one of these, each with its own whitelist file. */
                Subclasses = new Dictionary<string, string>
                {
                    ["Vice Officer"] = "policevice.txt",
                    ["Detective"] = "policedetective.txt",
                    ["Tactical Response Unit"] = "policetacticalresponse.txt",
                    ["Narcotics Bureau"] = "policenarcotics.txt",
                },
            },
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The police ladder: the GWPD and nothing else. The only built-in set.
    /// </summary>
    /// <remarks>
    /// THE MAFIAS ARE GONE FROM IT. Gambino and Colombo sat beside the GWPD as spawn-only
    /// factions until the police bot dropped them. Their roster files were not touched:
    /// gambinospawn.txt and colombospawn.txt stay on disk with whatever is in them. A
    /// membership record still filed under either name reads as a faction that is gone, which
    /// every lookup already handles.
    ///
    /// THE FALLOUT LADDERS ARE GONE TOO (owner decision, 2026-11-17), with the bot that ran
    /// them. Their roster files are not deleted either; this bot simply stops writing them.
    /// </remarks>
    public static FactionSet Police { get; } = FactionSet.Of(All.Values);

    /// <summary>What a bot runs when neither FACTION_SET nor FACTIONS_PATH says otherwise.</summary>
    public static FactionSet Default => Police;

    /// <summary>The preset name that used to select the Fallout ladders, refused by name.</summary>
    public const string RemovedFalloutName = "fallout";

    /// <summary>
    /// A built-in set by name, or null when the name is not one.
    /// </summary>
    /// <remarks>
    /// Null rather than a fallback, so a typo in the setting is refused at startup instead of
    /// quietly running the wrong factions. <c>default</c> and <c>builtin</c> used to mean the
    /// Fallout ladders; they now mean the only built-in set there is, which is what the words
    /// say. <c>fallout</c> is null like any other unknown name, and Program explains why.
    /// </remarks>
    public static FactionSet? Preset(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "police" or "gwpd" or "nypd" or "default" or "builtin" or "built-in" => Police,
        _ => null,
    };

    /// <summary>Every preset name, for the error message when one does not match.</summary>
    public static IReadOnlyList<string> PresetNames { get; } = ["police"];

    /// <summary>
    /// True when <paramref name="name"/> asks for the Fallout ladders, which were removed.
    /// </summary>
    /// <remarks>
    /// Its own check so the startup error can say "removed" instead of "unknown": somebody
    /// with <c>FACTION_SET=fallout</c> in a .env that worked last week needs to be told the
    /// set is gone, not that they made a typo.
    /// </remarks>
    public static bool IsRemovedPreset(string? name) =>
        string.Equals(name?.Trim(), RemovedFalloutName, StringComparison.OrdinalIgnoreCase);

    /// <summary>One faction by name (or former name) from the built-in set.</summary>
    public static FactionDefinition? Get(string? faction) => faction is null ? null : Police.Get(faction);

    public static IReadOnlyCollection<string> Names => All.Keys;
}
