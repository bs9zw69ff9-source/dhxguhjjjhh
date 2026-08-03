using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Factions;

namespace PavlovBot.Host.Factions;

/// <param name="Rank">The highest rank whose roster file contains them.</param>
public sealed record Membership(FactionDefinition Faction, string Player, string Rank);

/// <summary>
/// Reading and writing the game's roster files, with the membership rules enforced.
/// </summary>
/// <remarks>
/// The rosters are plain <c>.txt</c> files the game reads LIVE - one name per line, in the
/// server's config directory. Two consequences shape everything here:
///
///   THE STORAGE CANNOT EXPRESS THE RULES. Nothing stops a name appearing in six files at
///   once, so one faction per player, one rank, and the caps are all enforced here, before
///   anything is written. <see cref="MembershipRules"/> holds the decisions; this holds
///   the I/O.
///
///   A BAD WRITE IS LIVE IMMEDIATELY. There is no transaction and no undo - the game picks
///   up the file on the next read. So every write goes through
///   <see cref="RosterWriteGuard"/>, which refuses one that would delete more than a
///   handful of entries, and a pre-write copy is kept so a bad write is recoverable by hand.
/// </remarks>
public sealed class RosterService
{
    private readonly string? _directory;
    private readonly string _backupDirectory;
    private readonly ILogger<RosterService> _logger;

