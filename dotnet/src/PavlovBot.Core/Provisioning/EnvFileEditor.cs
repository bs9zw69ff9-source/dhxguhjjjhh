using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PavlovBot.Core.Provisioning;

/// <summary>
/// Setting and clearing keys in a <c>.env</c> without leaving the old lines behind.
/// </summary>
/// <remarks>
/// APPEND-ONLY WAS NOT ENOUGH. Adding and removing a server used to append a block either way,
/// relying on last-key-wins to make the newest line the effective one. That is correct and it is
/// unreadable: a slot provisioned and deleted a few times left half a dozen <c>RCON_HOST_2</c>
/// lines stacked up, and an operator opening the file could not tell which one the bot was
/// actually using, or that a deleted server's host was still sitting there in plain sight.
///
/// So this rewrites instead. Every existing assignment of a named key is REMOVED - all of them,
/// not just the last - and the new value is written once at the end. Clearing a key removes its
/// lines and writes nothing, so a deleted server leaves no trace rather than an empty override.
///
/// EVERY OTHER LINE IS PRESERVED BYTE FOR BYTE. The file is hand-maintained and full of comments
/// explaining why settings are what they are; this only ever touches lines that assign a key it
/// was given.
/// </remarks>
public static partial class EnvFileEditor
{
    /// <summary>
    /// The marker on comment lines this writes, so its own headers can be cleaned up on the next
    /// pass rather than accumulating beside the keys they used to describe.
    /// </summary>
    public const string Marker = "# pavlov-bot:";

    /// <summary>Matches a line assigning KEY, in either form the dotenv grammar accepts.</summary>
    /// <remarks>
    /// Mirrors the reader: an optional <c>export</c>, then <c>KEY=</c> or <c>KEY: </c>. Case
    /// insensitive because the reader's dictionary is, so <c>rcon_host_2</c> and
    /// <c>RCON_HOST_2</c> are one setting and clearing one must clear both.
    /// </remarks>
    private static Regex Assignment(string key) =>
        new($@"^\s*(?:export\s+)?{Regex.Escape(key)}\s*(?:=|:\s)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"^\s*# pavlov-bot:", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerComment { get; }

    /// <summary>
    /// Rewrite <paramref name="contents"/> with these keys set - or removed, where the value is
    /// null - and nothing of their previous lines left.
    /// </summary>
    /// <param name="contents">The current file.</param>
    /// <param name="values">Keys to set, or to remove entirely when the value is null.</param>
    /// <param name="note">A short line describing the change, written above the new keys.</param>
    public static string Set(string contents, IReadOnlyList<KeyValuePair<string, string?>> values, string note)
    {
        ArgumentNullException.ThrowIfNull(values);

        var matchers = values.Select(v => Assignment(v.Key)).ToList();

        /* Split on '\n' having normalised CRLF, so a file written on Windows is not left with
           stray '\r' on the lines that survive. */
        var lines = (contents ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var kept = lines
            .Where(line => !matchers.Any(m => m.IsMatch(line)))
            .Where(line => !MarkerComment.IsMatch(line))
            .ToList();

        // One trailing blank at most, so repeated edits do not walk the file downwards.
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);

        var written = values.Where(v => v.Value is not null).ToList();
        if (written.Count == 0)
        {
            // Everything asked for was a removal: the file just loses those lines.
            return string.Join("\n", kept) + "\n";
        }

        var sb = new StringBuilder();
        sb.Append(string.Join("\n", kept));
        sb.Append("\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"{Marker} {note}\n");
        foreach (var (key, value) in written)
            sb.Append(CultureInfo.InvariantCulture, $"{key}={value}\n");

        return sb.ToString();
    }

    /// <summary>A dated note for the header line, so the file says when it was last machine-edited.</summary>
    public static string Note(string what, DateOnly date) =>
        $"{what} on {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}
