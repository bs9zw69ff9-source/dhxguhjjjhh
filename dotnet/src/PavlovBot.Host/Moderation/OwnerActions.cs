using System.Globalization;
using System.Net;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <param name="Ok">False when the action refused. <paramref name="Detail"/> says why.</param>
/// <param name="Detail">One line, already written for a human. Never an exception message.</param>
/// <param name="Lines">Rows for a list-style result, empty for an action-style one.</param>
public sealed record OwnerActionResult(bool Ok, string Detail, IReadOnlyList<string> Lines)
{
    public static OwnerActionResult Done(string detail) => new(true, detail, []);
    public static OwnerActionResult Refused(string detail) => new(false, detail, []);
    public static OwnerActionResult List(string detail, IReadOnlyList<string> lines) => new(true, detail, lines);
}

/// <summary>
/// The owner control panel's actions, with no Discord in them.
/// </summary>
/// <remarks>
/// SPLIT FROM THE PANEL ON PURPOSE. These are the operations that wipe the registry, empty
/// the ban flags and delete every player's money - the ones where being able to test the
/// behaviour without a gateway connection is worth more than the indirection costs.
///
/// Every destructive action here reports what it actually affected rather than assuming it
/// worked. "Wiped 0 ledgers" is the answer that tells an owner the path is wrong; "done"
/// would let a misconfigured MODSAVE_PATH look like a successful wipe forever.
/// </remarks>
public sealed class OwnerActions(
    SerializedStore store,
    IpTrackingService tracking,
    string? ledgerDirectory = null)
{
    // ---- IP enforcement -----------------------------------------------------------------

    /// <summary>
    /// Flag an address or a username so anything matching is auto-banned on sight.
    /// </summary>
    /// <remarks>
    /// The kind is INFERRED, because the owner typing it knows which they meant and being
    /// asked to say so is friction at the exact moment somebody is mid-incident. Anything
    /// that parses as an address is one; everything else is treated as a name.
    /// </remarks>
    public async Task<OwnerActionResult> BlacklistAsync(string target, CancellationToken ct = default)
    {
        target = (target ?? "").Trim();
        if (target.Length == 0) return OwnerActionResult.Refused("Nothing to blacklist - the value was empty.");

        var isAddress = IPAddress.TryParse(target, out _);

        await store.UpdateAsync(Datasets.IpFlags, tracking.LoadStoredFlags(), flags => isAddress
            ? flags with { ManualIps = Add(flags.ManualIps, target, StringComparer.Ordinal) }
            : flags with { Names = Add(flags.Names, target, StringComparer.OrdinalIgnoreCase) }, ct)
            .ConfigureAwait(false);

        /* Which accounts this already matches, so the owner knows whether it caught anybody
           or is purely forward-looking. Naming them is the difference between "done" and
           "done, and these three are affected". */
        var matched = isAddress
            ? tracking.AccountsWithAddress(target).Select(a => a.Name ?? a.Id).ToList()
            : tracking.AccountByName(target) is { } account ? [account.Name ?? account.Id] : new List<string>();

        var kind = isAddress ? "address" : "username";
        return OwnerActionResult.List(
            matched.Count > 0
                ? $"{kind} `{target}` blacklisted. It matches **{matched.Count}** known account(s), listed below - ban them with `/permban`."
                : $"{kind} `{target}` blacklisted. No account on record matches it yet; future connections will be caught.",
            matched.Select(n => $"• **{n}**").ToList());
    }

    public OwnerActionResult ViewBlacklist()
    {
        var flags = tracking.LoadStoredFlags();
        var lines = new List<string>();

        lines.AddRange(flags.ManualIps.Select(ip => $"🚫 address `{ip}` *(set by an owner)*"));
        lines.AddRange(flags.Ips.Select(ip => $"🚫 address `{ip}` *(from a ban)*"));
        lines.AddRange(flags.Names.Select(n => $"🚫 username **{n}**"));
        lines.AddRange(flags.Ids.Select(id => $"🚫 account `{id}`"));

        return lines.Count == 0
            ? OwnerActionResult.Done("Nothing is blacklisted.")
            : OwnerActionResult.List($"**{lines.Count}** blacklist entr(ies).", lines);
    }

    /// <summary>Accounts that share a CONFIRMED address with this one.</summary>
    /// <remarks>
    /// Confirmed only. A guessed address is a correlation from a join line, and building an
    /// accusation of alt-accounting on a guess is how the wrong person gets banned.
    /// </remarks>
    public OwnerActionResult Alts(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return OwnerActionResult.Refused("No name given.");

        var account = tracking.AccountByName(name);
        if (account is null) return OwnerActionResult.Done($"No account on record uses the name **{name}**.");

        var alts = tracking.AltsOf(account.Id);
        return alts.Count == 0
            ? OwnerActionResult.Done($"**{name}** shares no confirmed address with any other account.")
            : OwnerActionResult.List($"**{name}** shares a confirmed address with **{alts.Count}** account(s).",
                alts.Select(a => $"• **{a.Name ?? a.Id}** — `{a.Id}`").ToList());
    }

    public async Task<OwnerActionResult> ClearAddressAsync(string ip, CancellationToken ct = default)
    {
        ip = (ip ?? "").Trim();
        if (ip.Length == 0) return OwnerActionResult.Refused("No address given.");

        var before = tracking.LoadStoredFlags();
        var removed = before.Ips.Count(i => i == ip) + before.ManualIps.Count(i => i == ip);
        if (removed == 0) return OwnerActionResult.Done($"`{ip}` was not flagged. Nothing changed.");

        await store.UpdateAsync(Datasets.IpFlags, before, flags => flags with
        {
            Ips = flags.Ips.Where(i => i != ip).ToList(),
            ManualIps = flags.ManualIps.Where(i => i != ip).ToList(),
        }, ct).ConfigureAwait(false);

        return OwnerActionResult.Done($"`{ip}` un-flagged. Anyone on that address can connect again.");
    }

    public async Task<OwnerActionResult> ClearFlaggedNamesAsync(CancellationToken ct = default)
    {
        var before = tracking.LoadStoredFlags();
        if (before.Names.Count == 0) return OwnerActionResult.Done("No usernames were flagged.");

        await store.UpdateAsync(Datasets.IpFlags, before, flags => flags with { Names = [] }, ct).ConfigureAwait(false);
        return OwnerActionResult.Done($"**{before.Names.Count}** username flag(s) cleared. Address and account flags are untouched.");
    }

    /// <summary>Everything the address tracker knows: the registry AND every flag.</summary>
    public async Task<OwnerActionResult> WipeAllIpDataAsync(CancellationToken ct = default)
    {
        var flags = tracking.LoadStoredFlags();
        var accounts = store.Read(Datasets.KnownPlayers, new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase));
        var flagCount = flags.Ips.Count + flags.ManualIps.Count + flags.Names.Count + flags.Ids.Count;

        await store.WriteAsync(Datasets.IpFlags, IpTrackingService.StoredFlags.Empty, ct).ConfigureAwait(false);
        await store.WriteAsync(Datasets.KnownPlayers,
            new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);

        return OwnerActionResult.Done(
            $"Wiped **{accounts.Count}** account record(s) and **{flagCount}** flag(s). " +
            "Bans themselves are untouched - this only clears what the evasion tracker knows.");
    }

    // ---- Discord command access ---------------------------------------------------------

    /* An id, not a mention. A mention resolves to an id anyway, and a bare id is what an
       owner has when the person has already left the server - which is exactly when barring
       them matters. */
    public async Task<OwnerActionResult> BarUserAsync(string userId, CancellationToken ct = default)
    {
        if (!TryUserId(userId, out var id)) return OwnerActionResult.Refused($"`{userId}` is not a Discord user id.");

        var barred = Barred();
        if (barred.Contains(id, StringComparer.Ordinal)) return OwnerActionResult.Done($"`{id}` is already barred.");

        await store.WriteAsync(Datasets.UserBlacklist, barred.Append(id).ToList(), ct).ConfigureAwait(false);

        /* An explicit un-bar outranks the bar list in the Node bot, so leaving a stale entry
           there would make this look like it did nothing. */
        var unbarred = store.Read(Datasets.UserUnbarred, new List<string>());
        if (unbarred.Contains(id, StringComparer.Ordinal))
        {
            await store.WriteAsync(Datasets.UserUnbarred,
                unbarred.Where(u => !string.Equals(u, id, StringComparison.Ordinal)).ToList(), ct).ConfigureAwait(false);
        }

        return OwnerActionResult.Done($"`{id}` is barred from every bot command.");
    }

    public async Task<OwnerActionResult> UnbarUserAsync(string userId, CancellationToken ct = default)
    {
        if (!TryUserId(userId, out var id)) return OwnerActionResult.Refused($"`{userId}` is not a Discord user id.");

        var barred = Barred();
        await store.WriteAsync(Datasets.UserBlacklist,
            barred.Where(b => !string.Equals(b, id, StringComparison.Ordinal)).ToList(), ct).ConfigureAwait(false);

        /* Recorded as explicitly un-barred, not merely absent. BLACKLIST_IDS in .env also
           feeds the bar list, and without this an id set there would come straight back on
           the next restart with no way to override it. */
        var unbarred = store.Read(Datasets.UserUnbarred, new List<string>());
        if (!unbarred.Contains(id, StringComparer.Ordinal))
            await store.WriteAsync(Datasets.UserUnbarred, unbarred.Append(id).ToList(), ct).ConfigureAwait(false);

        return OwnerActionResult.Done($"`{id}` may use bot commands again.");
    }

    public OwnerActionResult BarredUsers()
    {
        var barred = Barred();
        return barred.Count == 0
            ? OwnerActionResult.Done("No Discord user is barred.")
            : OwnerActionResult.List($"**{barred.Count}** barred Discord user(s).",
                barred.Select(id => $"• <@{id}> — `{id}`").ToList());
    }

    // ---- address tracking ignore list ----------------------------------------------------

    public async Task<OwnerActionResult> IgnoreAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return OwnerActionResult.Refused("No name given.");

        var ignored = Ignored();
        if (ignored.Contains(name, StringComparer.OrdinalIgnoreCase))
            return OwnerActionResult.Done($"**{name}** is already ignored.");

        await store.WriteAsync(Datasets.IgnoredNames, ignored.Append(name).ToList(), ct).ConfigureAwait(false);
        tracking.Untracked.Add(name);
        return OwnerActionResult.Done($"**{name}**'s addresses will no longer be recorded. Existing records are kept - clear those with a full wipe.");
    }

    public async Task<OwnerActionResult> UnignoreAsync(string name, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return OwnerActionResult.Refused("No name given.");

        var ignored = Ignored();
        if (!ignored.Contains(name, StringComparer.OrdinalIgnoreCase))
            return OwnerActionResult.Done($"**{name}** was not ignored.");

        await store.WriteAsync(Datasets.IgnoredNames,
            ignored.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList(), ct).ConfigureAwait(false);
        tracking.Untracked.Remove(name);
        return OwnerActionResult.Done($"**{name}** is tracked again.");
    }

    public OwnerActionResult IgnoredNames()
    {
        var ignored = Ignored();
        return ignored.Count == 0
            ? OwnerActionResult.Done("No name is ignored.")
            : OwnerActionResult.List($"**{ignored.Count}** ignored name(s).", ignored.Select(n => $"• **{n}**").ToList());
    }

    /// <summary>Load the persisted ignore list into the tracker. Called once at startup.</summary>
    public void RestoreIgnoreList()
    {
        foreach (var name in Ignored()) tracking.Untracked.Add(name);
    }

    // ---- whitelists ---------------------------------------------------------------------

    public async Task<OwnerActionResult> SaveWhitelistsAsync(CancellationToken ct = default)
    {
        var ranks = store.Read(Datasets.FactionRanks, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var config = store.Read(Datasets.FactionConfig, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        await store.WriteAsync(Datasets.FactionBackup, new WhitelistSnapshot(
            DateTimeOffset.UtcNow, ranks, config), ct).ConfigureAwait(false);

        return OwnerActionResult.Done($"Snapshot saved: **{ranks.Count}** rank entr(ies), **{config.Count}** config entr(ies). Restoring overwrites the current ones.");
    }

    public async Task<OwnerActionResult> LoadWhitelistsAsync(CancellationToken ct = default)
    {
        var snapshot = store.Read<WhitelistSnapshot?>(Datasets.FactionBackup, null);
        if (snapshot is null || snapshot.At == default)
            return OwnerActionResult.Refused("There is no snapshot to restore. Save one first.");

        await store.WriteAsync(Datasets.FactionRanks, snapshot.Ranks, ct).ConfigureAwait(false);
        await store.WriteAsync(Datasets.FactionConfig, snapshot.Config, ct).ConfigureAwait(false);

        return OwnerActionResult.Done(
            $"Restored the snapshot from {snapshot.At:yyyy-MM-dd HH:mm} UTC: " +
            $"**{snapshot.Ranks.Count}** rank entr(ies), **{snapshot.Config.Count}** config entr(ies).");
    }

    // ---- destructive wipes ---------------------------------------------------------------

    /// <summary>Delete every player's ledger file. There is no undo and no backup.</summary>
    public OwnerActionResult WipeMoney()
    {
        if (string.IsNullOrWhiteSpace(ledgerDirectory) || !Directory.Exists(ledgerDirectory))
            return OwnerActionResult.Refused("MODSAVE_PATH is not set, or does not exist. Nothing was deleted.");

        int deleted = 0, failed = 0;
        foreach (var file in Directory.EnumerateFiles(ledgerDirectory, "*.txt"))
        {
            try { File.Delete(file); deleted++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed++; }
        }

        /* Reporting the failures matters more than it looks: a file the bot cannot delete is
           usually one the game server holds open, and that player keeps their money while
           everyone else loses theirs. Silence would hide that entirely. */
        return OwnerActionResult.Done(failed == 0
            ? $"Deleted **{deleted}** player ledger(s)."
            : $"Deleted **{deleted}** player ledger(s). **{failed}** could not be deleted - most likely held open by the game server.");
    }

    /// <summary>Clear the bot's player knowledge. Bans, whitelists and warrants survive.</summary>
    public async Task<OwnerActionResult> WipePlayerDataAsync(CancellationToken ct = default)
    {
        var accounts = store.Read(Datasets.KnownPlayers, new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase)).Count;
        var playtime = store.Read(Datasets.Playtime, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)).Count;
        var lastSeen = store.Read(Datasets.LastSeen, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)).Count;

        await store.WriteAsync(Datasets.KnownPlayers, new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        await store.WriteAsync(Datasets.Playtime, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        await store.WriteAsync(Datasets.LastSeen, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);

        return OwnerActionResult.Done(
            $"Cleared **{accounts}** account record(s), **{playtime}** playtime entr(ies) and **{lastSeen}** last-seen entr(ies).\n" +
            "Bans, blacklist flags, warrants, whitelists and donators were **not** touched.");
    }

    // ---- ban and menu maintenance ---------------------------------------------------------
    // Carried over from the earlier flat /configure command so the panel is the ONE place
    // these live. Two entry points to "clear every ban" is one too many.

    public async Task<OwnerActionResult> ClearTempBansAsync(CancellationToken ct = default)
    {
        var before = store.Read<List<BanRecord>>(Datasets.TempBans, []);
        var kept = before.Where(b => b.Permanent).ToList();
        var removed = before.Count - kept.Count;

        await store.WriteAsync(Datasets.TempBans, kept, ct).ConfigureAwait(false);
        return OwnerActionResult.Done($"**{removed}** temporary ban(s) cleared. Permanent bans were kept.");
    }

    public async Task<OwnerActionResult> ClearAllBansAsync(CancellationToken ct = default)
    {
        var count = store.Read<List<BanRecord>>(Datasets.TempBans, []).Count;
        await store.WriteAsync<List<BanRecord>>(Datasets.TempBans, [], ct).ConfigureAwait(false);

        /* The bot's record only. Lifting the native server bans too would be one action that
           unbans everyone on every server, and the reconcile pass would then have nothing
           left to re-apply. */
        return OwnerActionResult.Done(
            $"**{count}** ban record(s) cleared from the bot. Native server bans are untouched - lift those with `/unban`.");
    }

    public async Task<OwnerActionResult> StripAllMenusAsync(CancellationToken ct = default)
    {
        var count = store.Read(Datasets.MenuGrants, new Dictionary<string, string>(StringComparer.Ordinal)).Count;
        await store.WriteAsync(Datasets.MenuGrants, new Dictionary<string, string>(StringComparer.Ordinal), ct).ConfigureAwait(false);

        // Name bindings survive. Clearing those too would let everybody re-claim as an alt,
        // which is the exact thing the binding exists to prevent.
        return OwnerActionResult.Done($"**{count}** menu grant(s) removed. Name bindings are unchanged.");
    }

    public async Task<OwnerActionResult> ClearAllFlagsAsync(CancellationToken ct = default)
    {
        var flags = tracking.LoadStoredFlags();
        var count = flags.Ips.Count + flags.ManualIps.Count + flags.Names.Count + flags.Ids.Count;

        await store.WriteAsync(Datasets.IpFlags, IpTrackingService.StoredFlags.Empty, ct).ConfigureAwait(false);
        return OwnerActionResult.Done($"**{count}** address, account and name flag(s) cleared. Ban records are unchanged.");
    }

    // ---- helpers -------------------------------------------------------------------------

    private List<string> Barred() => store.Read(Datasets.UserBlacklist, new List<string>());

    private List<string> Ignored() => store.Read(Datasets.IgnoredNames, new List<string>());

    private static IReadOnlyList<string> Add(IReadOnlyList<string> existing, string value, StringComparer comparer) =>
        existing.Contains(value, comparer) ? existing : existing.Append(value).ToList();

    /// <summary>A Discord snowflake, accepting a &lt;@id&gt; mention as well as a bare id.</summary>
    private static bool TryUserId(string? input, out string id)
    {
        id = "";
        var trimmed = (input ?? "").Trim().TrimStart('<', '@', '!').TrimEnd('>');
        if (!ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;

        id = parsed.ToString(CultureInfo.InvariantCulture);
        return true;
    }
}

/// <param name="At">Default when no snapshot has ever been taken.</param>
public sealed record WhitelistSnapshot(
    DateTimeOffset At,
    Dictionary<string, string> Ranks,
    Dictionary<string, string> Config);
