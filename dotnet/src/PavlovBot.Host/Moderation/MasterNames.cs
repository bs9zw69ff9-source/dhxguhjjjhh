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
        _masters = masterNames.Select(n => n.Trim()).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _store = store;
    }

    public bool IsMaster(string name) => _masters.Contains(name.Trim());

    public bool IsExempt(string name) =>
        Exemptions().TryGetValue(name.Trim(), out var until) && until > DateTimeOffset.UtcNow;

    private Dictionary<string, DateTimeOffset> Exemptions() =>
        _store.Read(Datasets.AutobanExempt, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Exempt a player from auto-ban re-catching.
    /// </summary>
    /// <param name="duration">
    /// Bounded rather than permanent. A forever-exemption is a hole somebody can be walked
    /// through later; the flags it protects against are cleared within minutes, so an hour
    /// is generous.
    /// </param>
    public Task ExemptAsync(string name, TimeSpan? duration = null, CancellationToken ct = default) =>
        _store.UpdateAsync(Datasets.AutobanExempt,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            exemptions =>
            {
                exemptions[name.Trim()] = DateTimeOffset.UtcNow + (duration ?? TimeSpan.FromHours(1));
                return exemptions;
            }, ct);

    /// <summary>Drop an exemption. A DELIBERATE ban clears one - that is the point of it.</summary>
    public Task RemoveExemptionAsync(string name, CancellationToken ct = default) =>
        _store.UpdateAsync(Datasets.AutobanExempt,
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase),
            exemptions => { exemptions.Remove(name.Trim()); return exemptions; }, ct);

    public IReadOnlyCollection<string> Masters => _masters;
}
