using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Core.Evasion;
using PavlovBot.Core.Logs;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Logs;

/// <param name="Ip">Null when the join could not be correlated to an address.</param>
/// <param name="Confident">False when the address is a guess. See <see cref="JoinCorrelator"/>.</param>
public sealed record PlayerJoined(string File, string AccountId, string? Name, string? Ip, bool Confident, DateTimeOffset At);

public sealed record PlayerConfirmed(string File, string AccountId, string? Name, string Ip, DateTimeOffset At);

/// <param name="Verdict">Which flag matched, and the wording the ban decision inspects.</param>
public sealed record FlaggedJoin(string AccountId, string? Name, string? Ip, FlagVerdict Verdict, DateTimeOffset At);

/// <summary>
/// Turning log lines into account knowledge, and catching evaders on sight.
/// </summary>
/// <remarks>
/// The specification this implements, in the order the pieces matter:
///
///   TRACK. For every account (UniqueId): its names and its CONFIRMED addresses - the
///   address and id that appear on the SAME line. Join-time addresses are correlated
///   guesses, kept for display only.
///
///   FLAG. On a ban: the account's confirmed addresses, plus its account id for a
///   PERMANENT ban only. A temp ban must not brand the account forever - it flags the
///   addresses and lets the id go.
///
///   CATCH. A live join whose EXACT address or account id is flagged. No subnets.
///
///   REVERSE. Unbanning clears what the ban created. A served temp ban never sticks.
///
/// The deferral in <see cref="RequestFlagAsync"/> is the subtle part: banning somebody who
/// is online flags nothing, because the line that confirms their address is the disconnect
/// the ban is about to cause. Without the deferral they reconnect from the same address and
/// nothing catches them.
/// </remarks>
public sealed class IpTrackingService
{
    private readonly SerializedStore _store;
    private readonly JoinCorrelator _correlator;
    private readonly MetricsRegistry _metrics;
    private readonly ILogger<IpTrackingService> _logger;
    private readonly TimeProvider _time;
    private readonly PavlovBot.Host.Moderation.IMasterNames? _masters;

    /// <summary>After a ban, how long a disconnect still counts as confirming that account.</summary>
    private static readonly TimeSpan PendingFlagWindow = TimeSpan.FromMinutes(5);

    /// <summary>Do not re-auto-ban the same account within this window.</summary>
    private static readonly TimeSpan AutoBanDebounce = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyAutoBanned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assembled from lines that may arrive split across several.</summary>
    private readonly Dictionary<string, KillEvent> _partialKills = new(StringComparer.Ordinal);

