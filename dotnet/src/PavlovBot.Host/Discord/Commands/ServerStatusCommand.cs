using System.Globalization;
using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Monitoring;
using PavlovBot.Core.Text;
using PavlovBot.Host.Monitoring;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/server</c> - the monitoring dashboard: one server in detail, every server at a glance, or the health history.
/// </summary>
public sealed class ServerStatusCommand(ServerMonitor monitor, Access access) : ISlashCommand
{
    public string Name => "server";
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

        var embed = sub?.Name == "health"
            ? one is not null ? HealthCard(one) : HealthOverview(servers)
            : one is not null ? StatusCard(one) : StatusOverview(servers);

        await Reply(command, embed).ConfigureAwait(false);
    }

    // ---- status ----

    private EmbedBuilder StatusCard(string server)
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
            .AddField("Last check", h.LastProbeAt is { } at ? $"<t:{at.ToUnixTimeSeconds()}:T>" : "never", inline: true)
            .AddField("Health",
                $"RCON {Tick(Answering(h.State))}  ·  Logs {Tick(!h.LogInactiveReported)}  ·  " +
                $"Players {Tick(h.Players is not null)}  ·  Process {Unknownable(h.ProcessRunning)}");

        return embed;
    }

    private EmbedBuilder StatusOverview(IReadOnlyList<string> servers)
    {
        var lines = servers.Select(s =>
        {
            var h = monitor.Snapshot(s);
            var players = h.Players is { } p ? $"{p}{(h.MaxPlayers is { } m ? $"/{m}" : "")}" : "—";
            var latency = h.Latency.Current is { } l ? Ms(l) : "—";
            return $"{StateDot(h.State)} **{Sanitize.Code(s)}** — {h.State.ToString().ToUpperInvariant()} · {players} · {latency}";
        });

        return Theme.Notice("Server status", string.Join("\n", lines))
            .WithFooter($"{servers.Count} server(s) · updated live");
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
