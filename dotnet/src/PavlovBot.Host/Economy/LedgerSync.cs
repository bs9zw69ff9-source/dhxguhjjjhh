using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Economy;

/// <summary>
/// Keeping a player's caps ledger identical across every Pavlov install.
/// </summary>
/// <remarks>
/// NEVER PORTED. The Node bot copies a player's ledger file onto every install when they
/// join and again when they leave, so their money follows them between servers. The C# bot
/// had no equivalent at all: each install kept its own copy, whichever server they played on
/// last had the real balance, and every other one was stale. From the outside that looks
/// like money vanishing on server 2 and files never appearing on server 3.
///
/// THE ZERO GUARD IS THE PART THAT MATTERS. A ledger that is empty or "0" is never copied
/// over one holding a positive balance. That case is not hypothetical: a server writes 0 for
/// a player it has never seen, and without the guard the first sync after they join a new
/// install would push that 0 over the balance they actually have. The rule is deliberately
/// asymmetric - a real number may overwrite a zero, a zero may never overwrite a real number.
///
/// MTIME DECIDES which copy is newest, and it is PRESERVED on the copies so the next sweep
/// agrees about which one that is. Copying without it would make every install look freshly
/// written, and the newest would then be whichever one was synced last.
/// </remarks>
public sealed class LedgerSync(
    IReadOnlyList<string> installs,
    string? ledgerDirectory,
    ILogger<LedgerSync> logger,
    bool enabled = true)
{
    /// <summary>
    /// How much newer a source must be before it is copied.
    /// </summary>
    /// <remarks>
    /// Filesystem timestamps are not identical across a copy, and without a margin two
    /// installs would take turns overwriting each other on every sweep forever.
    /// </remarks>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

    public bool Enabled => enabled && installs.Count > 1 && !string.IsNullOrWhiteSpace(ledgerDirectory);

    /// <summary>
    /// The same file inside every install.
    /// </summary>
    /// <remarks>
    /// PURE. Takes a path inside one install and rewrites its root for each of the others,
    /// which only works because every install has the same layout below its root - and
    /// returns just the original when the path is not inside any of them, so a misconfigured
    /// ledger directory syncs nothing rather than scattering copies.
    /// </remarks>
    internal static IReadOnlyList<string> Mirror(IReadOnlyList<string> installs, string path)
    {
        ArgumentNullException.ThrowIfNull(installs);

        var source = installs.FirstOrDefault(b =>
            path.Equals(b, StringComparison.Ordinal) ||
            path.StartsWith(b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        if (source is null) return [path];

        var trimmed = source.TrimEnd(Path.DirectorySeparatorChar);
        var relative = path[trimmed.Length..];

        return [.. new[] { path }
            .Concat(installs.Where(b => !b.Equals(source, StringComparison.Ordinal))
                            .Select(b => b.TrimEnd(Path.DirectorySeparatorChar) + relative))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether copying this content over that file would destroy a real balance.
    /// </summary>
    /// <remarks>
    /// PURE, and the single most important rule here. A blank or "0" source is exactly what
    /// an install that has never seen the player writes, and letting it win would zero
    /// somebody's balance the first time they hopped servers.
    /// </remarks>
    internal static bool WouldWipe(string source, string? destination)
    {
        var from = source.Trim();
        if (from.Length > 0 && from != "0") return false;   // real content - safe to copy

        var to = (destination ?? "").Trim();
        return to.Length > 0 && to.All(char.IsAsciiDigit) && to != "0" && to.TrimStart('0').Length > 0;
    }

    /// <summary>Copy the newest copy of one player's ledger onto every other install.</summary>
    public int Sync(string playerName)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(playerName)) return 0;

        var safe = new string(playerName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
        if (safe.Length == 0) return 0;

        var paths = Mirror(installs, Path.Combine(ledgerDirectory!, $"{safe}.txt"));
        if (paths.Count < 2) return 0;

        // The newest file wins. A player's balance is written by whichever server they were
        // last on, so "most recently written" is precisely "most correct".
        (string Path, DateTime At)? newest = null;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var at = File.GetLastWriteTimeUtc(path);
                if (newest is null || at > newest.Value.At) newest = (path, at);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (newest is not { } source) return 0;   // no ledger anywhere yet

        string content;
        try { content = File.ReadAllText(source.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }

        var synced = 0;
        foreach (var path in paths)
        {
            if (path.Equals(source.Path, StringComparison.Ordinal)) continue;
            if (Copy(path, content, source.At)) synced++;
        }

        if (synced > 0) logger.LogInformation("Ledger for {Player} synced to {Count} install(s)", safe, synced);
        return synced;
    }

    private bool Copy(string destination, string content, DateTime sourceWrittenAt)
    {
        try
        {
            if (File.Exists(destination))
            {
                if (File.GetLastWriteTimeUtc(destination) >= sourceWrittenAt - Margin) return false;   // already current

                if (WouldWipe(content, File.ReadAllText(destination)))
                {
                    logger.LogWarning(
                        "Refusing to sync an empty balance over {Destination}, which holds a real one. " +
                        "That install has the newer file and something wrote a zero elsewhere", destination);
                    return false;
                }
            }
            else if (WouldWipe(content, null))
            {
                return false;   // nothing there and nothing worth putting there
            }

            /* Only into a directory that already exists, one level at most - the same rule
               every other game-file write follows, and for the same reason: a wrong ledger
               path would otherwise scatter a ModSave tree across the disk. */
            if (!Storage.GameFiles.Prepare(destination, out var problem))
            {
                logger.LogWarning("Not syncing to {Destination}: {Problem}", destination, problem);
                return false;
            }

            var temp = destination + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, destination, overwrite: true);

            // Preserved so the next sweep still agrees which copy is newest.
            File.SetLastWriteTimeUtc(destination, sourceWrittenAt);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Ledger sync to {Destination} failed: {Message}", destination, ex.Message);
            return false;
        }
    }
}
