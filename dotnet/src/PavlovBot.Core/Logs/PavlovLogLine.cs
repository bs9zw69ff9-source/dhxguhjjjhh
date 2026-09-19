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

    /* The base RCON channel: `LogTemp: Rcon: <verb> [args]`. Anchored on the LogTemp prefix
       so a chat line or a name that happens to contain "Rcon:" cannot forge one, and it must
       NOT swallow the RCON+ line, which reads "Rcon Plus Command Executed:" - the ": " after
       the bare word Rcon is what separates them. */
    [GeneratedRegex(@"LogTemp:\s*Rcon:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RconBase { get; }

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
    /// One RCON action the game logged - a base RCON verb or an RCON+ menu command.
    /// </summary>
    /// <param name="Plus">True for an RCON+ menu command (Warp, Godmode, GiveItem, …), false for a base RCON verb (Ban, Kick, …).</param>
    /// <param name="Verb">
    /// The command word, e.g. "Ban" or "Godmode". A base-RCON verb Pavlov logs under an
    /// internal name (BanPlayer, KickPlayer, UnbanPlayer) is mapped back to the verb that was
    /// sent; see <see cref="Rcon"/>.
    /// </param>
    /// <param name="Instigator">
    /// The player who ran it, for RCON+ commands - the menu logs the acting player as the
    /// first argument, so <c>SetCash Bob 999</c> is Bob giving himself cash. Null for base RCON,
    /// where the line records no issuer (the caller is the RCON password holder - the bot, or a
    /// console).
    /// </param>
    /// <param name="Argument">The rest of the command after the verb and instigator, or null.</param>
    public sealed record RconAction(bool Plus, string Verb, string? Instigator, string? Argument)
    {
        /// <summary>The command as it reads: the verb and its argument, without the instigator.</summary>
        public string Command => Argument is { Length: > 0 } a ? $"{Verb} {a}" : Verb;

        /// <summary>
        /// The whole command exactly as it was executed - verb, instigator and argument
        /// rejoined - for matching a log line against a command the bot sent.
        /// </summary>
        public string Full => string.Join(' ',
            new[] { Verb, Instigator, Argument }.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>
    /// RCON+ verbs that act on the server, not a player, so their first argument is not an
    /// instigator. <c>CleanUp Items</c> is a cleanup of items, not something "Items" did.
    /// </summary>
    private static readonly HashSet<string> RconPlusServerVerbs =
        new(StringComparer.OrdinalIgnoreCase) { "CleanUp" };

    /// <summary>
    /// Verbs that are not audit-worthy actions: RCON connection lifecycle, and the read-only
    /// polls the bot and the menu tool issue constantly.
    /// </summary>
    /// <remarks>
    /// WITHOUT THIS THE FEED IS UNREADABLE. RefreshList alone is one line every ~70 seconds
    /// for as long as the bot runs, and "User authenticated &lt;ip&gt;" fires on every RCON
    /// reconnect - thousands of lines that say nothing changed and bury the ban that did. A
    /// state-changing command is never in this set, so nothing an operator would want to see
    /// is dropped; the lifecycle words "User" and "Connection" are the RCON server narrating
    /// its own socket, not verbs anybody sent.
    /// </remarks>
    private static readonly HashSet<string> RconNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "User", "Connection",
        "RefreshList", "ServerInfo", "ItemList", "MapList", "Banlist",
        "ModeratorList", "UGCModList", "InspectList", "InspectAll",
    };

    /// <summary>
    /// Base-RCON verbs Pavlov logs under an internal function name rather than the verb the
    /// command uses.
    /// </summary>
    /// <remarks>
    /// The RCON command is <c>Ban &lt;id&gt;</c>, but the engine echoes it as
    /// <c>LogTemp: Rcon: BanPlayer &lt;id&gt;</c> - the internal handler's name, not what was
    /// sent. Left as-is the audit feed shows an operator a "BanPlayer" they never typed, and a
    /// confirmation match against the bot's own <c>Ban</c> never lines up. Mapped back to the
    /// real verb. BASE RCON ONLY: an RCON+ menu command genuinely named BanPlayer is a
    /// different command on a different channel and keeps its name.
    /// </remarks>
    private static readonly Dictionary<string, string> BaseRconVerbAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BanPlayer"] = "Ban",
            ["KickPlayer"] = "Kick",
            ["UnbanPlayer"] = "Unban",
        };

    /// <summary>
    /// The RCON action a line records, or null when it is not one worth surfacing.
    /// </summary>
    /// <remarks>
    /// RCON+ FIRST, because its line ("Rcon Plus Command Executed: …") is checked by its own
    /// marker and never reaches the base branch. A base line is dropped when its verb is
    /// <see cref="RconNoise"/>; an RCON+ command is always kept, since the menu only logs
    /// deliberate actions. Both are already parsed elsewhere in this type - this is the one
    /// entry point that classifies and de-noises them for the audit feed.
    /// </remarks>
    public static RconAction? Rcon(string line)
    {
        if (RconPlus.Match(line) is { Success: true } plus)
        {
            var (verb, rest) = SplitVerb(plus.Groups[1].Value);
            if (verb.Length == 0) return null;

            // <verb> <instigator> <args>: the menu logs the acting player first. A server-wide
            // verb (CleanUp) has no player, so its argument stays the argument.
            if (RconPlusServerVerbs.Contains(verb)) return new RconAction(true, verb, null, rest);

            var (instigator, arg) = SplitVerb(rest ?? "");
            return new RconAction(true, verb, instigator.Length > 0 ? instigator : null, arg);
        }

        if (RconBase.Match(line) is { Success: true } bas)
        {
            var (verb, arg) = SplitVerb(bas.Groups[1].Value);
            if (verb.Length == 0 || RconNoise.Contains(verb)) return null;

            // `Ban Alice` is echoed as `Rcon: BanPlayer Alice`; show the verb that was sent.
            if (BaseRconVerbAliases.TryGetValue(verb, out var canonical)) verb = canonical;
            return new RconAction(false, verb, null, arg);
        }

        return null;
    }

    /// <summary>Split "BanPlayer Alice" into ("BanPlayer", "Alice"); a lone verb has no argument.</summary>
    private static (string Verb, string? Argument) SplitVerb(string command)
    {
        var text = command.Trim();
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? (text, null)
            : (text[..space], text[(space + 1)..].Trim() is { Length: > 0 } a ? a : null);
    }

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
