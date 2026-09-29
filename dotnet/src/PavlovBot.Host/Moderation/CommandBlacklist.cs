using System.Globalization;
using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>What a bar or an un-bar did.</summary>
public enum BarOutcome
{
    /// <summary>Added to the bar list.</summary>
    Barred,

    /// <summary>Already barred. Nothing changed.</summary>
    AlreadyBarred,

    /// <summary>An owner, who can never be barred. Nothing was written.</summary>
    Owner,

    /// <summary>No longer barred. When <c>BLACKLIST_IDS</c> named them, recorded as un-barred so it cannot bring them back.</summary>
    Unbarred,

    /// <summary>Was not barred. Nothing changed.</summary>
    NotBarred,

    /// <summary>The list could not be saved. <see cref="BarResult.Error"/> says why.</summary>
    Failed,
}

/// <param name="Error">Why the change was not saved, for <see cref="BarOutcome.Failed"/> only.</param>
public readonly record struct BarResult(BarOutcome Outcome, string? Error = null);

/// <param name="FromEnvironment">True when <c>BLACKLIST_IDS</c> bars them rather than <c>/configure</c>.</param>
public sealed record BarredUser(string Id, bool FromEnvironment);

/// <summary>
/// Discord users barred from every bot command.
/// </summary>
/// <remarks>
/// THE NODE BOT'S RULE, CARRIED OVER. Barred means named in <c>BLACKLIST_IDS</c> or barred from
/// <c>/configure</c> (<see cref="Datasets.UserBlacklist"/>), and not since un-barred
/// (<see cref="Datasets.UserUnbarred"/>). The un-bar record is how an owner lifts a .env bar from
/// Discord: there is no entry to remove, and .env would put one back on every restart anyway.
///
/// OWNERS ARE NEVER BARRED, whatever the lists say. Every other gate lets an owner through, and a
/// bar that could catch one would be a way to lock the owner out of their own bot.
///
/// ENFORCED ONCE, AT DISPATCH. The gateway asks <see cref="IsBarred"/> before any slash command,
/// autocomplete, button, select menu or modal runs, so a command added later cannot forget to.
/// The C# port wrote and listed this for months with nothing reading it: a barred user kept every
/// command their roles allowed, and <c>BLACKLIST_IDS</c> was not read at all.
///
/// READ THROUGH THE STORE ON EVERY CHECK rather than cached here. The storage backend already
/// caches reads, and a second copy held here would be one more thing each write had to refresh.
/// </remarks>
public sealed class CommandBlacklist
{
    private readonly SerializedStore _store;
    private readonly Func<ulong, bool> _isOwner;
    private readonly IReadOnlySet<string> _configured;

