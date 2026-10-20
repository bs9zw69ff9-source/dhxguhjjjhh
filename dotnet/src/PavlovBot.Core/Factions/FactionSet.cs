using System.Collections.Frozen;

namespace PavlovBot.Core.Factions;

/// <summary>
/// The factions one bot runs, as data rather than as a compiled-in list.
/// </summary>
/// <remarks>
/// WHY THIS IS AN INSTANCE. One codebase now serves more than one RP: the same binary runs
/// as the normal bot and as a themed clone with a completely different roster, chosen by
/// configuration at boot. A static list cannot do that, and forking the repo to change six
/// lines of data would mean every fix landing twice - which on the evidence of this repo's
/// history means it lands once and the other copy quietly rots.
///
/// IMMUTABLE ONCE BUILT. It is read on every whitelist command and every roster sweep, from
/// several threads, and nothing is allowed to change it after startup. That is also what
/// keeps it out of the "global mutable state" the house rules forbid: the built-in set is a
/// constant, and a configured one is built once and never written to.
///
/// <see cref="FactionRegistry"/> holds the BUILT-IN sets - the Fallout ladders, the default, and the GWPD -
/// so an existing deployment needs no configuration at all.
/// </remarks>
public sealed class FactionSet
{
    private FactionSet(FrozenDictionary<string, FactionDefinition> all, FrozenDictionary<string, FactionDefinition> former)
    {
        All = all;
        _former = former;
    }

    /// <summary>Former name -> the faction it is now. Resolved by <see cref="Get"/>, never listed.</summary>
    private readonly FrozenDictionary<string, FactionDefinition> _former;

    /// <summary>Faction name -> its definition. Case-insensitive, like every name here.</summary>
    /// <remarks>
    /// CURRENT NAMES ONLY, because this is what pickers, startup and roster sweeps enumerate.
    /// A former name listed here would show the same faction twice.
    /// </remarks>
    public FrozenDictionary<string, FactionDefinition> All { get; }

    public IReadOnlyCollection<string> Names => All.Keys;

    /// <summary>
    /// A faction by its name or by a name it used to have, or null.
    /// </summary>
    /// <remarks>
    /// THE CURRENT NAME WINS, so a former name that is also some other faction's current name
    /// (a configuration <see cref="Problems"/> reports) can never steal that faction's lookups.
    /// </remarks>
    public FactionDefinition? Get(string? faction) =>
        faction is null ? null
            : All.TryGetValue(faction, out var found) ? found
            : _former.TryGetValue(faction, out var renamed) ? renamed
            : null;

    /// <summary>Every former name -> the current name of the faction that had it.</summary>
    public IReadOnlyDictionary<string, string> Renames =>
        _former.ToDictionary(kv => kv.Key, kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Build a set from definitions. Duplicate names are a configuration error.</summary>
    /// <remarks>
    /// Does NOT validate - <see cref="Problems"/> does, and the caller decides whether a bad
    /// set is worth refusing to start over. Splitting the two is what lets startup report
    /// every problem at once rather than failing on the first.
    /// </remarks>
    public static FactionSet Of(IEnumerable<FactionDefinition> factions)
    {
        ArgumentNullException.ThrowIfNull(factions);

        var map = new Dictionary<string, FactionDefinition>(StringComparer.OrdinalIgnoreCase);
        var former = new Dictionary<string, FactionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var faction in factions)
        {
            map[faction.Name] = faction;
            foreach (var old in faction.FormerNames) former[old] = faction;
        }

        return new FactionSet(
            map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            former.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Everything wrong with this set, in terms an operator can act on. Empty means usable.
    /// </summary>
    /// <remarks>
    /// EVERY PROBLEM AT ONCE, the same rule the rest of startup follows. Fixing a roster file
    /// one restart at a time turns a five-minute edit into an afternoon.
    ///
    /// THE FILE NAMES ARE THE PART THAT MATTERS. Two factions sharing a roster file silently
    /// merge their memberships in game, and there is nothing downstream that could notice -
    /// the bot would report both rosters correctly and the server would honour one list for
    /// both. That check is the reason this method exists.
    /// </remarks>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (All.Count == 0) problems.Add("No factions are defined.");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var faction in All.Values)
        {
            if (string.IsNullOrWhiteSpace(faction.SpawnFile))
                problems.Add($"{faction.Name} has no spawn file, so nobody could ever play as it.");

            if (faction.Order.Count == 0)
            {
                problems.Add($"{faction.Name} has no ranks.");
                continue;
            }

            if (!faction.Order.Contains(faction.Default, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{faction.Name}'s default rank \"{faction.Default}\" is not one of its ranks.");

            foreach (var rank in faction.Order)
            {
                if (!faction.RankFiles.ContainsKey(rank))
                    problems.Add($"{faction.Name}/{rank} has no roster file.");
            }

            /* THE SPAWN FILE IS ALLOWED TO BE A RANK FILE, and only for a faction with no
               ladder - that is how a spawn-only faction is expressed, one file serving as
               both its membership and its single rank. Anywhere else it is a collision. */
            var owned = faction.RankFiles.Values
                .Concat(faction.Subclasses.Values)
                .Append(faction.SpawnFile)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var file in owned)
            {
                if (files.TryGetValue(file, out var owner) && !string.Equals(owner, faction.Name, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{faction.Name} and {owner} both use \"{file}\"; two factions sharing a roster file merge their members in game.");
                else
                    files[file] = faction.Name;
            }
        }

        /* A FORMER NAME MUST POINT AT ONE FACTION. Records filed under it are carried over to
           whichever faction claims it, so two claimants - or a former name that is another
           faction's current one - would hand somebody's memberships to the wrong roster. */
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var faction in All.Values)
        {
            foreach (var old in faction.FormerNames)
            {
                if (All.ContainsKey(old))
                    problems.Add($"{faction.Name} lists \"{old}\" as a former name, but that is a faction of its own.");
                else if (claimed.TryGetValue(old, out var other) && !string.Equals(other, faction.Name, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{faction.Name} and {other} both list \"{old}\" as a former name.");
                else
                    claimed[old] = faction.Name;
            }
        }

        return problems;
    }
}
