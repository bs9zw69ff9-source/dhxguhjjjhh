using System.Text.RegularExpressions;

namespace PavlovBot.Core.Provisioning;

/// <summary>
/// Every problem with a <see cref="ServerProvisionSpec"/>, collected at once.
/// </summary>
/// <remarks>
/// The same shape as <c>BotOptions.Validate</c> and for the same reason: an operator fixing a
/// provision request one rejection at a time, with a slow SteamCMD run wasted between each, is
/// how a two-minute command becomes an afternoon. Every field is checked and the whole list is
/// returned, so the reply names all of them together.
///
/// The RCON password charset is the load-bearing rule. That value is written UNQUOTED into both
/// <c>RconSettings.txt</c> (line-oriented: a newline would split the file) and the bot's
/// <c>.env</c> (dotenv grammar: an unquoted value ends at the first <c>#</c>, and dotenv's
/// quote-unescaping is lossy, so quoting is not a safe general fix). Constraining the alphabet
/// to characters that mean nothing to either format is what lets one password round-trip through
/// both untouched.
/// </remarks>
public static partial class ProvisionValidation
{
    /// <summary>The highest RCON slot the bot scans, matching <c>BotOptions.MaxServers</c>.</summary>
    public const int MaxServers = 9;

    /// <summary>
    /// Characters allowed in an RCON password. Deliberately excludes <c>#</c>, quotes, backtick,
    /// <c>$</c> and whitespace so the value survives both file formats unquoted.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-.!@%^*+=]{8,64}$")]
    private static partial Regex PasswordShape { get; }

    /// <summary>A map id: a plain name or a <c>UGC</c>-prefixed workshop id. No spaces or quotes.</summary>
    [GeneratedRegex(@"^(?:UGC\d{1,20}|[A-Za-z0-9_\-]{1,64})$")]
    private static partial Regex MapIdShape { get; }

    /// <summary>A game-mode token such as <c>SND</c> or <c>GUN</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,32}$")]
    private static partial Regex GameModeShape { get; }

    /// <summary>
    /// An absolute install path. Must START WITH <c>/</c> so it can never be read as an option by
    /// <c>mkdir</c>/<c>chown</c>, and holds only safe path characters.
    /// </summary>
    [GeneratedRegex(@"^/[A-Za-z0-9_./\-]{1,200}$")]
    private static partial Regex InstallDirShape { get; }

    /// <summary>Whether a string is a syntactically valid RCON password for this bot.</summary>
    public static bool IsValidPassword(string? password) =>
        password is not null && PasswordShape.IsMatch(password);

    /// <summary>
    /// Check a spec against the ports, units and install dirs already in use.
    /// </summary>
    /// <param name="spec">The request to validate. Its <see cref="ServerProvisionSpec.UnitName"/>
    /// must already have been checked for systemd plausibility by the caller.</param>
    /// <param name="rconPortsInUse">RCON ports of servers the bot already knows.</param>
    /// <param name="unitsInUse">systemd units already configured.</param>
    /// <param name="installDirsInUse">Install roots already configured.</param>
    /// <returns>Every problem found, empty when the spec is usable.</returns>
    public static IReadOnlyList<string> Check(
        ServerProvisionSpec spec,
        IReadOnlyCollection<int> rconPortsInUse,
        IReadOnlyCollection<string> unitsInUse,
        IReadOnlyCollection<string> installDirsInUse)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rconPortsInUse);
        ArgumentNullException.ThrowIfNull(unitsInUse);
        ArgumentNullException.ThrowIfNull(installDirsInUse);

        var problems = new List<string>();

        // ---- ports ----
        Port(problems, "game port", spec.GamePort);
        Port(problems, "query port", spec.QueryPort);
        Port(problems, "RCON port", spec.RconPort);

        // Distinct from EACH OTHER. Two of the three sharing a number binds two listeners to one
        // socket, which fails at bind time with a message that names neither setting.
        if (spec.GamePort == spec.QueryPort || spec.GamePort == spec.RconPort || spec.QueryPort == spec.RconPort)
            problems.Add("The game, query and RCON ports must all be different from each other.");

        // Only RCON ports are known to the bot; game/query collisions with other servers cannot
        // be seen from here and are the operator's to keep unique (said so in the reply).
        foreach (var port in new[] { spec.GamePort, spec.QueryPort, spec.RconPort })
            if (rconPortsInUse.Contains(port))
                problems.Add($"Port {port} is already an RCON port of another server on this box.");

        // ---- password ----
        if (!IsValidPassword(spec.RconPassword))
            problems.Add(
                "The RCON password must be 8-64 characters from letters, digits and _-.!@%^*+= only " +
                "(no spaces, quotes or #), so it is safe to write unquoted into both the server config and .env.");

        // ---- capacity ----
        if (spec.MaxPlayers is < 1 or > 100)
            problems.Add($"Max players must be between 1 and 100 (got {spec.MaxPlayers}).");

        // ---- server name ----
        if (string.IsNullOrWhiteSpace(spec.ServerName))
            problems.Add("The server name is empty after cleaning - give it a name with letters or digits in it.");

        // ---- maps ----
        if (spec.Maps.Count == 0)
        {
            problems.Add("At least one map is required for the rotation.");
        }
        else
        {
            foreach (var map in spec.Maps)
            {
                if (!MapIdShape.IsMatch(map.MapId))
                    problems.Add($"Map id \"{map.MapId}\" is not usable - use a plain name or a UGC id like UGC1758245796.");
                if (!GameModeShape.IsMatch(map.GameMode))
                    problems.Add($"Game mode \"{map.GameMode}\" is not a usable mode token (e.g. SND, GUN, DM).");
            }
        }

        // ---- install directory ----
        // An absolute, traversal-free path: it becomes an argv element to mkdir/chown/steamcmd,
        // so a leading '-' (an option) or a ".." (an escape from where it was meant to land) is
        // refused before anything privileged runs.
        if (!InstallDirShape.IsMatch(spec.InstallDir) || spec.InstallDir.Contains("..", StringComparison.Ordinal))
            problems.Add($"The install directory \"{spec.InstallDir}\" must be an absolute path (no \"..\") using letters, digits and _-./ only.");

        // ---- collisions with what is already configured ----
        if (unitsInUse.Contains(spec.UnitName, StringComparer.Ordinal))
            problems.Add($"The systemd unit \"{spec.UnitName}\" is already configured for another server.");
        if (installDirsInUse.Contains(spec.InstallDir, StringComparer.Ordinal))
            problems.Add($"The install directory \"{spec.InstallDir}\" is already configured for another server.");

        return problems;
    }

    private static void Port(List<string> problems, string label, int value)
    {
        // The same 1..65535 rule BotOptions.Validate uses, so a provisioned port and a bound one
        // are judged identically.
        if (value is <= 0 or > 65535)
            problems.Add($"The {label} is not a valid port (got {value}); it must be between 1 and 65535.");
    }
}
