using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The gateway connection, command registration and interaction dispatch.
/// </summary>
/// <remarks>
/// THE THREE-SECOND RULE drives the shape of this class. Discord closes an interaction that
/// is not acknowledged within three seconds, and the acknowledgement cannot be sent late -
/// the token is simply dead. So dispatch DEFERS FIRST and only then runs the handler. Every
/// "all commands are infinite loading" report is this rule being broken somewhere: a
/// handler that awaits RCON before acknowledging looks fine on a fast day and hangs every
/// command on a slow one.
///
/// Intents are the minimum that works. Every additional intent is more gateway traffic to
/// parse and more state to hold, and the privileged ones need portal approval; a
/// slash-command bot needs Guilds and nothing else.
/// </remarks>
public sealed class DiscordGateway : IHostedService, IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly BotOptions _options;
    private readonly IReadOnlyDictionary<string, ISlashCommand> _commands;
    private readonly MetricsRegistry _metrics;
    private readonly HealthRegistry _health;
    private readonly ILogger<DiscordGateway> _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _stopping;

    public DiscordGateway(
        BotOptions options,
        IEnumerable<ISlashCommand> commands,
        MetricsRegistry metrics,
        HealthRegistry health,
        ILogger<DiscordGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _options = options;
        _metrics = metrics;
        _health = health;
        _logger = logger;
        _commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            LogGatewayIntentWarnings = false,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 0,   // the bot never reads message history; caching it is pure memory
        });
    }

    /// <summary>Fetch a channel by id. Null when the bot cannot see it.</summary>
    public async Task<IChannel?> GetChannelAsync(ulong channelId)
    {
        // The socket cache first - a board refresh every few minutes must not be a REST
        // call every few minutes - then REST as the fallback for an uncached channel.
        if (_client.GetChannel(channelId) is IChannel cached) return cached;
        try { return await _client.Rest.GetChannelAsync(channelId).ConfigureAwait(false); }
        catch (Exception) { return null; }
    }

    /// <summary>True once the gateway has connected and commands are registered.</summary>
    public bool IsReady => _ready.Task.IsCompletedSuccessfully && _client.ConnectionState == ConnectionState.Connected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _client.Log += OnLog;
        _client.Ready += OnReady;
        _client.SlashCommandExecuted += OnSlashCommand;

        _health.Register("discord", _ =>
        {
            var state = _client.ConnectionState;
            return Task.FromResult(state switch
            {
                ConnectionState.Connected when IsReady => HealthResult.Healthy($"latency {_client.Latency}ms"),
                ConnectionState.Connected => HealthResult.Degraded("connected, commands not registered yet"),
                ConnectionState.Connecting => HealthResult.Degraded("connecting"),
                _ => HealthResult.Unhealthy($"gateway {state}"),
            });
        });

        await _client.LoginAsync(TokenType.Bot, _options.DiscordToken).ConfigureAwait(false);
        await _client.StartAsync().ConfigureAwait(false);
        _logger.LogInformation("Discord gateway starting with {Count} command(s)", _commands.Count);
    }

    private async Task OnReady()
    {
        try
        {
            await RegisterCommandsAsync().ConfigureAwait(false);
            _ready.TrySetResult();
            _logger.LogInformation("Logged in as {User}", _client.CurrentUser?.Username ?? "?");
        }
        catch (Exception ex)
        {
            // A registration failure must not kill the gateway. The bot stays connected and
            // /health reports degraded, which is recoverable; a dead client is not.
            _logger.LogError(ex, "Slash command registration failed");
        }
    }

    private async Task RegisterCommandsAsync()
    {
        var properties = _commands.Values.Select(c => c.Build()).ToArray();

        /* Guild-scoped registration when a guild is configured, because global commands take
           up to an hour to propagate - long enough that you conclude the code is broken.
           BulkOverwrite rather than per-command creation: it is one request instead of N,
           and it DELETES commands that no longer exist, so a renamed command does not leave
           its old name in the picker forever. */
        if (_options.GuildId is { } guildId)
        {
            var guild = _client.GetGuild(guildId);
            if (guild is null)
            {
                _logger.LogWarning("GUILD_ID {GuildId} is not a guild this bot is in - falling back to global commands", guildId);
                await _client.BulkOverwriteGlobalApplicationCommandsAsync(properties).ConfigureAwait(false);
                return;
            }
            await guild.BulkOverwriteApplicationCommandAsync(properties).ConfigureAwait(false);
            _logger.LogInformation("Registered {Count} command(s) in guild {Guild}", properties.Length, guild.Name);
            return;
        }

        await _client.BulkOverwriteGlobalApplicationCommandsAsync(properties).ConfigureAwait(false);
        _logger.LogInformation("Registered {Count} global command(s) - propagation can take up to an hour", properties.Length);
    }

    private async Task OnSlashCommand(SocketSlashCommand interaction)
    {
        var name = interaction.Data.Name;
        if (!_commands.TryGetValue(name, out var command))
        {
            // Almost always a command left registered from an older build.
            _logger.LogWarning("No handler for /{Name}", name);
            await interaction.RespondAsync("That command is no longer available.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        // DEFER FIRST, ALWAYS. See the class remarks: the acknowledgement cannot be sent
        // late, so it must not wait on anything that can be slow.
        try
        {
            await interaction.DeferAsync(ephemeral: command.Ephemeral).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not acknowledge /{Name} in time", name);
            return;
        }

        var ct = _stopping?.Token ?? CancellationToken.None;
        try
        {
            await _metrics.TimeAsync("command_duration_ms", MetricLabels.Of("command", name), async () =>
            {
                await command.HandleAsync(interaction, ct).ConfigureAwait(false);
                return true;
            }, "Slash command duration in milliseconds").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "/{Name} failed", name);
            _metrics.Increment("command_errors_total", MetricLabels.Of("command", name), help: "Slash commands that threw");
            try
            {
                /* The user is looking at a spinner. Something has to replace it, or the
                   failure is indistinguishable from the bot being dead. The message says
                   what happened without leaking an exception into a public channel. */
                await interaction.ModifyOriginalResponseAsync(m =>
                    m.Content = "That didn't work. The error has been logged.").ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    private Task OnLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
#pragma warning disable CA2254   // the message is Discord.Net's, not a template of ours
        _logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
#pragma warning restore CA2254
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null) await _stopping.CancelAsync().ConfigureAwait(false);
        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stopping?.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
