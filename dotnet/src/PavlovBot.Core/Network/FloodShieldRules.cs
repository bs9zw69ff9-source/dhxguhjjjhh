using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PavlovBot.Core.Network;

/// <summary>The limits the shield applies while an attack is on.</summary>
/// <param name="GamePorts">UDP ports and ranges to protect, e.g. <c>7777-7799,8177-8199</c>.</param>
/// <param name="PerSourcePps">One address over this rate to the game ports is blocked.</param>
/// <param name="UnknownPps">All traffic to the game ports from addresses that are not known players, together.</param>
/// <param name="BlockMinutes">How long a blocked address stays blocked.</param>
public sealed record ShieldLimits(
    string GamePorts = "7777-7799,8177-8199",
    int PerSourcePps = 1000,
    int UnknownPps = 5000,
    int BlockMinutes = 10);

/// <summary>What the shield did, from the firewall's own counters.</summary>
/// <param name="BlockedPackets">Dropped because their sender was on the block list.</param>
/// <param name="PerSourcePackets">Dropped by the per-address limit (each one also blocked its sender).</param>
/// <param name="CappedPackets">Dropped by the cap on unknown traffic.</param>
/// <param name="BlockedSources">Addresses on the block list now, IPv4 and IPv6.</param>
/// <param name="IcmpPackets">Pings dropped by the echo-request limit.</param>
/// <param name="SshPackets">New SSH connections dropped by the per-address connection limit.</param>
public sealed record ShieldStats(
    long BlockedPackets, long PerSourcePackets, long CappedPackets, int BlockedSources, long IcmpPackets = 0, long SshPackets = 0)
{
    public long Dropped => BlockedPackets + PerSourcePackets + CappedPackets + IcmpPackets + SshPackets;
}

/// <summary>
/// How much the firewall writes to the packet log. Filtering never depends on it.
/// </summary>
/// <remarks>
/// <see cref="Off"/> and <see cref="Minimal"/> are imposed by storage pressure, never chosen;
/// <see cref="Normal"/>, <see cref="Elevated"/> and <see cref="Incident"/> are the modes an owner
/// picks. Ordered by volume, so "the lower of what was asked and what the disk allows" is a min.
/// </remarks>
public enum LogMode
{
    Off,
    Minimal,
    Normal,
    Elevated,
    Incident,
}

/// <summary>The most packet records a second each kind of log rule may write in a mode.</summary>
/// <param name="DropsPerSecond">Dropped packets, shared by every drop rule.</param>
/// <param name="SuspectPerSecond">Packets from sources over a suspicion threshold, shared.</param>
/// <param name="SamplesPerMinute">Routine game traffic, sampled. Zero writes none.</param>
public sealed record LogRates(int DropsPerSecond, int SuspectPerSecond, int SamplesPerMinute)
{
    /// <summary>The rates of a mode. The numbers are the storage budget; see <see cref="WorstCasePerSecond"/>.</summary>
    public static LogRates For(LogMode mode) => mode switch
    {
        LogMode.Off => new LogRates(0, 0, 0),
        LogMode.Minimal => new LogRates(1, 1, 0),
        LogMode.Normal => new LogRates(2, 2, 6),
        LogMode.Elevated => new LogRates(20, 20, 60),
        LogMode.Incident => new LogRates(50, 50, 300),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>
    /// The most lines a second the kernel can hand the logger in this mode, whatever the traffic.
    /// </summary>
    /// <remarks>
    /// Each kind shares ONE named nftables limit across all its rules, so the total is the sum of
    /// the three rates, not the rates times the number of rules.
    /// </remarks>
    public double WorstCasePerSecond => DropsPerSecond + SuspectPerSecond + SamplesPerMinute / 60.0;
}

/// <summary>Everything the firewall policy is built from. Validated by <see cref="FloodShieldRules.Problem"/>.</summary>
public sealed record FirewallSettings
{
    /// <summary>The public interface. Traffic arriving anywhere else (loopback, Docker's bridges) is not touched.</summary>
    public required string Interface { get; init; }

    public ShieldLimits Limits { get; init; } = new();

    /// <summary>Whether the drop rules exist at all (<c>ATTACK_MITIGATION</c>). Off, the table only counts and logs.</summary>
    public bool Enforce { get; init; }

