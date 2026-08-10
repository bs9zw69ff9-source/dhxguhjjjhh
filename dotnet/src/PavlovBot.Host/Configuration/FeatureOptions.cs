using System.Globalization;
using Microsoft.Extensions.Configuration;
using PavlovBot.Core.Security;
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

    /// <summary>
    /// Plugins to refuse, by name. Wins over <see cref="EnabledPlugins"/>.
    /// </summary>
    /// <remarks>
    /// A DENY-LIST AS WELL AS AN ALLOW-LIST, because they answer different questions. The
    /// allow-list says "run exactly these", which is right for a curated deployment. The
    /// deny-list says "not that one", which is what an operator reaches for at 3am when one
    /// plugin is misbehaving - and it must work whether or not they also maintain an
    /// allow-list, or the answer at that moment is "I disabled it and it kept loading".
    ///
    /// PLUGINS ARE ENABLED AND DISABLED THROUGH CONFIGURATION, NOT AT RUNTIME, and that is a
    /// deliberate limit rather than a missing feature. A plugin is loaded into the host's
    /// AssemblyLoadContext and .NET cannot unload it, so a /plugin disable command could stop
    /// a plugin's WORK but could not unload its code, leave its event subscriptions, or free
    /// what it holds. A button that says disable and half-disables is worse than no button.
    /// </remarks>
    public IReadOnlyList<string> DisabledPlugins { get; init; } = [];

    public string? JoinWebhook { get; init; }
    public string? KillWebhook { get; init; }
    public string? MoneyWebhook { get; init; }

    /// <summary>The address-bearing connection feed. Private channels only.</summary>
    public string? ConnectWebhook { get; init; }

    /// <summary>
    /// Staff actions as they happen. A private staff channel - these name the staffer,
    /// the target and the reason.
    /// </summary>
    public string? StaffWebhook { get; init; }

    /// <summary>
    /// Channel for staff actions, by id. Works alongside <see cref="StaffWebhook"/>.
    /// </summary>
    /// <remarks>
    /// Separate from the ban channel even when both point at the same place, so splitting
    /// them later is a config change rather than a code change.
    /// </remarks>
    public ulong? ModLogChannel { get; init; }

    /// <summary>Channel for bans, unbans and automatic bans. Falls back to the mod log.</summary>
    public ulong? BanLogChannel { get; init; }

    /// <summary>Arrests, warrants, sentence releases and rank suspensions.</summary>
    public ulong? PoliceLogChannel { get; init; }

    /// <summary>Overrides <see cref="PoliceLogChannel"/> for arrest bookings only.</summary>
    public ulong? ArrestChannel { get; init; }

    public ulong? LeaderboardChannel { get; init; }
    public ulong? ArrestBoardChannel { get; init; }

    /// <summary>Where the live warrant board lives. Its own channel - it is a work queue, not a leaderboard.</summary>
    public ulong? WarrantBoardChannel { get; init; }

    // ---- payroll ----

    /// <summary>Paid to each on-duty member per period. Zero disables payroll entirely.</summary>
    public long PayrollAmount { get; init; }

    /// <summary>How often wages are paid. One period is paid per run, never a backlog.</summary>
    public TimeSpan PayrollInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long timeline events are kept. Zero switches the timeline off entirely.
    /// </summary>
    /// <remarks>
    /// RETENTION IS NOT OPTIONAL at any real scale. A busy server produces tens of thousands
    /// of joins a month, and a table nothing prunes becomes the largest thing in the database
    /// and the slowest thing to query. Ninety days covers "what happened last quarter" - the
    /// longest question anybody actually asks of a timeline - and stays small.
    /// </remarks>
    public int EventRetentionDays { get; init; } = 90;

    /// <summary>Which faction draws a wage. NYPD by default - they are the ones on duty.</summary>
    public string PayrollFaction { get; init; } = "NYPD";

    // ---- money anomaly detection ----

    /// <summary>Earnings inside the window above which a player is flagged. Zero disables it.</summary>
    public long MoneyAlertThreshold { get; init; }

    /// <summary>The window those earnings are summed over.</summary>
    public TimeSpan MoneyAlertWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether a crashed server is restarted automatically. Off unless asked for.</summary>
    public bool CrashRecovery { get; init; }
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

    /// <summary>
    /// The capacity shown when a server has never reported one. Pavlov Shack servers are 24.
    /// </summary>
    /// <remarks>
    /// A last resort only - a capacity read live from the server wins, and the last one it
    /// successfully reported wins over this. Configurable rather than a constant because a
    /// wrong denominator is worse than a missing one, and 24 is only right for the usual
    /// Shack build.
    /// </remarks>
    public int DefaultServerCapacity { get; init; } = 24;

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
            StaffWebhook = Text(configuration, "STAFF_WEBHOOK_URL"),
            ModLogChannel = Snowflake(configuration, "MOD_LOG_CHANNEL"),
            BanLogChannel = Snowflake(configuration, "BAN_LOG_CHANNEL"),
            PoliceLogChannel = Snowflake(configuration, "POLICE_LOG_CHANNEL"),
            ArrestChannel = Snowflake(configuration, "ARREST_CHANNEL"),
            JoinWebhook = Text(configuration, "JOIN_WEBHOOK_URL"),
            KillWebhook = Text(configuration, "KILL_WEBHOOK_URL"),
            MoneyWebhook = Text(configuration, "MONEY_WEBHOOK_URL"),

            LeaderboardChannel = Snowflake(configuration, "LEADERBOARD_CHANNEL"),
            ArrestBoardChannel = Snowflake(configuration, "ARREST_LEADERBOARD_CHANNEL"),
            WarrantBoardChannel = Snowflake(configuration, "WARRANT_BOARD_CHANNEL"),

            PayrollAmount = Money(configuration, "PAYROLL_AMOUNT"),
            PayrollInterval = Minutes(configuration, "PAYROLL_INTERVAL_MINUTES", TimeSpan.FromMinutes(30)),
            EventRetentionDays = Int(configuration, "EVENT_RETENTION_DAYS", 90),
            PayrollFaction = Text(configuration, "PAYROLL_FACTION") ?? "NYPD",

            MoneyAlertThreshold = Money(configuration, "MONEY_ALERT_THRESHOLD"),
            MoneyAlertWindow = Minutes(configuration, "MONEY_ALERT_WINDOW_MINUTES", TimeSpan.FromMinutes(15)),

            CrashRecovery = Flag(configuration, "CRASH_RECOVERY"),
            PlayerCountChannels = Snowflakes(configuration, "PLAYER_COUNT_CHANNELS"),
            ShackTotalChannel = Snowflake(configuration, "SHACK_TOTAL_CHANNEL"),
            DefaultServerCapacity = Int(configuration, "SERVER_MAX_PLAYERS", 24),
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
            DisabledPlugins = List(configuration, "PLUGINS_DISABLED"),

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

    /// <summary>A money amount. Zero is a MEANINGFUL value here - it is how a feature is left off.</summary>
    private static long Money(IConfiguration configuration, string key) =>
        long.TryParse(configuration[key]?.Trim(), CultureInfo.InvariantCulture, out var value) && value > 0 ? value : 0;

    /// <summary>A span given in MINUTES, which is how anybody writing a wage interval thinks about it.</summary>
    private static TimeSpan Minutes(IConfiguration configuration, string key, TimeSpan fallback) =>
        double.TryParse(configuration[key], CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : fallback;

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
        /* A PATH THAT DOES NOT EXIST IS REPORTED AS OFF. RosterService treats a missing
           directory exactly like an unset one, so printing the path here as though whitelists
           were on described a state the bot was not in - and every /whitelist add then failed
           for a reason the startup line said could not be the problem. */
        $"timeline: {(EventRetentionDays <= 0
            ? "off (EVENT_RETENTION_DAYS=0)"
            : $"keeping {EventRetentionDays} days")}",
        $"whitelists: {(RosterDirectory is null
            ? "off (FACTION_ROLES_PATH not set)"
            : Directory.Exists(RosterDirectory)
                ? RosterDirectory
                : $"off (FACTION_ROLES_PATH is {RosterDirectory}, which does not exist)")}",
        $"systemd units: {string.Join(", ", PavlovUnits)}",
        $"join feed: {(JoinWebhook is null ? "off" : "on")}",
        $"kill feed: {(KillWebhook is null ? "off" : "on")}",
        $"money feed: {(MoneyWebhook is null ? "off" : "on")}",
        $"cash leaderboard: {(LeaderboardChannel is null ? "off (LEADERBOARD_CHANNEL not set)" : $"channel {LeaderboardChannel}, every {LeaderboardInterval.TotalSeconds:0}s")}",
        $"arrest board: {(ArrestBoardChannel is null ? "off (ARREST_LEADERBOARD_CHANNEL not set)" : $"channel {ArrestBoardChannel}")}",
        $"warrant board: {(WarrantBoardChannel is null ? "off (WARRANT_BOARD_CHANNEL not set)" : $"channel {WarrantBoardChannel}")}",
        $"payroll: {(PayrollAmount <= 0
            ? "off (PAYROLL_AMOUNT not set)"
            : $"{PayrollAmount:N0} to on-duty {PayrollFaction} every {PayrollInterval.TotalMinutes:0}m")}",
        $"money alerts: {(MoneyAlertThreshold <= 0
            ? "off (MONEY_ALERT_THRESHOLD not set)"
            : $"over {MoneyAlertThreshold:N0} earned in {MoneyAlertWindow.TotalMinutes:0}m")}",
        $"crash recovery: {(CrashRecovery
            ? $"on - a failed unit is restarted, up to {Servers.CrashRecovery.MaxAttempts}x per {Servers.CrashRecovery.AttemptWindow.TotalMinutes:0}m"
            : "off (CRASH_RECOVERY not set)")}",
        $"player-count channels: {(PlayerCountChannels.Count == 0 ? "off (PLAYER_COUNT_CHANNELS not set)" : $"{PlayerCountChannels.Count} configured")}",
        $"shack total channel: {(ShackTotalChannel is null ? "off (SHACK_TOTAL_CHANNEL not set)" : $"channel {ShackTotalChannel}")}",
        $"connect feed: {(ConnectWebhook is null ? "off (CONNECT_WEBHOOK_URL not set)" : "on")}",
        $"staff feed: {(StaffWebhook is null ? "off (STAFF_WEBHOOK_URL not set)" : "on")}",
        $"staff log channels: {(ModLogChannel is null && BanLogChannel is null ? "off (MOD_LOG_CHANNEL / BAN_LOG_CHANNEL not set)" : $"mod {ModLogChannel?.ToString(CultureInfo.InvariantCulture) ?? "unset"}, ban {BanLogChannel?.ToString(CultureInfo.InvariantCulture) ?? "unset"}")}",
        $"menu panel: {(MenuPanelChannel is null
            ? "off (MENU_PANEL_CHANNEL not set)"
            : MenuRoleStaff is null && MenuRoleHighStaff is null
                ? $"channel {MenuPanelChannel} - NO MENU_ROLE_STAFF/HIGHSTAFF, nobody qualifies"
                : $"channel {MenuPanelChannel}")}",
        $"verification: {(VerifyChannel is null || VerifyStaffChannel is null
            ? "off (needs VERIFY_CHANNEL and VERIFY_STAFF_CHANNEL)"
            : $"panel in {VerifyChannel}, requests to {VerifyStaffChannel}" +
              (VerifiedRole is null ? " - NO VERIFIED_ROLE, approval grants nothing" : $", grants role {VerifiedRole}"))}",
        $"owners: {Owners.Count + SuperOwners.Count} configured, plus the built-in super owner",

        /* Rendered THROUGH the same union the auto-ban check uses, rather than restated.
           A summary that lists the configured names alone would have said "none" on a
           deployment where one account is in fact protected - and the startup summary is
           the only place most people ever look. */
        $"master names: {string.Join(", ", OwnerGuard.WithBuiltIn(MasterNames))}",
    ];
}
