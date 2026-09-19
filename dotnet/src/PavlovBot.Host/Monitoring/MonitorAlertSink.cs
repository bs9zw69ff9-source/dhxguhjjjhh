using Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Monitoring;
using PavlovBot.Core.Text;
using PavlovBot.Host.Discord;

namespace PavlovBot.Host.Monitoring;

/// <summary>Where a monitoring alert goes. An interface so the monitor is tested without Discord.</summary>
public interface IMonitorAlertSink
{
    Task PostAsync(string server, MonitorSignal signal, ServerHealth health, CancellationToken ct = default);

    /// <summary>
    /// Post a synthetic alert to prove the channel and permissions work, returning what happened
    /// in one line for a human. This is what <c>/monitor test</c> reports, so unlike the normal
    /// post it does NOT swallow the outcome.
    /// </summary>
    Task<string> TestAsync(CancellationToken ct = default);
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
///
/// THE GATEWAY IS RESOLVED ON USE, not taken in the constructor, for the same reason
/// <see cref="Discord.GatewayGuildDirectory"/> does: DiscordGateway depends on every ISlashCommand,
/// this sink is reached from one (via the monitor), and injecting the gateway here would close a
/// DiscordGateway -> command -> monitor -> sink -> DiscordGateway cycle that deadlocks graph build.
/// </remarks>
public sealed class DiscordMonitorAlertSink(
    IServiceProvider services, ulong? channelId, ulong? roleId, ILogger<DiscordMonitorAlertSink> logger) : IMonitorAlertSink
{
    private DiscordGateway Gateway => services.GetRequiredService<DiscordGateway>();

    public async Task PostAsync(string server, MonitorSignal signal, ServerHealth health, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (channelId is not { } channel) return;

        try
        {
            if (await Gateway.GetChannelAsync(channel).ConfigureAwait(false) is not IMessageChannel target)
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

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        if (channelId is not { } channel)
            return "No monitoring channel is configured. Set `MONITOR_ALERT_CHANNEL` in this bot's `.env`.";

        try
        {
            if (await Gateway.GetChannelAsync(channel).ConfigureAwait(false) is not IMessageChannel target)
                return $"Channel `{channel}` is not a text channel this bot can see. Check the ID, and that the bot is in that server.";

            var signal = new MonitorSignal(SignalKind.ServerOnline, Severity.Info,
                "Monitoring test alert - if you can see this, the channel and permissions are working. Real alerts fire only when something changes (offline, recovery, high latency, and so on).");
            var health = ServerHealth.Initial with { State = HealthState.Online };

            var ping = roleId is { } role ? $"<@&{role}>" : null;
            var mentions = ping is null ? AllowedMentions.None : new AllowedMentions(AllowedMentionTypes.Roles);

            await target.SendMessageAsync(text: ping, embed: MonitorEmbeds.For("test", signal, health),
                allowedMentions: mentions, options: new RequestOptions { CancelToken = ct }).ConfigureAwait(false);

            return $"Posted a test alert to <#{channel}>{(roleId is { } r ? $" and pinged <@&{r}>" : "")}. If it did not appear there, the bot is missing a permission in that channel.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Monitor alert test failed for channel {Channel}", channel);
            return $"Could not post: {ex.Message}. The bot most likely lacks **Send Messages** or **Embed Links** in <#{channel}>.";
        }
    }
}
