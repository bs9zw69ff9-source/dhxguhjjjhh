using System.Globalization;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Servers;

/// <summary>
/// One Pavlov server found on this box: its <c>PAVLOV_UNITS</c> entry (a systemd unit, or
/// <c>docker:</c> and a container) and the install it runs.
/// </summary>
public sealed record DetectedServer(string Unit, string Install);

/// <summary>
/// Finding the Pavlov servers from systemd and Docker, so PAVLOV_UNITS and PAVLOV_BASE_1 can be left blank.
/// </summary>
/// <remarks>
/// THE UNIT FILES ALREADY SAY IT. Every server is a service whose ExecStart runs
/// <c>PavlovServer.sh</c> out of its install, so the unit name and the install directory are
/// both on disk - typing them into .env as well was a second copy that went stale the moment a
/// server was added, deleted or renamed, and a stale PAVLOV_UNITS restarts the wrong server.
///
/// ORDERED BY THE NUMBER ON THE UNIT NAME, so <c>pavlovserver</c>, <c>pavlovserver1</c>,
/// <c>pavlovserver2</c> come out as servers 1, 2 and 3 to match RCON_*_1, _2, _3 - the layout
/// /provisionserver builds - and <c>pavlovserver10</c> sorts after <c>pavlovserver2</c>.
///
/// Only a unit whose install actually holds a <c>Pavlov</c> directory counts, the same test install
/// discovery uses, so a leftover unit pointing at a deleted server is not picked up.
///
/// A CONTAINER SAYS IT TOO: the host folder it mounts as <c>Pavlov/Saved</c> is two levels under
/// its install. Only containers carrying the bot's label are read, so an unrelated container on
/// the box is never mistaken for a game server, and the label is on everything /provisionserver
/// creates and in the INSTALL.md migration command.
/// </remarks>
public static class PavlovServerDetection
{
    /// <summary>Where administrator-installed units live, and where /provisionserver writes them.</summary>
    public const string UnitDirectory = "/etc/systemd/system";

    private const string Launcher = "PavlovServer.sh";

    /// <summary>
    /// Every Pavlov server on the box - systemd units and the bot's containers - in server order.
    /// </summary>
    /// <param name="unitDirectory">Where to read unit files.</param>
    /// <param name="docker">The container lookup; null asks the real Docker daemon.</param>
    /// <remarks>
    /// A CONTAINER WINS OVER A UNIT FOR THE SAME INSTALL. That is a server half way through moving
    /// to Docker: the unit file can outlive the move, the container is what is actually running.
    /// </remarks>
    public static IReadOnlyList<DetectedServer> Detect(
        string unitDirectory = UnitDirectory, Func<IReadOnlyList<DetectedServer>>? docker = null) =>
        [.. (docker ?? FromDocker)()
            .Concat(FromSystemd(unitDirectory))
            .DistinctBy(s => s.Install, StringComparer.Ordinal)
            .OrderBy(s => Stem(BareName(s.Unit)), StringComparer.Ordinal)
            .ThenBy(s => Number(BareName(s.Unit)))];

    /// <summary>
    /// The bot's labelled containers and the installs they mount, or none when Docker cannot say.
    /// </summary>
    /// <remarks>
    /// RUNS AT STARTUP, while configuration is bound, which is synchronous - so this blocks on the
    /// two short <c>docker</c> calls, bounded at five seconds each. A box without Docker, or a bot
    /// that may not reach its socket, gets an empty list rather than a delay or an error: systemd
    /// detection and the configured lists still apply.
    /// </remarks>
    public static IReadOnlyList<DetectedServer> FromDocker()
    {
        var ids = Docker(["ps", "-aq", "--filter", "label=" + DockerCli.ManagedLabel]);
        if (ids is null) return [];

        var list = ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.All(char.IsAsciiHexDigit))
            .ToList();
        if (list.Count == 0) return [];

