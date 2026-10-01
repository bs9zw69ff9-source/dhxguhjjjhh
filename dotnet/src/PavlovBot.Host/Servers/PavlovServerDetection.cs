using System.Globalization;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Servers;

/// <summary>One Pavlov server found on this box: the systemd unit that runs it and the install it runs.</summary>
public sealed record DetectedServer(string Unit, string Install);

/// <summary>
/// Finding the Pavlov servers from systemd, so PAVLOV_UNITS and PAVLOV_BASE_1 can be left blank.
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
/// </remarks>
public static class PavlovServerDetection
{
    /// <summary>Where administrator-installed units live, and where /provisionserver writes them.</summary>
    public const string UnitDirectory = "/etc/systemd/system";

    private const string Launcher = "PavlovServer.sh";

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
