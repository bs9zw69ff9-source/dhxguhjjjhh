namespace PavlovBot.Core.Events;

/// <summary>
/// Which lens an event belongs to, so a timeline can be filtered without knowing every kind.
/// </summary>
/// <remarks>
/// THE FILTER IS THE CATEGORY, NOT THE KIND. An investigation asks "what happened on the
/// server" or "what has staff been doing", not "show me kind 17". Kinds are added constantly
/// as features land; categories should almost never change, so a stored event stays
/// filterable by a query written before its kind existed.
/// </remarks>
public enum EventCategory
{
    /// <summary>Server lifecycle: restarts, crashes, map changes, RCON state.</summary>
    Server,

    /// <summary>Players coming and going.</summary>
    Player,

    /// <summary>Anything a staff member did.</summary>
    Staff,

    /// <summary>Detections: evasion, VPN, flags, auto-bans.</summary>
    Security,

    /// <summary>Money moving.</summary>
    Economy,

    /// <summary>Rosters, ranks, whitelists.</summary>
    Faction,
}

/// <summary>
/// One thing that happened, normalised across every system that produces events.
/// </summary>
/// <remarks>
/// APPEND-ONLY AND NEVER EDITED. A timeline whose past can be rewritten is not evidence, and
/// this is meant to be read during an argument about a ban. There is no update path, by
/// design; the only thing that removes a row is retention.
///
/// DELIBERATELY FLAT AND DENORMALISED. Player and server are plain strings rather than
/// references, so an event still reads correctly after the player is renamed or the server is
/// removed from the config. A timeline that changes its account of the past when present-day
/// config changes is worse than no timeline.
///
/// STRINGS RATHER THAN A PAYLOAD BLOB. A JSON detail column would let anything be recorded
/// and nothing be queried, and the queries are the entire point of moving this out of the
/// document store. What does not fit in <see cref="Detail"/> does not belong in a timeline.
/// </remarks>
/// <param name="At">When it happened. Indexed - every query is time-bounded.</param>
/// <param name="Category">The lens. Indexed with time.</param>
/// <param name="Kind">
/// A short stable slug: <c>player.join</c>, <c>staff.ban</c>. Free-form on purpose, so a new
/// feature can record events without a schema change, and readable in a database browser.
/// </param>
/// <param name="Player">The player this concerns, when there is one. Indexed with time.</param>
/// <param name="Server">Which server, when it is server-specific. Indexed with time.</param>
/// <param name="Actor">Who caused it: a staff name, "auto", or null when nobody did.</param>
/// <param name="Detail">One human sentence. Shown verbatim in the timeline.</param>
public sealed record ServerEvent(
    DateTimeOffset At,
    EventCategory Category,
    string Kind,
    string? Player = null,
    string? Server = null,
    string? Actor = null,
    string? Detail = null)
{
    /// <summary>Longest a kind slug may be. Keeps the index small and the column predictable.</summary>
    public const int MaxKind = 48;

    /// <summary>Longest a detail line may be, matching what a Discord field can show.</summary>
    public const int MaxDetail = 512;

    /// <summary>
    /// The event as it will be stored: trimmed, bounded, and with empties normalised to null.
    /// </summary>
    /// <remarks>
    /// NORMALISED ON THE WAY IN, not on the way out. An empty string and a null mean the same
    /// thing to every reader here, and storing both means every query needs to test for both -
    /// which is the sort of thing that is remembered in four places out of five.
    ///
    /// TRUNCATION IS SILENT AND THAT IS THE RIGHT TRADE. The alternative is rejecting the
    /// event, and losing a timeline entry entirely because its detail was long is a worse
    /// outcome than losing the tail of a sentence.
    /// </remarks>
    public ServerEvent Normalised() => new(
        At,
        Category,
        Clamp(Kind, MaxKind) ?? "unknown",
        Clamp(Player, 128),
        Clamp(Server, 64),
        Clamp(Actor, 128),
        Clamp(Detail, MaxDetail));

    private static string? Clamp(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// What to select from the timeline.
/// </summary>
/// <remarks>
/// EVERY FIELD IS OPTIONAL EXCEPT THE LIMIT, and the limit is not optional on purpose. An
/// unbounded timeline query against a table that grows forever is the exact shape of the
/// problem this store was built to avoid, so the type does not let a caller forget it.
/// </remarks>
/// <param name="Limit">Hard cap on rows. Required.</param>
/// <param name="Since">Only events at or after this instant.</param>
/// <param name="Until">Only events strictly before this instant.</param>
/// <param name="Category">Restrict to one lens.</param>
/// <param name="Player">Restrict to one player, matched case-insensitively.</param>
/// <param name="Server">Restrict to one server.</param>
/// <param name="Actor">Restrict to who caused it - the staff-activity view.</param>
public sealed record EventQuery(
    int Limit,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    EventCategory? Category = null,
    string? Player = null,
    string? Server = null,
    string? Actor = null)
{
    /// <summary>Most rows any single query may return, whatever it asks for.</summary>
    /// <remarks>
    /// A ceiling the caller cannot raise. Discord cannot render more than a page or two of
    /// this anyway, and the number exists to stop a bug - or a plugin - turning one command
    /// into a full table scan materialised in memory.
    /// </remarks>
    public const int MaxLimit = 500;

    public int EffectiveLimit => Math.Clamp(Limit, 1, MaxLimit);
}