    /// <summary>TCP ports SSH listens on. Always let through, after the per-address connection limit.</summary>
    public string SshPorts { get; init; } = "22";

    /// <summary>New SSH connections a minute one address may open before the rest are dropped.</summary>
    public int SshNewPerMinute { get; init; } = 30;

    /// <summary>Pings a second, from everybody together, before the rest are dropped.</summary>
    public int IcmpEchoPps { get; init; } = 50;

    /// <summary>One non-player address sending the game ports more than this is logged as suspicious. Never dropped for it.</summary>
    public int SuspectUdpPps { get; init; } = 300;

    /// <summary>One address opening TCP connections faster than this is logged as suspicious.</summary>
    public int SuspectSynPerSecond { get; init; } = 20;

    /// <summary>The NFLOG group the packet logger (ulogd2) listens on.</summary>
    public int NflogGroup { get; init; } = 7;
}

/// <summary>
/// The nftables policy: what is filtered, what is counted, what is logged - and the reading of it back.
/// </summary>
/// <remarks>
/// PURE TEXT, so every rule the bot will load is pinned by a test before it reaches a kernel, and
/// <c>nft -c</c> checks it on the box before it is applied.
///
/// ITS OWN TABLE, <c>inet pavlovbot_ddos</c>, and nothing else is touched: ufw's rules, Docker's,
/// CrowdSec's and anybody else's stay exactly as they are, and deleting this one table undoes all
/// of it. Every chain's policy is ACCEPT: this table can only ever drop the specific things below,
/// so it cannot turn into a default-deny that locks the owner out. And an accept here ends only this
/// table's chain - ufw and CrowdSec still judge the packet - so nothing on the allow list here can
/// open what another firewall closes.
///
/// TWO BASE CHAINS, BOTH ON PREROUTING, so traffic to Docker containers on a bridge is covered as
/// well as traffic to the host:
///   GUARD, priority -300 (raw), BEFORE CONNECTION TRACKING. A flood dropped here never costs a
///   conntrack entry, which is what a flood exhausts first. In order, and the order is the safety:
///     1. trusted addresses (the owner's allow list) skip everything below;
///     2. SSH is always let through - after a per-address new-connection limit that no human
///        reaches - so a block can never lock the owner out;
///     3. known players are always let through, so the people the servers exist for are never on
///        the wrong side of a limit;
///     4. addresses on the block list are dropped, and pings are capped;
///     WHILE AN ATTACK IS ON:
///     5. one address over <see cref="ShieldLimits.PerSourcePps"/> to the game ports is blocked
///        for <see cref="ShieldLimits.BlockMinutes"/>;
///     6. everything else to the game ports is capped at <see cref="ShieldLimits.UnknownPps"/>
///        in total - the answer to a spoofed flood no per-address limit can see.
///   OBSERVE, priority -150, after conntrack: counts what got through per source, protocol and
///   port in kernel sets, and logs suspicious and sampled packets. It never drops.
///
/// THE KERNEL DOES THE COUNTING. Per-source totals are dynamic sets with counters, read once a
/// minute - no packet is copied to userspace to be counted. Return traffic of the box's own
/// connections (<c>ct direction reply</c>) is counted in total but not per source, or every
/// download would look like a port scan.
///
/// LOGGING IS BOUNDED IN THE KERNEL. Every log rule draws on one of three named limits per mode
/// (<see cref="LogRates"/>), so the packet log cannot outrun its budget whatever arrives, and the
/// log rules are separate from the drop rules - a mode change can never weaken a filter. Logged
/// packets go to an NFLOG group, not the kernel log, so nothing loops through journald.
/// </remarks>
public static partial class FloodShieldRules
{
    public const string Table = "pavlovbot_ddos";

    private const string Family = "inet";
    private const string T = Family + " " + Table;

    /// <summary>The chains packets are dropped in, one per reason; each counts and logs its own.</summary>
    public static readonly IReadOnlyList<string> DropReasons = ["blocked", "per-source", "unknown-cap", "icmp-flood", "ssh-rate"];

    /// <summary>Sizes of the kernel's per-source sets: bounded memory, whatever a spoofed flood sends.</summary>
    internal const int AccountingSize = 65_535;
    internal const int SmallSetSize = 16_384;

