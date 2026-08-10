using System.Text.RegularExpressions;

namespace PavlovBot.Core.Intelligence;

/// <summary>
/// Removing IP addresses from text that is about to be shown to somebody who may not see them.
/// </summary>
/// <remarks>
/// THE LAST LINE, NOT THE FIRST. Nothing should be putting an address into a sentence that a
/// moderator without network access will read, and the services here do not. This exists
/// because "should not" is not a guarantee: the first version of <see cref="ProfileRedaction"/>
/// cleared the evidence field of a risk signal and left the summary alone, and a signal
/// reading "Shares 203.0.113.77 with banned player Bob" went straight through it.
///
/// An address in a Discord channel cannot be un-posted - deleting the message does not unsee
/// it - so this is one of the few places in the bot where belt and braces is the correct
/// amount of caution rather than clutter.
///
/// DELIBERATELY BROAD, and it will occasionally scrub something that is not an address. A
/// version number in a ban reason becoming "[address withheld]" is a cosmetic annoyance; the
/// failure in the other direction is somebody's home address in a public channel. When a
/// regex has to be wrong, it should be wrong in the direction that costs least.
/// </remarks>
public static partial class Addresses
{
    public const string Placeholder = "[address withheld]";

    /// <summary>Text with anything that looks like an IP address replaced.</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var scrubbed = Ipv4().Replace(text, Placeholder);
        return Ipv6().Replace(scrubbed, Placeholder);
    }

    /// <summary>Whether this text carries something that looks like an address.</summary>
    public static bool Contains(string? text) =>
        !string.IsNullOrEmpty(text) && (Ipv4().IsMatch(text) || Ipv6().IsMatch(text));

    /// <remarks>
    /// NOT ANCHORED, because it is searching inside a sentence rather than validating a field.
    /// The octet range is checked so that ordinary decimal runs - a version string, a score
    /// like 100.100.100.100 notwithstanding - are less likely to be caught, but the boundary
    /// assertions matter more: without them this matches four digits inside a longer number.
    /// </remarks>
    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d{2}|[1-9]?\d)\b")]
    private static partial Regex Ipv4();

    /// <remarks>
    /// Deliberately simple: two or more groups of hex separated by colons, which covers the
    /// compressed forms without trying to be a full IPv6 grammar. A stricter pattern here
    /// would fail closed in the wrong direction - an unusual but valid address it did not
    /// recognise would be printed.
    /// </remarks>
    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){2,7}(?::|[0-9a-fA-F]{1,4})\b")]
    private static partial Regex Ipv6();
}