    /// <param name="isOwner">Owners are immune. By id, because a user being barred is not the one interacting.</param>
    /// <param name="configured"><c>BLACKLIST_IDS</c>. An un-bar from <c>/configure</c> overrides these.</param>
    public CommandBlacklist(SerializedStore store, Func<ulong, bool> isOwner, IEnumerable<ulong>? configured = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(isOwner);

        _store = store;
        _isOwner = isOwner;
        _configured = (configured ?? []).Select(Key).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether every command is refused to this Discord user. Never true for an owner.</summary>
    public bool IsBarred(ulong userId)
    {
        if (_isOwner(userId)) return false;

        var id = Key(userId);
        return (_configured.Contains(id) || Stored().Contains(id, StringComparer.Ordinal)) &&
               !Unbarred().Contains(id, StringComparer.Ordinal);
    }

    /// <summary>Everybody barred right now. Owners are left out: nothing is refused to them.</summary>
    public IReadOnlyList<BarredUser> Entries()
    {
        var unbarred = Unbarred().ToHashSet(StringComparer.Ordinal);
        return Stored().Select(id => new BarredUser(id, FromEnvironment: false))
            .Concat(_configured.Order(StringComparer.Ordinal).Select(id => new BarredUser(id, FromEnvironment: true)))
            .Where(u => !unbarred.Contains(u.Id) && !IsOwnerKey(u.Id))
            .DistinctBy(u => u.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<BarResult> BarAsync(ulong userId, CancellationToken ct = default)
    {
        if (_isOwner(userId)) return new BarResult(BarOutcome.Owner);

        var id = Key(userId);
        var unbarred = Unbarred().Contains(id, StringComparer.Ordinal);
        if (!unbarred && Stored().Contains(id, StringComparer.Ordinal)) return new BarResult(BarOutcome.AlreadyBarred);

        /* THE STALE UN-BAR GOES FIRST. It outranks the bar list, so adding the id while one
           stood would report success and bar nobody. */
        var error = unbarred
            ? await ChangeAsync(Datasets.UserUnbarred, ids => ids.RemoveAll(i => i == id) > 0, ct).ConfigureAwait(false)
            : null;

        /* SAVED EVEN WHEN BLACKLIST_IDS ALREADY NAMES THEM, so a bar set here does not quietly
           end the day somebody tidies .env. */
        error ??= await ChangeAsync(Datasets.UserBlacklist, ids => Add(ids, id), ct).ConfigureAwait(false);

        return error is null ? new BarResult(BarOutcome.Barred) : new BarResult(BarOutcome.Failed, error);
    }

    public async Task<BarResult> UnbarAsync(ulong userId, CancellationToken ct = default)
    {
        if (!IsBarred(userId)) return new BarResult(BarOutcome.NotBarred);

        var id = Key(userId);

        /* AN UN-BAR RECORD ONLY WHEN .ENV IS WHAT BARS THEM. It is the one way to lift a
           BLACKLIST_IDS bar from Discord, and it overrides that list for good - so writing one
           for a /configure bar would silently cancel a .env bar somebody adds for them later. */
        var fromEnvironment = _configured.Contains(id);
        if (fromEnvironment)
        {
            var recorded = await ChangeAsync(Datasets.UserUnbarred, ids => Add(ids, id), ct).ConfigureAwait(false);
            if (recorded is not null) return new BarResult(BarOutcome.Failed, recorded);
        }

        var error = await ChangeAsync(Datasets.UserBlacklist, ids => ids.RemoveAll(i => i == id) > 0, ct).ConfigureAwait(false);

        // Once the un-bar record is written it outranks the list, so they are free either way.
        return error is null || fromEnvironment ? new BarResult(BarOutcome.Unbarred) : new BarResult(BarOutcome.Failed, error);
    }

    private static string Key(ulong userId) => userId.ToString(CultureInfo.InvariantCulture);

    private bool IsOwnerKey(string id) =>
        ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && _isOwner(parsed);

    private static bool Add(List<string> ids, string id)
    {
        if (ids.Contains(id, StringComparer.Ordinal)) return false;
        ids.Add(id);
        return true;
    }

    private List<string> Stored() => Ids(Datasets.UserBlacklist);

    private List<string> Unbarred() => Ids(Datasets.UserUnbarred);

    /// <summary>The ids stored under one key: empty when there are none, or when it holds no list.</summary>
    /// <remarks>
    /// SHAPE-CHECKED BEFORE IT IS PARSED. Builds before 2026-07-31 kept the ADDRESS FLAGS under
    /// <see cref="Datasets.UserBlacklist"/>, as an object, and one can still be sitting there on
    /// an install nobody has been barred on since. It is not a bar list, so it bars nobody - and
    /// it is kept away from the typed reader, which would report it as an unreadable dataset
    /// from inside the check every interaction makes.
    /// </remarks>
    private List<string> Ids(string key) =>
        IsList(_store.ReadRaw(key)) ? _store.Read(key, new List<string>()) : [];

    private static bool IsList(string? raw) => raw?.TrimStart() is ['[', ..];

    /// <summary>One read-modify-write of an id list. Null when saved, or when there was nothing to change.</summary>
    private async Task<string?> ChangeAsync(string key, Func<List<string>, bool> change, CancellationToken ct)
    {
        var raw = _store.ReadRaw(key);
        if (raw is not null && !IsList(raw) && raw.Trim() is not ("" or "null"))
            return await ReplaceOldFlagsAsync(key, change, ct).ConfigureAwait(false);

        var result = await _store.UpdateAsync<List<string>>(key, [], ids => change(ids) ? ids : null, ct)
            .ConfigureAwait(false);
        return result.Ok || result.Vetoed ? null : result.Error;
    }

    /// <summary>
    /// Replace the old address-flag object (see <see cref="Ids"/>) with a real list.
    /// </summary>
    /// <remarks>
    /// ONLY ONCE THE FLAGS ARE PROVABLY ELSEWHERE. The first flag change after the move copied
    /// them into <see cref="Datasets.IpFlags"/>, so while that is empty the object here may be the
    /// only copy, and overwriting it would unflag every banned address.
    /// </remarks>
    private async Task<string?> ReplaceOldFlagsAsync(string key, Func<List<string>, bool> change, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_store.ReadRaw(Datasets.IpFlags)))
        {
            return $"\"{key}\" still holds address flags from an older build that have not been moved yet. " +
                   "Blacklist and then clear any address in /configure to move them, then try again.";
        }

        var ids = new List<string>();
        change(ids);
        return await _store.WriteAsync(key, ids, ct).ConfigureAwait(false) ? null : $"\"{key}\" could not be written";
    }
}