    /// <summary>Snap length for logged packets: headers only.</summary>
    private const int LogSnapLength = 128;

    /// <summary>
    /// A ports value: comma-separated ports and ranges, nothing else.
    /// </summary>
    /// <remarks>It is pasted into an nft script, so it is allow-listed rather than escaped.</remarks>
    [GeneratedRegex(@"^\s*\d{1,5}(\s*-\s*\d{1,5})?(\s*,\s*\d{1,5}(\s*-\s*\d{1,5})?)*\s*$")]
    private static partial Regex PortsPattern { get; }

    /// <summary>An interface name nft is given: what the kernel allows, nothing that could end a string.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,15}$")]
    private static partial Regex InterfacePattern { get; }

    /// <summary>The ports in nft's set syntax, or null when they are not a ports list.</summary>
    public static string? Ports(string? spec)
    {
        if (spec is null || !PortsPattern.IsMatch(spec)) return null;

        var parts = new List<string>();
        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = item.Split('-', StringSplitOptions.TrimEntries);
            var from = int.Parse(range[0], CultureInfo.InvariantCulture);
            var to = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : from;
            if (from is < 1 or > 65535 || to is < 1 or > 65535 || to < from) return null;
            parts.Add(from == to ? Number(from) : $"{Number(from)}-{Number(to)}");
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Why these settings cannot become rules, or null when they can.</summary>
    public static string? Problem(FirewallSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!InterfacePattern.IsMatch(settings.Interface ?? "")) return $"\"{settings.Interface}\" is not an interface name";
        // Loopback carries the box's own services to each other - CrowdSec's API, RCON, the bot.
        // Filtering it would let a "flood" from 127.0.0.1 cut them all off.
        if (settings.Interface == "lo") return "the loopback interface is never filtered - set ATTACK_INTERFACE to the public one";
        if (Ports(settings.Limits.GamePorts) is null) return $"ATTACK_GAME_PORTS \"{settings.Limits.GamePorts}\" is not a list of ports";
        if (Ports(settings.SshPorts) is null) return $"ANTIDDOS_SSH_PORTS \"{settings.SshPorts}\" is not a list of ports";
        if (settings.NflogGroup is < 1 or > 65535) return $"ANTIDDOS_NFLOG_GROUP {settings.NflogGroup} is not 1-65535";

        foreach (var (name, value) in new[]
                 {
                     ("ATTACK_PER_IP_PPS", settings.Limits.PerSourcePps), ("ATTACK_UNKNOWN_PPS", settings.Limits.UnknownPps),
                     ("ATTACK_BLOCK_MINUTES", settings.Limits.BlockMinutes), ("ANTIDDOS_SSH_NEW_PER_MINUTE", settings.SshNewPerMinute),
                     ("ANTIDDOS_ICMP_PPS", settings.IcmpEchoPps), ("ANTIDDOS_SUSPECT_UDP_PPS", settings.SuspectUdpPps),
                     ("ANTIDDOS_SUSPECT_SYN_PPS", settings.SuspectSynPerSecond),
                 })
        {
            if (value is < 1 or > 10_000_000) return $"{name} {value} is out of range";
        }
        return null;
    }