        return ParseDocker(Docker(["container", "inspect", "--format", InspectFormat, .. list]));
    }

    /// <summary>Name, then the host source of the Saved mount: <c>/pavlovserver2|/home/steam/pavlovserver2/Pavlov/Saved</c>.</summary>
    internal const string InspectFormat =
        "{{.Name}}|{{range .Mounts}}{{if eq .Destination \"" + DockerCli.ContainerSavedDir + "\"}}{{.Source}}{{end}}{{end}}";

    /// <summary>What <see cref="InspectFormat"/> printed, as servers whose install is really there.</summary>
    internal static IReadOnlyList<DetectedServer> ParseDocker(string? stdout, Func<string, bool>? installExists = null)
    {
        var exists = installExists ?? (install => Directory.Exists(Path.Combine(install, "Pavlov")));
        var found = new List<DetectedServer>();

        foreach (var line in (stdout ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 2);
            if (parts.Length != 2) continue;

            var name = parts[0].TrimStart('/');
            var saved = parts[1].Trim().TrimEnd('/');
            const string suffix = "/Pavlov/Saved";
            if (!ServerUnit.IsPlausibleContainerName(name) || !saved.EndsWith(suffix, StringComparison.Ordinal)) continue;

            var install = saved[..^suffix.Length];
            if (install.Length > 0 && exists(install)) found.Add(new DetectedServer(ServerUnit.DockerEntry(name), install));
        }

        return found;
    }

    /// <summary>One <c>docker</c> call's stdout, or null when it could not run or failed.</summary>
    private static string? Docker(IReadOnlyList<string> argv)
    {
        try
        {
            var run = ProcessRunner.RunAsync("docker", argv, TimeSpan.FromSeconds(5), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None)
                .GetAwaiter().GetResult();
            return run.Started && !run.TimedOut && run.ExitCode == 0 ? run.Stdout : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string BareName(string unit) => ServerUnit.Parse(unit)?.Name ?? unit;

    /// <summary>Every Pavlov server with a unit file in <paramref name="unitDirectory"/>, in server order.</summary>
    public static IReadOnlyList<DetectedServer> FromSystemd(string unitDirectory = UnitDirectory)
    {
        var found = new List<DetectedServer>();
        if (!Directory.Exists(unitDirectory)) return found;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(unitDirectory, "*.service").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return found; }

        foreach (var file in files)
        {
            if (Parse(file) is { } install && Directory.Exists(Path.Combine(install, "Pavlov")))
                found.Add(new DetectedServer(Path.GetFileNameWithoutExtension(file), install.TrimEnd('/')));
        }

        return [.. found
            .DistinctBy(s => s.Install, StringComparer.Ordinal)
            .OrderBy(s => Stem(s.Unit), StringComparer.Ordinal)
            .ThenBy(s => Number(s.Unit))];
    }

    /// <summary>
    /// The units for these installs, in the same order: the unit systemd runs each one with, or the
    /// install's folder name - the /provisionserver convention - when no unit file was found for it.
    /// </summary>
    public static IReadOnlyList<string> UnitsFor(IReadOnlyList<string> installs, IReadOnlyList<DetectedServer> detected)
    {
        ArgumentNullException.ThrowIfNull(installs);
        ArgumentNullException.ThrowIfNull(detected);

        return [.. installs.Select(install =>
            detected.FirstOrDefault(d => string.Equals(d.Install, install.TrimEnd('/'), StringComparison.Ordinal))?.Unit
            ?? Path.GetFileName(install.TrimEnd('/')))];
    }

    /// <summary>The install a unit file runs Pavlov from, or null when it does not run Pavlov.</summary>
    internal static string? Parse(string unitFile)
    {
        string[] lines;
        try { lines = File.ReadAllLines(unitFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        string? workingDirectory = null;
        string? launcher = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (Value(line, "WorkingDirectory=") is { } dir) workingDirectory = dir.TrimStart('-');
            else if (Value(line, "ExecStart=") is { } exec)
            {
                // ExecStart may carry systemd prefixes (-, @, +, !, :) before the path.
                var program = exec.TrimStart('-', '@', '+', '!', ':').Split(' ', 2)[0];
                if (program.EndsWith(Launcher, StringComparison.Ordinal)) launcher = program;
            }
        }

        if (launcher is null) return null;

        // The script sits in the install root; WorkingDirectory is used when the path is relative.
        var install = Path.IsPathRooted(launcher) ? Path.GetDirectoryName(launcher) : workingDirectory;
        return string.IsNullOrWhiteSpace(install) ? null : install;
    }

    private static string? Value(string line, string key) =>
        line.StartsWith(key, StringComparison.Ordinal) ? line[key.Length..].Trim() : null;

    private static string Stem(string unit) => unit.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    private static int Number(string unit)
    {
        var digits = unit[Stem(unit).Length..];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