    public IpTrackingService(
        SerializedStore store,
        MetricsRegistry metrics,
        ILogger<IpTrackingService> logger,
        JoinCorrelator? correlator = null,
        TimeProvider? time = null,
        PavlovBot.Host.Moderation.IMasterNames? masters = null)
    {
        _masters = masters;
        _store = store;
        _metrics = metrics;
        _logger = logger;
        _correlator = correlator ?? new JoinCorrelator();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised for every login. The address may be a guess - check <c>Confident</c>.</summary>
    public event Func<PlayerJoined, Task>? Joined;

    /// <summary>Raised when an address is CERTAIN. This is the event that may flag.</summary>
    public event Func<PlayerConfirmed, Task>? Confirmed;

    /// <summary>Raised when a join matched a standing flag. The handler decides what to do.</summary>
    public event Func<FlaggedJoin, Task>? Flagged;

    public event Func<KillEvent, Task>? Kill;

    /// <summary>Names that must never be tracked, so master accounts leave no address trail.</summary>
    public HashSet<string> Untracked { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- storage ----

    private Dictionary<string, AccountRecord> LoadAccounts() =>
        _store.Read(Datasets.KnownPlayers, new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase));

    public FlagSet LoadFlags()
    {
        var stored = LoadStoredFlags();
        return new FlagSet(
            new HashSet<string>(stored.Ips, StringComparer.Ordinal),
            new HashSet<string>(stored.Names, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(stored.Ids, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(stored.ManualIps, StringComparer.Ordinal));
    }

    /// <summary>
    /// The flags, reading through to the old location once.
    /// </summary>
    /// <remarks>
    /// These used to be stored under <c>user_blacklist</c>, which is the Node bot's array
    /// of barred Discord ids - see <see cref="Datasets.IpFlags"/>. An installation that ran
    /// the older build still has its flags there, and simply switching keys would present
    /// as every flag having been forgotten: banned players walk back in and nothing logs a
    /// reason.
    ///
    /// So a missing new dataset falls back to the old one. The fallback is READ-ONLY and
    /// never writes back - the next mutation writes to the new key naturally, and the stale
    /// object stays where it is until the Node bot overwrites it with its own array, which
    /// is exactly the outcome we want.
    /// </remarks>
    internal StoredFlags LoadStoredFlags()
    {
        var stored = _store.Read(Datasets.IpFlags, StoredFlags.Empty);
        if (stored != StoredFlags.Empty && (stored.Ips.Count > 0 || stored.Names.Count > 0 ||
                                            stored.Ids.Count > 0 || stored.ManualIps.Count > 0))
        {
            return stored;
        }

        /* Deserialising Node's ARRAY as StoredFlags fails and yields Empty, so this cannot
           mistake the Node format for legacy C# data. */
        var legacy = _store.Read(Datasets.UserBlacklist, StoredFlags.Empty);
        return legacy.Ips.Count > 0 || legacy.Names.Count > 0 ||
               legacy.Ids.Count > 0 || legacy.ManualIps.Count > 0
            ? legacy
            : stored;
    }

    public AccountRecord? Account(string accountId) => LoadAccounts().GetValueOrDefault(accountId);

    /// <summary>Every display name the bot has ever recorded, most recent per account first.</summary>
    public IReadOnlyList<string> KnownNames() =>
        LoadAccounts().Values
            .OrderByDescending(a => a.LastSeen ?? DateTimeOffset.MinValue)
            .SelectMany(a => a.Names)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Look an account up by any name it has used.</summary>
    public AccountRecord? AccountByName(string name) =>
        LoadAccounts().Values.FirstOrDefault(a => a.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>Every account seen on this address, CONFIRMED sightings only.</summary>
    public IReadOnlyList<AccountRecord> AccountsWithAddress(string ip) =>
        LoadAccounts().Values.Where(a => a.ConfirmedIps.Contains(ip, StringComparer.Ordinal)).ToList();

    /// <summary>
    /// Other accounts that share a confirmed address with this one.
    /// </summary>
    /// <remarks>
    /// CONFIRMED ADDRESSES ONLY - never the guessed ones. A guess is a correlation between
    /// a join line and a nearby address line, and on a busy server two players connecting
    /// in the same second can be correlated to each other's address. Building "these are
    /// the same person" on that would produce confident, wrong accusations, and the person
    /// being accused has no way to disprove it.
    ///
    /// Shared addresses are still EVIDENCE, not proof: households, phone tethering and
    /// student halls all put unrelated people on one address.
    /// </remarks>
    public IReadOnlyList<AccountRecord> AltsOf(string accountId)
    {
        var accounts = LoadAccounts();
        if (!accounts.TryGetValue(accountId, out var subject)) return [];

        var addresses = subject.ConfirmedIps.ToHashSet(StringComparer.Ordinal);
        if (addresses.Count == 0) return [];

        return accounts.Values
            .Where(a => !string.Equals(a.Id, accountId, StringComparison.OrdinalIgnoreCase))
            .Where(a => a.ConfirmedIps.Any(addresses.Contains))
            .ToList();
    }

    // ---- ingest ----

    /// <summary>Feed one line in. Safe to call for every line of the log.</summary>
    public async Task IngestAsync(LogLine line, CancellationToken ct = default)
    {
        var at = PavlovLog.TimestampOf(line.Text) ?? _time.GetUtcNow();

        if (PavlovLog.AcceptedAddress(line.Text) is { } accepted)
        {
            _correlator.Accepted(line.File, accepted, at);
            return;
        }

        if (PavlovLog.Confirmed(line.Text) is { } pairing)
        {
            await OnConfirmedAsync(line.File, pairing, at, ct).ConfigureAwait(false);
            return;
        }

        if (PavlovLog.Login(line.Text) is { } login)
        {
            await OnLoginAsync(line.File, login, at, ct).ConfigureAwait(false);
            return;
        }

        if (PavlovLog.Kill(line.Text) is { } kill)
        {
            await OnKillAsync(line.File, kill).ConfigureAwait(false);
        }
    }

    private async Task OnLoginAsync(string file, LoginRequest login, DateTimeOffset at, CancellationToken ct)
    {
        var placeholder = PavlovLog.IsPlaceholderId(login.Id);
        var guess = _correlator.Login(file, at, placeholder);

        // A pre-authentication line identifies nobody; correlating it would attribute a
        // real address to a phantom account.
        if (placeholder) return;

        if (login.Name is { } named && Untracked.Contains(named))
        {
            /* An ignore-listed name is not recorded - no address, no alt trail, which is
               the whole point of the exemption. A MASTER still gets the public join LINE,
               because a join log that silently omits the owner reads as a broken log to the
               one person most likely to be watching it. It carries no address either way. */
            if (_masters?.IsMaster(named) == true && Joined is { } forMaster)
                await forMaster(new PlayerJoined(file, login.Id, named, null, false, at)).ConfigureAwait(false);

            return;
        }

        await RecordAsync(login.Id, login.Name, guessedIp: guess.Confident ? guess.Ip : null, confirmedIp: null, at, ct)
            .ConfigureAwait(false);

        if (Joined is { } joined)
            await joined(new PlayerJoined(file, login.Id, login.Name, guess.Ip, guess.Confident, at)).ConfigureAwait(false);

        await CheckFlagsAsync(login.Id, login.Name, guess.Confident ? guess.Ip : null, at).ConfigureAwait(false);
    }

    private async Task OnConfirmedAsync(string file, ConfirmedPairing pairing, DateTimeOffset at, CancellationToken ct)
    {
        if (PavlovLog.IsPlaceholderId(pairing.Id)) return;

        var existing = Account(pairing.Id);
        if (existing?.Name is { } name && Untracked.Contains(name)) return;

        await RecordAsync(pairing.Id, null, guessedIp: null, confirmedIp: pairing.Ip, at, ct).ConfigureAwait(false);

        // A ban issued while they were online has been waiting for exactly this line.
        await ResolvePendingFlagAsync(pairing.Id, pairing.Ip, at, ct).ConfigureAwait(false);

        if (Confirmed is { } confirmed)
            await confirmed(new PlayerConfirmed(file, pairing.Id, existing?.Name, pairing.Ip, at)).ConfigureAwait(false);

        _metrics.Increment("ip_confirmations_total", help: "Addresses confirmed against an account");
    }

    private async Task OnKillAsync(string file, KillEvent kill)
    {
        /* Pavlov writes the record whole or split across lines. Merge into whatever is
           already partial for this file, and only report once every field has arrived. */
        var merged = _partialKills.TryGetValue(file, out var partial)
            ? new KillEvent(kill.Killer ?? partial.Killer, kill.Killed ?? partial.Killed, kill.KilledBy ?? partial.KilledBy)
            : kill;

        if (merged.Killed is null)
        {
            _partialKills[file] = merged;
            return;
        }

        _partialKills.Remove(file);
        if (Kill is { } handler) await handler(merged).ConfigureAwait(false);
    }

    private Task RecordAsync(string id, string? name, string? guessedIp, string? confirmedIp, DateTimeOffset at, CancellationToken ct) =>
        _store.UpdateAsync(
            Datasets.KnownPlayers,
            new Dictionary<string, AccountRecord>(StringComparer.OrdinalIgnoreCase),
            accounts =>
            {
                var existing = accounts.GetValueOrDefault(id) ?? AccountRecord.Empty(id);

                // Most recent name first - the display name is what staff search by, and the
                // one they used last is the one being asked about.
                var names = existing.Names.ToList();
                if (name is { Length: > 0 })
                {
                    names.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                    names.Insert(0, name);
                }

                var confirmed = existing.ConfirmedIps.ToList();
                if (confirmedIp is not null && !confirmed.Contains(confirmedIp, StringComparer.Ordinal))
                    confirmed.Add(confirmedIp);

                var guessed = existing.GuessedIps.ToList();
                /* A guess that has since been CONFIRMED is promoted out of the guess list
                   rather than living in both - two lists disagreeing about the same address
                   is how a display ends up showing it twice with different confidence. */
                if (guessedIp is not null && !confirmed.Contains(guessedIp, StringComparer.Ordinal)
                                          && !guessed.Contains(guessedIp, StringComparer.Ordinal))
                {
                    guessed.Add(guessedIp);
                }
                guessed.RemoveAll(confirmed.Contains);

                accounts[id] = existing with
                {
                    Names = names,
                    ConfirmedIps = confirmed,
                    GuessedIps = guessed,
                    FirstSeen = existing.FirstSeen ?? at,
                    LastSeen = at,
                };
                return accounts;
            },
            ct);

    // ---- flags ----

    private async Task CheckFlagsAsync(string accountId, string? name, string? ip, DateTimeOffset at)
    {
        var verdict = FlagMatching.Check(LoadFlags(), ip, name, accountId);
        if (!verdict.Hit) return;

        /* Debounced per account. A flagged player retrying produces a login line every few
           seconds, and without this each one fires a fresh ban, a fresh audit entry and a
           fresh feed message - a self-inflicted flood exactly when staff are watching. */
        if (_recentlyAutoBanned.TryGetValue(accountId, out var last) && at - last < AutoBanDebounce) return;
        _recentlyAutoBanned[accountId] = at;

        _logger.LogWarning("FLAGGED JOIN - {Name} [{Id}] from {Ip}: {Detail}",
            name ?? "unknown", accountId, ip ?? "unknown", verdict.Detail);
        _metrics.Increment("flagged_joins_total", MetricLabels.Of("match", verdict.Match.ToString()),
            help: "Joins that matched a standing flag");

        if (Flagged is { } handler)
            await handler(new FlaggedJoin(accountId, name, ip, verdict, at)).ConfigureAwait(false);
    }

    /// <summary>
    /// Flag an account's addresses because it was banned.
    /// </summary>
    /// <param name="flagAccountId">
    /// PERMANENT bans only. A temp ban flags the addresses but must not brand the account
    /// id forever - the id flag has no expiry of its own, so a served ban would keep
    /// catching them.
    /// </param>
    public async Task<FlagOutcome> RequestFlagAsync(string accountId, bool flagAccountId, CancellationToken ct = default)
    {
        var account = Account(accountId);
        var confirmed = account?.ConfirmedIps ?? [];

        if (confirmed.Count == 0)
        {
            /* Nothing to flag YET. They are almost certainly still online: the line that
               confirms their address is the disconnect this ban is about to cause. Record
               the intent so that disconnect resolves it. */
            await _store.UpdateAsync(
                Datasets.AutobanExempt + "_pending",
                new Dictionary<string, PendingFlag>(StringComparer.OrdinalIgnoreCase),
                pending =>
                {
                    pending[accountId] = new PendingFlag(accountId, _time.GetUtcNow(), flagAccountId);
                    return pending;
                }, ct).ConfigureAwait(false);

            _logger.LogWarning(
                "{Id} has NO confirmed address yet - they were likely never seen disconnecting. " +
                "Their address will be flagged the moment a disconnect confirms it (pending, persisted across restarts)",
                accountId);

            return new FlagOutcome([], flagAccountId ? [accountId] : [], account?.Names ?? [], Pending: true);
        }

        await ApplyFlagsAsync(confirmed, flagAccountId ? [accountId] : [], ct).ConfigureAwait(false);
        return new FlagOutcome(confirmed, flagAccountId ? [accountId] : [], account?.Names ?? [], Pending: false);
    }

    private async Task ResolvePendingFlagAsync(string accountId, string ip, DateTimeOffset at, CancellationToken ct)
    {
        var pending = _store.Read(Datasets.AutobanExempt + "_pending",
            new Dictionary<string, PendingFlag>(StringComparer.OrdinalIgnoreCase));

        if (!pending.TryGetValue(accountId, out var request)) return;

        if (!request.IsLive(at, PendingFlagWindow))
        {
            // Too late: after a few minutes, any disconnect is somebody else's.
            await _store.UpdateAsync(Datasets.AutobanExempt + "_pending", pending,
                p => { p.Remove(accountId); return p; }, ct).ConfigureAwait(false);
            return;
        }

        await ApplyFlagsAsync([ip], request.FlagAccountId ? [accountId] : [], ct).ConfigureAwait(false);
        await _store.UpdateAsync(Datasets.AutobanExempt + "_pending", pending,
            p => { p.Remove(accountId); return p; }, ct).ConfigureAwait(false);

        _logger.LogInformation("Pending flag resolved for {Id}: {Ip} confirmed and flagged", accountId, ip);
    }

    private Task ApplyFlagsAsync(IReadOnlyCollection<string> ips, IReadOnlyCollection<string> ids, CancellationToken ct) =>
        _store.UpdateAsync(Datasets.IpFlags, LoadStoredFlags(), flags => new StoredFlags(
            flags.Ips.Union(ips, StringComparer.Ordinal).ToList(),
            flags.Names,
            flags.Ids.Union(ids, StringComparer.OrdinalIgnoreCase).ToList(),
            flags.ManualIps), ct);

    /// <summary>
    /// Clear the flags a ban created.
    /// </summary>
    /// <remarks>
    /// MANUAL address flags are left alone. An unban lifts what that ban created; it must
    /// not silently undo a standing administrative block an admin set through /configure.
    /// </remarks>
    public async Task<FlagOutcome> ClearFlagsAsync(string accountId, CancellationToken ct = default)
    {
        var account = Account(accountId);
        var ips = account?.ConfirmedIps ?? [];
        var names = account?.Names ?? [];

        await _store.UpdateAsync(Datasets.IpFlags, LoadStoredFlags(), flags => new StoredFlags(
            flags.Ips.Except(ips, StringComparer.Ordinal).ToList(),
            flags.Names.Except(names, StringComparer.OrdinalIgnoreCase).ToList(),
            flags.Ids.Where(i => !string.Equals(i, accountId, StringComparison.OrdinalIgnoreCase)).ToList(),
            flags.ManualIps), ct).ConfigureAwait(false);

        // Also drop any pending intent, or a later disconnect would re-flag them.
        await _store.UpdateAsync(Datasets.AutobanExempt + "_pending",
            new Dictionary<string, PendingFlag>(StringComparer.OrdinalIgnoreCase),
            pending => { pending.Remove(accountId); return pending; }, ct).ConfigureAwait(false);

        _recentlyAutoBanned.TryRemove(accountId, out _);
        return new FlagOutcome(ips, [accountId], names, Pending: false);
    }

    /// <summary>An address flagged by hand. Survives an unban, by design.</summary>
    public Task FlagAddressManuallyAsync(string ip, CancellationToken ct = default) =>
        _store.UpdateAsync(Datasets.IpFlags, LoadStoredFlags(), flags => new StoredFlags(
            flags.Ips, flags.Names, flags.Ids,
            flags.ManualIps.Union([ip], StringComparer.Ordinal).ToList()), ct);

    /// <param name="Ips">Ban-derived address flags, cleared by an unban.</param>
    /// <param name="ManualIps">Admin-set address flags, NOT cleared by an unban.</param>
    public sealed record StoredFlags(
        IReadOnlyList<string> Ips,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> Ids,
        IReadOnlyList<string> ManualIps)
    {
        public static StoredFlags Empty { get; } = new([], [], [], []);
    }
}
