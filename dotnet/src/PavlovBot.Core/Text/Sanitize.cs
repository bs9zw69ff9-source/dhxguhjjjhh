using System.Text.RegularExpressions;

namespace PavlovBot.Core.Text;

/// <summary>
/// Turning attacker-controlled text into something safe to put in a command or a message.
/// </summary>
/// <remarks>
/// Every string here originates with a player: a display name, a ban reason, an
/// autocomplete pick. Two separate destinations need two separate treatments, and using
/// the wrong one is the bug:
///
///   RCON COMMANDS are space-delimited with no quoting, so a name containing a space or a
///   newline is not an escaping problem, it is a COMMAND INJECTION. <see cref="Id"/>
///   reduces to an alphabet that cannot express one.
///
///   PUBLIC MESSAGES leak whatever the bot prints. <see cref="RedactPrivate"/> is for the
///   update log, where a stack trace or a path would otherwise publish the server layout.
/// </remarks>
public static partial class Sanitize
{
    /// <summary>Autocomplete display labels the bot itself appends, stripped back off.</summary>
    [GeneratedRegex(@"\s*\((?:manual entry|offline)\)", RegexOptions.IgnoreCase)]
    private static partial Regex AutocompleteLabel { get; }

    [GeneratedRegex(@"\s*\[(?:s1|s2|s1\+s2)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ServerTag { get; }

    [GeneratedRegex(@"[^a-zA-Z0-9_\-.]")]
    private static partial Regex NotIdSafe { get; }

    /// <summary>
    /// A player name or id, reduced to what can safely be a single RCON argument.
    /// </summary>
    /// <remarks>
    /// An ALLOW-list, not a block-list. A block-list of "dangerous" characters is a list
    /// somebody has to keep complete forever; this can only ever emit letters, digits,
    /// underscore, hyphen and dot, so there is nothing left to think of. The 64-character
    /// cap bounds the command line regardless of what was pasted in.
    /// </remarks>
    public static string Id(string? raw)
    {
        var text = raw ?? "";
        text = AutocompleteLabel.Replace(text, "");
        text = ServerTag.Replace(text, "").Trim();
        text = NotIdSafe.Replace(text, "");
        return text.Length > 64 ? text[..64] : text;
    }

    [GeneratedRegex(@"[\r\n\t]")]
    private static partial Regex Whitespace { get; }

    [GeneratedRegex(@"[^\x20-\x7E]")]
    private static partial Regex NonPrintable { get; }

    /// <summary>
    /// A free-text message bound for an RCON broadcast.
    /// </summary>
    /// <remarks>
    /// Newlines become spaces rather than being dropped: the RCON protocol is line
    /// oriented, so an embedded newline would split one command into two, and the second
    /// half would be whatever the player wrote. Non-printables go entirely - they cannot
    /// render in game and are a reliable way to smuggle control characters past a
    /// reviewer reading the audit log.
    /// </remarks>
    public static string Message(string? raw)
    {
        var text = Whitespace.Replace(raw ?? "", " ");
        text = NonPrintable.Replace(text, "");
        return text.Length > 200 ? text[..200] : text;
    }

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex IPv4 { get; }

    [GeneratedRegex(@"(?<![\w:.])[0-9a-f:]{3,}(?![\w:])", RegexOptions.IgnoreCase)]
    private static partial Regex MaybeIPv6 { get; }

    [GeneratedRegex(@"\b[0-9a-f]{24,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex LongHex { get; }

    [GeneratedRegex(@"\b\d{17,20}\b")]
    private static partial Regex Snowflake { get; }

    [GeneratedRegex(@"/(?:home/[^\s/]+|root)(?=/|\b)")]
    private static partial Regex HomePath { get; }

    /// <summary>
    /// Scrub anything private out of text bound for a PUBLIC surface.
    /// </summary>
    /// <remarks>
    /// Covers IPv4, IPv6 (detected by colon count, which also catches <c>::</c>
    /// compression), long hex runs (tokens and EOS ids), Discord snowflakes, and absolute
    /// <c>/home/&lt;user&gt;</c> or <c>/root</c> paths.
    ///
    /// The IPv6 rule OVER-matches - a clock range like <c>12:30:45</c> is redacted too.
    /// That is the right trade for a changelog: a mangled timestamp is cosmetic, a
    /// published player IP is not. Short git hashes, version numbers and URLs pass through.
    /// </remarks>
    public static string RedactPrivate(string? text)
    {
        var output = IPv4.Replace(text ?? "", "[ip redacted]");
        output = MaybeIPv6.Replace(output, m => m.Value.Count(c => c == ':') >= 2 ? "[ip redacted]" : m.Value);
        output = LongHex.Replace(output, "[id redacted]");
        output = Snowflake.Replace(output, "[id redacted]");
        return HomePath.Replace(output, "[path redacted]");
    }

    /// <summary>
    /// Neutralise Discord markdown in a name so a player cannot inject formatting into a
    /// staff-facing message. Backticks become apostrophes rather than being escaped -
    /// escaping inside a code span does not work, the span just ends.
    /// </summary>
    public static string Code(string? name) => (name ?? "").Replace("`", "'", StringComparison.Ordinal);
}