    /// <summary>
    /// The whole table, with the attack-time limits on or off and the logging at a mode.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT, AND ONE TRANSACTION. <c>add</c> creates what is missing and leaves what exists;
    /// the chains and the port sets are flushed and rewritten. The trusted, player, block and
    /// accounting sets are NOT flushed, so a re-apply never forgets who is blocked, who is a
    /// player, or what has been counted. Loaded with <c>nft -f -</c>, it applies whole or not at
    /// all: a refused script leaves the previous rules in force.
    ///
    /// The single <c>input</c> chain of the first version is removed (add, flush, delete is a
    /// no-op when it is already gone), so an upgrade needs no manual step.
    /// </remarks>
    public static string Script(FirewallSettings settings, bool engaged, LogMode mode)
    {
        if (Problem(settings) is { } problem) throw new ArgumentException(problem, nameof(settings));

        var s = settings;
        var l = s.Limits;
        var b = new StringBuilder();
        void Line(string text) => b.Append(text).Append('\n');

        Line($"add table {T}");

        // The v1 chain, gone.
        Line($"add chain {T} input");
        Line($"flush chain {T} input");
        Line($"delete chain {T} input");

        Line($"add set {T} gameports {{ type inet_service; flags interval; }}");
        Line($"add set {T} sshports {{ type inet_service; flags interval; }}");
        Line($"add set {T} trusted4 {{ type ipv4_addr; flags interval; }}");
        Line($"add set {T} trusted6 {{ type ipv6_addr; flags interval; }}");
        Line($"add set {T} players4 {{ type ipv4_addr; flags timeout; timeout 30m; }}");
        // No default timeout: every entry carries its own, so changing ATTACK_BLOCK_MINUTES never
        // conflicts with the set already in the kernel.
        Line($"add set {T} blocked4 {{ type ipv4_addr; flags dynamic,timeout; }}");
        Line($"add set {T} blocked6 {{ type ipv6_addr; flags dynamic,timeout; }}");
        foreach (var v in new[] { "4", "6" })
        {
            var addr = v == "4" ? "ipv4_addr" : "ipv6_addr";
            var saddr = Saddr(v);
            Line($"add set {T} rate{v} {{ type {addr}; flags dynamic,timeout; timeout 1m; size {Number(AccountingSize)}; }}");
            Line($"add set {T} ssh{v} {{ type {addr}; flags dynamic,timeout; timeout 2m; size {Number(SmallSetSize)}; }}");
            Line($"add set {T} watch{v} {{ type {addr}; flags dynamic,timeout; timeout 1m; size {Number(AccountingSize)}; }}");
            Line($"add set {T} synwatch{v} {{ type {addr}; flags dynamic,timeout; timeout 1m; size {Number(SmallSetSize)}; }}");
            Line($"add set {T} acct{v} {{ typeof {saddr} . meta l4proto . th dport; flags dynamic,timeout; timeout 3m; size {Number(AccountingSize)}; counter; }}");
            Line($"add set {T} syn{v} {{ typeof {saddr} . th dport; flags dynamic,timeout; timeout 3m; size {Number(SmallSetSize)}; counter; }}");
            Line($"add set {T} other{v} {{ typeof {saddr} . meta l4proto; flags dynamic,timeout; timeout 3m; size {Number(SmallSetSize)}; counter; }}");
            Line($"add set {T} dropped{v} {{ typeof {saddr}; flags dynamic,timeout; timeout 3m; size {Number(AccountingSize)}; counter; }}");
        }

        // Every mode's limits always exist, so switching mode only re-points the rules.
        foreach (var m in Enum.GetValues<LogMode>())
        {
            var rates = LogRates.For(m);
            var name = Name(m);
            Line($"add limit {T} drops_{name} {{ rate {Number(Math.Max(1, rates.DropsPerSecond))}/second burst {Number(Math.Max(1, rates.DropsPerSecond))} packets }}");
            Line($"add limit {T} suspect_{name} {{ rate {Number(Math.Max(1, rates.SuspectPerSecond))}/second burst {Number(Math.Max(1, rates.SuspectPerSecond))} packets }}");
            Line($"add limit {T} samples_{name} {{ rate {Number(Math.Max(1, rates.SamplesPerMinute))}/minute burst {Number(Math.Max(1, Math.Min(rates.SamplesPerMinute, 10)))} packets }}");
        }

        Line($"add chain {T} guard {{ type filter hook prerouting priority -300; policy accept; }}");
        Line($"add chain {T} observe {{ type filter hook prerouting priority -150; policy accept; }}");
        // The base chains first: flushing them drops their jumps into the drop chains.
        Line($"flush chain {T} guard");
        Line($"flush chain {T} observe");

        var rates0 = LogRates.For(mode);
        foreach (var reason in DropReasons)
        {
            Line($"add chain {T} drop_{reason}");
            Line($"flush chain {T} drop_{reason}");
            Line($"add rule {T} drop_{reason} update @dropped4 {{ ip saddr }}");
            Line($"add rule {T} drop_{reason} update @dropped6 {{ ip6 saddr }}");
            if (rates0.DropsPerSecond > 0) Line($"add rule {T} drop_{reason} limit name \"drops_{Name(mode)}\" {Log(s, "drop", reason)}");
            Line($"add rule {T} drop_{reason} counter drop comment \"{reason}\"");
        }

        Line($"flush set {T} gameports");
        Line($"add element {T} gameports {{ {Ports(l.GamePorts)} }}");
        Line($"flush set {T} sshports");
        Line($"add element {T} sshports {{ {Ports(s.SshPorts)} }}");

        // ---- guard: before conntrack ----
        var wan = $"iifname != \"{s.Interface}\"";
        Line($"add rule {T} guard {wan} return comment \"other-interfaces\"");
        Line($"add rule {T} guard ip saddr 127.0.0.0/8 return comment \"loopback\"");
        Line($"add rule {T} guard ip6 saddr ::1 return comment \"loopback\"");
        Line($"add rule {T} guard ip saddr @trusted4 counter accept comment \"trusted\"");
        Line($"add rule {T} guard ip6 saddr @trusted6 counter accept comment \"trusted\"");
        if (s.Enforce)
        {
            var ssh = Number(s.SshNewPerMinute);
            Line($"add rule {T} guard tcp dport @sshports tcp flags & (syn | ack) == syn update @ssh4 {{ ip saddr limit rate over {ssh}/minute burst {ssh} packets }} jump drop_ssh-rate");
            Line($"add rule {T} guard tcp dport @sshports tcp flags & (syn | ack) == syn update @ssh6 {{ ip6 saddr limit rate over {ssh}/minute burst {ssh} packets }} jump drop_ssh-rate");
        }
        Line($"add rule {T} guard tcp dport @sshports accept comment \"ssh\"");
        Line($"add rule {T} guard ip saddr @players4 accept comment \"players\"");
        if (s.Enforce)
        {
            Line($"add rule {T} guard ip saddr @blocked4 jump drop_blocked");
            Line($"add rule {T} guard ip6 saddr @blocked6 jump drop_blocked");
            var icmp = Number(s.IcmpEchoPps);
            var icmpBurst = Number(s.IcmpEchoPps * 2);
            Line($"add rule {T} guard icmp type echo-request limit rate over {icmp}/second burst {icmpBurst} packets jump drop_icmp-flood");
            Line($"add rule {T} guard icmpv6 type echo-request limit rate over {icmp}/second burst {icmpBurst} packets jump drop_icmp-flood");

            if (engaged)
            {
                var perSource = Number(l.PerSourcePps);
                var perSourceBurst = Number(l.PerSourcePps * 2);
                var minutes = Number(l.BlockMinutes);
                Line($"add rule {T} guard udp dport @gameports update @rate4 {{ ip saddr limit rate over {perSource}/second burst {perSourceBurst} packets }} add @blocked4 {{ ip saddr timeout {minutes}m }} jump drop_per-source");
                Line($"add rule {T} guard udp dport @gameports update @rate6 {{ ip6 saddr limit rate over {perSource}/second burst {perSourceBurst} packets }} add @blocked6 {{ ip6 saddr timeout {minutes}m }} jump drop_per-source");
                Line($"add rule {T} guard udp dport @gameports limit rate over {Number(l.UnknownPps)}/second burst {Number(l.UnknownPps * 2)} packets jump drop_unknown-cap");
            }
        }

        // ---- observe: after conntrack, never drops ----
        Line($"add rule {T} observe {wan} return comment \"other-interfaces\"");
        Line($"add rule {T} observe ct direction reply counter return comment \"reply\"");
        Line($"add rule {T} observe meta l4proto udp counter comment \"udp\"");
        Line($"add rule {T} observe meta l4proto tcp counter comment \"tcp\"");
        Line($"add rule {T} observe meta l4proto {{ icmp, ipv6-icmp }} counter comment \"icmp\"");
        Line($"add rule {T} observe meta l4proto {{ tcp, udp }} update @acct4 {{ ip saddr . meta l4proto . th dport }}");
        Line($"add rule {T} observe meta l4proto {{ tcp, udp }} update @acct6 {{ ip6 saddr . meta l4proto . th dport }}");
        Line($"add rule {T} observe meta l4proto != {{ tcp, udp }} update @other4 {{ ip saddr . meta l4proto }}");
        Line($"add rule {T} observe meta l4proto != {{ tcp, udp }} update @other6 {{ ip6 saddr . meta l4proto }}");
        Line($"add rule {T} observe tcp flags & (syn | ack) == syn update @syn4 {{ ip saddr . th dport }}");
        Line($"add rule {T} observe tcp flags & (syn | ack) == syn update @syn6 {{ ip6 saddr . th dport }}");

        if (rates0.SuspectPerSecond > 0)
        {
            var udp = Number(s.SuspectUdpPps);
            var syn = Number(s.SuspectSynPerSecond);
            var limit = $"limit name \"suspect_{Name(mode)}\"";
            Line($"add rule {T} observe udp dport @gameports ip saddr != @players4 update @watch4 {{ ip saddr limit rate over {udp}/second burst {udp} packets }} {limit} {Log(s, "suspect", "udp-rate")}");
            Line($"add rule {T} observe udp dport @gameports update @watch6 {{ ip6 saddr limit rate over {udp}/second burst {udp} packets }} {limit} {Log(s, "suspect", "udp-rate")}");
            Line($"add rule {T} observe tcp flags & (syn | ack) == syn update @synwatch4 {{ ip saddr limit rate over {syn}/second burst {syn} packets }} {limit} {Log(s, "suspect", "syn-burst")}");
            Line($"add rule {T} observe tcp flags & (syn | ack) == syn update @synwatch6 {{ ip6 saddr limit rate over {syn}/second burst {syn} packets }} {limit} {Log(s, "suspect", "syn-burst")}");
        }
        if (rates0.SamplesPerMinute > 0)
            Line($"add rule {T} observe udp dport @gameports limit name \"samples_{Name(mode)}\" {Log(s, "sample", "game")}");

        return b.ToString();
    }

