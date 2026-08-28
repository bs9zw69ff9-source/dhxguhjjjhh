using System.Globalization;
using System.Text;

namespace PavlovBot.Core.Provisioning;

/// <summary>
/// Every file the provisioner writes, generated as an exact string.
/// </summary>
/// <remarks>
/// Pure text, no I/O, so the format is pinned by golden-string tests rather than discovered on
/// a live box. The provisioner (in the host) does the privileged writing; this only decides what
/// goes in. Lines end in <c>\n</c> - these are Linux server files and the bot's own <c>.env</c>.
/// </remarks>
public static class ProvisionText
{
    /// <summary>
    /// <c>RconSettings.txt</c>: the two lines the game reads to enable and secure RCON.
    /// </summary>
    public static string RconSettings(string password, int rconPort)
    {
        // The password is charset-checked by ProvisionValidation, so it is safe on one line
        // unquoted - which is exactly the format the game expects here.
        return $"Password={password}\nPort={rconPort.ToString(CultureInfo.InvariantCulture)}\n";
    }

    /// <summary>
    /// <c>LinuxServer/Game.ini</c>: the dedicated-server section with name, capacity and rotation.
    /// </summary>
    /// <summary>
    /// The mod every server on this network loads, from the working config this was taken from.
    /// </summary>
    /// <remarks>
    /// One constant rather than scattered through the template, because it is the line most
    /// likely to change and the one an operator will come looking for.
    /// </remarks>
    public const string AdditionalMods = "UGC3462586";

