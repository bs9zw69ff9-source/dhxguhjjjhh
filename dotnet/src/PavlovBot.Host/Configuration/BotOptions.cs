using System.Globalization;
using Microsoft.Extensions.Configuration;
using PavlovBot.Rcon;

namespace PavlovBot.Host.Configuration;

/// <summary>Everything the host needs, read once at startup and then immutable.</summary>
/// <remarks>
/// Binding is explicit rather than <c>Configuration.Bind()</c> because the keys are flat
/// UPPER_SNAKE names shared with the Node bot, not a nested section tree - and because
/// every "missing" case here has a specific correct answer that a generic binder cannot
/// know: an absent metrics port disables the endpoint, an absent RCON password is fatal,
/// an absent interval takes the Node bot's value.
/// </remarks>
public sealed record BotOptions
{
    public required string DiscordToken { get; init; }

    /// <summary>
    /// Guild to register commands in. When set, commands appear immediately; global
    /// registration takes up to an hour to propagate, which makes it useless for
    /// iterating and is why the Node bot registers per-guild too.
    /// </summary>
    public ulong? GuildId { get; init; }

    /// <summary>
    /// The guild whose roles decide a person's access when the interaction is not in it.
    /// </summary>
    /// <remarks>
    /// STAFF ROLES LIVE IN ONE PLACE, and outside it there is no member to read them from.
    /// In a DM - and in any server this bot was carried into as a user-installed app - every
    /// role check answered false, so only owners, who are matched by user id, could use
    /// anything at all. A moderator messaging the bot privately was a member of the public.
    ///
    /// Naming the guild is what makes that answerable without guessing. Unset, it falls back
    /// to <see cref="GuildId"/>, and then to the single guild the bot is in; a bot in several
    /// guilds with none named refuses to pick one, because "roles from whichever server we
    /// happen to share" is a privilege escalation dressed as a convenience.
    /// </remarks>
    public ulong? HomeGuildId { get; init; }

    /// <summary>
    /// Register as a USER-INSTALLABLE app, so commands follow the installing account into
    /// DMs and servers the bot is not in.
    /// </summary>
    /// <remarks>
    /// THIS FORCES GLOBAL REGISTRATION AND IGNORES <see cref="GuildId"/>. Discord does not
    /// allow the two to be combined: integration types and contexts exist only on global
    /// commands, and a guild-scoped command is guild-installed by definition. Registering
    /// both would put every command in the picker twice inside that guild, which is the
    /// duplicate this codebase has already fixed once.
    ///
    /// The cost is propagation: a global command can take up to an hour to appear after a
    /// deploy, where a guild one is instant. That trade is the whole of this setting.
    ///
    /// PERMISSIONS DO NOT TRAVEL WITH IT. Every tier below owner is a Discord ROLE check, and
    /// outside a guild there is no member to read roles from - so in a DM the mod, admin,
    /// faction leader and police tiers are all false and only OWNER_IDS and SUPER_OWNER_IDS,
    /// which are user ids, still apply. That is a property of the permission model, not
    /// something this setting can grant.
    /// </remarks>
    public bool UserApp { get; init; }

    /// <summary>
    /// Commands this bot does not run, by name. Empty means every command.
    /// </summary>
    /// <remarks>
    /// THE POINT IS ONE BINARY SERVING TWO RPs. A Fallout server has no use for /arrest,
    /// /warrant or /bail, and leaving them in the picker for a moderator who can never use
    /// them is clutter that reads as a half-finished bot.
    ///
    /// A LIST OF NAMES, NOT A PROFILE FLAG. This repo tried the flag once - RP_PROFILE, which
    /// branched the whole command surface on an enum - and reverted it in full. A name list is
    /// data: it needs no code change to cover a command nobody has written yet, and it cannot
    /// grow a second meaning the way a profile does.
    ///
    /// Filtered where the command dictionary is BUILT, so one filter covers registration,
    /// dispatch and /help. A command disabled here is not registered with Discord at all, and
    /// the bulk overwrite removes it from the picker on the next start.
    /// </remarks>
    public IReadOnlyList<string> DisabledCommands { get; init; } = [];

    /// <summary>
    /// A SECOND Discord application that owns the whitelist commands. Both null disables it.
    /// </summary>
    /// <remarks>
    /// Its own bot, invited to each faction's guild, running in THIS process and sharing all
    /// state - so there are no file races between the two. It registers only the whitelist
    /// commands and the main bot then does not, which is what keeps them from appearing
    /// twice in the picker.
    /// </remarks>
    public string? FactionToken { get; init; }
    public ulong? FactionClientId { get; init; }

    /// <summary>True when both halves are set. One without the other is a mistake, not a mode.</summary>
    public bool FactionBotEnabled => !string.IsNullOrWhiteSpace(FactionToken) && FactionClientId is not null;

    public required IReadOnlyList<RconOptions> Servers { get; init; }

    public required MonitoringOptions Monitoring { get; init; }

    /// <summary>Where the JSON datasets live.</summary>
    public required string DataDirectory { get; init; }

    public TimeSpan RconHealthInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PlayerCacheInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan SupervisorInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Highest RCON_HOST_n index looked for. The Node bot ships three servers.</summary>
    private const int MaxServers = 9;

    public static BotOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var readCache = Milliseconds(configuration, "RCON_READ_CACHE_MS", TimeSpan.FromMilliseconds(2500));