    /// <summary>
    /// The log statement. The prefix is how a record says what happened: <c>pbd &lt;action&gt; &lt;reason&gt;</c>.
    /// </summary>
    private static string Log(FirewallSettings s, string action, string reason) =>
        $"log prefix \"{Prefix} {action} {reason} \" group {Number(s.NflogGroup)} snaplen {Number(LogSnapLength)}";

    /// <summary>The start of every log prefix this table writes, so its records are told from anybody else's.</summary>
    public const string Prefix = "pbd";

    private static string Saddr(string version) => version == "4" ? "ip saddr" : "ip6 saddr";

    private static string Name(LogMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>
    /// Add these addresses to the player allow list, in batches nft accepts in one go.
    /// </summary>
    /// <remarks>Each entry lasts 30 minutes, so somebody not seen again ages out without a sweep.</remarks>
    public static IEnumerable<string> AllowScripts(IEnumerable<uint> addresses, int batch = 500)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        foreach (var chunk in addresses.Distinct().Chunk(batch))
            yield return $"add element {T} players4 {{ {string.Join(", ", chunk.Select(CapturedPacket.Address))} }}\n";
    }

    /// <summary>
    /// Replace the trusted sets with exactly these networks, in one transaction.
    /// </summary>
    /// <remarks>
    /// The owner's list is the truth: it is rewritten whole rather than edited, so the kernel can
    /// never drift from it. Networks are canonical (see <see cref="IpNetworks"/>), never raw input.
    /// </remarks>
    public static string TrustedScript(IEnumerable<string> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"flush set {T} trusted4\nflush set {T} trusted6\n");
        var canonical = networks.Select(IpNetworks.Canonical).OfType<IpNetworks.Network>().ToList();
        foreach (var (family, set) in new[] { (4, "trusted4"), (6, "trusted6") })
        {
            var items = canonical.Where(n => n.Family == family).Select(n => n.Text).Distinct(StringComparer.Ordinal).ToList();
            if (items.Count > 0) b.Append(CultureInfo.InvariantCulture, $"add element {T} {set} {{ {string.Join(", ", items)} }}\n");
        }
        return b.ToString();
    }

