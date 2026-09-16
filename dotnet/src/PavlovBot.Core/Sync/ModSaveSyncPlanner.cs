namespace PavlovBot.Core.Sync;

/// <summary>One install's ModSave tree, as relative paths to their modified time.</summary>
/// <param name="Root">The install root, echoed back on every copy that comes from it.</param>
/// <param name="Files">Relative path (forward-slashed) -> last-modified, Unix milliseconds.</param>
public sealed record ModSaveSnapshot(string Root, IReadOnlyDictionary<string, long> Files);

/// <summary>A single file to copy from one install into another.</summary>
/// <param name="RelPath">The path relative to each install's ModSave root.</param>
/// <param name="FromRoot">The install holding the newest copy.</param>
/// <param name="ToRoot">The install whose copy is stale or absent.</param>
/// <param name="ModifiedUnixMs">The source's modified time, stamped onto the destination so the two then compare equal.</param>
public sealed record ModSaveCopy(string RelPath, string FromRoot, string ToRoot, long ModifiedUnixMs);

/// <summary>
/// Which files to propagate to keep every install's ModSave tree identical.
/// </summary>
/// <remarks>
/// A PORT OF THE NODE BOT'S <c>syncAllModSave</c>, and it stays a port on purpose: this is
/// the thing that stopped a player's caps differing between server 1 and server 2, and a
/// re-derivation that drifts from what the old bot did is how that difference comes back.
///
/// CONVERGENT NEWEST-WINS. For every relative path, the copy with the most recent modified
/// time is the source, and it is propagated to every install whose copy is older or missing.
/// The destination is stamped with the source's modified time, so once two installs hold the
/// same file they compare equal and the sweep stops touching them - without that, two clocks
/// a few milliseconds apart would copy back and forth forever.
///
/// PURE, AND mtime-ONLY BY DESIGN. The money and online guards need file CONTENT, and reading
/// every file in every install on every sweep is the cost this is shaped to avoid: the
/// planner decides candidates from modified times alone, and the caller reads content only
/// for the handful of files a candidate actually touches. The content guards live beside this
/// as their own pure helpers - see <see cref="LedgerFile"/> - so the whole decision is
/// testable without a disk.
///
/// IT NEVER PROPOSES A DELETE. A file present in one install and not another is copied IN, not
/// removed from the one that has it. A sync that deleted would turn one install's missing file
/// into everyone's missing file, and there is no undo on a player's balance.
/// </remarks>
public static class ModSaveSyncPlanner
{
    /// <summary>
    /// Filesystem timestamps are not infinitely precise, and a copy re-stamps the destination
    /// from the source; a small tolerance stops a sub-tick difference reading as "stale" and
    /// bouncing the file between installs on every sweep.
    /// </summary>
    public const long DefaultToleranceMs = 2000;

    /// <summary>
    /// The copies that would make every install's ModSave tree hold the newest of each file.
    /// </summary>
    /// <param name="installs">One snapshot per install. Fewer than two is nothing to sync.</param>
    /// <param name="skip">
    /// A relative path this returns true for is never mirrored. The RCON+ menu-access files
    /// are managed live and blindly newest-winning them wipes a player's menu, so they are the
    /// reason this parameter exists.
    /// </param>
    /// <param name="toleranceMs">See <see cref="DefaultToleranceMs"/>.</param>
    public static IReadOnlyList<ModSaveCopy> Candidates(
        IReadOnlyList<ModSaveSnapshot> installs,
        Func<string, bool>? skip = null,
        long toleranceMs = DefaultToleranceMs)
    {
        ArgumentNullException.ThrowIfNull(installs);
        if (installs.Count < 2) return [];

        // rel -> the install holding its newest copy, and that copy's modified time.
        var newest = new Dictionary<string, (string Root, long Modified)>(StringComparer.Ordinal);
        foreach (var install in installs)
        {
            foreach (var (rel, modified) in install.Files)
            {
                if (skip is not null && skip(rel)) continue;

                if (!newest.TryGetValue(rel, out var winner) || modified > winner.Modified)
                    newest[rel] = (install.Root, modified);
            }
        }

        var copies = new List<ModSaveCopy>();
        foreach (var (rel, winner) in newest)
        {
            foreach (var install in installs)
            {
                if (string.Equals(install.Root, winner.Root, StringComparison.Ordinal)) continue;

                // Absent is a copy; present-and-recent is not. The tolerance is one-sided:
                // a destination as new as the source, within the window, is left alone.
                var stale = !install.Files.TryGetValue(rel, out var destModified)
                    || destModified < winner.Modified - toleranceMs;
                if (!stale) continue;

                copies.Add(new ModSaveCopy(rel, winner.Root, install.Root, winner.Modified));
            }
        }

        // Deterministic order so the sweep's log and the tests read the same every run.
        copies.Sort(static (a, b) =>
        {
            var byRel = string.CompareOrdinal(a.RelPath, b.RelPath);
            return byRel != 0 ? byRel : string.CompareOrdinal(a.ToRoot, b.ToRoot);
        });
        return copies;
    }
}
