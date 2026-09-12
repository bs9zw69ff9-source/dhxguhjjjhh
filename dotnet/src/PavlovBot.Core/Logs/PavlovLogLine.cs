using System.Globalization;
using System.Text.RegularExpressions;

namespace PavlovBot.Core.Logs;

/// <param name="Name">
/// Some close lines carry <c>?Name=</c> as well. Null when this one did not.
/// </param>
/// <summary>An address and account seen together on ONE line - a certain pairing.</summary>
public sealed record ConfirmedPairing(string Ip, string Id, string? Name = null);

/// <param name="Name">May be null: some login lines carry only the id.</param>
public sealed record LoginRequest(string? Name, string Id);

/// <param name="Killer">Empty for a world kill (fall damage, explosion with no owner).</param>
public sealed record KillEvent(string? Killer, string? Killed, string? KilledBy);

/// <summary>
/// Parsing one line of Pavlov.log.
/// </summary>
/// <remarks>
/// Every pattern here was validated against real server logs, and the distinctions they
/// draw are the whole basis of the ban-evasion system:
///
///   A CONFIRMED PAIRING is an address and a UniqueId on the SAME line - a disconnect
///   line, typically. That pairing is CERTAIN and is the only thing allowed to flag an
///   address or trigger an auto-ban.
///
///   A JOIN-TIME ADDRESS is an "accept" line correlated to a later "login" line. That is a
///   GUESS. It is kept for display and never used to flag, because a wrong guess bans a
///   stranger - and the guess is wrong exactly when two people connect at once, which is
///   normal on a busy server.
///
/// Every extractor is field-order independent. Pavlov's field order is not stable across
/// builds, and a positional parse breaks silently on an update - the log still parses, it
/// just stops finding anyone.
/// </remarks>
public static partial class PavlovLog
{
    [GeneratedRegex(@"^\[(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2}):(\d{3})\]")]
    private static partial Regex Timestamp { get; }

    [GeneratedRegex(@"(?:NotifyAcceptingConnection accepted from:|NotifyAcceptedConnection:.*?RemoteAddr:|AddClientConnection:.*?RemoteAddr:)\s*((?:\d{1,3}\.){3}\d{1,3})")]
    private static partial Regex Accept { get; }