    /// <summary>Block one address for a while: the kernel fallback when CrowdSec is not installed.</summary>
    public static string? BlockScript(string address, int minutes)
    {
        if (IpNetworks.Canonical(address) is not { IsSingle: true } ip || minutes < 1 || IpNetworks.IsLoopback(ip)) return null;
        return $"add element {T} blocked{ip.Family} {{ {ip.Address} timeout {Number(minutes)}m }}\n";
    }

    /// <summary>
    /// Let one address off the kernel's block list. Add-then-delete, so it is not an error when it was not there.
    /// </summary>
    public static string? UnblockOneScript(string address)
    {
        if (IpNetworks.Canonical(address) is not { IsSingle: true } ip) return null;
        return $"add element {T} blocked{ip.Family} {{ {ip.Address} timeout 1s }}\ndelete element {T} blocked{ip.Family} {{ {ip.Address} }}\n";
    }

    /// <summary>Empty the block list: everybody blocked is let back in now.</summary>
    public static string UnblockScript() => $"flush set {T} blocked4\nflush set {T} blocked6\n";

    /// <summary>Remove the table entirely: every rule the bot added, gone.</summary>
    public static string RemoveScript() => $"delete table {T}\n";

    /// <summary>
    /// The counters and block list out of <c>nft -j list table inet pavlovbot_ddos</c>, or null
    /// when it is not that.
    /// </summary>
    /// <remarks>
    /// Rules are told apart by their comments, not their position, so a future rule inserted
    /// above them cannot shift which counter is read as which.
    /// </remarks>
    public static ShieldStats? ParseStats(string? json)
    {
        if (ParseTable(json) is not { } table) return null;
        long Packets(string comment) => table.Counters.TryGetValue(comment, out var c) ? c.Packets : 0;
        return new ShieldStats(
            Packets("blocked"), Packets("per-source"), Packets("unknown-cap"), table.Blocked.Count, Packets("icmp-flood"), Packets("ssh-rate"));
    }

