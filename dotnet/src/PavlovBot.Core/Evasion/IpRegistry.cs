namespace PavlovBot.Core.Evasion;

/// <summary>What is known about one account.</summary>
/// <param name="Id">The EOS UniqueId. The only identifier a player cannot change.</param>
/// <param name="ConfirmedIps">
/// Addresses seen on the SAME log line as this id. Certain, and the ONLY ones allowed to
/// flag or trigger a ban.
/// </param>
/// <param name="GuessedIps">
/// Addresses correlated from a nearby accept line. Display only. Promoting one of these to
/// a flag bans whoever else was connecting at that moment.
/// </param>
/// <param name="Names">Every display name this account has used, most recent first.</param>
public sealed record AccountRecord(
    string Id,
    IReadOnlyList<string> ConfirmedIps,
    IReadOnlyList<string> GuessedIps,
    IReadOnlyList<string> Names,
    DateTimeOffset? FirstSeen = null,
    DateTimeOffset? LastSeen = null)
{
    /* EVERY LIST DEFAULTS TO EMPTY, AND THE ?? IS LOAD-BEARING.

       Nullable reference types are a COMPILE-TIME promise. System.Text.Json does not honour
       them: a stored record missing "names", or carrying an explicit null, deserializes with
       these properties set to null even though nothing here is declared nullable. The
       compiler then cheerfully lets every consumer write `record.Names.Count`.

       That is not hypothetical - it is the NullReferenceException that was firing in
       `log-tail` every few minutes. This dataset is shared with the Node bot, which writes
       its own shape, and any account row lacking one of these arrays threw on EVERY login and
       EVERY disconnect for that player, out of RecordAsync. Because the ingest loop had no
       per-line guard, that one throw also discarded the rest of the batch, which is why joins
       duplicated, leaves vanished and connection cards never appeared.

       An absent list means "none recorded", never "explode".

       THE COALESCE IS IN THE SETTER, not a property initializer. An initializer alone runs
       only in the constructor, and `existing with { Names = whatever }` - which is how
       RecordAsync writes every update - assigns straight through it. The null would come
       back one layer further down, where it is even harder to trace. */
    private readonly IReadOnlyList<string> _confirmedIps = ConfirmedIps ?? [];
    private readonly IReadOnlyList<string> _guessedIps = GuessedIps ?? [];
    private readonly IReadOnlyList<string> _names = Names ?? [];
    private readonly string _id = Id ?? "";

    public IReadOnlyList<string> ConfirmedIps { get => _confirmedIps; init => _confirmedIps = value ?? []; }
    public IReadOnlyList<string> GuessedIps { get => _guessedIps; init => _guessedIps = value ?? []; }
    public IReadOnlyList<string> Names { get => _names; init => _names = value ?? []; }

    /// <summary>The id a foreign or truncated row omitted, so it is never null downstream.</summary>
    public string Id { get => _id; init => _id = value ?? ""; }

    public string? Name => Names.Count > 0 ? Names[0] : null;

    public static AccountRecord Empty(string id) => new(id, [], [], []);
}

