using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using AccountRecord = PavlovBot.Core.Evasion.AccountRecord;
using PavlovBot.Core.Moderation;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Moderation;

/// <summary>A ufw deny this feature created, and the ban it is for.</summary>
public sealed record FirewalledBan(string Player, DateTimeOffset At);

/// <summary>What one pass changed.</summary>
public sealed record BanFirewallPass(int Denied, int Lifted, int Failed, int Deferred);

/// <summary>
/// Keeps a ufw deny on every address of every actively banned player, temp bans included,
/// and removes it when the ban ends.
/// </summary>
/// <remarks>
/// A RECONCILE, NOT A HOOK ON EACH BAN. Bans start and end in a dozen places - the commands,
/// the evasion and VPN responders, the ban-file import, expiry, the owner panel - and a player's
/// address is often not known until their disconnect line, after the ban. Comparing "what should
/// be denied" with "what this feature has denied" every pass covers all of them, including the
/// ones added later, and heals after a failed ufw call or a restart.
///
/// ONLY ITS OWN RULES ARE EVER REMOVED. Every deny it adds is recorded in
/// <see cref="Datasets.FirewallBans"/>, and only those are lifted. A manual <c>/configure</c>
/// address block is skipped entirely - its rule belongs to the owner panel. A rule added by hand
/// with <c>/firewall</c> for an address that is also a banned player's is NOT known to this, and
/// is removed with the ban.
///
/// A UFW DENY BLOCKS EVERY PORT, SSH INCLUDED. That is the danger of automating it, and why these
/// are never denied: addresses that are not public (loopback - RCON runs over it - private,
/// link-local, carrier NAT), any address a master name has connected from, anything in
/// <c>FIREWALL_NEVER_BLOCK</c>, and players who are masters, protected or exempt.
///
/// BOUNDED. At most <see cref="MaxChangesPerPass"/> ufw calls a pass, so a sudden flood of bans
/// cannot tie the host up, and a failed address is retried only after <see cref="RetryAfter"/>, so
/// a box without ufw logs a failure every ten minutes rather than every thirty seconds.
/// </remarks>
public sealed class BanFirewall
{
    /// <summary>The most ufw calls one pass makes. The rest wait for the next pass.</summary>
    public const int MaxChangesPerPass = 50;

