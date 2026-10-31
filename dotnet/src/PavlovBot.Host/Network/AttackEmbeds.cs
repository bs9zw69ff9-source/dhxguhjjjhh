using Discord;
using PavlovBot.Core.Network;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Network;

/// <summary>
/// The attack messages: started, live, and the report (summary and traffic).
/// </summary>
/// <remarks>
/// NO ADDRESSES IN AN EMBED. Source IPs go in the attached report only. An attack channel is
/// still a Discord channel, and a spoofed flood's "sources" are often real people's addresses
/// that somebody else forged.
/// </remarks>
public static class AttackEmbeds
{
    public static EmbedBuilder Started(string host, string iface, LinkSecond now, FloodDetector detector, ShieldOutcome? shield = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        return Alert(host, iface)
            .AddField("Now", Rate(now), true)
            .AddField("Threshold", $"{AttackReport.Count(detector.TriggerPps)} pkt/s or {detector.TriggerBytes * 8 / 1_000_000:0} Mbit/s", true)
            .AddField("Started", $"<t:{now.At.ToUnixTimeSeconds()}:T>", true)
            .AddField("Filter", Filter(shield), false)
            .Brand();
    }

    public static EmbedBuilder Live(
        string host, string iface, IReadOnlyList<LinkSecond> timeline, int activeSeconds, FloodDetector detector,
        IReadOnlyList<PlayerDrop>? drops = null, ShieldOutcome? shield = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(detector);
        var now = timeline.Count > 0 ? timeline[^1] : default;
        var peak = timeline.Count > 0 ? timeline.MaxBy(s => s.Packets) : default;

        return Alert(host, iface)
            .AddField("Now", Rate(now), true)
            .AddField("Peak so far", Rate(peak), true)
            .AddField("Flooding for", AttackReport.Duration(TimeSpan.FromSeconds(activeSeconds)), true)
            .AddField("Players dropped", Dropped(drops ?? [], 10), false)
            .AddField("Filter", Filter(shield), false)
            .AddField("Timeline", $"`{AttackReport.Sparkline([.. timeline.Select(s => s.Packets)])}`", false)
            .Brand();
    }

    /// <summary>The message the summary embeds and the report file come with: the report's head, as code.</summary>
    public static string Head(AttackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return "```\n" + report.Head().Replace("`", "'", StringComparison.Ordinal) + "\n```";
    }

    /// <summary>The summary, then the traffic: two embeds in one message, under Discord's 6,000-character total.</summary>
    /// <param name="postedLate">True when the bot could not post it at the time and is posting it now.</param>
    public static IReadOnlyList<EmbedBuilder> Report(AttackReport report, bool postedLate = false) =>
        [Summary(report, postedLate), Traffic(report)];