    /// <summary>
    /// Everything the table holds: rule counters by comment, block lists, and the per-source
    /// accounting sets. Null when the JSON is not nft's.
    /// </summary>
    public static FirewallSnapshot? ParseTable(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("nftables", out var items) || items.ValueKind != JsonValueKind.Array) return null;

            var counters = new Dictionary<string, Counter>(StringComparer.Ordinal);
            var blocked = new List<BlockedAddress>();
            var flows = new Dictionary<FlowKey, Counter>();
            var syn = new Dictionary<FlowKey, Counter>();
            var other = new Dictionary<FlowKey, Counter>();
            var dropped = new Dictionary<string, Counter>(StringComparer.Ordinal);
            var trusted = new List<string>();
            var sizes = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("rule", out var rule))
                {
                    if (rule.TryGetProperty("comment", out var c) && c.GetString() is { } comment && RuleCounter(rule) is { } counter)
                        counters[comment] = counters.TryGetValue(comment, out var seen) ? seen + counter : counter;
                    continue;
                }

                if (!item.TryGetProperty("set", out var set) || !set.TryGetProperty("name", out var nameElement)) continue;
                var name = nameElement.GetString() ?? "";
                var elements = set.TryGetProperty("elem", out var e) && e.ValueKind == JsonValueKind.Array ? e : default;
                sizes[name] = elements.ValueKind == JsonValueKind.Array ? elements.GetArrayLength() : 0;
                if (elements.ValueKind != JsonValueKind.Array) continue;

                foreach (var element in elements.EnumerateArray())
                {
                    var (value, expires, counter) = Element(element);
                    switch (name)
                    {
                        case "blocked4" or "blocked6" when value.ValueKind == JsonValueKind.String:
                            blocked.Add(new BlockedAddress(value.GetString()!, expires));
                            break;
                        case "trusted4" or "trusted6":
                            if (Network(value) is { } network) trusted.Add(network);
                            break;
                        case "acct4" or "acct6" when counter is { } c1 && Concat(value) is [var src, var proto, var port]:
                            flows[new FlowKey(Text(src), Text(proto), PortOf(port))] = c1;
                            break;
                        case "syn4" or "syn6" when counter is { } c2 && Concat(value) is [var src, var port]:
                            syn[new FlowKey(Text(src), "tcp", PortOf(port))] = c2;
                            break;
                        case "other4" or "other6" when counter is { } c3 && Concat(value) is [var src, var proto]:
                            other[new FlowKey(Text(src), Text(proto), 0)] = c3;
                            break;
                        case "dropped4" or "dropped6" when counter is { } c4 && value.ValueKind == JsonValueKind.String:
                            dropped[value.GetString()!] = c4;
                            break;
                    }
                }
            }

            return new FirewallSnapshot(counters, blocked, trusted, new Accounting(flows, syn, other, dropped), sizes);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A JSON value of an unexpected kind: not the table this code wrote.
            return null;
        }
    }

    private static (JsonElement Value, int? Expires, Counter? Counter) Element(JsonElement element)
    {
        // A plain element is the value itself; one with a counter or timeout is wrapped in "elem".
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("elem", out var inner)) return (element, null, null);

        var value = inner.TryGetProperty("val", out var v) ? v : default;
        int? expires = inner.TryGetProperty("expires", out var x) && x.TryGetInt32(out var seconds) ? seconds : null;
        Counter? counter = inner.TryGetProperty("counter", out var c) && c.ValueKind == JsonValueKind.Object &&
                           c.TryGetProperty("packets", out var p) && p.TryGetInt64(out var packets) &&
                           c.TryGetProperty("bytes", out var bb) && bb.TryGetInt64(out var bytes)
            ? new Counter(packets, bytes)
            : null;
        return (value, expires, counter);
    }

    private static JsonElement[]? Concat(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("concat", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? [.. parts.EnumerateArray()]
            : null;

    private static string? Network(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("prefix", out var prefix) &&
            prefix.TryGetProperty("addr", out var addr) && prefix.TryGetProperty("len", out var len))
        {
            return $"{addr.GetString()}/{Number(len.GetInt32())}";
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.Array)
            return string.Join("-", range.EnumerateArray().Select(r => r.GetString()));
        return null;
    }

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ToString();

    private static int PortOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) ? port
        : int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static Counter? RuleCounter(JsonElement rule)
    {
        if (!rule.TryGetProperty("expr", out var expressions) || expressions.ValueKind != JsonValueKind.Array) return null;
        foreach (var expression in expressions.EnumerateArray())
        {
            if (expression.TryGetProperty("counter", out var counter) && counter.ValueKind == JsonValueKind.Object &&
                counter.TryGetProperty("packets", out var packets) && packets.TryGetInt64(out var p) &&
                counter.TryGetProperty("bytes", out var bytes) && bytes.TryGetInt64(out var b))
            {
                return new Counter(p, b);
            }
        }
        return null;
    }

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A packet and byte count from the kernel.</summary>
public readonly record struct Counter(long Packets, long Bytes)
{
    public static Counter operator +(Counter a, Counter b) => new(a.Packets + b.Packets, a.Bytes + b.Bytes);
}

