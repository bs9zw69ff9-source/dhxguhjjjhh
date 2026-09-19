using System.Text;
using Discord;
using PavlovBot.Core.Monitoring;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Monitoring;

/// <summary>
/// Builds the ONE live monitoring board: a rich embed, edited in place on the timer, that shows
/// every server's current state and stats and the recent run of events - offline, recovery, high
/// latency, player drops - instead of a stream of separate alert messages.
/// </summary>
/// <remarks>
/// NO GATEWAY DEPENDENCY, ON PURPOSE. The board is posted through <see cref="AutoPost"/>, which
/// owns the channel and the edit-in-place logic; this class only computes the embed. Taking a
/// <c>DiscordGateway</c> here would reopen the DiscordGateway -> command -> monitor -> ... cycle
/// that the alert sink documents and that already hung <c>--selftest</c> once. It reads the
/// monitor's snapshots and, best-effort, systemd's CPU/RAM, and returns a value.
///
/// CPU/RAM IS SAMPLED ONCE PER BUILD, not per server: <see cref="ServiceControl.StatsAsync"/>
/// costs a ~600ms sampling window, so it is read a single time for the whole board and any server
/// it cannot be matched to simply shows no CPU/RAM rather than failing the board.
/// </remarks>
public sealed class MonitorBoard(ServerMonitor monitor, ServiceControl service)
{
    /// <summary>How many recent events the board prints. The monitor keeps a few more than this.</summary>
    private const int EventsShown = 10;

    /// <summary>Discord caps a field value at 1024 chars; stop well short and truncate the tail.</summary>
    private const int EventsFieldBudget = 900;

    /// <summary>Build the board embed. Never null: the board always has something to say, even "no servers".</summary>
    public async Task<Embed?> BuildAsync(CancellationToken ct = default)
    {
        var servers = monitor.Servers.ToList();
        var usage = await UsageAsync(ct).ConfigureAwait(false);

        // The board's colour and header dot follow the WORST server: one red server must not be
        // hidden behind a green header.
        var worst = servers.Count == 0
            ? HealthState.Unknown
            : servers.Select(s => monitor.Snapshot(s).State).Aggregate(Worse);

        var embed = new EmbedBuilder()
            .WithColor(MonitorFormat.StateColour(worst))
            .WithTitle($"{MonitorFormat.StateDot(worst)} Server monitor")
            .WithFooter($"{servers.Count} server(s) · 100% cpu = one core · updates live · {DateTimeOffset.UtcNow:HH:mm:ss} UTC")
            .WithCurrentTimestamp();

        if (servers.Count == 0)
        {
            embed.WithDescription("No servers are configured to monitor.");
            return embed.Build();
        }

        foreach (var server in servers)
            embed.AddField($"{MonitorFormat.StateDot(monitor.Snapshot(server).State)} {Sanitize.Code(server)}",
                ServerLines(server, usage), inline: true);

        embed.AddField("Recent events", RecentEvents());
        return embed.Build();
    }

    /// <summary>One server's stat block: state, players, map, latency, CPU/RAM, uptime.</summary>
    private string ServerLines(string server, IReadOnlyDictionary<string, UnitStats> usage)
    {
        var h = monitor.Snapshot(server);
        var sb = new StringBuilder();

        sb.Append("**").Append(h.State.ToString().ToUpperInvariant()).Append("**\n");
        sb.Append("👥 ").Append(h.Players is { } p ? $"{p}{(h.MaxPlayers is { } m ? $"/{m}" : "")}" : "—").Append('\n');
        sb.Append("🗺️ ").Append(h.Map is { Length: > 0 } map ? Sanitize.Code(map) : "—").Append('\n');
        sb.Append("📶 ").Append(h.Latency.Current is { } l
            ? $"{MonitorFormat.Ms(l)} (avg {MonitorFormat.Ms(h.Latency.Average ?? l)})"
            : "—").Append('\n');

        if (usage.TryGetValue(server, out var u))
        {
            sb.Append("🔥 ").Append(MonitorFormat.Cpu(u)).Append(" · 💾 ").Append(MonitorFormat.Ram(u)).Append('\n');
            sb.Append("⏱️ ").Append(u.Uptime is { } up ? ServerHealth.Humanize(up) : "—").Append('\n');
        }

        sb.Append("🔌 ").Append(MonitorFormat.Answering(h.State) ? "RCON up" : "RCON down");
        return sb.ToString();
    }

    /// <summary>The recent-events log, newest first, severity-dotted and length-capped.</summary>
    private string RecentEvents()
    {
        var events = monitor.RecentEvents();
        if (events.Count == 0) return "_No events yet. Transitions (offline, recovery, latency, player drops) show here._";

        var sb = new StringBuilder();
        // Newest first: the most recent thing to happen is the thing being looked for.
        foreach (var e in events.Reverse().Take(EventsShown))
        {
            var line =
                $"{MonitorFormat.SeverityDot(e.Severity)} <t:{e.At.ToUnixTimeSeconds()}:R> **{Sanitize.Code(e.Server)}** — {Sanitize.Message(Trim(e.Detail))}\n";

            // Budget guard: Discord rejects a field over 1024 chars, so stop adding lines rather
            // than build an embed the API refuses and lose the whole board to it.
            if (sb.Length + line.Length > EventsFieldBudget) break;
            sb.Append(line);
        }

        return sb.Length == 0 ? "_(events pending)_" : sb.ToString();
    }

    /// <summary>Keep one event line short so ten of them fit a single field.</summary>
    private static string Trim(string detail)
    {
        const int max = 110;
        detail = detail.ReplaceLineEndings(" ");
        return detail.Length <= max ? detail : detail[..(max - 1)] + "…";
    }

    /// <summary>Each server's systemd-unit stats, mapped through the same server->unit link the lifecycle uses.</summary>
    private async Task<IReadOnlyDictionary<string, UnitStats>> UsageAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, UnitStats>(StringComparer.Ordinal);
        try
        {
            var byUnit = (await service.StatsAsync(ct).ConfigureAwait(false))
                .ToDictionary(s => s.Unit, StringComparer.Ordinal);

            foreach (var server in monitor.Servers)
            {
                if (ServiceControl.NumberFor(server) is { } number
                    && service.UnitFor(number) is { } unit
                    && byUnit.TryGetValue(unit, out var stats))
                {
                    map[server] = stats;
                }
            }
        }
        catch (Exception)
        {
            // No systemd, or it could not be read: the board simply shows no CPU/RAM.
        }
        return map;
    }

    /// <summary>Rank two states so the board's header can show the worst one. Offline beats all.</summary>
    private static HealthState Worse(HealthState a, HealthState b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(HealthState state) => state switch
    {
        HealthState.Offline => 4,
        HealthState.Reconnecting => 3,
        HealthState.Degraded => 2,
        HealthState.Online => 1,
        _ => 0,   // Unknown
    };
}
