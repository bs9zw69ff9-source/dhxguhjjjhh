using System.Globalization;
using Microsoft.Extensions.Configuration;
using PavlovBot.Core.Vpn;
using PavlovBot.Host.Vpn;

namespace PavlovBot.Host.Configuration;

/// <summary>
/// Everything the ported features read from the environment.
/// </summary>
/// <remarks>
/// Separate from <see cref="BotOptions"/> because these settings share one property: an
/// absent one turns a FEATURE off, rather than being a misconfiguration. A bot with no
/// webhook URLs and no VPN keys is a perfectly valid bot that simply does less - so none of
/// these produce a validation failure, and each says so at startup instead.
/// </remarks>
public sealed record FeatureOptions
{
    /// <summary>Where the game writes its per-player balance files. Null disables the economy.</summary>
    public string? LedgerDirectory { get; init; }

    /// <summary>Pavlov.log paths, comma separated. Empty means auto-detect.</summary>
    public string? LogPaths { get; init; }

    public string? JoinWebhook { get; init; }
    public string? KillWebhook { get; init; }
    public string? MoneyWebhook { get; init; }

    public ulong? LeaderboardChannel { get; init; }
    public ulong? ArrestBoardChannel { get; init; }
    public ulong? PlayerListChannel { get; init; }
    public ulong? DashboardChannel { get; init; }

    public VpnKeys VpnKeys { get; init; } = new();
    public VpnThresholds VpnThresholds { get; init; } = VpnThresholds.Default;
    public TimeSpan VpnCacheTtl { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Discord ids that hold owner powers. NOT a role - see <c>Access</c>.</summary>
    public IReadOnlyList<ulong> Owners { get; init; } = [];
    public IReadOnlyList<ulong> SuperOwners { get; init; } = [];

    /// <summary>In-game names that must never be banned by any path.</summary>
    public IReadOnlyList<string> MasterNames { get; init; } = [];

    public TimeSpan LogPollInterval { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan MoneyLogInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LeaderboardInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan BanSweepInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BanExpiryInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan BanReconcileInterval { get; init; } = TimeSpan.FromMinutes(5);

    public static FeatureOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new FeatureOptions
        {
            LedgerDirectory = Text(configuration, "MODSAVE_PATH"),
            LogPaths = Text(configuration, "PAVLOV_LOGS"),

            JoinWebhook = Text(configuration, "CONNECT_WEBHOOK_URL"),
            KillWebhook = Text(configuration, "KILL_WEBHOOK_URL"),
            MoneyWebhook = Text(configuration, "MONEY_WEBHOOK_URL"),

            LeaderboardChannel = Snowflake(configuration, "LEADERBOARD_CHANNEL"),
            ArrestBoardChannel = Snowflake(configuration, "ARREST_LEADERBOARD_CHANNEL"),
            PlayerListChannel = Snowflake(configuration, "PLAYERLIST_CHANNEL"),
            DashboardChannel = Snowflake(configuration, "DASHBOARD_CHANNEL"),

            VpnKeys = new VpnKeys(
                IpHub: Text(configuration, "IPHUB_API_KEY"),
                Ipqs: Text(configuration, "IPQS_API_KEY"),
                ProxyCheck: Text(configuration, "PROXYCHECK_API_KEY"),
                VpnApi: Text(configuration, "VPNAPI_KEY"),
                IpapiIs: Text(configuration, "IPAPIIS_KEY"),
                Sentinel: Text(configuration, "SENTINEL_API_KEY")),

            VpnThresholds = new VpnThresholds(
                Int(configuration, "VPN_SCREEN_MIN", 1),
                Int(configuration, "VPN_CONFIRM_MIN", 1),
                Int(configuration, "VPN_SCREEN_BAN_MIN", 2)),

            VpnCacheTtl = TimeSpan.FromDays(Int(configuration, "VPN_CACHE_TTL_DAYS", 30)),

            Owners = Snowflakes(configuration, "OWNER_IDS"),
            SuperOwners = Snowflakes(configuration, "SUPER_OWNER_IDS"),
            MasterNames = List(configuration, "MASTER_NAMES"),

            MoneyLogInterval = Milliseconds(configuration, "MONEY_LOG_INTERVAL_MS", TimeSpan.FromSeconds(10)),
            LeaderboardInterval = Milliseconds(configuration, "LEADERBOARD_INTERVAL_MS", TimeSpan.FromMinutes(5)),
        };
    }

    private static string? Text(IConfiguration configuration, string key) =>
        configuration[key]?.Trim() is { Length: > 0 } value ? value : null;

    private static int Int(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private static ulong? Snowflake(IConfiguration configuration, string key) =>
        ulong.TryParse(configuration[key]?.Trim(), CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    private static IReadOnlyList<ulong> Snowflakes(IConfiguration configuration, string key) =>
        List(configuration, key)
            .Select(v => ulong.TryParse(v, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

    private static IReadOnlyList<string> List(IConfiguration configuration, string key) =>
        (configuration[key] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static TimeSpan Milliseconds(IConfiguration configuration, string key, TimeSpan fallback) =>
        double.TryParse(configuration[key], CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : fallback;

    /// <summary>
    /// Which features are on, in one line each.
    /// </summary>
    /// <remarks>
    /// Printed at startup because a feature that is off for want of one environment
    /// variable is otherwise indistinguishable from one that is broken - and the two get
    /// debugged very differently.
    /// </remarks>
    public IReadOnlyList<string> Describe() =>
    [
        $"economy: {(LedgerDirectory is null ? "off (MODSAVE_PATH not set)" : LedgerDirectory)}",
        $"join feed: {(JoinWebhook is null ? "off" : "on")}",
        $"kill feed: {(KillWebhook is null ? "off" : "on")}",
        $"money feed: {(MoneyWebhook is null ? "off" : "on")}",
        $"leaderboard: {(LeaderboardChannel is null ? "off" : $"channel {LeaderboardChannel}")}",
        $"owners: {Owners.Count + SuperOwners.Count} configured",
        $"master names: {(MasterNames.Count == 0 ? "none - NOTHING is protected from an auto-ban" : string.Join(", ", MasterNames))}",
    ];
}