    /// <summary>How long a failed address waits before it is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private static readonly IPNetwork[] NotPublic =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("224.0.0.0/3"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
    ];

    private readonly BanService _bans;
    private readonly IpTrackingService _tracking;
    private readonly MasterNames _masters;
    private readonly IFirewall _firewall;
    private readonly SerializedStore _store;
    private readonly IReadOnlySet<string> _neverBlock;
    private readonly bool _enabled;
    private readonly ILogger<BanFirewall> _logger;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedAt = new(StringComparer.Ordinal);

    public BanFirewall(
        BanService bans,
        IpTrackingService tracking,
        MasterNames masters,
        IFirewall firewall,
        SerializedStore store,
        bool enabled,
        IEnumerable<string> neverBlock,
        ILogger<BanFirewall> logger,
        TimeProvider? time = null)
    {
        _bans = bans;
        _tracking = tracking;
        _masters = masters;
        _firewall = firewall;
        _store = store;
        _enabled = enabled;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        var parsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in neverBlock ?? [])
        {
            if (Canonical(raw) is { } ip) parsed.Add(ip);
            else logger.LogWarning("FIREWALL_NEVER_BLOCK entry \"{Entry}\" is not an address and is ignored", raw);
        }
        _neverBlock = parsed;
    }

    /// <summary>The address in the one spelling ufw and the store both use, or null.</summary>
    internal static string? Canonical(string? raw)
    {
        if (!IPAddress.TryParse((raw ?? "").Trim(), out var address)) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    /// <summary>Whether an address is one the internet could never be banned from.</summary>
    internal static bool IsPublic(string canonical)
    {
        if (!IPAddress.TryParse(canonical, out var address)) return false;
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        return !NotPublic.Any(n => n.BaseAddress.AddressFamily == address.AddressFamily && n.Contains(address));
    }

    /// <summary>Every address that should carry a deny right now, with the player it is for.</summary>
    internal IReadOnlyDictionary<string, string> Wanted()
    {
        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!_enabled) return wanted;

        // One read of the accounts per pass, not one per ban: this runs every thirty seconds.
        var accounts = new AccountIndex(_tracking.Accounts());
        var spared = SparedAddresses(accounts);

        foreach (var ban in _bans.ActiveBans())
        {
            if (_masters.IsMaster(ban.PlayerId) || _masters.IsProtected(ban.PlayerId) || _masters.IsExempt(ban.PlayerId))
                continue;

            foreach (var raw in AddressesOf(ban, accounts))
            {
                if (Canonical(raw) is not { } ip || spared.Contains(ip) || !IsPublic(ip)) continue;
                wanted.TryAdd(ip, ban.PlayerId);
            }
        }
        return wanted;
    }

    /// <summary>
    /// A banned player's addresses: every CONFIRMED one their account has used, plus any the ban
    /// snapshotted. Guessed addresses never - a guess can be the next player's.
    /// </summary>
    private static IEnumerable<string> AddressesOf(BanRecord ban, AccountIndex accounts)
    {
        var account = (ban.UniqueId is { Length: > 0 } id ? accounts.ById(id) : null) ?? accounts.ByName(ban.PlayerId);
        return (account?.ConfirmedIps ?? []).Concat(ban.Network?.Ips ?? []);
    }

    /// <summary>The accounts, looked up by id and by any name they have used.</summary>
    private sealed class AccountIndex
    {
        private readonly IReadOnlyDictionary<string, AccountRecord> _byId;
        private readonly Dictionary<string, AccountRecord> _byName = new(StringComparer.OrdinalIgnoreCase);

        public AccountIndex(IReadOnlyDictionary<string, AccountRecord> accounts)
        {
            _byId = accounts;
            foreach (var account in accounts.Values)
            {
                foreach (var name in account.Names) _byName.TryAdd(name, account);
            }
        }

        public AccountRecord? ById(string id) => _byId.GetValueOrDefault(id);
        public AccountRecord? ByName(string name) => _byName.GetValueOrDefault(name);
    }

    /// <summary>Addresses that are never denied, and never lifted either when they are manual blocks.</summary>
    private HashSet<string> SparedAddresses(AccountIndex accounts)
    {
        var spared = new HashSet<string>(_neverBlock, StringComparer.Ordinal);

        foreach (var master in _masters.Masters)
        {
            foreach (var raw in accounts.ByName(master)?.ConfirmedIps ?? [])
            {
                if (Canonical(raw) is { } ip) spared.Add(ip);
            }
        }

        foreach (var raw in ManualBlocks()) spared.Add(raw);
        return spared;
    }

    private HashSet<string> ManualBlocks() =>
        [.. _tracking.LoadFlags().ManualIps.Select(Canonical).OfType<string>()];

    public IReadOnlyDictionary<string, FirewalledBan> Managed() =>
        _store.ReadMap(Datasets.FirewallBans, new Dictionary<string, FirewalledBan>(StringComparer.Ordinal));

    /// <summary>One pass: deny what is wanted and not yet denied, lift what is denied and no longer wanted.</summary>
    public async Task<BanFirewallPass> SyncAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var wanted = Wanted();
        var managed = Managed();
        var manual = ManualBlocks();

        var denied = 0;
        var lifted = 0;
        var failed = 0;
        var deferred = 0;
        var budget = MaxChangesPerPass;

        foreach (var ip in managed.Keys.Where(ip => !wanted.ContainsKey(ip)).ToList())
        {
            // A manual /configure block now owns this address; its rule is the owner panel's.
            if (manual.Contains(ip))
            {
                await ForgetAsync(ip, ct).ConfigureAwait(false);
                continue;
            }

            if (!Due(ip, now)) continue;
            if (budget-- <= 0) { deferred++; continue; }

            var result = await _firewall.UndenyAsync(ip, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                await ForgetAsync(ip, ct).ConfigureAwait(false);
                _failedAt.TryRemove(ip, out _);
                lifted++;
                _logger.LogWarning("FIREWALL LIFT | ip={Ip} | ban on {Player} has ended", ip, managed[ip].Player);
            }
            else
            {
                Failed(ip, now, "remove the deny for", result.Detail);
                failed++;
            }
        }

        foreach (var (ip, player) in wanted.Where(w => !managed.ContainsKey(w.Key)))
        {
            if (!Due(ip, now)) continue;
            if (budget-- <= 0) { deferred++; continue; }

            var result = await _firewall.DenyAsync(ip, ct).ConfigureAwait(false);
            if (result.Ok)
            {
                // Recorded immediately, so a crash between two denies cannot orphan a rule.
                await _store.UpdateMapAsync(Datasets.FirewallBans,
                    new Dictionary<string, FirewalledBan>(StringComparer.Ordinal),
                    rules => { rules[ip] = new FirewalledBan(player, now); return rules; }, ct).ConfigureAwait(false);
                _failedAt.TryRemove(ip, out _);
                denied++;
                _logger.LogWarning("FIREWALL DENY | ip={Ip} | banned player {Player}", ip, player);
            }
            else
            {
                Failed(ip, now, "deny", result.Detail);
                failed++;
            }
        }

        if (deferred > 0)
            _logger.LogInformation("Ban firewall: {Deferred} change(s) deferred to the next pass (limit {Limit})",
                deferred, MaxChangesPerPass);

        return new BanFirewallPass(denied, lifted, failed, deferred);
    }

    private bool Due(string ip, DateTimeOffset now) =>
        !_failedAt.TryGetValue(ip, out var at) || now - at >= RetryAfter;

    private void Failed(string ip, DateTimeOffset now, string action, string detail)
    {
        _failedAt[ip] = now;
        _logger.LogError("FIREWALL FAILED to {Action} {Ip}: {Detail} - retrying in {Minutes}m",
            action, ip, detail, (int)RetryAfter.TotalMinutes);
    }

    private Task ForgetAsync(string ip, CancellationToken ct) =>
        _store.UpdateMapAsync(Datasets.FirewallBans,
            new Dictionary<string, FirewalledBan>(StringComparer.Ordinal),
            rules => rules.Remove(ip) ? rules : null, ct);
}