/// <summary>One source's traffic to one protocol and port. Port 0 when there is none (ICMP).</summary>
public readonly record struct FlowKey(string Source, string Protocol, int Port);

/// <summary>An address on the kernel's block list and the seconds until it is let back in.</summary>
public sealed record BlockedAddress(string Address, int? ExpiresSeconds);

/// <summary>The kernel's per-source counters at one moment. They only ever grow, until an idle entry expires.</summary>
public sealed record Accounting(
    IReadOnlyDictionary<FlowKey, Counter> Flows,
    IReadOnlyDictionary<FlowKey, Counter> Syn,
    IReadOnlyDictionary<FlowKey, Counter> Other,
    IReadOnlyDictionary<string, Counter> Dropped)
{
    public static Accounting Empty { get; } = new(
        new Dictionary<FlowKey, Counter>(), new Dictionary<FlowKey, Counter>(), new Dictionary<FlowKey, Counter>(),
        new Dictionary<string, Counter>());
}

/// <summary>What <c>nft -j list table inet pavlovbot_ddos</c> says.</summary>
/// <param name="Counters">Rule counters by comment, summed where IPv4 and IPv6 rules share one.</param>
/// <param name="SetSizes">How many entries each set holds, to tell when a per-source set is full.</param>
public sealed record FirewallSnapshot(
    IReadOnlyDictionary<string, Counter> Counters,
    IReadOnlyList<BlockedAddress> Blocked,
    IReadOnlyList<string> Trusted,
    Accounting Accounting,
    IReadOnlyDictionary<string, int> SetSizes)
{
    /// <summary>Whether a per-source set is (nearly) full, so new sources go uncounted singly.</summary>
    public bool AccountingSaturated =>
        SetSizes.Any(s => s.Key.StartsWith("acct", StringComparison.Ordinal) && s.Value >= FloodShieldRules.AccountingSize * 95 / 100);
}
