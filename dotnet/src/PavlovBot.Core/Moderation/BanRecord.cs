using PavlovBot.Core.Evasion;

namespace PavlovBot.Core.Moderation;

/// <summary>One ban, as stored.</summary>
/// <param name="PlayerId">The display name the ban is filed under.</param>
/// <param name="Permanent">
/// True for a ban with no expiry. Mutually exclusive with <see cref="Expires"/> in
/// practice, but both are read so a malformed record cannot become permanent by accident.
/// </param>
/// <param name="Tier">
/// The staff tier that issued it, for <see cref="StaffHierarchy.CanOverride"/>. Null on
/// records predating the hierarchy, which stay liftable by anyone.
/// </param>
/// <param name="Network">
/// Network intelligence captured AT BAN TIME. Snapshotted alongside the ban rather than
/// looked up later, because the VPN cache entry expires and the player may never
/// reconnect from the same address - by the time you want to correlate a join against
/// this ban, the evidence would otherwise be gone.
/// </param>
public sealed record BanRecord
{
    public required string PlayerId { get; init; }
    public string? Reason { get; init; }
    public string? Moderator { get; init; }
    public DateTimeOffset? At { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public bool Permanent { get; init; }
    public string? DurationLabel { get; init; }
    public StaffTier? Tier { get; init; }
    public BanNetwork? Network { get; init; }

    /// <summary>
    /// A record is usable only if it can actually enforce something.
    /// </summary>
    /// <remarks>
    /// A ban with no player id enforces nothing; a ban that is neither permanent nor
    /// dated has no answer to "is this still in force?", and guessing either way is
    /// wrong - guessing permanent leaves someone banned forever over a corrupt field,
    /// guessing expired quietly frees them.
    /// </remarks>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(PlayerId) && (Permanent || Expires is not null);

    public bool IsActiveAt(DateTimeOffset now) => Permanent || (Expires is { } e && e > now);
}

/// <summary>What the IP guard should do about a join that matched a flag.</summary>
public enum AutoBanAction
{
    /// <summary>An unexpired temp ban already covers this name. Log it, never escalate.</summary>
    Block,

    /// <summary>The covering temp ban has been SERVED. Lift it rather than escalating.</summary>
    Lift,

    /// <summary>Nothing covers them - an evasion alt or a fresh match. Auto-ban.</summary>
    Ban,
}

/// <summary>
/// The decisions around bans that are pure - no RCON, no storage, no Discord.
/// </summary>
public static class BanRules
{
    /// <summary>Valid, in-force bans. Invalid records are dropped rather than guessed at.</summary>
    public static IReadOnlyList<BanRecord> Active(IEnumerable<BanRecord> bans, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bans);
        return bans.Where(b => b.IsValid && b.IsActiveAt(now)).ToList();
    }

    public static bool SamePlayer(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What to do about a join that hit a flagged IP, name or account id.
    /// </summary>
    /// <remarks>
    /// THE "LIFT" CASE IS THE ONE THAT MATTERS. A player who served a two-day ban still
    /// carries the IP and account flags that ban created - the sweep that clears them
    /// runs on a timer, so there is a window where a served ban's own leftovers look
    /// exactly like evasion. Escalating there turns a served temp ban into a permanent
    /// one, which is the worst failure this system can produce.
    ///
    /// A USERNAME match is treated differently on purpose: a name flag is only ever set
    /// deliberately by an admin through <c>/configure</c>, never as a side effect of a
    /// ban, so it has no expiry to have served and still escalates.
    /// </remarks>
    public static AutoBanAction AutoBanDecision(BanRecord? existing, string? matchReason, DateTimeOffset now)
    {
        if (existing is { Permanent: false, Expires: { } expires })
        {
            if (expires > now) return AutoBanAction.Block;

            var reason = matchReason ?? "";
            if (reason.Contains("blacklisted ip", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("blacklisted account", StringComparison.OrdinalIgnoreCase))
            {
                return AutoBanAction.Lift;
            }
        }
        return AutoBanAction.Ban;
    }

    /// <summary>
    /// Whether a ban is a REAL punishment on a person, rather than machinery.
    /// </summary>
    /// <remarks>
    /// Used to find the original offence behind an auto-ban, so <c>/checkban</c> shows
    /// what somebody actually did instead of "IP-Guard". Excluded:
    ///
    ///   - auto-bans and evasion bans, or the answer is circular
    ///   - broad <c>/configure</c> blacklist entries, which are not a punishment on one
    ///     person - copying their moderator makes every later match look as though that
    ///     admin personally banned the player
    /// </remarks>
    public static bool IsRealBan(BanRecord? ban)
    {
        if (ban?.Reason is not { Length: > 0 } reason) return false;

        if (reason.StartsWith("auto-ban", StringComparison.OrdinalIgnoreCase) ||
            reason.StartsWith("ban evasion", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("blacklisted via", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var moderator = ban.Moderator ?? "";
        return !string.Equals(moderator, "IP-Guard", StringComparison.Ordinal) &&
               !moderator.Contains("evasion", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The ban whose flag caught this player, so the record can show the real offence.
    /// </summary>
    /// <remarks>
    /// Matched by SAME ACCOUNT first - an evader who kept the account and changed name is
    /// the common case, and the EOS id is the only identifier they cannot change. Name
    /// and known-alt matching is the fallback.
    /// </remarks>
    public static BanRecord? SourceBanFor(
        IEnumerable<BanRecord> bans,
        string name,
        string? uniqueId,
        Func<string, string?> eosIdOf,
        IReadOnlyCollection<string>? altNames = null)
    {
        ArgumentNullException.ThrowIfNull(bans);
        ArgumentNullException.ThrowIfNull(eosIdOf);

        var real = bans.Where(IsRealBan).ToList();

        if (!string.IsNullOrEmpty(uniqueId))
        {
            var byAccount = real.FirstOrDefault(b =>
                string.Equals(eosIdOf(b.PlayerId), uniqueId, StringComparison.OrdinalIgnoreCase));
            if (byAccount is not null) return byAccount;
        }

        var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        foreach (var alt in altNames ?? []) related.Add(alt);

        return real.FirstOrDefault(b => related.Contains(b.PlayerId));
    }
}
