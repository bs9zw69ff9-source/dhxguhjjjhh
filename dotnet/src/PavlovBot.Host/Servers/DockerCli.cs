using System.Globalization;
using System.Text.RegularExpressions;
using PavlovBot.Core.Provisioning;

namespace PavlovBot.Host.Servers;

/// <summary>
/// Every <c>docker</c> command line the bot runs, and the parsing of what comes back.
/// </summary>
/// <remarks>
/// PURE, so each exact argv is pinned by a test instead of being found out against a live
/// daemon - the same reason the SteamCMD and systemctl argv are extracted. Nothing here runs a
/// process; <see cref="ServiceControl"/> and <see cref="ServerProvisioner"/> do, through
/// <see cref="ProcessRunner"/>, with these arrays and no shell.
///
/// THE IMAGE IS XavierSJC/pavlov-vr-server's Shack build. Its entrypoint updates the game with
/// SteamCMD (<c>-beta shack</c>, the branch this bot is built around) and starts
/// <c>PavlovServer.sh -PORT=$SERVER_PORT</c>. The bot mounts only <c>Pavlov/Saved</c> from the
/// host, which is every file it reads or writes: whitelists, bans, rosters, ModSave and the log
/// it tails. The game binaries stay inside the image.
/// </remarks>
public static partial class DockerCli
{
    /// <summary>
    /// The image a new server runs, pinned to a release.
    /// </summary>
    /// <remarks>
    /// PINNED, NOT <c>latest</c>. The tag fixes the image's scripts and base, which is what
    /// could change under a server between two provisions; the GAME is updated by the entrypoint
    /// on every start either way. <c>PAVLOV_DOCKER_IMAGE</c> overrides it.
    /// </remarks>
    public const string DefaultImage = "powersjk/pavlov-vr-shack-server:1.8.0";

    /// <summary>Where the image keeps the server's <c>Saved</c> directory.</summary>
    internal const string ContainerSavedDir = "/home/steam/pavlovserver/Pavlov/Saved";

    /// <summary>The label every container this bot creates carries, so it can be told apart.</summary>
    internal const string ManagedLabel = "pavlovbot.managed=true";

    /// <summary>How long Docker waits for a clean shutdown before it kills the server.</summary>
    /// <remarks>Docker's default is 10s; Pavlov saves on the way down, so it gets longer.</remarks>
    internal const int StopTimeoutSeconds = 30;

