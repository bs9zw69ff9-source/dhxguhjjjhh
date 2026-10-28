namespace PavlovBot.Host.Servers;

/// <summary>What runs a game server: a systemd unit, or a Docker container.</summary>
public enum ServerRuntime
{
    Systemd,
    Docker,
}

/// <summary>
/// One <c>PAVLOV_UNITS</c> entry: <c>pavlovserver1</c> is a systemd unit, <c>docker:pavlovserver1</c>
/// a container.
/// </summary>
/// <remarks>
/// A PREFIX ON THE ENTRY, NOT A SETTING FOR THE BOX. One binary runs both bots, and the Fallout
/// bot's servers are systemd units while new ones are containers. A box-wide switch would flip
/// every existing server to a runtime it is not running under, so each entry says what it is and
/// a bare name keeps meaning exactly what it always meant.
///
/// BOTH NAMES BECOME ONE ARGUMENT TO A ROOT COMMAND, so both are allow-listed here, before any
/// caller can use them: a letter or digit first (so <c>--rm</c> can never be read as a flag), then
/// only characters the two tools accept in a name.
/// </remarks>
public readonly record struct ServerUnit(ServerRuntime Runtime, string Name)
{
    public const string DockerPrefix = "docker:";

    /// <summary>The entry, or null when it is neither a plausible unit nor a plausible container.</summary>
    public static ServerUnit? Parse(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var trimmed = entry.Trim();

        if (trimmed.StartsWith(DockerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[DockerPrefix.Length..];
            return IsPlausibleContainerName(name) ? new ServerUnit(ServerRuntime.Docker, name) : null;
        }

        return ServiceControl.IsPlausibleSystemdName(trimmed) ? new ServerUnit(ServerRuntime.Systemd, trimmed) : null;
    }

    /// <summary>A Docker entry for <paramref name="container"/>.</summary>
    public static string DockerEntry(string container) => DockerPrefix + container;

    /// <summary>
    /// Whether Docker would accept this as a container name.
    /// </summary>
    /// <remarks>
    /// Docker's own rule is <c>[a-zA-Z0-9][a-zA-Z0-9_.-]+</c>. Capped at 64 like a unit name: no
    /// server needs a longer one, and a bound keeps the argument small.
    /// </remarks>
    public static bool IsPlausibleContainerName(string? name) =>
        name is { Length: > 0 and <= 64 } &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public bool IsDocker => Runtime == ServerRuntime.Docker;

    /// <summary>How it is written back into <c>PAVLOV_UNITS</c>.</summary>
    public override string ToString() => IsDocker ? DockerEntry(Name) : Name;

    /// <summary>For people: "docker container `pavlovserver2`" or "systemd unit `pavlovserver2`".</summary>
    public string Describe() => IsDocker ? $"docker container `{Name}`" : $"systemd unit `{Name}`";

    /// <summary>The command somebody runs by hand to see why it is down.</summary>
    public string StatusHint() => IsDocker ? $"docker logs --tail 50 {Name}" : $"systemctl status {Name}";
}
