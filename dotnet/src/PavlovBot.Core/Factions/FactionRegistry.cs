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

    public string Lowest => Order[0];
    public string Highest => Order[^1];

    /// <summary>
    /// Whether this faction has a ladder at all.
    /// </summary>
    /// <remarks>
    /// DERIVED FROM THE DATA, not a separate flag that could disagree with it. A faction with
    /// one entry in <see cref="Order"/> has nowhere to be promoted TO, so promotion and
    /// demotion are meaningless rather than merely unavailable - the mafias are spawn access
    /// and nothing else.
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
    /// <summary>The single nominal rank a spawn-only faction uses. Never shown to a user.</summary>
    public const string SpawnOnly = "Member";

    public static readonly FrozenDictionary<string, FactionDefinition> All =
        new Dictionary<string, FactionDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            /* SPAWN ACCESS, NO LADDER. The mafias used to carry a six-rank ladder with caps
               (Associate through Boss). They are whitelist-only now: one file each, one
               nominal rank, nothing to promote to.

               "Member" is not shown to anybody - it exists because Order and Default are
               required and the roster writer needs a key for the file. HasRanks reports false
               off the back of it, and the promotion commands refuse on that.

               THE FILE NAME IS THE SPAWN FILE, and this used to say gambino.txt. No such file
               exists in a live install - the roster directory holds gambinospawn.txt - so
               every mafia whitelist created a brand new file the game never opens, reported
               success, and left the player unable to spawn. */
            ["Gambino"] = new()
            {
                Name = "Gambino",
                Order = [SpawnOnly],
                Default = SpawnOnly,
                SpawnFile = "gambinospawn.txt",
                RankFiles = new Dictionary<string, string> { [SpawnOnly] = "gambinospawn.txt" },
            },
            ["Colombo"] = new()
            {
                Name = "Colombo",
                Order = [SpawnOnly],
                Default = SpawnOnly,
                SpawnFile = "colombospawn.txt",
                RankFiles = new Dictionary<string, string> { [SpawnOnly] = "colombospawn.txt" },
            },
            ["NYPD"] = new()
            {
                Name = "NYPD",
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
    /// The built-in factions as a set, and the default when nothing is configured.
    /// </summary>
    /// <remarks>
    /// Lazy so it is built once, on first use, rather than in a static initialiser that runs
    /// before configuration has been read. An existing deployment gets exactly this and needs
    /// no configuration at all, which is the whole point of it being the default.
    /// </remarks>
    public static FactionSet Default { get; } = FactionSet.Of(All.Values);

    public static FactionDefinition? Get(string? faction) =>
        faction is not null && All.TryGetValue(faction, out var def) ? def : null;

    public static IReadOnlyCollection<string> Names => All.Keys;
}
