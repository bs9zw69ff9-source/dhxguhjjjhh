using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Factions;

/// <param name="Faction">The faction they were whitelisted into.</param>
/// <param name="Name">The in-game name written to the roster file.</param>
/// <param name="At">When they were added.</param>
/// <param name="By">Who added them, for the audit trail.</param>
public sealed record FactionMember(string Faction, string Name, DateTimeOffset At, string By);

/// <summary>
/// Which Discord account owns which in-game name in which faction.
/// </summary>
/// <remarks>
/// THE ROSTER FILES REMAIN THE SOURCE OF TRUTH. The game reads them, staff can edit them by
/// hand, and a member is in a faction because their name is in a file - not because of
/// anything recorded here. This is an INDEX over that, and it exists for one reason: so
/// /promotion, /demotion and /whitelist remove can take a Discord user instead of asking a
/// moderator to type an in-game name they have to look up and spell exactly.
///
/// WHY NOT REUSE VERIFICATION. VerificationService already maps a Discord id to a confirmed
/// Pavlov name, but it is gated on IP confirmation and staff approval. Keying faction
/// commands off it would mean a whitelisted member who never went through verification could
/// not be promoted or removed by user at all - a whole class of member invisible to the
/// commands meant to manage them. The link is recorded where it is known instead: at the
/// moment somebody is whitelisted, which is why /whitelist add takes both the user and the
/// name.
///
/// WHEN THE TWO DISAGREE, THE FILE WINS. An index entry whose name is no longer in any roster
/// file describes a membership that does not exist, and <see cref="ForgetMissingAsync"/>
/// drops it. The reverse - a name in a file with no index entry - is a member added by hand
/// or before this existed; they stay whitelisted and are simply not reachable by user until
/// somebody re-adds them through the command. Neither direction removes anybody from the
/// game, which is the property worth keeping: a bookkeeping disagreement must never revoke
/// access.
/// </remarks>
public sealed class FactionMembers(SerializedStore store)
{
    private Dictionary<string, FactionMember> Load() =>
        store.Read(Datasets.FactionMembers, new Dictionary<string, FactionMember>(StringComparer.Ordinal));

    /// <summary>The membership recorded for a Discord id, or null when there is none.</summary>
    public FactionMember? Of(ulong discordId) => Load().GetValueOrDefault(Key(discordId));

    /// <summary>Every recorded membership in a faction.</summary>
    public IReadOnlyList<(ulong DiscordId, FactionMember Member)> InFaction(string faction) =>
        [.. Load()
            .Where(kv => string.Equals(kv.Value.Faction, faction, StringComparison.OrdinalIgnoreCase))
            .Where(kv => ulong.TryParse(kv.Key, out _))
            .Select(kv => (ulong.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv.Value))];

    /// <summary>The Discord id recorded against an in-game name, or null.</summary>
    /// <remarks>
    /// For the path that starts from a roster file rather than from a command - showing who a
    /// listed name belongs to. Names are compared the way the rest of the bot compares them.
    /// </remarks>
    public ulong? OwnerOf(string name) =>
        Load().FirstOrDefault(kv => string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
            is { Key: { } key } && ulong.TryParse(key, out var id) ? id : null;

    /// <summary>Record a membership, replacing any previous one for that account.</summary>
    /// <remarks>
    /// REPLACES rather than appends. One faction per player is already the rule, so a second
    /// record for one account describes a state that cannot exist and would make removal
    /// depend on which entry was found first.
    /// </remarks>
    public Task RememberAsync(ulong discordId, FactionMember member, CancellationToken ct = default) =>
        store.UpdateAsync<Dictionary<string, FactionMember>>(
            Datasets.FactionMembers,
            new Dictionary<string, FactionMember>(StringComparer.Ordinal),
            index => { index[Key(discordId)] = member; return index; },
            ct);

    /// <summary>Drop the membership recorded for an account. A no-op when there is none.</summary>
    public Task ForgetAsync(ulong discordId, CancellationToken ct = default) =>
        store.UpdateAsync<Dictionary<string, FactionMember>>(
            Datasets.FactionMembers,
            new Dictionary<string, FactionMember>(StringComparer.Ordinal),
            // Veto when absent, so a removal that changes nothing does not rewrite the file.
            index => index.Remove(Key(discordId)) ? index : null,
            ct);

    /// <summary>
    /// Drop entries whose name is in no roster file, and report how many went.
    /// </summary>
    /// <remarks>
    /// The index follows the files, never the other way round. Called after a roster read so
    /// a member removed by editing a file by hand does not linger here and make /promotion
    /// act on somebody who is not in the faction any more.
    /// </remarks>
    public async Task<int> ForgetMissingAsync(
        IReadOnlyCollection<string> namesStillWhitelisted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(namesStillWhitelisted);

        var live = namesStillWhitelisted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = 0;

        await store.UpdateAsync<Dictionary<string, FactionMember>>(
            Datasets.FactionMembers,
            new Dictionary<string, FactionMember>(StringComparer.Ordinal),
            index =>
            {
                var stale = index.Where(kv => !live.Contains(kv.Value.Name)).Select(kv => kv.Key).ToList();
                foreach (var key in stale) index.Remove(key);
                dropped = stale.Count;
                return dropped > 0 ? index : null;
            },
            ct).ConfigureAwait(false);

        return dropped;
    }

    /// <remarks>
    /// A STRING KEY, because the dataset is JSON and a ulong key would serialise as a number
    /// that round-trips through double on some readers - the Node bot's files are still read
    /// by this bot, and a snowflake past 2^53 does not survive that intact.
    /// </remarks>
    private static string Key(ulong discordId) =>
        discordId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