    /// <summary>
    /// A plausible image reference: <c>repo/name</c>, an optional tag, an optional digest.
    /// </summary>
    /// <remarks>It becomes one argv element, so it must never start with '-' or hold a space.</remarks>
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._/-]{0,199}(:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(@sha256:[a-f0-9]{64})?$")]
    private static partial Regex ImagePattern { get; }

    public static bool IsPlausibleImage(string? image) => image is not null && ImagePattern.IsMatch(image);

    /// <summary>The host directory bind-mounted as the container's <c>Saved</c>.</summary>
    public static string HostSavedDir(string installDir) => Path.Combine(installDir.TrimEnd('/'), "Pavlov", "Saved");

    /// <summary><c>docker version</c>, reporting the SERVER - i.e. whether the daemon answers at all.</summary>
    internal static IReadOnlyList<string> VersionArgv() => ["version", "--format", "{{.Server.Version}}"];

    internal static IReadOnlyList<string> PullArgv(string image) => ["pull", image];

    /// <summary>Whether a container of this name exists, running or not.</summary>
    internal static IReadOnlyList<string> ExistsArgv(string container) => ["container", "inspect", "--format", "{{.Id}}", container];

    /// <summary>The uid and gid the image runs as, so the mounted tree can be handed to it.</summary>
    internal static IReadOnlyList<string> UserIdArgv(string image, bool group) =>
        ["run", "--rm", "--entrypoint", "id", image, group ? "-g" : "-u"];

    /// <summary>
    /// <c>docker run</c> for one server.
    /// </summary>
    /// <remarks>
    /// HOST NETWORKING, so the server binds its game, query and RCON ports exactly as a systemd
    /// server does - no port mapping to keep in step with Game.ini and RconSettings.txt, the
    /// firewall rules stay as they are, and the bot reaches RCON on 127.0.0.1. It also avoids
    /// pushing a game's UDP traffic through Docker's userland proxy.
    ///
    /// <c>--restart unless-stopped</c> is what systemd's Restart= and enable were: it comes back
    /// after a crash and after a reboot, and stays down when somebody stopped it on purpose.
    ///
    /// NO PASSWORD IN THE ENVIRONMENT. The entrypoint only uses <c>RCON_PWD</c> to rewrite a
    /// placeholder in RconSettings.txt, and the bot writes that file itself - passing it would
    /// only put the password in <c>docker inspect</c>. <c>SERVER_NAME</c> is the container name
    /// for the same reason: the entrypoint refuses to start without one but uses it only to
    /// replace a placeholder the bot's Game.ini does not contain, so the real name, which can
    /// hold any character, never reaches its <c>sed</c>.
    /// </remarks>
    internal static IReadOnlyList<string> RunArgv(ServerProvisionSpec spec, string container, string image) =>
    [
        "run", "-d",
        "--name", container,
        "--restart", "unless-stopped",
        "--network", "host",
        "--stop-timeout", StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        "--label", ManagedLabel,
        "--label", $"pavlovbot.slot={spec.Slot.ToString(CultureInfo.InvariantCulture)}",
        "-e", $"SERVER_NAME={container}",
        "-e", $"SERVER_PORT={spec.GamePort.ToString(CultureInfo.InvariantCulture)}",
        "-v", $"{HostSavedDir(spec.InstallDir)}:{ContainerSavedDir}",
        image,
    ];

    /// <summary><c>docker rm -f</c>: stop and remove in one step. Absent is not an error worth stopping for.</summary>
    internal static IReadOnlyList<string> RemoveArgv(string container) => ["rm", "-f", container];

    /// <summary><c>docker start|stop|restart</c>, with the long stop timeout where it applies.</summary>
    internal static IReadOnlyList<string> VerbArgv(UnitAction action, string container) => action switch
    {
        UnitAction.Start => ["start", container],
        UnitAction.Stop => ["stop", "-t", StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture), container],
        _ => ["restart", "-t", StopTimeoutSeconds.ToString(CultureInfo.InvariantCulture), container],
    };

    /// <summary>State, start time and memory limit in one call: <c>running|2026-...|0</c>.</summary>
    internal static IReadOnlyList<string> InspectArgv(string container) =>
        ["container", "inspect", "--format", "{{.State.Status}}|{{.State.StartedAt}}|{{.HostConfig.Memory}}", container];

    /// <summary>One sample of CPU, memory and process count for every named container.</summary>
    internal static IReadOnlyList<string> StatsArgv(IEnumerable<string> containers) =>
        ["stats", "--no-stream", "--format", "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.PIDs}}", .. containers];

    /// <summary>
    /// The state for a word <c>.State.Status</c> printed.
    /// </summary>
    /// <remarks>
    /// EXITED IS "STOPPED ON PURPOSE", and that rests on the restart policy. Under
    /// <c>unless-stopped</c> a crash is restarted and reads <c>restarting</c>; only a container
    /// somebody stopped stays <c>exited</c>. Anything unrecognised is Unknown, never a guess.
    /// </remarks>
    internal static UnitState StateFrom(string? status) => status?.Trim() switch
    {
        "running" => UnitState.Active,
        "restarting" or "created" => UnitState.Starting,
        "exited" or "paused" or "removing" => UnitState.Inactive,
        "dead" => UnitState.Failed,
        _ => UnitState.Unknown,
    };

    /// <summary>What <see cref="InspectArgv"/> printed, split.</summary>
    internal static (UnitState State, TimeSpan? Uptime, long? MemoryLimit) ParseInspect(string? stdout, DateTimeOffset now)
    {
        var parts = (stdout ?? "").Trim().Split('|');
        var state = StateFrom(parts.ElementAtOrDefault(0));

        TimeSpan? uptime = null;
        if (state == UnitState.Active && StartedAt(parts.ElementAtOrDefault(1)) is { } started && now > started)
            uptime = now - started;

        // 0 is "no limit", which is not a number worth showing.
        long? limit = long.TryParse(parts.ElementAtOrDefault(2), CultureInfo.InvariantCulture, out var bytes) && bytes > 0
            ? bytes
            : null;

        return (state, uptime, limit);
    }

    /// <summary>
    /// Docker's RFC 3339 time, which carries NINE fractional digits.
    /// </summary>
    /// <remarks>.NET parses at most seven, so the rest is cut rather than letting the parse fail.</remarks>
    internal static DateTimeOffset? StartedAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = FractionPattern.Replace(raw.Trim(), m => m.Groups[1].Value);
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) &&
               at.Year > 1
            ? at
            : null;
    }

    [GeneratedRegex(@"(\.\d{1,7})\d*")]
    private static partial Regex FractionPattern { get; }

    /// <summary>One line of <see cref="StatsArgv"/>: name, CPU %, memory in use, process count.</summary>
    internal static (string Name, double? Cpu, long? Memory, int? Pids)? ParseStatsLine(string? line)
    {
        var parts = (line ?? "").Trim().Split('|');
        if (parts.Length < 4 || parts[0].Length == 0) return null;

        double? cpu = double.TryParse(parts[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var c)
            ? c
            : null;
        var memory = Size(parts[2].Split('/')[0]);
        int? pids = int.TryParse(parts[3].Trim(), CultureInfo.InvariantCulture, out var p) ? p : null;

        return (parts[0].Trim(), cpu, memory, pids);
    }

    /// <summary>"1.234GiB", "512MiB", "10kB" as bytes, or null.</summary>
    internal static long? Size(string? raw)
    {
        var match = SizePattern.Match(raw?.Trim() ?? "");
        if (!match.Success ||
            !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        double scale = match.Groups[2].Value switch
        {
            "B" => 1,
            "kB" or "KB" => 1e3,
            "KiB" => 1024,
            "MB" => 1e6,
            "MiB" => 1024d * 1024,
            "GB" => 1e9,
            "GiB" => 1024d * 1024 * 1024,
            "TB" => 1e12,
            "TiB" => 1024d * 1024 * 1024 * 1024,
            _ => double.NaN,
        };

        return double.IsNaN(scale) ? null : (long)(value * scale);
    }

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([A-Za-z]+)$")]
    private static partial Regex SizePattern { get; }
}
