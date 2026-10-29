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
/// <param name="BlockedSources">Addresses on the block list now.</param>
public sealed record ShieldStats(long BlockedPackets, long PerSourcePackets, long CappedPackets, int BlockedSources)
{
    public long Dropped => BlockedPackets + PerSourcePackets + CappedPackets;
}

/// <summary>
/// The nftables rules that block and rate-limit a flood, and the reading of their counters.
/// </summary>
/// <remarks>
/// PURE TEXT, so every rule the bot will load is pinned by a test before it reaches a kernel.
///
/// ITS OWN TABLE, <c>inet pavlovbot_ddos</c>, and nothing else is touched: ufw's rules, Docker's
/// and anybody else's stay exactly as they are, and deleting this one table undoes all of it.
/// The chain hooks input at priority -10, ahead of ufw's filter, so a dropped flood costs the
/// least it can.
///
/// IN ORDER, AND THE ORDER IS THE SAFETY:
///   1. SSH is always let through, so a block can never lock the owner out of the box.
///   2. Known players are always let through - every confirmed address of somebody seen in the
///      last month, refreshed during the attack - so the people the servers exist for are never
///      on the wrong side of a limit.
///   3. Addresses on the block list are dropped.
///   ONLY WHILE AN ATTACK IS ON:
///   4. One address sending more than <see cref="ShieldLimits.PerSourcePps"/> to the game ports
///      goes on the block list for <see cref="ShieldLimits.BlockMinutes"/>. No real client sends
///      that much; a flooding host does.
///   5. Everything else to the game ports is capped at <see cref="ShieldLimits.UnknownPps"/> in
///      total. That is the answer to a spoofed flood from thousands of addresses at a few
///      packets each, which no per-address limit can see. The cost is that somebody new joining
///      mid-attack shares that cap with the flood; once it ends the cap is lifted.
///
/// THE BLOCK LIST OUTLIVES THE ATTACK by its timeout, so a flood that pauses and comes back from
/// the same hosts is still dropped. Lifting the limits never unblocks anybody early;
/// <c>/packettools unblock</c> does.
/// </remarks>
public static partial class FloodShieldRules
{
    public const string Table = "pavlovbot_ddos";

    private const string Family = "inet";

    /// <summary>
    /// A ports value: comma-separated ports and ranges, nothing else.
    /// </summary>
    /// <remarks>It is pasted into an nft script, so it is allow-listed rather than escaped.</remarks>
    [GeneratedRegex(@"^\s*\d{1,5}(\s*-\s*\d{1,5})?(\s*,\s*\d{1,5}(\s*-\s*\d{1,5})?)*\s*$")]
    private static partial Regex PortsPattern { get; }

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

