using System.Globalization;
using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Monitoring;
using PavlovBot.Core.Text;
using PavlovBot.Host.Monitoring;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/monitor</c> - the monitoring dashboard: one server in detail, every server at a glance, or the health history.
/// </summary>
/// <remarks>
/// NAMED <c>/monitor</c>, NOT <c>/server</c>: <see cref="ServerLookupCommand"/> already owns
/// <c>/server</c> (the public server browser). Two <see cref="ISlashCommand"/> with one name wedge
/// command registration - it is the monitoring picture, so it is named for that.
/// </remarks>
public sealed class ServerStatusCommand(ServerMonitor monitor, ServiceControl service, Access access) : ISlashCommand
{
    public string Name => "monitor";
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build()
    {
        static SlashCommandOptionBuilder ServerOption() =>
            new SlashCommandOptionBuilder()
                .WithName("server").WithDescription("Which server (default: all)")
                .WithType(ApplicationCommandOptionType.String).WithRequired(false).WithAutocomplete(true);

        return new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Mod - Live server monitoring")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("status").WithDescription("Current status, one server or all")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(ServerOption()))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("health").WithDescription("Health statistics over the last 24 hours")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption(ServerOption()))
            .Build();
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Mod, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Mod, command))).ConfigureAwait(false);
            return;
        }

        var sub = command.Data.Options.FirstOrDefault();
        var wanted = Sanitize.Id(sub?.Options.FirstOrDefault(o => o.Name == "server")?.Value as string ?? "");

        var servers = monitor.Servers.ToList();
        if (servers.Count == 0)
        {
            await Reply(command, Theme.Notice("No servers configured", "There is nothing to monitor.")).ConfigureAwait(false);
            return;
        }

        var one = wanted.Length > 0
            ? servers.FirstOrDefault(s => string.Equals(s, wanted, StringComparison.OrdinalIgnoreCase))
            : null;

        if (wanted.Length > 0 && one is null)
        {
            await Reply(command, Theme.Failure("Unknown server",
                $"`{Sanitize.Code(wanted)}` is not one of: {string.Join(", ", servers.Select(Sanitize.Code))}.")).ConfigureAwait(false);
            return;
        }

        EmbedBuilder embed;
        if (sub?.Name == "health")
        {
            embed = one is not null ? HealthCard(one) : HealthOverview(servers);
        }
        else
        {
            // CPU/RAM comes from systemd and costs a ~600ms sampling round trip, so it is read
            // once here, only for the status view, and never on the 30s monitor tick.
            var usage = await UsageAsync(ct).ConfigureAwait(false);
            embed = one is not null ? StatusCard(one, usage) : StatusOverview(servers, usage);
        }

        await Reply(command, embed).ConfigureAwait(false);
    }

    /// <summary>Each server's systemd-unit CPU/RAM, mapped through the same server->unit link the lifecycle uses.</summary>
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

    // ---- status ----

    private EmbedBuilder StatusCard(string server, IReadOnlyDictionary<string, UnitStats> usage)
    {
        var h = monitor.Snapshot(server);
        var embed = new EmbedBuilder()
            .WithColor(StateColour(h.State))
            .WithTitle($"{StateDot(h.State)} {Sanitize.Code(server)}")
            .AddField("Status", h.State.ToString().ToUpperInvariant(), inline: true)
            .AddField("Players", h.Players is { } p ? $"{p}{(h.MaxPlayers is { } m ? $"/{m}" : "")}" : "—", inline: true)
            .AddField("Map", h.Map is { Length: > 0 } map ? Sanitize.Code(map) : "—", inline: true)
            .AddField("RCON", Answering(h.State) ? "Connected" : "Disconnected", inline: true)
            .AddField("Latency", h.Latency.Current is { } l ? $"{Ms(l)} (avg {Ms(h.Latency.Average ?? l)})" : "—", inline: true)
            .AddField("Last check", h.LastProbeAt is { } at ? $"<t:{at.ToUnixTimeSeconds()}:T>" : "never", inline: true);

        if (usage.TryGetValue(server, out var u))
        {
            embed.AddField("CPU", Cpu(u), inline: true);
            embed.AddField("RAM", Ram(u), inline: true);
            embed.AddField("Server uptime", u.Uptime is { } up ? ServerHealth.Humanize(up) : "—", inline: true);
        }

        return embed.AddField("Health",
            $"RCON {Tick(Answering(h.State))}  ·  Logs {Tick(!h.LogInactiveReported)}  ·  " +
            $"Players {Tick(h.Players is not null)}  ·  Process {Unknownable(h.ProcessRunning)}");
    }

    private EmbedBuilder StatusOverview(IReadOnlyList<string> servers, IReadOnlyDictionary<string, UnitStats> usage)
    {
        var lines = servers.Select(s =>
        {
            var h = monitor.Snapshot(s);
            var players = h.Players is { } p ? $"{p}{(h.MaxPlayers is { } m ? $"/{m}" : "")}" : "—";
            var latency = h.Latency.Current is { } l ? Ms(l) : "—";
            var box = usage.TryGetValue(s, out var u)
                ? $" · {Cpu(u)} cpu · {Ram(u)}"
                : "";
            return $"{StateDot(h.State)} **{Sanitize.Code(s)}** — {h.State.ToString().ToUpperInvariant()} · {players} · {latency}{box}";
        });

        return Theme.Notice("Server status", string.Join("\n", lines))
            .WithFooter($"{servers.Count} server(s) · 100% cpu = one core · updated live");
    }

    private static string Cpu(UnitStats u) => u.CpuPercent is { } c ? $"{c.ToString("0.0", CultureInfo.InvariantCulture)}%" : "—";

    private static string Ram(UnitStats u)
    {
        if (u.MemoryBytes is not { } bytes) return "—";
        var used = $"{(bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture)} MB";
        // A limit is shown only when systemd actually caps the unit; unset reads as "infinity"
        // and comes back null, so an uncapped server shows just its usage, not "/ ∞".
        return u.MemoryLimitBytes is { } limit && limit > 0
            ? $"{used} / {(limit / 1048576.0).ToString("0", CultureInfo.InvariantCulture)} MB"
            : used;
    }

    // ---- health history ----

    private EmbedBuilder HealthCard(string server)
    {
        var s = monitor.Stats(server, TimeSpan.FromHours(24));
        if (s.Samples == 0)
            return Theme.Notice($"{Sanitize.Code(server)} — last 24h", "No monitoring data yet.");

        return Theme.Notice($"{Sanitize.Code(server)} — last 24h")
            .AddField("Uptime", $"{s.UptimePercent.ToString("N2", CultureInfo.InvariantCulture)}%", inline: true)
            .AddField("RCON failures", s.RconFailures.ToString(CultureInfo.InvariantCulture), inline: true)
            .AddField("Unexpected restarts", s.UnexpectedRestarts.ToString(CultureInfo.InvariantCulture), inline: true)
            .AddField("Average latency", s.AverageLatencyMs is { } a ? $"{a.ToString("N0", CultureInfo.InvariantCulture)}ms" : "—", inline: true)
            .AddField("Longest outage", ServerHealth.Humanize(s.LongestOutage), inline: true)
            .AddField("Samples", s.Samples.ToString(CultureInfo.InvariantCulture), inline: true);
    }

    private EmbedBuilder HealthOverview(IReadOnlyList<string> servers)
    {
        var lines = servers.Select(server =>
        {
            var s = monitor.Stats(server, TimeSpan.FromHours(24));
            if (s.Samples == 0) return $"**{Sanitize.Code(server)}** — no data yet";
            return $"**{Sanitize.Code(server)}** — {s.UptimePercent.ToString("N2", CultureInfo.InvariantCulture)}% up · " +
                   $"{s.RconFailures} RCON fails · {s.UnexpectedRestarts} restarts";
        });

        return Theme.Notice("Health — last 24h", string.Join("\n", lines));
    }

    // ---- helpers ----

    private static bool Answering(HealthState state) => state is HealthState.Online or HealthState.Degraded;

    private static string StateDot(HealthState state) => state switch
    {
        HealthState.Online => "🟢",
        HealthState.Degraded => "🟡",
        HealthState.Reconnecting => "🟠",
        HealthState.Offline => "🔴",
        _ => "⚪",
    };

    private static Color StateColour(HealthState state) => state switch
    {
        HealthState.Online => Theme.Green,
        HealthState.Degraded => Theme.Amber,
        HealthState.Reconnecting => Theme.Amber,
        HealthState.Offline => Theme.BanRed,
        _ => Theme.Grey,
    };

    private static string Tick(bool ok) => ok ? "🟢" : "🔴";
    private static string Unknownable(bool? state) => state switch { true => "🟢", false => "🔴", null => "⚪" };
    private static string Ms(TimeSpan t) => $"{t.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture)}ms";

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