/// <param name="Ips">Flagged addresses. EXACT match only - never a subnet.</param>
/// <param name="Names">Flagged display names, set deliberately by an admin.</param>
/// <param name="Ids">Flagged account ids. Set only by a PERMANENT ban.</param>
/// <param name="ManualIps">
/// Admin-flagged addresses, kept separate so a player unban cannot clear them. An unban
/// lifts what that ban created; it must not undo a standing administrative block.
/// </param>
public sealed record FlagSet(
    IReadOnlySet<string> Ips,
    IReadOnlySet<string> Names,
    IReadOnlySet<string> Ids,
    IReadOnlySet<string> ManualIps)
{
    /* Same reason as AccountRecord, and the same setter-not-initializer rule: a null set here
       is a crash in the flag check that runs on every single join, and nothing in the type
       system prevents one arriving. Addresses match exactly; names and ids case-insensitively. */
    private readonly IReadOnlySet<string> _ips = Ips ?? new HashSet<string>(StringComparer.Ordinal);
    private readonly IReadOnlySet<string> _names = Names ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlySet<string> _ids = Ids ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlySet<string> _manualIps = ManualIps ?? new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> Ips { get => _ips; init => _ips = value ?? new HashSet<string>(StringComparer.Ordinal); }
    public IReadOnlySet<string> Names { get => _names; init => _names = value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    public IReadOnlySet<string> Ids { get => _ids; init => _ids = value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    public IReadOnlySet<string> ManualIps { get => _manualIps; init => _manualIps = value ?? new HashSet<string>(StringComparer.Ordinal); }

    public static FlagSet Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>Why a join was refused, if it was.</summary>
public enum FlagMatch
{
    None = 0,
    Ip,
    Name,
    AccountId,
}

/// <param name="Detail">The reason text the ban decision inspects; wording matters.</param>
public sealed record FlagVerdict(FlagMatch Match, string? Detail = null)
{
    public bool Hit => Match != FlagMatch.None;
    public static FlagVerdict Clean { get; } = new(FlagMatch.None);
}

/// <summary>
/// Matching a join against the standing flags.
/// </summary>
/// <remarks>
/// EXACT MATCHES ONLY. No subnets, ever. A /24 covers 254 addresses, and on a residential
/// ISP those belong to 254 unrelated households - one evader would ban a neighbourhood.
/// The narrow rule catches fewer evaders and has never banned a stranger, which is the
/// right side of that trade for a moderation tool.
///
/// The DETAIL WORDING is load-bearing, not decoration: the auto-ban decision reads it to
/// tell a ban-derived flag (which expires with the ban that made it) from an
/// administrative name flag (which does not). See <c>BanRules.AutoBanDecision</c>.
/// </remarks>
public static class FlagMatching
{
    public static FlagVerdict Check(FlagSet flags, string? ip, string? name, string? accountId)
    {
        ArgumentNullException.ThrowIfNull(flags);

        if (ip is { Length: > 0 } && (flags.Ips.Contains(ip) || flags.ManualIps.Contains(ip)))
            return new FlagVerdict(FlagMatch.Ip, $"blacklisted ip {ip}");

        if (accountId is { Length: > 0 } && flags.Ids.Contains(accountId))
            return new FlagVerdict(FlagMatch.AccountId, $"blacklisted account {accountId}");

        /* Checked LAST. A name flag is the only one an admin sets by hand, and it is also
           the only one that outlives a served ban - so an address or account match should
           win when both apply, because those carry the ban's own expiry semantics. */
        if (name is { Length: > 0 } && flags.Names.Contains(name))
            return new FlagVerdict(FlagMatch.Name, $"blacklisted username {name}");

        return FlagVerdict.Clean;
    }
}

/// <summary>
/// An account banned while still online, waiting for its address to be confirmed.
/// </summary>
/// <remarks>
/// The case this exists for: banning somebody who is currently connected. Their address is
/// not confirmed yet - the confirming line is the DISCONNECT, which the ban itself is about
/// to cause. Without this, the ban flags nothing, and the moment they reconnect from the
/// same address nothing catches them.
///
/// So the intent is recorded, and the disconnect the kick produces resolves it. The window
/// is short because after a few minutes any disconnect is somebody else's.
/// </remarks>
public sealed record PendingFlag(string AccountId, DateTimeOffset RequestedAt, bool FlagAccountId)
{
    public bool IsLive(DateTimeOffset now, TimeSpan window) => now - RequestedAt <= window;
}

/// <summary>What a ban actually flagged, for the audit line.</summary>
/// <param name="Pending">
/// True when the account had no confirmed address and the flag is deferred. Worth saying
/// out loud - a ban that flagged nothing looks identical to one that worked until an alt
/// walks straight back in.
/// </param>
public sealed record FlagOutcome(
    IReadOnlyList<string> Ips,
    IReadOnlyList<string> Ids,
    IReadOnlyList<string> AltNames,
    bool Pending);
