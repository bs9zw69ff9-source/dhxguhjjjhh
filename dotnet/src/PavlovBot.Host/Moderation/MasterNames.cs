using PavlovBot.Core.Security;
using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>
/// The two protections every enforcement path must consult before acting.
/// </summary>
/// <remarks>
/// MASTER NAMES come from the environment, never from the database. They are the owner's own
/// accounts, and locking yourself out of your own server through a false-positive auto-ban
/// is unrecoverable without console access. A database-backed list could be edited by
/// anything with write access, including a bug.
///
/// EXEMPTIONS are players who have served a ban. Their flags linger until the clean-up sweep
/// runs, so without the exemption the sweep re-catches them the moment they reconnect and a
/// served ban silently becomes permanent. These DO live in the database, because they are
/// created and cleared by the bot itself.
/// </remarks>
public sealed class MasterNames : IMasterNames
{
    private readonly HashSet<string> _masters;
    private readonly SerializedStore _store;

    public MasterNames(IEnumerable<string> masterNames, SerializedStore store)
    {
        /* The compiled-in master name is unioned in and asserted - see OwnerGuard. Losing it
           would let the bot's own auto-ban system ban the owner off their own server. */
        _masters = new HashSet<string>(OwnerGuard.WithBuiltIn(masterNames ?? []), StringComparer.OrdinalIgnoreCase);
        _store = store;
    }

    public bool IsMaster(string name)
    {
        var trimmed = name.Trim();

        // At the point of use too, so emptying the set is not enough to unprotect the account.
        return string.Equals(trimmed, OwnerGuard.MasterName, StringComparison.OrdinalIgnoreCase) || _masters.Contains(trimmed);
    }

    /// <summary>
    /// A player an owner has said must never be auto-banned.
    /// </summary>
    /// <remarks>
    /// THE ESCAPE HATCH FOR A FALSE POSITIVE NOBODY CAN EXPLAIN. Every other protection here
    /// answers a question about WHY somebody was caught - a master account, a served ban.
    /// This one does not care: the owner has looked at it, decided the machine is wrong, and
    /// said stop. That has to work even when the evidence trail is unreadable, because a
    /// player sitting locked out is not a good reason to keep debugging.
    ///
    /// Checked at exactly the same points as <see cref="IsMaster"/>, and nowhere else - it
    /// grants no privileges and changes nothing a human can do.
    /// </remarks>
    public bool IsProtected(string name) => Protected().ContainsKey(name.Trim()) || IsPardoned(name);

    /// <summary>
    /// A player a MASTER OWNER unbanned, which nobody below a master owner may re-ban.
    /// </summary>
    /// <remarks>
    /// PART OF <see cref="IsProtected"/>, so every automated path (the sweep, the reconcile,
    /// the evasion and VPN responders) honours it without a fifth check to forget. The manual
    /// ban commands check it themselves against the issuer's tier. Cleared only by a master
    /// owner banning them again.
    /// </remarks>
    public bool IsPardoned(string name) => Pardons().ContainsKey(name.Trim());

    public IReadOnlyDictionary<string, DateTimeOffset> Pardons() =>
        _store.ReadMap(Datasets.MasterPardons, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

    /// <returns>False when the pardon could not be saved, which the caller must say.</returns>
    public async Task<bool> PardonAsync(string name, CancellationToken ct = default) =>
        (await _store.UpdateMapAsync(Datasets.MasterPardons,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            pardons => { pardons[name.Trim()] = DateTimeOffset.UtcNow; return pardons; }, ct).ConfigureAwait(false)).Ok;

    public Task ClearPardonAsync(string name, CancellationToken ct = default) =>
        _store.UpdateMapAsync(Datasets.MasterPardons,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            pardons => pardons.Remove(name.Trim()) ? pardons : null, ct);

    /// <summary>Every protected player, with when the protection was set.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Protected() =>
        _store.ReadMap(Datasets.NeverBan, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

    public Task ProtectAsync(string name, CancellationToken ct = default) =>
        _store.UpdateMapAsync(Datasets.NeverBan,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            protectedNames => { protectedNames[name.Trim()] = DateTimeOffset.UtcNow; return protectedNames; }, ct);

    public Task UnprotectAsync(string name, CancellationToken ct = default) =>
        _store.UpdateMapAsync(Datasets.NeverBan,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            protectedNames => { protectedNames.Remove(name.Trim()); return protectedNames; }, ct);

    public bool IsExempt(string name) =>
        Exemptions().TryGetValue(name.Trim(), out var until) && until > DateTimeOffset.UtcNow;

    private Dictionary<string, DateTimeOffset> Exemptions() =>
        _store.ReadMap(Datasets.AutobanExempt, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Exempt a player from auto-ban re-catching.
    /// </summary>
    /// <param name="duration">
    /// Bounded rather than permanent. A forever-exemption is a hole somebody can be walked
    /// through later; the flags it protects against are cleared within minutes, so an hour
    /// is generous.
    /// </param>
    public Task ExemptAsync(string name, TimeSpan? duration = null, CancellationToken ct = default) =>
        _store.UpdateMapAsync(Datasets.AutobanExempt,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            exemptions =>
            {
                exemptions[name.Trim()] = DateTimeOffset.UtcNow + (duration ?? TimeSpan.FromHours(1));
                return exemptions;
            }, ct);

    /// <summary>Drop an exemption. A DELIBERATE ban clears one - that is the point of it.</summary>
    public Task RemoveExemptionAsync(string name, CancellationToken ct = default) =>
        _store.UpdateMapAsync(Datasets.AutobanExempt,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            exemptions => { exemptions.Remove(name.Trim()); return exemptions; }, ct);

    public IReadOnlyCollection<string> Masters => _masters;
}
