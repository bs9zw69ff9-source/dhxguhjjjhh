namespace PavlovBot.Core.Provisioning;

/// <summary>One entry in a Pavlov map rotation.</summary>
/// <param name="MapId">
/// A built-in name (<c>datacenter</c>) or a workshop id with its <c>UGC</c> prefix
/// (<c>UGC1758245796</c>). The prefix is the game's, not ours - a bare mod.io number will not
/// load, so it is required by <see cref="ProvisionValidation"/> rather than added here.
/// </param>
/// <param name="GameMode">The mode token: <c>SND</c>, <c>GUN</c>, <c>DM</c>, and so on.</param>
public sealed record MapEntry(string MapId, string GameMode);

/// <summary>
/// A validated request to stand up one new Pavlov dedicated server.
/// </summary>
/// <remarks>
/// A plain data carrier: every field here becomes either a line in a config file the game
/// reads or an argument to a privileged command, so nothing is computed in the constructor and
/// nothing is trusted until <see cref="ProvisionValidation.Check"/> has passed. The command
/// builds one of these from Discord options, validates it, and only then hands it to the
/// provisioner. Kept in <c>PavlovBot.Core</c> because it is pure data with no OS dependency.
/// </remarks>
public sealed record ServerProvisionSpec
{
    /// <summary>The 1-based server number this becomes for the bot: <c>server{Slot}</c>.</summary>
    public required int Slot { get; init; }

    /// <summary>The systemd unit name, e.g. <c>pavlovserver1</c>. Validated as a plausible unit.</summary>
    public required string UnitName { get; init; }

    /// <summary>The install root, e.g. <c>/home/steam/pavlovserver1</c>.</summary>
    public required string InstallDir { get; init; }

    /// <summary>The public server name shown in the browser. Cleaned of quotes/newlines before use.</summary>
    public required string ServerName { get; init; }

    public required int GamePort { get; init; }
    public required int QueryPort { get; init; }
    public required int RconPort { get; init; }

    /// <summary>The RCON password. Constrained to a charset that is safe unquoted in both files.</summary>
    public required string RconPassword { get; init; }

    public required int MaxPlayers { get; init; }

    /// <summary>The map rotation, lowest priority first. Never empty after validation.</summary>
    public required IReadOnlyList<MapEntry> Maps { get; init; }

    /// <summary>Optional join password for the game itself. Null or empty means an open server.</summary>
    public string? GamePassword { get; init; }
}
