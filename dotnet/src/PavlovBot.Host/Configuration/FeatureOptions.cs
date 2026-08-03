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

    /// <summary>Where the game keeps its whitelist .txt rosters. Null disables the faction commands.</summary>
    public string? RosterDirectory { get; init; }

    /// <summary>
    /// systemd units for the game servers, in server order. See <c>ServiceControl</c>.
    /// </summary>
    /// <remarks>
    /// Configuration rather than a constant because unit names are a property of the box,
    /// not of the bot - and this string ends up as an argument to a privileged command, so
    /// it is validated on the way in rather than trusted for being in .env.
    /// </remarks>
    public IReadOnlyList<string> PavlovUnits { get; init; } = [];

    /// <summary>
    /// Force sudo on or off. Null - the default - detects whether the bot is already root.
    /// </summary>
    /// <remarks>
    /// systemctl has to run as root either way; this is only about HOW. Left unset, the bot
    /// calls systemctl directly when it is root and goes through <c>sudo -n</c> when it is
    /// not, which is right for both deployment shapes without anyone declaring one.
    /// </remarks>
    public bool? SystemctlSudo { get; init; }

    /// <summary>The game's own ban-list file - the message a banned player sees.</summary>
    public string? ModsaveBanlistPath { get; init; }

    /// <summary>Where plugin assemblies live. Null uses ./plugins.</summary>
    public string? PluginDirectory { get; init; }

    /// <summary>Plugins to load. Empty means all of them - a list is how a crashing
    /// plugin is disabled without deleting the file.</summary>
    public IReadOnlyList<string> EnabledPlugins { get; init; } = [];

    public string? JoinWebhook { get; init; }
    public string? KillWebhook { get; init; }
    public string? MoneyWebhook { get; init; }

    /// <summary>The address-bearing connection feed. Private channels only.</summary>
    public string? ConnectWebhook { get; init; }

    public ulong? LeaderboardChannel { get; init; }
    public ulong? ArrestBoardChannel { get; init; }
    /// <summary>
    /// Voice channels renamed to each server's live player count, in server order.
    /// </summary>
    /// <remarks>
    /// Replaced the player-list board. A voice channel is readable from the sidebar without
    /// opening anything, and unlike the board it publishes no player NAMES to everybody who
    /// can see the channel list.
    /// </remarks>
    public IReadOnlyList<ulong> PlayerCountChannels { get; init; } = [];

    /// <summary>A voice channel for the platform-wide total, from the Pavlov master server.</summary>
    public ulong? ShackTotalChannel { get; init; }

    /// <summary>Public channel holding the Verify button.</summary>
    public ulong? VerifyChannel { get; init; }

    /// <summary>Private staff channel that receives accept/deny requests.</summary>
    public ulong? VerifyStaffChannel { get; init; }

    /// <summary>Role granted on approval. Without it approval records the link and grants nothing.</summary>
    public ulong? VerifiedRole { get; init; }

    /// <summary>Channel holding the self-serve "Get Menu" panel.</summary>
    public ulong? MenuPanelChannel { get; init; }

    /// <summary>Roles that decide WHICH menu a claimer gets, and who is barred outright.</summary>
    public ulong? MenuRoleStaff { get; init; }
    public ulong? MenuRoleHighStaff { get; init; }
    public ulong? MenuRoleBlacklist { get; init; }

    public VpnKeys VpnKeys { get; init; } = new();
    public VpnThresholds VpnThresholds { get; init; } = VpnThresholds.Default;
    public TimeSpan VpnCacheTtl { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Geoapify key. Deliberately NOT part of <see cref="VpnKeys"/>: those are reputation
    /// providers that vote on whether an address is a VPN, and this one only says where an
    /// address is. Putting it there would let it be counted as a detector.
    /// </summary>
    public string? GeoapifyKey { get; init; }

    /// <summary>Discord ids that hold owner powers. NOT a role - see <c>Access</c>.</summary>
    public IReadOnlyList<ulong> Owners { get; init; } = [];
    public IReadOnlyList<ulong> SuperOwners { get; init; } = [];

    /// <summary>In-game names that must never be banned by any path.</summary>
    public IReadOnlyList<string> MasterNames { get; init; } = [];

    /// <summary>
    /// The Pavlov build the public server browser asks about. Null uses the built-in default.
    /// </summary>
    /// <remarks>
    /// Vankrupt put the version in the URL PATH rather than in the response, so a game update
    /// makes the master server return an empty list instead of an error. That is
    /// indistinguishable from "no servers are online" unless somebody can change the version
    /// without a deploy - which is what this setting is for.
    /// </remarks>
    public string? PavlovVersion { get; init; }

    public TimeSpan LogPollInterval { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan MoneyLogInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>
    /// How often the boards redraw. Also how long after a restart the first one appears.
    /// </summary>
    /// <remarks>
    /// A minute, not the five it was. Boards do not run on start - that would post to
    /// Discord before the gateway has finished connecting - so this interval IS the delay
    /// before anything shows up after a deploy. At five minutes a restart looked exactly
    /// like a broken board for long enough to go looking for the bug.
    ///
    /// The Node bot redraws every 30 seconds. A minute is close to that and costs one
    /// message edit, plus a directory read for the cash board.
    /// </remarks>
    public TimeSpan LeaderboardInterval { get; init; } = TimeSpan.FromMinutes(1);
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
            PavlovVersion = Text(configuration, "PAVLOV_VERSION"),
            RosterDirectory = Text(configuration, "FACTION_ROLES_PATH"),
            PavlovUnits = PavlovBot.Host.Servers.ServiceControl.ParseUnits(Text(configuration, "PAVLOV_UNITS")),
            SystemctlSudo = OptionalFlag(configuration, "PAVLOV_SYSTEMCTL_SUDO"),
            /* Resolved the way the Node bot resolves it: an explicit override first, then
               derived from the server install root. It was previously built as
               <MODSAVE_PATH>/ModSave/banlist.txt - but MODSAVE_PATH already points AT the
               ModSave directory, so that was a doubled path that does not exist, and
               MODSAVE_BLACKLIST_PATH was ignored entirely. The ban-message file was
               therefore written somewhere the game never reads. */
            ModsaveBanlistPath = Text(configuration, "MODSAVE_BLACKLIST_PATH")
                ?? System.IO.Path.Combine(
                    Text(configuration, "PAVLOV_BASE_1") ?? "/home/steam/pavlovserver",
                    "Pavlov", "Saved", "Config", "ModSave", "banlist.txt"),

            /* Two DIFFERENT webhooks. CONNECT carries addresses and belongs in a private
               channel; JOIN is the plain public log. The port read CONNECT into the join
               feed and never read JOIN_WEBHOOK_URL at all. */
            ConnectWebhook = Text(configuration, "CONNECT_WEBHOOK_URL"),
            JoinWebhook = Text(configuration, "JOIN_WEBHOOK_URL"),
            KillWebhook = Text(configuration, "KILL_WEBHOOK_URL"),
            MoneyWebhook = Text(configuration, "MONEY_WEBHOOK_URL"),

            LeaderboardChannel = Snowflake(configuration, "LEADERBOARD_CHANNEL"),
            ArrestBoardChannel = Snowflake(configuration, "ARREST_LEADERBOARD_CHANNEL"),
            PlayerCountChannels = Snowflakes(configuration, "PLAYER_COUNT_CHANNELS"),
            ShackTotalChannel = Snowflake(configuration, "SHACK_TOTAL_CHANNEL"),
            VerifyChannel = Snowflake(configuration, "VERIFY_CHANNEL"),
            VerifyStaffChannel = Snowflake(configuration, "VERIFY_STAFF_CHANNEL"),
            VerifiedRole = Snowflake(configuration, "VERIFIED_ROLE"),
            MenuPanelChannel = Snowflake(configuration, "MENU_PANEL_CHANNEL"),
            MenuRoleStaff = Snowflake(configuration, "MENU_ROLE_STAFF"),
            MenuRoleHighStaff = Snowflake(configuration, "MENU_ROLE_HIGHSTAFF"),
            MenuRoleBlacklist = Snowflake(configuration, "MENU_ROLE_BLACKLIST"),

            VpnKeys = new VpnKeys(
                IpHub: Text(configuration, "IPHUB_API_KEY"),
                Ipqs: Text(configuration, "IPQS_API_KEY"),
                ProxyCheck: Text(configuration, "PROXYCHECK_API_KEY"),
                VpnApi: Text(configuration, "VPNAPI_KEY"),
                IpapiIs: Text(configuration, "IPAPIIS_KEY"),
                Sentinel: Text(configuration, "SENTINEL_API_KEY")),

            GeoapifyKey = Text(configuration, "GEOAPIFY_API_KEY"),

            /* VPN_BAN_MIN is the current name; VPN_SCREEN_BAN_MIN is what it used to be
               called, kept working so an existing .env is not silently reset to the default
               the first time somebody deploys this. */
            VpnThresholds = new VpnThresholds(
                Int(configuration, "VPN_SCREEN_MIN", 1),
                Int(configuration, "VPN_CONFIRM_MIN", 1),
                Int(configuration, "VPN_BAN_MIN", Int(configuration, "VPN_SCREEN_BAN_MIN", 2))),

            VpnCacheTtl = TimeSpan.FromDays(Int(configuration, "VPN_CACHE_TTL_DAYS", 30)),

            Owners = Snowflakes(configuration, "OWNER_IDS"),
            SuperOwners = Snowflakes(configuration, "SUPER_OWNER_IDS"),
            MasterNames = List(configuration, "MASTER_NAMES"),
            PluginDirectory = Text(configuration, "PLUGIN_DIR"),
            EnabledPlugins = List(configuration, "PLUGINS_ENABLED"),

            MoneyLogInterval = Milliseconds(configuration, "MONEY_LOG_INTERVAL_MS", TimeSpan.FromSeconds(10)),
            LeaderboardInterval = Milliseconds(configuration, "LEADERBOARD_INTERVAL_MS", TimeSpan.FromMinutes(1)),
        };
    }

    private static string? Text(IConfiguration configuration, string key) =>
        configuration[key]?.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>
    /// An opt-in switch. Anything that is not affirmative is off.
    /// </summary>
    /// <remarks>
    /// "1", "yes" and "on" are accepted alongside "true" because a .env is written by hand
    /// and a switch that silently ignores <c>=1</c> is a switch somebody spends an hour on.
    /// </remarks>
    private static bool Flag(IConfiguration configuration, string key) => OptionalFlag(configuration, key) == true;

    /// <summary>The same switch, but "unset" is distinguishable from "off".</summary>
    /// <remarks>
    /// The distinction matters wherever the default is DETECTED rather than fixed - forcing
    /// a behaviour off and never having asked for it are different intentions, and a plain
    /// bool cannot tell them apart.
    /// </remarks>
    private static bool? OptionalFlag(IConfiguration configuration, string key) =>
        Text(configuration, key)?.ToLowerInvariant() switch
        {
            null => null,
            "1" or "true" or "yes" or "on" => true,
            _ => false,
        };

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
        $"whitelists: {(RosterDirectory is null ? "off (FACTION_ROLES_PATH not set)" : RosterDirectory)}",
        $"systemd units: {string.Join(", ", PavlovUnits)}",
        $"join feed: {(JoinWebhook is null ? "off" : "on")}",
        $"kill feed: {(KillWebhook is null ? "off" : "on")}",
        $"money feed: {(MoneyWebhook is null ? "off" : "on")}",
        $"cash leaderboard: {(LeaderboardChannel is null ? "off (LEADERBOARD_CHANNEL not set)" : $"channel {LeaderboardChannel}, every {LeaderboardInterval.TotalSeconds:0}s")}",
        $"arrest board: {(ArrestBoardChannel is null ? "off (ARREST_LEADERBOARD_CHANNEL not set)" : $"channel {ArrestBoardChannel}")}",
        $"player-count channels: {(PlayerCountChannels.Count == 0 ? "off (PLAYER_COUNT_CHANNELS not set)" : $"{PlayerCountChannels.Count} configured")}",
        $"shack total channel: {(ShackTotalChannel is null ? "off (SHACK_TOTAL_CHANNEL not set)" : $"channel {ShackTotalChannel}")}",
        $"connect feed: {(ConnectWebhook is null ? "off (CONNECT_WEBHOOK_URL not set)" : "on")}",
        $"menu panel: {(MenuPanelChannel is null
            ? "off (MENU_PANEL_CHANNEL not set)"
            : MenuRoleStaff is null && MenuRoleHighStaff is null
                ? $"channel {MenuPanelChannel} - NO MENU_ROLE_STAFF/HIGHSTAFF, nobody qualifies"
                : $"channel {MenuPanelChannel}")}",
        $"verification: {(VerifyChannel is null || VerifyStaffChannel is null
            ? "off (needs VERIFY_CHANNEL and VERIFY_STAFF_CHANNEL)"
            : $"panel in {VerifyChannel}, requests to {VerifyStaffChannel}" +
              (VerifiedRole is null ? " - NO VERIFIED_ROLE, approval grants nothing" : $", grants role {VerifiedRole}"))}",
        $"owners: {Owners.Count + SuperOwners.Count} configured",
        $"master names: {(MasterNames.Count == 0 ? "none - NOTHING is protected from an auto-ban" : string.Join(", ", MasterNames))}",
    ];
}