    /// <summary>
    /// The whole table, with the attack-time limits on or off.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT. <c>add</c> creates what is missing and leaves what exists, and the chain and the
    /// port set are flushed and rewritten - but the player and block lists are not, so switching
    /// the limits on or off never forgets who is blocked or who is a player.
    /// </remarks>
    public static string Script(ShieldLimits limits, bool engaged)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var ports = Ports(limits.GamePorts) ?? throw new ArgumentException($"\"{limits.GamePorts}\" is not a ports list", nameof(limits));
        var t = $"{Family} {Table}";

        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"add table {t}");
        b.AppendLine(CultureInfo.InvariantCulture, $"add set {t} gameports {{ type inet_service; flags interval; }}");
        b.AppendLine(CultureInfo.InvariantCulture, $"add set {t} players4 {{ type ipv4_addr; flags timeout; timeout 30m; }}");
        b.AppendLine(CultureInfo.InvariantCulture, $"add set {t} blocked4 {{ type ipv4_addr; flags dynamic,timeout; timeout {Number(limits.BlockMinutes)}m; }}");
        b.AppendLine(CultureInfo.InvariantCulture, $"add set {t} rate4 {{ type ipv4_addr; flags dynamic,timeout; timeout 1m; size 65535; }}");
        b.AppendLine(CultureInfo.InvariantCulture, $"add chain {t} input {{ type filter hook input priority -10; policy accept; }}");
        b.AppendLine(CultureInfo.InvariantCulture, $"flush chain {t} input");
        b.AppendLine(CultureInfo.InvariantCulture, $"flush set {t} gameports");
        b.AppendLine(CultureInfo.InvariantCulture, $"add element {t} gameports {{ {ports} }}");
        b.AppendLine(CultureInfo.InvariantCulture, $"add rule {t} input tcp dport 22 accept comment \"ssh\"");
        b.AppendLine(CultureInfo.InvariantCulture, $"add rule {t} input ip saddr @players4 accept comment \"players\"");
        b.AppendLine(CultureInfo.InvariantCulture, $"add rule {t} input ip saddr @blocked4 counter drop comment \"blocked\"");

        if (engaged)
        {
            var perSource = Number(limits.PerSourcePps);
            var unknown = Number(limits.UnknownPps);
            b.AppendLine(CultureInfo.InvariantCulture,
                $"add rule {t} input udp dport @gameports update @rate4 {{ ip saddr limit rate over {perSource}/second burst {Number(limits.PerSourcePps * 2)} packets }} add @blocked4 {{ ip saddr }} counter drop comment \"per-source\"");
            b.AppendLine(CultureInfo.InvariantCulture,
                $"add rule {t} input udp dport @gameports limit rate over {unknown}/second burst {Number(limits.UnknownPps * 2)} packets counter drop comment \"unknown-cap\"");
        }

        return b.ToString();
    }

    /// <summary>
    /// Add these addresses to the player allow list, in batches nft accepts in one go.
    /// </summary>
    /// <remarks>Each entry lasts 30 minutes, so somebody not seen again ages out without a sweep.</remarks>
    public static IEnumerable<string> AllowScripts(IEnumerable<uint> addresses, int batch = 500)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        foreach (var chunk in addresses.Distinct().Chunk(batch))
            yield return $"add element {Family} {Table} players4 {{ {string.Join(", ", chunk.Select(CapturedPacket.Address))} }}\n";
    }

    /// <summary>Empty the block list: everybody blocked is let back in now.</summary>
    public static string UnblockScript() => $"flush set {Family} {Table} blocked4\n";

    /// <summary>Remove the table entirely: every rule the bot added, gone.</summary>
    public static string RemoveScript() => $"delete table {Family} {Table}\n";

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
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("nftables", out var items) || items.ValueKind != JsonValueKind.Array) return null;

            long blocked = 0, perSource = 0, capped = 0;
            var sources = 0;

            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("rule", out var rule))
                {
                    var comment = rule.TryGetProperty("comment", out var c) ? c.GetString() : null;
                    var packets = CounterPackets(rule);
                    switch (comment)
                    {
                        case "blocked": blocked += packets; break;
                        case "per-source": perSource += packets; break;
                        case "unknown-cap": capped += packets; break;
                    }
                }
                else if (item.TryGetProperty("set", out var set) &&
                         set.TryGetProperty("name", out var name) && name.GetString() == "blocked4" &&
                         set.TryGetProperty("elem", out var elements) && elements.ValueKind == JsonValueKind.Array)
                {
                    sources = elements.GetArrayLength();
                }
            }

            return new ShieldStats(blocked, perSource, capped, sources);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long CounterPackets(JsonElement rule)
    {
        if (!rule.TryGetProperty("expr", out var expressions) || expressions.ValueKind != JsonValueKind.Array) return 0;
        foreach (var expression in expressions.EnumerateArray())
        {
            if (expression.TryGetProperty("counter", out var counter) &&
                counter.TryGetProperty("packets", out var packets) && packets.TryGetInt64(out var n))
            {
                return n;
            }
        }
        return 0;
    }

    private static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
}