        var servers = new List<RconOptions>();
        for (var i = 1; i <= MaxServers; i++)
        {
            var host = configuration[$"RCON_HOST_{i}"]?.Trim();
            if (string.IsNullOrEmpty(host)) continue;   // gaps are allowed: 1 and 3 without 2

            servers.Add(new RconOptions
            {
                Name = $"server{i}",
                Host = host,
                // A missing or unparseable port is left as 0 and caught by Validate, so the
                // failure names the setting instead of surfacing as a connection refused.
                Port = int.TryParse(configuration[$"RCON_PORT_{i}"], CultureInfo.InvariantCulture, out var port) ? port : 0,
                Password = configuration[$"RCON_PASSWORD_{i}"] ?? "",
                ReadCacheDuration = readCache,
            });
        }

        return new BotOptions
        {
            DiscordToken = configuration["DISCORD_TOKEN"]?.Trim() ?? "",
            GuildId = ulong.TryParse(configuration["GUILD_ID"], CultureInfo.InvariantCulture, out var guild) ? guild : null,
            HomeGuildId = ulong.TryParse(configuration["HOME_GUILD_ID"], CultureInfo.InvariantCulture, out var home) ? home : null,
            UserApp = configuration["USER_APP"]?.Trim().ToLowerInvariant()
                is "1" or "true" or "yes" or "on",

            /* Leading slashes trimmed, because that is how people write a command name and a
               list that silently failed on "/arrest" would be worse than one that rejected it. */
            DisabledCommands = (configuration["COMMANDS_DISABLED"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => c.TrimStart('/').Trim())
                .Where(c => c.Length > 0)
                .ToList(),
            FactionToken = configuration["FACTION_BOT_TOKEN"]?.Trim() is { Length: > 0 } ft ? ft : null,
            FactionClientId = ulong.TryParse(configuration["FACTION_CLIENT_ID"], CultureInfo.InvariantCulture, out var fc) ? fc : null,
            Servers = servers,
            Monitoring = MonitoringOptions.Bind(configuration),
            DataDirectory = configuration["DATA_DIR"]?.Trim() is { Length: > 0 } dir
                ? dir
                : Path.Combine(Directory.GetCurrentDirectory(), "data"),
            RconHealthInterval = Milliseconds(configuration, "RCON_HEALTH_INTERVAL_MS", TimeSpan.FromSeconds(60)),
        };
    }

    private static TimeSpan Milliseconds(IConfiguration configuration, string key, TimeSpan fallback) =>
        double.TryParse(configuration[key], CultureInfo.InvariantCulture, out var ms) && ms >= 0
            ? TimeSpan.FromMilliseconds(ms)
            : fallback;

    /// <summary>
    /// Every configuration problem, not just the first.
    /// </summary>
    /// <remarks>
    /// Returning the whole list matters more than it looks: fixing a bot's environment one
    /// crash at a time, with a restart between each, is how a five-minute deploy becomes an
    /// hour. Collect them, print them together, exit once.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(DiscordToken))
            problems.Add("DISCORD_TOKEN is not set - the bot cannot log in.");

        if (Servers.Count == 0)
            problems.Add("No RCON servers configured - set RCON_HOST_1, RCON_PORT_1 and RCON_PASSWORD_1.");

        foreach (var server in Servers)
        {
            var index = server.Name.Replace("server", "", StringComparison.Ordinal);
            if (server.Port is <= 0 or > 65535)
                problems.Add($"RCON_PORT_{index} is missing or not a valid port (got \"{server.Port}\").");
            if (string.IsNullOrEmpty(server.Password))
                problems.Add($"RCON_PASSWORD_{index} is not set - the RCON handshake will fail.");
        }

        problems.AddRange(Monitoring.Validate());
        return problems;
    }
}

/// <param name="Port">
/// Null means NOT CONFIGURED - no listener, no attack surface. This is a tri-state on
/// purpose: the Node bot once read a configured port of 0 as "disabled" because the check
/// was a truthiness test, and the metrics endpoint silently never came up.
/// </param>
/// <param name="Host">
/// Defaults to loopback. These endpoints expose service names, error messages and internal
/// state; on a game host with a public interface a default of 0.0.0.0 would publish that to
/// the internet. Widening it has to be a decision somebody made.
/// </param>
/// <param name="Token">Optional bearer token, for when the endpoint must be exposed.</param>
public sealed record MonitoringOptions(int? Port, string Host, string? Token)
{
    public bool Enabled => Port is not null;

    public static MonitoringOptions Bind(IConfiguration configuration)
    {
        var raw = configuration["METRICS_PORT"]?.Trim();
        int? port = string.IsNullOrEmpty(raw)
            ? null
            : int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;   // -1 => "set but nonsense", reported by Validate

        return new MonitoringOptions(
            Port: port,
            Host: configuration["METRICS_HOST"]?.Trim() is { Length: > 0 } host ? host : "127.0.0.1",
            Token: configuration["METRICS_TOKEN"]?.Trim() is { Length: > 0 } token ? token : null);
    }

    public IEnumerable<string> Validate()
    {
        if (Port is null) yield break;

        if (Port is < 1 or > 65535)
            yield return $"METRICS_PORT is set but is not a usable port (got \"{Port}\"). " +
                         "Leave it unset to disable the endpoint. Port 0 (ephemeral) is not supported here - " +
                         "the .NET listener needs a fixed port to build its URL prefix.";

        if (!string.Equals(Host, "127.0.0.1", StringComparison.Ordinal) &&
            !string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            Token is null)
        {
            yield return $"METRICS_HOST is \"{Host}\" (not loopback) but METRICS_TOKEN is not set - " +
                         "the health report would be readable by anyone who can reach the port.";
        }
    }
}