    /// <param name="postedLate">True when the bot could not post it at the time and is posting it now.</param>
    public static EmbedBuilder Summary(AttackReport report, bool postedLate = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        var d = report.Breakdown;
        var hits = report.Players.Drops.Count;
        var stats = report.Shield is { Engaged: true, Stats: { } st } ? st : null;
        var active = AttackReport.Duration(TimeSpan.FromSeconds(report.ActiveSeconds));

        // Plain rather than Theme.Failure: a report is not an error, and its ❌ would say it was.
        var embed = new EmbedBuilder().WithColor(Theme.BanRed).WithTitle("Attack ended · summary")
            .WithDescription($"**{report.Pattern}** on {report.Host} · {active}" +
                             (stats is not null ? $" · {AttackReport.Percent(report.DroppedShare)} of attack packets dropped" : "") +
                             $" · **{hits} real-player hit(s)**" +
                             (postedLate
                                 ? $"\n⚠️ **Posted late** - the bot could not reach Discord when this attack ended at <t:{report.End.ToUnixTimeSeconds()}:T>."
                                 : ""))
            .AddField("Window",
                $"{AttackReport.Stamp(report.Start)} → {report.End.UtcDateTime:HH:mm:ss} UTC ({AttackReport.Duration(report.Window)})", false)
            .AddField("Attack traffic that reached the server",
                $"{AttackReport.Count(report.AttackPackets)} packets · {AttackReport.Bytes(report.AttackBytes)}\n" +
                $"avg {report.AverageActiveMbps:0} Mbit/s over {active} of active flooding", false)
            .AddField("Peak (1 second)", $"{AttackReport.Count(report.AttackPeak.Packets)} pkt/s · {report.AttackPeak.Mbps:0} Mbit/s", true);

        embed.AddField("Pattern", report.Capture.Ran
            ? $"{(d.DistinctSizes > 50 ? "random sizes" : "sizes")} {d.MinSize}-{d.MaxSize} B\n({AttackReport.Count(d.DistinctSizes)} different)\n" +
              $"from {AttackReport.Count(d.DistinctSources)}{(d.SourcesCapped ? "+" : "")} IPs"
            : $"no breakdown - {report.Capture.Problem}", true);

        embed.AddField("Dropped by the filter", report.Shield switch
        {
            null => "nothing - `ATTACK_MITIGATION` is off, so nothing was blocked",
            { Engaged: false } s => $"nothing - the filter could not engage: {s.Problem}",
            { Stats: null } => "engaged, but its counters could not be read",
            { Stats: { } s } => $"{AttackReport.Count(s.Dropped)} packets · {AttackReport.Bytes(s.DroppedBytes)}\n{AttackReport.Percent(report.DroppedShare)} of the attack",
        }, true);

        embed.AddField("Got through to the game", report.GotThrough is { } through
            ? $"{AttackReport.Count(through)} attack packets ({AttackReport.Share(through, report.AttackPackets)})"
            : report.Shield is null ? "all of it - nothing was filtered" : "unknown - no capture to compare against", false);

        embed.AddField($"Server link ({report.Interface})",
            $"peak {AttackReport.Count(report.Peak.Packets)} pkt/s · {report.Peak.Mbps:0} Mbit/s", false);

        if (report.Connection is { } lost)
        {
            embed.AddField("⚠️ Connection lost",
                $"The bot could not reach Discord for **{AttackReport.Duration(TimeSpan.FromSeconds(lost.Seconds))}** " +
                $"(<t:{lost.From.ToUnixTimeSeconds()}:T> → <t:{lost.Until.ToUnixTimeSeconds()}:T>). The flood was likely filling the " +
                "server's connection itself - no firewall on the server can stop that; it needs the host's DDoS protection.", false);
        }

        embed.AddField("Real players",
            $"online: {report.Players.Before} before · {report.Players.Lowest} lowest · {report.Players.After} after" +
            (report.Capture.Ran
                ? $"\n{AttackReport.Count(d.PlayerPackets)} packets from {report.Players.AddressesKnown} players during the window"
                : "") +
            $"\n**{hits} player hit(s)**{(hits > 0 ? $" ({report.Players.Rejoined} rejoined), listed below" : "")}" +
            (report.Players.StaleSeconds > 0
                ? $"\nRCON could not be read for {report.Players.StaleSeconds}s, so a disconnect then may be missing"
                : ""), false);
        if (hits > 0) embed.AddField($"Players hit: {hits} ({report.Players.Rejoined} rejoined)", Dropped(report.Players.Drops, 12), false);

        embed.AddField("Recorded / banned", Banned(report), false);
        return embed.Brand();
    }

    /// <summary>The traffic: what the filter dropped by rule, the targets, the sizes and the timeline.</summary>
    public static EmbedBuilder Traffic(AttackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var d = report.Breakdown;
        var embed = new EmbedBuilder().WithColor(Theme.Amber).WithTitle("Traffic");

        if (report.Shield is { Engaged: true, Stats: { } st } shield)
        {
            (string Rule, long Packets)[] rules =
            [
                ($"banned ({shield.Limits.BlockMinutes}m)", st.BlockedPackets),
                ("per-address limit", st.PerSourcePackets),
                ("unknown-traffic cap", st.CappedPackets),
                ("SYN flood", st.SynFloodPackets),
                ("ping limit", st.IcmpPackets),
                ("SSH limit", st.SshPackets),
            ];
            var top = rules.Max(r => r.Packets);
            var lines = rules.Where(r => r.Packets > 0).OrderByDescending(r => r.Packets).Select(r =>
                $"{r.Rule}: {(r.Packets == top ? $"**{AttackReport.Count(r.Packets)}**" : AttackReport.Count(r.Packets))} pkts " +
                $"({AttackReport.Share(r.Packets, st.Dropped)})");
            embed.AddField("Dropped by rule", st.Dropped == 0 ? "nothing" : string.Join("\n", lines) +
                $"\n{AttackReport.Count(st.BlockedSources)} address(es) on the block list · known players never limited", false);
        }

        if (report.Capture.Ran)
        {
            if (d.TopPorts.Count > 0)
            {
                var shown = d.TopPorts.Take(5).ToList();
                var rest = d.AttackPackets - shown.Sum(p => p.Packets) - d.Icmp;
                embed.AddField("Targets", string.Join("\n", shown.Select(p =>
                    $"{Sanitize.Code(report.PortName(p.Port))}: {AttackReport.Count(p.Packets)} pkts" +
                    (d.PortBytes is not null && d.PortBytes.TryGetValue(p.Port, out var bytes) ? $" · {AttackReport.Bytes(bytes)}" : "") +
                    $" ({AttackReport.Share(p.Packets, d.AttackPackets)})")) +
                    (rest > 0 ? $"\nother ports: {AttackReport.Count(rest)} pkts" : ""), false);
            }

            embed.AddField("Packet sizes (payload)", "```\n" + string.Join("\n", d.Bands.Select(b =>
                $"{b.Label,10} {AttackReport.Bar(b.Packets, d.AttackPackets),-20} {AttackReport.Share(b.Packets, d.AttackPackets)}")) + "\n```", false);

            embed.AddField("Protocols",
                $"UDP {AttackReport.Share(d.Udp, d.AttackPackets)} · TCP {AttackReport.Share(d.Tcp, d.AttackPackets)}" +
                $"{(d.Syn > 0 ? $" (SYN {AttackReport.Count(d.Syn)})" : "")} · ICMP {AttackReport.Share(d.Icmp, d.AttackPackets)}", false);
        }

        var seconds = report.AttackSeconds;
        var peak = report.AttackPeak;
        embed.AddField(report.Capture.Ran ? "Timeline (attack packets/s)" : "Timeline (link packets/s)",
            $"```\n{AttackReport.Sparkline([.. seconds.Select(s => s.Packets)])}\n```" +
            $"{report.Start.UtcDateTime:HH:mm:ss} → {report.End.UtcDateTime:HH:mm:ss} · peak {AttackReport.Count(peak.Packets)}/s at {peak.At.UtcDateTime:HH:mm:ss}", false);

        embed.AddField("Capture", report.Capture.Ran
            ? $"tshark saw {AttackReport.Percent(report.Capture.Completeness)} of the packets" +
              $"{(report.Capture.DroppedByKernel > 0 ? $" ({AttackReport.Count(report.Capture.DroppedByKernel)} dropped by the kernel - the breakdown is a sample)" : "")}" +
              $"{(report.PcapPath is { } pcap ? $"\npcap kept at `{pcap}` - `/packettools analyze` reads it again" : "")}" +
              "\nEvery source address is in the attached report"
            : $"none - {report.Capture.Problem}", false);

        return embed.Brand();
    }