    [GeneratedRegex(@"RemoteAddr:\s*((?:\d{1,3}\.){3}\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex IpField { get; }

    [GeneratedRegex(@"UniqueId:\s*([^\s,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UniqueIdField { get; }

    /* The value ends at the next URL option, at a following "field: value" token, or at
       end of line. Real lines are `?Name=Bob userId: EOS:...` with no separator before
       userId, so a lazy match to the next `?` or `&` alone would swallow the id. */
    [GeneratedRegex(@"[?&]Name=([^?&]+?)(?=[?&]|\s+(?:userId|userid|PlayerId|UniqueId|platform|pid)\s*[:=]|\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex NameOption { get; }

    [GeneratedRegex(@"(?:userId|PlayerId|UniqueId|userid)\s*[:=]\s*([^\s,?&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginId { get; }

    [GeneratedRegex(@"Login request:|Join request:", RegexOptions.IgnoreCase)]
    private static partial Regex LoginMarker { get; }

    [GeneratedRegex(@"Rcon:\s*BanPlayer\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex RconBan { get; }

    [GeneratedRegex(@"Rcon:\s*UnbanPlayer\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex RconUnban { get; }

    /* RCON+ (the menu mod) announces every command it runs, as either
         LogTemp: Warning: Rcon Plus Command Executed: GiveMenu SomePlayer
         LogTemp: Error:   Rcon Plus Command Executed: <whatever was typed>
       The Warning/Error level is just how the mod prints - not a failure - so both count. */
    [GeneratedRegex(@"Rcon Plus Command Executed:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RconPlus { get; }

    [GeneratedRegex(@"""Killer"":\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Killer { get; }

    [GeneratedRegex(@"""Killed"":\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Killed { get; }

    [GeneratedRegex(@"""KilledBy"":\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex KilledBy { get; }

    [GeneratedRegex(@"^(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)$")]
    private static partial Regex StrictIpv4 { get; }

    /// <summary>
    /// The line's own timestamp, in UTC.
    /// </summary>
    /// <remarks>
    /// Pavlov writes UTC in <c>[YYYY.MM.DD-HH.MM.SS:mmm]</c>. Using the line's timestamp
    /// rather than the read time is what makes the correlation window meaningful when the
    /// tailer catches up on a backlog: read time would put a thousand lines in the same
    /// instant and correlate every join to the last address seen.
    /// </remarks>
    public static DateTimeOffset? TimestampOf(string line)
    {
        var match = Timestamp.Match(line);
        if (!match.Success) return null;

        static int G(Match m, int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        try
        {
            return new DateTimeOffset(
                G(match, 1), G(match, 2), G(match, 3), G(match, 4), G(match, 5), G(match, 6),
                G(match, 7), TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;   // a corrupt timestamp is not a reason to stop reading the log
        }
    }

    /// <summary>An incoming connection's address, before anybody has authenticated.</summary>
    public static string? AcceptedAddress(string line)
    {
        var match = Accept.Match(line);
        return match.Success && IsIpv4(match.Groups[1].Value) ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// An address and account on the same line. This pairing is CERTAIN.
    /// </summary>
    /// <remarks>
    /// Order independent by design: any line carrying both a RemoteAddr and a UniqueId is
    /// a confirmed pairing regardless of which comes first.
    /// </remarks>
    public static ConfirmedPairing? Confirmed(string line)
    {
        var ip = IpField.Match(line);
        var id = UniqueIdField.Match(line);
        if (!ip.Success || !id.Success) return null;

        var cleaned = CleanId(id.Groups[1].Value);
        if (cleaned.Length == 0 || !IsIpv4(ip.Groups[1].Value)) return null;

        /* Some close lines carry ?Name= as well, and taking it matters more than it looks:
           an account first seen on a DISCONNECT - anybody already connected when the bot
           started - otherwise has no name at all, and a leave line for a nameless account is
           dropped. That is every player online across a restart, silently. */
        var name = NameOption.Match(line);

        return new ConfirmedPairing(ip.Groups[1].Value, cleaned,
            name.Success && name.Groups[1].Value.Trim() is { Length: > 0 } who ? who : null);
    }

    /// <summary>A login or join request: an id, and a name when the line carries one.</summary>
    public static LoginRequest? Login(string line)
    {
        if (!LoginMarker.IsMatch(line)) return null;

        var id = LoginId.Match(line);
        if (!id.Success) return null;

        var cleaned = CleanId(id.Groups[1].Value);
        if (cleaned.Length == 0) return null;

        var name = NameOption.Match(line);
        return new LoginRequest(name.Success ? name.Groups[1].Value.Trim() : null, cleaned);
    }

    public static string? BannedByRcon(string line) => RconBan.Match(line) is { Success: true } m ? m.Groups[1].Value : null;
    public static string? UnbannedByRcon(string line) => RconUnban.Match(line) is { Success: true } m ? m.Groups[1].Value : null;
    public static string? RconPlusCommand(string line) => RconPlus.Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// A kill record. Requires <c>bVerboseLogging=true</c> in Game.ini.
    /// </summary>
    /// <remarks>
    /// Pavlov logs this either as one JSON line or split across several, so each field is
    /// matched independently and a line contributing any of them is reported. The caller
    /// assembles partial records.
    /// </remarks>
    public static KillEvent? Kill(string line)
    {
        var killer = Killer.Match(line);
        var killed = Killed.Match(line);
        var by = KilledBy.Match(line);
        if (!killer.Success && !killed.Success && !by.Success) return null;

        return new KillEvent(
            killer.Success ? killer.Groups[1].Value : null,
            killed.Success ? killed.Groups[1].Value : null,
            by.Success ? by.Groups[1].Value : null);
    }

    /// <summary>
    /// Normalise an account id: "NULL:abc123" becomes "abc123", punctuation removed.
    /// </summary>
    /// <remarks>
    /// The trailing-punctuation strip is not cosmetic - NetworkFailure lines quote the id,
    /// and an id with a stray apostrophe on it does not match the same account seen
    /// anywhere else, so an evader's flag would silently miss.
    /// </remarks>
    public static string CleanId(string? raw)
    {
        var text = raw ?? "";
        var colon = text.LastIndexOf(':');
        if (colon >= 0) text = text[(colon + 1)..];
        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>
    /// Ids that identify nobody: a pre-authentication placeholder or the server's own
    /// connection to itself. Tracking either attributes real addresses to a phantom account.
    /// </summary>
    public static bool IsPlaceholderId(string? id) =>
        string.IsNullOrEmpty(id) ||
        id.Contains("INVALID", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("localhost-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Octet-bounded, so "999.1.1.1" is not treated as an address.</summary>
    public static bool IsIpv4(string? value) => value is not null && StrictIpv4.IsMatch(value);
}
