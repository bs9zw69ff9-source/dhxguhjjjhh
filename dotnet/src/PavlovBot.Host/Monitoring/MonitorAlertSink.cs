using Discord;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Monitoring;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Monitoring;

/// <summary>Where a monitoring alert goes. An interface so the monitor is tested without Discord.</summary>
public interface IMonitorAlertSink
{
    Task PostAsync(string server, MonitorSignal signal, ServerHealth health, CancellationToken ct = default);
}

/// <summary>Turns a signal into the embed staff see. Pure, so its shape is pinned by a test.</summary>
public static class MonitorEmbeds
{
    public static Embed For(string server, MonitorSignal signal, ServerHealth health)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(health);

        var (icon, title) = Heading(signal.Kind);
        var colour = signal.Severity switch
        {
            Severity.Critical => Theme.BanRed,
            Severity.Warning => Theme.Amber,
            _ => Theme.Green,
        };

        var embed = new EmbedBuilder()
            .WithColor(colour)
            .WithTitle($"{icon} {title}")
            .WithDescription(Sanitize.Message(signal.Detail))
            .AddField("Server", Sanitize.Code(server), inline: true)
            .AddField("Status", health.State.ToString().ToUpperInvariant(), inline: true);

        if (health.Latency.Current is { } latency)
            embed.AddField("RCON latency", $"{latency.TotalMilliseconds:N0}ms", inline: true);

        if (health.Players is { } players)
            embed.AddField("Players", $"{players}{(health.MaxPlayers is { } m ? $"/{m}" : "")}", inline: true);

        return embed.WithCurrentTimestamp().Build();
    }

    private static (string Icon, string Title) Heading(SignalKind kind) => kind switch
    {
        SignalKind.ServerOffline => ("🚨", "SERVER OFFLINE"),
        SignalKind.ServerOnline => ("✅", "SERVER ONLINE"),
        SignalKind.StillOffline => ("⏳", "STILL OFFLINE"),
        SignalKind.RconDisconnected => ("🔌", "RCON DISCONNECTED"),
        SignalKind.RconReconnected => ("✅", "RCON RECONNECTED"),
        SignalKind.RepeatedRconFailures => ("🚨", "RCON FAILING"),
        SignalKind.HighLatency => ("🐌", "HIGH RCON LATENCY"),
        SignalKind.LatencyRecovered => ("✅", "LATENCY RECOVERED"),
        SignalKind.LogInactive => ("🔇", "LOG WENT QUIET"),
        SignalKind.UnexpectedRestart => ("♻️", "UNEXPECTED RESTART"),
        SignalKind.PlayerCountZero => ("📉", "PLAYERS DROPPED TO ZERO"),
        SignalKind.PlayerCapacityReached => ("📈", "SERVER FULL"),
        SignalKind.PlayerAnomaly => ("📉", "PLAYER COUNT ANOMALY"),
        _ => ("ℹ️", kind.ToString()),
    };
}

/// <summary>
/// Posts monitoring alerts to the configured staff channel, pinging the alert role on the loud ones.
/// </summary>
/// <remarks>
/// NEVER THROWS. A monitoring alert that could not be delivered must not take down the monitoring
/// tick that produced it, so a Discord failure is logged and swallowed - the state has already been
/// recorded, and the next transition will try again. A missing channel disables alerting quietly;
/// that is a configuration choice, not a fault.
/// </remarks>
public sealed class DiscordMonitorAlertSink(
    DiscordGateway gateway, ulong? channelId, ulong? roleId, ILogger<DiscordMonitorAlertSink> logger) : IMonitorAlertSink
{
    public async Task PostAsync(string server, MonitorSignal signal, ServerHealth health, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (channelId is not { } channel) return;

        try
        {
            if (await gateway.GetChannelAsync(channel).ConfigureAwait(false) is not IMessageChannel target)
            {
                logger.LogWarning("Monitor alert channel {Channel} is not a message channel - alert dropped", channel);
                return;
            }

            // Ping the role only on the alerts worth pinging for, and only that role.
            var ping = roleId is { } role && signal.Severity >= Severity.Warning ? $"<@&{role}>" : null;
            var mentions = ping is null
                ? AllowedMentions.None
                : new AllowedMentions(AllowedMentionTypes.Roles);

            await target.SendMessageAsync(text: ping, embed: MonitorEmbeds.For(server, signal, health),
                allowedMentions: mentions, options: new RequestOptions { CancelToken = ct }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not post monitor alert for {Server} ({Kind})", server, signal.Kind);
        }
    }
}