    /// <summary>Serialised per FILE. Two commands editing one roster would otherwise
    /// read-modify-write over each other and silently drop a member.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public RosterService(string? rosterDirectory, ILogger<RosterService> logger, string? backupDirectory = null)
    {
        _directory = rosterDirectory;
        _backupDirectory = backupDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "faction_file_bak");
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_directory) && Directory.Exists(_directory);

    private string PathFor(string file) => Path.Combine(_directory!, file);

    /// <summary>Read a roster. Null means UNREADABLE, which is not the same as empty.</summary>
    /// <remarks>
    /// The distinction is load-bearing: an empty roster is a valid state the game accepts,
    /// so treating "cannot read" as "empty" and writing that back wipes a faction.
    /// </remarks>
    public IReadOnlyList<string>? Read(string file)
    {
        if (!Enabled) return null;
        var path = PathFor(file);

        try
        {
            if (!File.Exists(path)) return [];
            return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Cannot read {File}", file);
            return null;
        }
    }

    /// <param name="allowBulk">
    /// Skip the destruction guard. Set ONLY for an operation that legitimately rewrites a
    /// whole roster - a restore or a deliberate wipe.
    /// </param>
    public async Task<bool> WriteAsync(string file, IReadOnlyList<string> lines, bool allowBulk = false, CancellationToken ct = default)
    {
        if (!Enabled) return false;

        var gate = _gates.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Read(file);

            if (!allowBulk)
            {
                var verdict = RosterWriteGuard.Evaluate(current, lines);
                if (!verdict.IsAllowed)
                {
                    _logger.LogError("REFUSED write to {File}: {Reason}. The roster is untouched.", file, verdict.Explanation);
                    return false;
                }
            }

            if (current is not null) Backup(file, current);

            var path = PathFor(file);
            var temp = $"{path}.tmp";
            /* Only into a directory that already exists. The bot never creates one inside a
               game install - a missing one means the configured roster path is wrong, and
               building it produces a tree the game never reads. */
            if (Storage.GameFiles.Problem(path) is { } problem)
            {
                _logger.LogWarning("Not writing {Path}: {Problem}", path, problem);
                return false;
            }

            await File.WriteAllTextAsync(temp, string.Join("\n", lines) + "\n", ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Write failed for {File}", file);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// One rolling pre-write copy per roster, OUTSIDE the game tree.
    /// </summary>
    /// <remarks>
    /// Best effort - a failed backup never blocks the real write. Kept outside the game's
    /// config directory deliberately: a <c>.bak</c> sitting next to the live files is one
    /// glob away from being read as a roster.
    /// </remarks>
    private void Backup(string file, IReadOnlyList<string> contents)
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            File.WriteAllText(Path.Combine(_backupDirectory, file + ".bak"), string.Join("\n", contents) + "\n");
        }
        catch (Exception) { }
    }

    /// <summary>Where a player currently sits, or null when they are on no roster.</summary>
    public Task<Membership?> FindAsync(string player, CancellationToken ct = default)
    {
        foreach (var faction in FactionRegistry.All.Values)
        {
            // Highest first: a member listed in several rank files is reported at their
            // TOP rank, which is the one that actually governs what they can do in game.
            foreach (var rank in faction.Order.Reverse())
            {
                var roster = Read(faction.RankFiles[rank]);
                if (roster is not null && roster.Contains(player, StringComparer.OrdinalIgnoreCase))
                    return Task.FromResult<Membership?>(new Membership(faction, player, rank));
            }
        }
        return Task.FromResult<Membership?>(null);
    }

    /// <summary>Every member of a faction with their rank.</summary>
    public Task<IReadOnlyList<Membership>> RosterAsync(FactionDefinition faction, CancellationToken ct = default)
    {
        var seen = new Dictionary<string, Membership>(StringComparer.OrdinalIgnoreCase);

        foreach (var rank in faction.Order)
        {
            foreach (var player in Read(faction.RankFiles[rank]) ?? [])
                seen[player] = new Membership(faction, player, rank);   // later ranks are higher, so they win
        }
        return Task.FromResult<IReadOnlyList<Membership>>(seen.Values.ToList());
    }

    private Task<int> CountAsync(FactionDefinition faction, string rank) =>
        Task.FromResult((Read(faction.RankFiles[rank]) ?? []).Count);

    private Func<string, int> Counter(FactionDefinition faction) =>
        rank => (Read(faction.RankFiles[rank]) ?? []).Count;

    /// <summary>Add a player to a faction at its default rank.</summary>
    public async Task<MembershipDecision> JoinAsync(FactionDefinition faction, string player, CancellationToken ct = default)
    {
        var existing = await FindAsync(player, ct).ConfigureAwait(false);
        var current = existing is null ? Array.Empty<string>() : [existing.Faction.Name];

        var decision = MembershipRules.Join(faction, current, Counter(faction));
        if (!decision.IsAllowed) return decision;

        var file = faction.RankFiles[decision.Rank!];
        var roster = Read(file);
        if (roster is null) return new MembershipDecision(MembershipOutcome.UnknownFaction);

        return await WriteAsync(file, [.. roster, player], ct: ct).ConfigureAwait(false)
            ? decision
            : new MembershipDecision(MembershipOutcome.UnknownFaction);
    }

    /// <summary>Remove a player from every rank and sub-class file of a faction.</summary>
    public async Task<MembershipDecision> LeaveAsync(FactionDefinition faction, string player, CancellationToken ct = default)
    {
        var removed = false;

        // Every file, not just their current rank: a member listed in two rank files
        // (which the storage permits) would otherwise be half-removed and reappear.
        foreach (var file in faction.RankFiles.Values.Concat(faction.Subclasses.Values))
        {
            var roster = Read(file);
            if (roster is null) continue;

            var next = roster.Where(p => !string.Equals(p, player, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == roster.Count) continue;

            if (await WriteAsync(file, next, ct: ct).ConfigureAwait(false)) removed = true;
        }

        return removed
            ? MembershipDecision.Allow()
            : new MembershipDecision(MembershipOutcome.NotWhitelisted);
    }

    /// <summary>Move a member one step up (+1) or down (-1).</summary>
    public async Task<MembershipDecision> ChangeRankAsync(
        FactionDefinition faction, string player, int direction, CancellationToken ct = default)
    {
        var membership = await FindAsync(player, ct).ConfigureAwait(false);

        var decision = MembershipRules.ChangeRank(faction, membership?.Rank, direction, Counter(faction));
        if (!decision.IsAllowed) return decision;

        /* Remove from EVERY rank file before adding to the target. Removing only from their
           current rank leaves a stale entry behind whenever the storage already had them in
           two, and then a promotion silently gives them two ranks. */
        foreach (var (rank, file) in faction.RankFiles)
        {
            if (string.Equals(rank, decision.Rank, StringComparison.OrdinalIgnoreCase)) continue;

            var roster = Read(file);
            if (roster is null || !roster.Contains(player, StringComparer.OrdinalIgnoreCase)) continue;

            await WriteAsync(file, roster.Where(p => !string.Equals(p, player, StringComparison.OrdinalIgnoreCase)).ToList(), ct: ct)
                .ConfigureAwait(false);
        }

        var target = faction.RankFiles[decision.Rank!];
        var targetRoster = Read(target) ?? [];
        if (!targetRoster.Contains(player, StringComparer.OrdinalIgnoreCase))
            await WriteAsync(target, [.. targetRoster, player], ct: ct).ConfigureAwait(false);

        return decision;
    }

    /// <summary>Assign or remove a sub-class, which is additive to the member's rank.</summary>
    public async Task<MembershipDecision> ChangeSubclassAsync(
        FactionDefinition faction, string player, string subclass, bool removing, CancellationToken ct = default)
    {
        var held = faction.Subclasses
            .Where(s => (Read(s.Value) ?? []).Contains(player, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Key).ToList();

        var decision = MembershipRules.ChangeSubclass(faction, subclass, held, removing);
        if (!decision.IsAllowed) return decision;

        var file = faction.Subclasses[subclass];
        var roster = Read(file) ?? [];

        var next = removing
            ? roster.Where(p => !string.Equals(p, player, StringComparison.OrdinalIgnoreCase)).ToList()
            : [.. roster, player];

        return await WriteAsync(file, next, ct: ct).ConfigureAwait(false)
            ? decision
            : new MembershipDecision(MembershipOutcome.NoSuchSubclass);
    }
}
