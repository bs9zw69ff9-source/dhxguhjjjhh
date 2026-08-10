using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Plugins;

/// <summary>What a plugin has asked to be able to reach.</summary>
/// <remarks>
/// SCOPES NAME CAPABILITIES, NOT TYPES. A plugin declares "I read player data", not "I want
/// IpTrackingService" - so adding a class to a capability later does not need every plugin
/// re-declaring, and an operator reading a plugin's manifest sees what it can DO rather than
/// a list of internal names that mean nothing to them.
/// </remarks>
[Flags]
public enum PluginPermission
{
    None = 0,

    /// <summary>Read player identity, playtime and history.</summary>
    PlayerRead = 1 << 0,

    /// <summary>Read addresses and network intelligence. The most sensitive scope there is.</summary>
    PlayerNetwork = 1 << 1,

    /// <summary>Read bans, warnings and cases.</summary>
    ModerationRead = 1 << 2,

    /// <summary>Issue bans, warnings and other punishments.</summary>
    ModerationWrite = 1 << 3,

    /// <summary>Read balances and payroll.</summary>
    EconomyRead = 1 << 4,

    /// <summary>Move money.</summary>
    EconomyWrite = 1 << 5,

    /// <summary>Send arbitrary RCON commands.</summary>
    RconExecute = 1 << 6,

    /// <summary>Read the event timeline.</summary>
    TimelineRead = 1 << 7,
}

/// <summary>
/// A service provider that only hands a plugin what its declared scopes allow.
/// </summary>
/// <remarks>
/// REAL ENFORCEMENT, NOT A DECLARATION. The previous design gave every plugin the host's whole
/// IServiceProvider in InitializeAsync, so a "permissions" field would have been documentation
/// that nothing checked - a plugin declaring PlayerRead could resolve BanService and ban
/// people. This refuses.
///
/// WHAT IT DOES NOT DEFEND AGAINST, said plainly because a security control whose limits are
/// undocumented gets trusted past them: a plugin runs IN THIS PROCESS. It can use reflection,
/// walk static state, or load its own copy of anything. The gate stops honest mistakes and
/// casual overreach - a plugin quietly using a service it never declared - and it stops the
/// manifest from being a lie. It does not contain a hostile plugin, and nothing in-process
/// can. The defence against a hostile plugin is not installing it, which is why installation
/// is an operator action through .env rather than anything the bot can do to itself.
///
/// UNKNOWN TYPES ARE REFUSED, not passed through. An allow-list that defaults to allow is not
/// an allow-list, and a service added next year would otherwise be reachable by every plugin
/// ever written without anybody deciding it should be.
/// </remarks>
public sealed class ScopedPluginServices(
    IServiceProvider inner,
    string plugin,
    PluginPermission granted,
    ILogger logger) : IServiceProvider
{
    /// <summary>
    /// Which scope each reachable service needs.
    /// </summary>
    /// <remarks>
    /// BY NAME rather than by typeof, so this table does not force Host to reference every
    /// assembly a service might live in, and so a plugin cannot smuggle a type through by
    /// implementing an interface the table matches on.
    /// </remarks>
    private static readonly Dictionary<string, PluginPermission> Required = new(StringComparer.Ordinal)
    {
        ["PavlovBot.Host.Intelligence.PlayerIntelligenceService"] = PluginPermission.PlayerRead,
        ["PavlovBot.Host.Discord.Boards"] = PluginPermission.PlayerRead,
        ["PavlovBot.Host.Logs.IpTrackingService"] = PluginPermission.PlayerNetwork,
        ["PavlovBot.Host.Vpn.VpnScreeningService"] = PluginPermission.PlayerNetwork,
        ["PavlovBot.Host.Moderation.BanService"] = PluginPermission.ModerationWrite,
        ["PavlovBot.Host.Moderation.WarningService"] = PluginPermission.ModerationWrite,
        ["PavlovBot.Host.Cases.CaseService"] = PluginPermission.ModerationRead,
        ["PavlovBot.Host.Moderation.AuditLog"] = PluginPermission.ModerationRead,
        ["PavlovBot.Core.Economy.Ledger"] = PluginPermission.EconomyWrite,
        ["PavlovBot.Core.Economy.IBalanceStore"] = PluginPermission.EconomyRead,
        ["PavlovBot.Host.Economy.Payroll"] = PluginPermission.EconomyRead,
        ["PavlovBot.Host.Rcon.RconRegistry"] = PluginPermission.RconExecute,
        ["PavlovBot.Host.Events.IEventStore"] = PluginPermission.TimelineRead,
        ["PavlovBot.Host.Events.AnalyticsService"] = PluginPermission.TimelineRead,
    };

    /// <summary>
    /// Services any plugin may have, because they grant no authority over anything.
    /// </summary>
    /// <remarks>
    /// A logger and a clock. Without these every plugin would have to declare a scope to write
    /// a log line, which teaches authors to ask for scopes they do not need - the exact habit
    /// that makes a permission model worthless.
    /// </remarks>
    private static readonly HashSet<string> Harmless = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.Logging.ILoggerFactory",
        "System.TimeProvider",
    };

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        var name = serviceType.FullName ?? serviceType.Name;

        // Generic loggers - ILogger<TPlugin> - are harmless whatever they are closed over.
        if (Harmless.Contains(name) || name.StartsWith("Microsoft.Extensions.Logging.ILogger", StringComparison.Ordinal))
            return inner.GetService(serviceType);

        if (!Required.TryGetValue(name, out var needed))
        {
            logger.LogWarning(
                "Plugin {Plugin} asked for {Service}, which is not offered to plugins at all. " +
                "Refused - the plugin surface is an allow-list, so a service is reachable only " +
                "once somebody decides which scope it belongs to", plugin, name);
            return null;
        }

        if (!granted.HasFlag(needed))
        {
            logger.LogWarning(
                "Plugin {Plugin} asked for {Service} without declaring {Needed}. Refused - it " +
                "declared {Granted}", plugin, name, needed, granted);
            return null;
        }

        return inner.GetService(serviceType);
    }

    /// <summary>Every scope, for the manifest display.</summary>
    public static IReadOnlyList<PluginPermission> AllScopes =>
        [.. Enum.GetValues<PluginPermission>().Where(p => p != PluginPermission.None)];

    /// <summary>Scopes as a readable list, for logs and /plugins.</summary>
    public static string Describe(PluginPermission permission) =>
        permission == PluginPermission.None
            ? "none"
            : string.Join(", ", AllScopes.Where(p => permission.HasFlag(p)).Select(Slug));

    /// <summary>The dotted name used in documentation and config.</summary>
    public static string Slug(PluginPermission permission) => permission switch
    {
        PluginPermission.PlayerRead => "plugin.player.read",
        PluginPermission.PlayerNetwork => "plugin.player.network",
        PluginPermission.ModerationRead => "plugin.moderation.read",
        PluginPermission.ModerationWrite => "plugin.moderation.write",
        PluginPermission.EconomyRead => "plugin.economy.read",
        PluginPermission.EconomyWrite => "plugin.economy.write",
        PluginPermission.RconExecute => "plugin.rcon.execute",
        PluginPermission.TimelineRead => "plugin.timeline.read",
        _ => permission.ToString().ToLowerInvariant(),
    };
}
