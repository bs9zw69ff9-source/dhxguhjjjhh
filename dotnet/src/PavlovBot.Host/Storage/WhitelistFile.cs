using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Storage;

/// <param name="Path">The file this refers to, so a failure names which install.</param>
/// <param name="Changed">False when the entry was already there, or already gone.</param>
public sealed record WhitelistResult(string Path, bool Ok, bool Changed, string? Error = null)
{
    /// <summary>Just the install directory, which is what a person recognises.</summary>
    public string Install
    {
        get
        {
            // .../pavlovserver2/Pavlov/Saved/Config/whitelist.txt -> pavlovserver2
            var directory = System.IO.Path.GetDirectoryName(Path);
            for (var i = 0; i < 3 && directory is not null; i++) directory = System.IO.Path.GetDirectoryName(directory);
            return System.IO.Path.GetFileName(directory ?? "") is { Length: > 0 } name ? name : Path;
        }
    }
}

/// <summary>
/// Pavlov's own <c>whitelist.txt</c>, across every install.
/// </summary>
/// <remarks>
/// This is the file an operator edits with <c>nano</c>, and everything here exists to make
/// the bot no more dangerous than that:
///
///   THE WRITE IS ATOMIC - a temp file and a rename. A crash or a full disk part-way
///   through a direct write leaves a TRUNCATED whitelist, which on a whitelisted server
///   locks out everybody below the cut. The rename either happens or it does not.
///
///   EVERY OTHER LINE IS PRESERVED, including comments and blanks, because the file is
///   hand-maintained and rewriting it from the bot's idea of the contents would silently
///   delete entries added with nano.
///
///   EACH INSTALL IS REPORTED SEPARATELY. "Added" when one of three servers rejected the
///   write is the report that gets somebody told their access works when it does not.
/// </remarks>
public sealed class WhitelistFile(ILogger<WhitelistFile> logger)
{
    /// <summary>Read the entries, ignoring blanks and comments.</summary>
    public IReadOnlyList<string> Read(string path)
    {
        try
        {
            return File.Exists(path) ? Entries(File.ReadAllLines(path)) : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not read {Path}: {Message}", path, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// A username, reduced to what may safely be one line of this file.
    /// </summary>
    /// <remarks>
    /// NOT <c>Sanitize.Id</c>, which strips everything outside [a-zA-Z0-9_-.]. That is the
    /// right rule for an RCON argument and the wrong one here: the file is matched against
    /// the player's actual in-game name, so a name containing a space would be written
    /// without it and would then match nobody.
    ///
    /// The only character that MUST go is a newline. The file is one entry per line, so a
    /// name containing one would write two entries - which is how somebody whitelists a
    /// second account by asking to be whitelisted once.
    /// </remarks>
    public static string Entry(string? raw)
    {
        var text = (raw ?? "").Replace('\r', ' ').Replace('\n', ' ');

        // Control characters would be invisible in the file and in the reply confirming it.
        text = new string([.. text.Where(c => !char.IsControl(c))]).Trim();

        return text.Length > 64 ? text[..64].Trim() : text;
    }

    /// <summary>The meaningful lines. Pure, and the only place the format is interpreted.</summary>
    internal static IReadOnlyList<string> Entries(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return [.. lines
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith("//", StringComparison.Ordinal))];
    }

    /// <summary>Serialised per FILE, so two admins editing one install cannot interleave.</summary>
    /// <remarks>
    /// A read-modify-write over a file the game reads live. Without this, two concurrent
    /// adds both read the old contents and the second write drops the first entry - the
    /// classic lost update, and invisible afterwards because the file looks well-formed.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Add an entry, if it is not already there.</summary>
    public Task<WhitelistResult> AddAsync(string path, string entry, CancellationToken ct = default) =>
        EditAsync(path, entry, removing: false, ct);

    /// <summary>Remove an entry, if it is there.</summary>
    public Task<WhitelistResult> RemoveAsync(string path, string entry, CancellationToken ct = default) =>
        EditAsync(path, entry, removing: true, ct);

    /// <summary>
    /// One line added or removed, with every other line left exactly as it was.
    /// </summary>
    /// <remarks>
    /// A LINE EDIT, NOT A REWRITE. The file is hand-maintained: it has comments in it saying
    /// who somebody is and why they were added, and blank lines separating groups. Rebuilding
    /// it from <see cref="Read"/> - which drops both - would silently delete all of that on
    /// the first add. So the original lines are carried through untouched and exactly one is
    /// inserted or dropped.
    ///
    /// THE GUARD IS ON THE SIZE OF THE CHANGE, borrowed from the faction rosters, and it
    /// earns its place here for the same reason: an empty whitelist is a perfectly valid file
    /// that the server accepts without complaint, and on a whitelisted server it locks out
    /// every single player. A bug that produced one would show up as "the server is broken".
    /// </remarks>
    private async Task<WhitelistResult> EditAsync(string path, string entry, bool removing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return new WhitelistResult(path, Ok: false, Changed: false, "there was no usable name to write");

        /* Only into a directory that already exists, and never into one the bot would have
           to create. A missing one means the configured install path is wrong, and building
           it produces a tree the game server never reads. */
        if (GameFiles.Problem(path) is { } problem)
        {
            logger.LogWarning("Not writing {Path}: {Problem}", path, problem);
            return new WhitelistResult(path, Ok: false, Changed: false, problem);
        }

        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var original = File.Exists(path) ? await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false) : [];
            var before = Entries(original);

            var present = before.Contains(entry, StringComparer.OrdinalIgnoreCase);
            if (present == !removing)
            {
                // Already in the state asked for. Reported, not written: rewriting a file the
                // game reads live to change nothing is all risk and no benefit.
                return new WhitelistResult(path, Ok: true, Changed: false);
            }

            var next = removing
                ? original.Where(l => !string.Equals(l.Trim(), entry, StringComparison.OrdinalIgnoreCase)).ToList()
                : [.. original, entry];

            var verdict = PavlovBot.Core.Data.RosterWriteGuard.Evaluate(before, Entries(next));
            if (!verdict.IsAllowed)
            {
                logger.LogError("REFUSED write to {Path}: {Reason}. The whitelist is untouched.", path, verdict.Explanation);
                return new WhitelistResult(path, Ok: false, Changed: false, verdict.Explanation);
            }

            Backup(path, original);

            /* ATOMIC. A crash or a full disk part-way through a direct write leaves a
               TRUNCATED whitelist, which on a whitelisted server locks out everybody below
               the cut. The rename either happens or it does not. */
            var temp = $"{path}.bot.tmp";
            await File.WriteAllTextAsync(temp, string.Join("\n", next) + "\n", ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            logger.LogInformation("whitelist {Action} | \"{Entry}\" | {Path}",
                removing ? "remove" : "add", entry, path);

            return new WhitelistResult(path, Ok: true, Changed: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Write failed for {Path}", path);
            return new WhitelistResult(path, Ok: false, Changed: false, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// One rolling pre-write copy, OUTSIDE the game tree.
    /// </summary>
    /// <remarks>
    /// Best effort - a failed backup never blocks the write. Kept outside the install
    /// deliberately: a <c>.bak</c> sitting next to whitelist.txt is one glob away from being
    /// read as a whitelist, and the bot does not create directories in there anyway.
    /// </remarks>
    private void Backup(string path, IReadOnlyList<string> contents)
    {
        try
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "whitelist_bak");
            Directory.CreateDirectory(directory);

            // Named for the install, so three servers do not overwrite each other's copy.
            var install = new WhitelistResult(path, Ok: true, Changed: false).Install;
            File.WriteAllText(
                Path.Combine(directory, $"{Sanitise(install)}-whitelist.txt.bak"),
                string.Join("\n", contents) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            logger.LogWarning(ex, "Could not write the whitelist backup for {Path}", path);
        }
    }

    /// <summary>An install name reduced to something safe as a filename.</summary>
    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Where(c => !invalid.Contains(c))]).Trim();
        return cleaned.Length > 0 ? cleaned : "install";
    }
}
