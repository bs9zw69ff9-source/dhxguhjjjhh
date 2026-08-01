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