    /// <summary>What happened to the attack's addresses.</summary>
    private static string Banned(AttackReport report)
    {
        var recorded = report.Capture.Ran ? report.Breakdown.DistinctSources : 0;
        if (report.Bans is not { } bans)
        {
            return report.Capture.Ran
                ? $"{AttackReport.Count(recorded)} IPs recorded · none banned (no source sent the minimum to the game ports)"
                : "nothing recorded without a capture";
        }

        var how = bans.Via switch { "crowdsec" => "banned permanently", "nftables" => "blocked", _ => "banned" };
        var line = $"{AttackReport.Count(recorded)} IPs recorded · **{AttackReport.Count(bans.Banned)} {how}**";
        if (bans.Already > 0) line += $" · {AttackReport.Count(bans.Already)} already banned";
        if (bans.Spared > 0) line += $" · {AttackReport.Count(bans.Spared)} spared (players, trusted, private)";
        if (bans.Problem is { } problem) line += $"\n{(problem.Length > 300 ? problem[..300] + "…" : problem)}";
        return line + "\n-# `/antiddos unban ip:<address>` lifts one";
    }

    /// <summary>The dropped players, newest last, capped so the field stays inside Discord's 1,024 characters.</summary>
    private static string Dropped(IReadOnlyList<PlayerDrop> drops, int shown)
    {
        if (drops.Count == 0) return "nobody so far";

        var lines = drops.Take(shown).Select(d =>
            $"`{d.At.UtcDateTime:HH:mm:ss}` {Sanitize.Code(Shorten(d.Name, 32))}{(d.Back is { } back ? $" · back {back.UtcDateTime:HH:mm:ss}" : " · not back")}");
        var text = string.Join("\n", lines);
        return drops.Count > shown ? $"{text}\n…and {drops.Count - shown} more (all in the attached report)" : text;
    }

    private static string Shorten(string name, int max) => name.Length <= max ? name : name[..(max - 1)] + "…";

    private static string Filter(ShieldOutcome? shield) => shield switch
    {
        null => "off - `ATTACK_MITIGATION` is not on, so nothing is being blocked",
        { Engaged: false } s => $"could not engage: {s.Problem}",
        { Limits: var l } =>
            $"**engaged** · over {AttackReport.Count(l.PerSourcePps)} pkt/s from one address is blocked for {l.BlockMinutes} min · " +
            $"unknown traffic to the game ports capped at {AttackReport.Count(l.UnknownPps)} pkt/s · known players always pass",
    };

    private static EmbedBuilder Alert(string host, string iface) =>
        new EmbedBuilder().WithColor(Theme.Amber).WithTitle($"🚨 Attack in progress · {host}")
            .WithDescription($"Inbound traffic on `{iface}` is over the attack threshold. A summary follows when it ends.");

    private static string Rate(LinkSecond s) => $"{AttackReport.Count(s.Packets)} pkt/s · {s.Mbps:0} Mbit/s";
}
