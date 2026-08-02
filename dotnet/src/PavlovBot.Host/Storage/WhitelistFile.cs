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

    /// <summary>
    /// Add an entry if it is not already present.
    /// </summary>
    /// <remarks>
    /// APPENDED to whatever is already in the file, never rewritten from a parsed copy -
    /// anything the parser did not understand would otherwise be dropped on the first edit.
    /// </remarks>
    public async Task<WhitelistResult> AddAsync(string path, string entry, CancellationToken ct = default)
    {
        try
        {
            var existing = File.Exists(path)
                ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)
                : "";

            if (Entries(existing.Split('\n')).Contains(entry, StringComparer.OrdinalIgnoreCase))
                return new WhitelistResult(path, Ok: true, Changed: false);

            // A file that does not end in a newline would otherwise join its last entry to
            // the new one, silently corrupting both.
            var body = existing.Length > 0 && !existing.EndsWith('\n') ? existing + "\n" : existing;

            await WriteAsync(path, body + entry + "\n", ct).ConfigureAwait(false);
            return new WhitelistResult(path, Ok: true, Changed: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* ANY failure is reported rather than thrown. A bad path throws ArgumentException
               rather than IOException, and one install being misconfigured must not stop the
               other two being written - the reply says which one did not take. */
            logger.LogWarning("Could not add to {Path}: {Message}", path, ex.Message);
            return new WhitelistResult(path, Ok: false, Changed: false, ex.Message);
        }
    }

    /// <summary>Remove every line matching the entry, leaving everything else untouched.</summary>
    public async Task<WhitelistResult> RemoveAsync(string path, string entry, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return new WhitelistResult(path, Ok: true, Changed: false);

            var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            var kept = lines.Where(l => !string.Equals(l.Trim(), entry, StringComparison.OrdinalIgnoreCase)).ToList();

            if (kept.Count == lines.Length) return new WhitelistResult(path, Ok: true, Changed: false);

            await WriteAsync(path, string.Join("\n", kept).TrimEnd('\n') + "\n", ct).ConfigureAwait(false);
            return new WhitelistResult(path, Ok: true, Changed: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not remove from {Path}: {Message}", path, ex.Message);
            return new WhitelistResult(path, Ok: false, Changed: false, ex.Message);
        }
    }

    /// <summary>
    /// Temp file, then rename.
    /// </summary>
    /// <remarks>
    /// A rename within a filesystem is atomic, so a reader - which here is a game server
    /// that may load this file at any map change - sees either the old file or the new one,
    /// never a half-written one.
    /// </remarks>
    private static async Task WriteAsync(string path, string contents, CancellationToken ct)
    {
        /* NOT CreateDirectory. Config/ is the game's directory and already exists on a real
           install, so a missing one means this install root is wrong - and creating it
           builds a Pavlov tree the game never reads, which /tester would then report as a
           successful grant. */
        if (!GameFiles.Prepare(path, out var problem)) throw new DirectoryNotFoundException(problem);

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, contents, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
