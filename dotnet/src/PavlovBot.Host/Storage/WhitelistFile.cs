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
    /// Refuses. whitelist.txt belongs to the game server and the bot does not write it.
    /// </summary>
    /// <remarks>
    /// Reading is unaffected, so <c>/tester list</c> still shows who is on it and the
    /// command can still tell an admin the exact line to add by hand.
    /// </remarks>
    public Task<WhitelistResult> AddAsync(string path, string entry, CancellationToken ct = default) =>
        Task.FromResult(Refused(path));

    public Task<WhitelistResult> RemoveAsync(string path, string entry, CancellationToken ct = default) =>
        Task.FromResult(Refused(path));

    private WhitelistResult Refused(string path)
    {
        logger.LogWarning("Not writing {Path}: {Refusal}", path, GameFiles.Refusal("whitelist.txt"));
        return new WhitelistResult(path, Ok: false, Changed: false, GameFiles.Refusal("whitelist.txt"));
    }
}
