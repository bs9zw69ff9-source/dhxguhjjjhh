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
    /// <summary>
    /// The commands the whitelist bot owns when it is configured.
    /// </summary>
    /// <remarks>
    /// The same four the Node bot partitions off. When the second bot is on these register
    /// on IT and not on the main application - registering both would put two identical
    /// entries in the picker, and whichever the user clicked would answer twice.
    /// </remarks>
    private static readonly HashSet<string> FactionCommands =
        new(StringComparer.Ordinal) { "whitelist", "promotion", "demotion", "subclass" };

    private readonly DiscordSocketClient _client;

    /// <summary>The whitelist bot. Null unless FACTION_BOT_TOKEN and FACTION_CLIENT_ID are set.</summary>
    private readonly DiscordSocketClient? _factionClient;

    private readonly BotOptions _options;
    private readonly IReadOnlyDictionary<string, ISlashCommand> _commands;
    private readonly MetricsRegistry _metrics;
    private readonly HealthRegistry _health;
    private readonly CommandCatalog _catalog;
    private readonly PlayerAutocomplete _autocomplete;
    private readonly IReadOnlyList<IComponentHandler> _components;
    private readonly ILogger<DiscordGateway> _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _stopping;

    public DiscordGateway(
        BotOptions options,
        IEnumerable<ISlashCommand> commands,
        IEnumerable<IComponentHandler> components,
        MetricsRegistry metrics,
        HealthRegistry health,
        CommandCatalog catalog,
        PlayerAutocomplete autocomplete,
        ILogger<DiscordGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(components);
        _options = options;

        /* Longest prefix first, so dispatch is deterministic if two prefixes ever overlap
           rather than depending on the order the container happened to resolve them. */
        _components = components
            .OrderByDescending(h => h.Prefix.Length)
            .ToList();
        _metrics = metrics;
        _health = health;
        _catalog = catalog;
        _autocomplete = autocomplete;
        _logger = logger;
        _commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);

        /* Populate the catalog HERE rather than letting /help inject the command
           collection: a command that lists every command would otherwise depend on itself,
           and the container refuses to build the graph. */
        _catalog.Populate(_commands.Values
            .Select(c => c.Build())
            .OfType<SlashCommandProperties>()
            .Select(c => new CommandSummary(c.Name.Value, c.Description.Value)));

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            LogGatewayIntentWarnings = false,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 0,   // the bot never reads message history; caching it is pure memory
        });

        /* Second gateway connection, same process, same handlers, same state - exactly what
           the Node bot does. Sharing the process is the point: two processes would mean two
           writers to bot.db and the file races that design avoids. */
        if (_options.FactionBotEnabled)
        {
            _factionClient = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds,
                LogGatewayIntentWarnings = false,
                AlwaysDownloadUsers = false,
                MessageCacheSize = 0,
            });
        }
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
        _client.AutocompleteExecuted += OnAutocomplete;
        _client.ButtonExecuted += OnComponent;
        _client.SelectMenuExecuted += OnComponent;
        _client.ModalSubmitted += OnComponent;

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

        if (_factionClient is null) return;

        // The SAME handlers. The whitelist bot is a second connection, not a second bot -
        // it shares every command implementation and all state with the main one.
        _factionClient.Log += OnLog;
        _factionClient.Ready += OnFactionReadyAsync;
        _factionClient.SlashCommandExecuted += OnSlashCommand;
        _factionClient.AutocompleteExecuted += OnAutocomplete;
        _factionClient.ButtonExecuted += OnComponent;
        _factionClient.SelectMenuExecuted += OnComponent;
        _factionClient.ModalSubmitted += OnComponent;

        _health.Register("discord-whitelist", _ =>
        {
            var state = _factionClient.ConnectionState;
            return Task.FromResult(state switch
            {
                ConnectionState.Connected => HealthResult.Healthy($"latency {_factionClient.Latency}ms"),
                ConnectionState.Connecting => HealthResult.Degraded("connecting"),
                _ => HealthResult.Unhealthy($"whitelist bot gateway {state}"),
            });
        });

        try
        {
            await _factionClient.LoginAsync(TokenType.Bot, _options.FactionToken).ConfigureAwait(false);
            await _factionClient.StartAsync().ConfigureAwait(false);
            _logger.LogInformation("Whitelist bot starting with {Count} command(s)", FactionCommands.Count);
        }
        catch (Exception ex)
        {
            /* A bad FACTION_BOT_TOKEN must not take the main bot down with it. The whitelist
               bot shows offline and /health says which one is broken; everything else keeps
               working. */
            _logger.LogError(ex, "Whitelist bot could not log in - FACTION_BOT_TOKEN may be wrong or revoked. " +
                                 "The main bot is unaffected, but the whitelist commands will not appear");
        }
    }

    private async Task OnFactionReadyAsync()
    {
        try
        {
            var properties = _commands.Values
                .Where(c => FactionCommands.Contains(c.Name))
                .Select(c => c.Build())
                .ToArray();

            /* Global, matching the Node bot: this application is invited to each faction's
               guild, and guild-scoped registration would need every guild id listed here. */
            await _factionClient!.BulkOverwriteGlobalApplicationCommandsAsync(properties).ConfigureAwait(false);

            _logger.LogInformation("Whitelist bot logged in as {User} with {Count} command(s)",
                _factionClient.CurrentUser?.Username ?? "?", properties.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whitelist bot command registration failed");
        }
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
        /* When the whitelist bot is on, these register on IT and not here. Registering them
           on both applications puts two identical entries in the picker and whichever one
           the user clicks answers twice. */
        var properties = _commands.Values
            .Where(c => !_options.FactionBotEnabled || !FactionCommands.Contains(c.Name))
            .Select(c => c.Build())
            .ToArray();

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

    /// <summary>
    /// Route a button, select menu or modal to the handler that owns its custom id prefix.
    /// </summary>
    /// <remarks>
    /// NOT deferred here, unlike a slash command. The right acknowledgement depends on what
    /// the handler does - a modal cannot be opened on an interaction that has already been
    /// acknowledged - so each handler acknowledges in the way that suits it, and the three
    /// second budget is theirs to spend.
    ///
    /// An unrecognised id is answered rather than ignored. These arrive from buttons on
    /// messages that outlived the build that created them, and a silent drop leaves the
    /// clicker looking at a button that does nothing at all.
    /// </remarks>
    private async Task OnComponent(SocketInteraction interaction)
    {
        var customId = interaction switch
        {
            SocketMessageComponent component => component.Data.CustomId,
            SocketModal modal => modal.Data.CustomId,
            _ => null,
        };

        if (string.IsNullOrEmpty(customId)) return;

        var id = ComponentId.Parse(customId);
        var handler = _components.FirstOrDefault(h => string.Equals(h.Prefix, id.Prefix, StringComparison.Ordinal));

        if (handler is null)
        {
            _logger.LogWarning("No handler for component {CustomId}", customId);
            try
            {
                await interaction.RespondAsync(
                    "That control belongs to an older version of this message. Run the command again.",
                    ephemeral: true).ConfigureAwait(false);
            }
            catch (Exception) { }
            return;
        }

        var ct = _stopping?.Token ?? CancellationToken.None;
        try
        {
            await _metrics.TimeAsync("component_duration_ms", MetricLabels.Of("component", id.Prefix), async () =>
            {
                await handler.HandleAsync(interaction, id, ct).ConfigureAwait(false);
                return true;
            }, "Component interaction duration in milliseconds").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Component {CustomId} failed", customId);
            _metrics.Increment("component_errors_total", MetricLabels.Of("component", id.Prefix),
                help: "Component interactions that threw");
            try
            {
                // Whether it was acknowledged decides which call is legal, and guessing
                // wrong throws again - leaving the click with no response at all.
                if (interaction.HasResponded)
                    await interaction.FollowupAsync("That didn't work. The error has been logged.", ephemeral: true).ConfigureAwait(false);
                else
                    await interaction.RespondAsync("That didn't work. The error has been logged.", ephemeral: true).ConfigureAwait(false);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Answer an autocomplete request.
    /// </summary>
    /// <remarks>
    /// Discord gives roughly three seconds and shows a spinner until then, so this only
    /// reads what is already in memory. A command that DECLARES autocomplete and never
    /// answers leaves that spinner up forever, which reads as the whole bot being hung.
    /// </remarks>
    private async Task OnAutocomplete(SocketAutocompleteInteraction interaction)
    {
        try
        {
            var typed = interaction.Data.Current.Value?.ToString() ?? "";
            var choices = _autocomplete.Suggest(typed)
                .Select(c => new AutocompleteResult(c.Name, c.Value));

            await interaction.RespondAsync(choices).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never leave the spinner up. An empty result closes it cleanly and lets the
            // moderator type the name by hand.
            _logger.LogDebug(ex, "Autocomplete failed");
            try { await interaction.RespondAsync([]).ConfigureAwait(false); }
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

        if (_factionClient is not null)
        {
            await _factionClient.StopAsync().ConfigureAwait(false);
            await _factionClient.LogoutAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping?.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
        if (_factionClient is not null) await _factionClient.DisposeAsync().ConfigureAwait(false);
    }
}
