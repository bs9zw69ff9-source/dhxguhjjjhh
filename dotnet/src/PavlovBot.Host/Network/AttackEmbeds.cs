using Discord;
using PavlovBot.Core.Network;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Network;

/// <summary>
/// The three attack messages: started, live, and the summary.
/// </summary>
/// <remarks>
/// NO ADDRESSES IN AN EMBED. Source IPs go in the attached report only. An attack channel is
/// still a Discord channel, and a spoofed flood's "sources" are often real people's addresses
/// that somebody else forged.
/// </remarks>
public static class AttackEmbeds
{
    public static EmbedBuilder Started(string host, string iface, LinkSecond now, FloodDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        return Alert(host, iface)
            .AddField("Now", Rate(now), true)
            .AddField("Threshold", $"{AttackReport.Count(detector.TriggerPps)} pkt/s or {detector.TriggerBytes * 8 / 1_000_000:0} Mbit/s", true)
            .AddField("Started", $"<t:{now.At.ToUnixTimeSeconds()}:T>", true)
            .Brand();
    }

    public static EmbedBuilder Live(string host, string iface, IReadOnlyList<LinkSecond> timeline, int activeSeconds, FloodDetector detector)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(detector);
        var now = timeline.Count > 0 ? timeline[^1] : default;
        var peak = timeline.Count > 0 ? timeline.MaxBy(s => s.Packets) : default;

        return Alert(host, iface)
            .AddField("Now", Rate(now), true)
            .AddField("Peak so far", Rate(peak), true)
            .AddField("Flooding for", AttackReport.Duration(TimeSpan.FromSeconds(activeSeconds)), true)
            .AddField("Timeline", $"`{AttackReport.Sparkline([.. timeline.Select(s => s.Packets)])}`", false)
            .Brand();
    }

    public static EmbedBuilder Summary(AttackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var d = report.Breakdown;
        var peak = report.Peak;

        // Plain rather than Theme.Failure: a report is not an error, and its ❌ would say it was.
        var embed = new EmbedBuilder().WithColor(Theme.BanRed).WithTitle("Attack ended · summary")
            .WithDescription($"**{report.Verdict}** · players {report.Players.Before} → {report.Players.Lowest} lowest → {report.Players.After}")
            .AddField("Window",
                $"{AttackReport.Stamp(report.Start)} → {report.End.UtcDateTime:HH:mm:ss} ({AttackReport.Duration(report.Window)})", false)
            .AddField("Traffic that reached the server",
                $"{AttackReport.Count(report.LinkPackets)} packets · {AttackReport.Bytes(report.LinkBytes)}\n" +
                $"avg {report.AverageActiveMbps:0} Mbit/s over {AttackReport.Duration(TimeSpan.FromSeconds(report.ActiveSeconds))} of active flooding", false)
            .AddField("Peak (1 second)", $"{AttackReport.Count(peak.Packets)} pkt/s · {peak.Mbps:0} Mbit/s", true);

        if (report.Capture.Ran)
        {
            embed.AddField("Pattern",
                    $"{d.Pattern}\nsizes {d.MinSize}-{d.MaxSize} B ({d.DistinctSizes} different)\n" +
                    $"from {AttackReport.Count(d.DistinctSources)} IP(s){(d.SourcesCapped ? "+" : "")}", true)
                .AddField("Protocols",
                    $"UDP {AttackReport.Share(d.Udp, d.AttackPackets)} · TCP {AttackReport.Share(d.Tcp, d.AttackPackets)}" +
                    $"{(d.Syn > 0 ? $" (SYN {AttackReport.Count(d.Syn)})" : "")} · ICMP {AttackReport.Share(d.Icmp, d.AttackPackets)}", true);

            if (d.TopPorts.Count > 0)
            {
                embed.AddField("Targets", string.Join("\n", d.TopPorts.Take(5).Select(p =>
                    $"port {p.Port}: {AttackReport.Count(p.Packets)} pkts ({AttackReport.Share(p.Packets, d.AttackPackets)})")), false);
            }

            embed.AddField("Packet sizes (payload)", "```\n" + string.Join("\n", d.Bands.Select(b =>
                $"{b.Label,10} {AttackReport.Bar(b.Packets, d.AttackPackets),-20} {AttackReport.Share(b.Packets, d.AttackPackets)}")) + "\n```", false);

            embed.AddField("Capture",
                $"tcpdump saw {AttackReport.Percent(report.Capture.Completeness)} of the packets" +
                $"{(report.Capture.DroppedByKernel > 0 ? $" ({AttackReport.Count(report.Capture.DroppedByKernel)} dropped by the kernel - the breakdown is a sample)" : "")}" +
                $"{(report.PcapPath is { } pcap ? $"\npcap for Wireshark: `{pcap}`" : "")}", false);
        }
        else
        {
            embed.AddField("Breakdown", $"none - {report.Capture.Problem}", false);
        }

        embed.AddField("Timeline (packets/s)",
                $"`{AttackReport.Sparkline([.. report.Timeline.Select(s => s.Packets)])}`\n" +
                $"{report.Start.UtcDateTime:HH:mm:ss} → {report.End.UtcDateTime:HH:mm:ss} · peak at {peak.At.UtcDateTime:HH:mm:ss}", false)
            .AddField("Real players",
                $"online: {report.Players.Before} before · {report.Players.Lowest} lowest · {report.Players.After} after" +
                (report.Capture.Ran
                    ? $"\n{AttackReport.Count(d.PlayerPackets)} packets from {report.Players.AddressesKnown} known player address(es), kept out of the attack figures"
                    : ""), false);

        return embed.Brand();
    }

    private static EmbedBuilder Alert(string host, string iface) =>
        new EmbedBuilder().WithColor(Theme.Amber).WithTitle($"🚨 Attack in progress · {host}")
            .WithDescription($"Inbound traffic on `{iface}` is over the attack threshold. A summary follows when it ends.");

    private static string Rate(LinkSecond s) => $"{AttackReport.Count(s.Packets)} pkt/s · {s.Mbps:0} Mbit/s";
}