    public static string GameIni(ServerProvisionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var sb = new StringBuilder();
        sb.Append("[/Script/Pavlov.DedicatedServer]\n");
        sb.Append("bEnabled=true\n");

        /* UNQUOTED, unlike the wiki's example. This follows the config that is actually running:
           quoting made the quotes part of the name in the browser. */
        sb.Append(CultureInfo.InvariantCulture, $"ServerName={CleanServerName(spec.ServerName)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"MaxPlayers={spec.MaxPlayers.ToString(CultureInfo.InvariantCulture)}\n");
        sb.Append("bSecured=true\n");
        sb.Append("bCustomServer=true\n");

        /* NOT COSMETIC. The kill feed parses lines the game only writes with verbose logging on -
           see PavlovLogLine - so a server provisioned without this looks healthy and silently
           produces no kills. */
        sb.Append("bVerboseLogging=true\n");

        sb.Append("bEnableBots=false\n");
        sb.Append("bCompetitive=false #This only works for SND\n");
        sb.Append("bWhitelist=false\n");
        sb.Append("RefreshListTime=120\n");
        sb.Append("LimitedAmmoType=0\n");
        sb.Append("TickRate=60\n");
        sb.Append("TimeLimit=60\n");
        sb.Append("AFKTimeLimit=300\n");

        /* COMMENTED OUT WHEN THERE IS NO PASSWORD, rather than omitted. An empty Password line
           reads as "the password is the empty string" and locks everyone out; leaving the
           commented example is what the working config does and shows where to put one. */
        sb.Append(!string.IsNullOrEmpty(spec.GamePassword)
            ? string.Create(CultureInfo.InvariantCulture, $"Password={spec.GamePassword}\n")
            : "#Password=0000\n");

        sb.Append("#BalanceTableURL=\"vankruptgames/BalancingTable/main\"\n");

        foreach (var map in spec.Maps)
            sb.Append(CultureInfo.InvariantCulture, $"MapRotation=(MapId=\"{map.MapId}\", GameMode=\"{map.GameMode}\")\n");

        sb.Append(CultureInfo.InvariantCulture, $"AdditionalMods={AdditionalMods}\n");

        return sb.ToString();
    }

    /// <summary>
    /// The systemd unit that runs the server as the <c>steam</c> user and restarts it if it dies.
    /// </summary>
    public static string SystemdUnit(ServerProvisionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var dir = spec.InstallDir.TrimEnd('/');
        var port = spec.GamePort.ToString(CultureInfo.InvariantCulture);

        return
            "[Unit]\n" +
            $"Description=Pavlov VR Dedicated Server ({spec.UnitName})\n" +
            "After=network-online.target\n" +
            "Wants=network-online.target\n" +
            "\n" +
            "[Service]\n" +
            "Type=simple\n" +
            "User=steam\n" +
            $"WorkingDirectory={dir}\n" +
            $"ExecStart={dir}/PavlovServer.sh -PORT={port}\n" +
            "Restart=on-failure\n" +
            "RestartSec=5\n" +
            "\n" +
            "[Install]\n" +
            "WantedBy=multi-user.target\n";
    }

    /// <summary>
    /// The labelled block appended to the bot's <c>.env</c> to wire the new server in.
    /// </summary>
    /// <remarks>
    /// APPEND-ONLY, LAST-KEY-WINS. The bot reads <c>.env</c> with dotenv semantics where a later
    /// line for a key overrides an earlier one, so the safe way to change configuration is to add
    /// a block at the end rather than edit lines in place (the same pattern SECOND-BOT.md uses for
    /// a second bot). The RCON triple is added as three INDEXED keys, which need no rewrite; the
    /// positional lists are rewritten IN FULL because a positional list only has one meaning as a
    /// whole value.
    /// </remarks>
    public static string EnvWithServer(
        string existing,
        ServerProvisionSpec spec,
        string rconHost,
        IReadOnlyList<string> finalUnits,
        IReadOnlyList<string> finalBases,
        IReadOnlyList<string>? finalPlayerCountChannels,
        DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(finalUnits);
        ArgumentNullException.ThrowIfNull(finalBases);

        var values = new List<KeyValuePair<string, string?>>
        {
            new($"RCON_HOST_{spec.Slot}", rconHost),
            new($"RCON_PORT_{spec.Slot}", spec.RconPort.ToString(CultureInfo.InvariantCulture)),
            new($"RCON_PASSWORD_{spec.Slot}", spec.RconPassword),
            new("PAVLOV_UNITS", string.Join(",", finalUnits)),
            new("PAVLOV_BASES", string.Join(",", finalBases)),
        };

        // Only when the operator supplied a channel for this server, so an untouched
        // PLAYER_COUNT_CHANNELS is neither blanked nor rewritten.
        if (finalPlayerCountChannels is { Count: > 0 })
            values.Add(new("PLAYER_COUNT_CHANNELS", string.Join(",", finalPlayerCountChannels)));

        return EnvFileEditor.Set(existing, values,
            EnvFileEditor.Note($"server {spec.Slot} ({spec.UnitName}) provisioned", date));
    }

    /// <summary>
    /// The appended block that takes a server back OUT of the bot's <c>.env</c>.
    /// </summary>
    /// <remarks>
    /// STILL APPEND-ONLY. The file is read last-key-wins, so removing a setting means appending an
    /// EMPTY value for it rather than deleting the line it was on - an appended blank clears what
    /// was inherited above, which is the same rule a second bot's overrides rely on. Editing the
    /// original lines out would work too and is far easier to get wrong; this way the file only
    /// ever grows, and its history stays readable.
    ///
    /// The positional lists are rewritten whole, as they are when adding, because a positional
    /// list only has one meaning as a complete value.
    /// </remarks>
    public static string EnvWithoutServer(
        string existing,
        int slot,
        IReadOnlyList<string> finalUnits,
        IReadOnlyList<string> finalBases,
        DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(finalUnits);
        ArgumentNullException.ThrowIfNull(finalBases);

        /* NULL MEANS GONE, not blank. Writing an empty override cleared the setting but left the
           deleted server's host sitting in the file - and after a few provision/delete cycles the
           slot had a stack of them. Removing the lines outright is what "wipe every mention"
           means, and it is also the only version an operator can read. */
        var values = new List<KeyValuePair<string, string?>>
        {
            new($"RCON_HOST_{slot}", null),
            new($"RCON_PORT_{slot}", null),
            new($"RCON_PASSWORD_{slot}", null),
            new("PAVLOV_UNITS", string.Join(",", finalUnits)),
            new("PAVLOV_BASES", string.Join(",", finalBases)),
        };

        return EnvFileEditor.Set(existing, values, EnvFileEditor.Note($"server {slot} removed", date));
    }

    /// <summary>
    /// A server name reduced to something safe inside a quoted INI value on one line.
    /// </summary>
    /// <remarks>
    /// The only characters that MUST go are the double quote (it would close the value early) and
    /// newlines (they would split the line). Everything else a player might want in a name is
    /// kept; the length is capped so a pasted essay cannot run the line away.
    /// </remarks>
    public static string CleanServerName(string? raw)
    {
        var text = (raw ?? "")
            .Replace("\"", "", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        text = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return text.Length > 63 ? text[..63].Trim() : text;
    }
}
