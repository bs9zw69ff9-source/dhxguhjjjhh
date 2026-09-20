using System.Globalization;
using Microsoft.Extensions.Configuration;
using PavlovBot.Core.Monitoring;
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

    /// <summary>
    /// Pavlov's Stats.log files, comma separated. Blank derives them from the log paths.
    /// </summary>
    /// <remarks>
    /// The better source for kills: structured JSON, a headshot flag, the game's own
    /// timestamp, and no dependency on bVerboseLogging. See <c>StatsLogService</c>.
    /// </remarks>
    public string? StatsLogPaths { get; init; }

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
    /// <remarks>The FIRST of <see cref="BanFilePaths"/>, kept for the startup summary and for
    /// anything that only needs to name one file.</remarks>
    public string? BanFilePath { get; init; }

    /// <summary>
    /// Every install's ban file, so a ban or unban reaches all of them.
    /// </summary>
    /// <remarks>
    /// AUTO-DISCOVERED from the installs unless <c>BLACKLIST_PATH</c> is set explicitly, in
    /// which case that single file is honoured exactly - someone who named a path meant that
    /// path. Multiple servers on one box each read their OWN Config/blacklist.txt, so unbanning
    /// on one while leaving the others listed is how a lifted player is refused by server 2 and
    /// re-banned when it restarts. Empty when <c>BLACKLIST_SYNC=false</c>.
    /// </remarks>
    public IReadOnlyList<string> BanFilePaths { get; init; } = [];

    /// <summary>
    /// Every Pavlov install root on the box, so the per-install files (logs, ban lists,
    /// whitelists) can be found for all servers rather than just the first.
    /// </summary>
    public IReadOnlyList<string> InstallRoots { get; init; } = [];

    /// <summary>
    /// A retired MODSAVE_BLACKLIST_PATH found in the environment, so startup can say it is
    /// being ignored. Nothing reads this to decide anything.
    /// </summary>
    public string? IgnoredBanFilePath { get; init; }

    /// <summary>
    /// Whether the ModSave tree is kept identical across every install. On unless turned off.
    /// </summary>
    /// <remarks>
    /// Defaults ON, and disabled only by an explicit <c>MODSAVE_SYNC=off</c> - the same switch
    /// the Node bot used. It is opt-OUT rather than opt-in because a multi-install box that is
    /// NOT syncing is the surprising, money-losing state, not the safe default.
    /// </remarks>
    public bool ModSaveSync { get; init; } = true;

    /// <summary>Extra path fragments never mirrored, beyond the built-in menu-access ones.</summary>
    public IReadOnlyList<string> ModSaveSyncSkipExtra { get; init; } = [];

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
    public string? RconWebhook { get; init; }

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

    /// <summary>
    /// The name stamped on every embed. <c>BOT_NAME</c>, which nothing read until now.
    /// </summary>
    public string? BotName { get; init; }

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

    /// <summary>
    /// Where the live player board lives - who is on, per server, with their faction.
    /// </summary>
    /// <remarks>
    /// <c>PLAYERLIST_CHANNEL</c>, which is the Node bot's name for it and the one already
    /// sitting in every deployed <c>.env</c>. The port dropped the board and documented the
    /// variable as dead; bringing the board back under a NEW name would have meant every
    /// install silently keeping a board that does not post, with the id already filled in one
    /// line above the one that matters.
    ///
    /// IT PUBLISHES NAMES, unlike the player-count voice channels, so point it at a channel
    /// whose audience is meant to see who is on.
    /// </remarks>
    public ulong? PlayerBoardChannel { get; init; }

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
    /// <summary>
    /// A JSON file defining the factions this bot runs. Null means the built-in set.
    /// </summary>
    /// <remarks>
    /// The setting that lets one binary serve two RPs. Unset is the normal deployment and
    /// gets the mafias and the police exactly as before.
    /// </remarks>
    public string? FactionsPath { get; init; }

    /// <summary>
    /// A built-in faction set by name, used when no <see cref="FactionsPath"/> is given.
    /// </summary>
    /// <remarks>
    /// THE FILE WAS THE HASSLE. Running a themed server meant keeping a JSON file by hand -
    /// SSH, an exact path, valid JSON, a restart - for ladders that do not change and are the
    /// same on every server running that theme. This is the same data, shipped, chosen by one
    /// word. FACTIONS_PATH still wins for anybody whose ladders are genuinely their own.
    /// </remarks>
    public string? FactionSetName { get; init; }

    /// <summary>
    /// Paths this bot must never WRITE to, however else it is configured.
    /// </summary>
    /// <remarks>
    /// For a second bot sharing one game server. Several subsystems rewrite a whole file from
    /// their own database - the ban list, the whitelist, a player's balance - so two bots
    /// pointed at one file erase each other's work on a timer.
    ///
    /// Reads are unaffected: a read cannot corrupt somebody else's file, and the second bot
    /// still needs to read the rosters to enforce one faction per player.
    /// </remarks>
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];

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

    /// <summary>
    /// Whether a VPN verdict may issue an automatic PERMANENT ban. On unless turned off.
    /// </summary>
    /// <remarks>
    /// THE ONE SWITCH THAT STOPS THE BOT BANNING PEOPLE BY ITSELF. Every other automatic ban
    /// needs an explicit blacklist entry somebody typed; this one acts on a PROBABILISTIC
    /// verdict from third-party providers, and those providers are wrong about residential
    /// and mobile addresses often enough that the merge code carries an example of it - one
    /// of them calls Cloudflare's own resolver "vpn+abuser". A false positive here is a
    /// permanent ban on somebody who did nothing, issued in the seconds after they joined.
    ///
    /// Off still SCREENS and still REPORTS: the connection card, the feed line and the log
    /// all say what the verdict was. It only withholds the consequence, which is the part a
    /// human can be in the loop for.
    /// </remarks>
    public bool VpnAutoBan { get; init; } = true;
    public TimeSpan VpnCacheTtl { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Geoapify key. Deliberately NOT part of <see cref="VpnKeys"/>: those are reputation
    /// providers that vote on whether an address is a VPN, and this one only says where an
    /// address is. Putting it there would let it be counted as a detector.
    /// </summary>
    public string? GeoapifyKey { get; init; }

    /// <summary>Discord ids that hold owner powers. NOT a role - see <c>Access</c>.</summary>
    /// <summary>
    /// Who gets a direct message when evasion, alts or a VPN are detected.
    /// </summary>
    /// <remarks>
    /// FALLS BACK TO THE OWNERS when unset, because the alternative default is "nobody",
    /// which is indistinguishable from the feature being broken. Set it explicitly to send
    /// the alerts to one person rather than everybody with owner rights.
    /// </remarks>
    public IReadOnlyList<ulong> SecurityDmIds { get; init; } = [];

    public IReadOnlyList<ulong> Owners { get; init; } = [];
    public IReadOnlyList<ulong> SuperOwners { get; init; } = [];

    /// <summary>
    /// Who actually receives a security alert: the explicit list, or the owners.
    /// </summary>
    /// <remarks>
    /// DEDUPED, because an id in both OWNER_IDS and SUPER_OWNER_IDS is an ordinary way to
    /// write "this person owns the bot" and must not mean two identical direct messages.
    /// </remarks>
    public IReadOnlyList<ulong> SecurityAlertRecipients =>
        SecurityDmIds.Count > 0
            ? [.. SecurityDmIds.Distinct()]
            : [.. SuperOwners.Concat(Owners).Distinct()];

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

    // ---- server monitoring ----

    /// <summary>Whether the automatic server-health monitor runs at all.</summary>
    public bool MonitoringEnabled { get; init; } = true;

    /// <summary>Where monitoring alerts are posted. Unset disables alerting (state is still tracked).</summary>
    public ulong? MonitorAlertChannel { get; init; }

    /// <summary>Role pinged on WARNING/CRITICAL monitoring alerts. Unset pings nobody.</summary>
    public ulong? MonitorAlertRole { get; init; }

    /// <summary>
    /// When on (the default), the monitor posts ONE live board to <see cref="MonitorAlertChannel"/>
    /// - a rich embed edited in place with every server's stats and a running event log - instead
    /// of a stream of one-off alert messages. Set <c>MONITOR_BOARD</c> false to go back to the
    /// per-event alerts.
    /// </summary>
    public bool MonitorBoardEnabled { get; init; } = true;

    /// <summary>
    /// Every monitoring threshold, in one record so none of them is a magic number in the loop.
    /// </summary>
    public MonitorSettings MonitorSettings { get; init; } = MonitorSettings.Default;

    /// <summary>
    /// Also deny a manually blacklisted ADDRESS at the OS firewall (ufw), not just in the bot.
    /// </summary>
    /// <remarks>
    /// Applies to a manual address block through <c>/configure blacklist</c> only - the owner's
    /// deliberate, exact-match decision, the same category <c>/firewall</c> serves. The auto-ban
    /// path still never touches the firewall: a false-positive ban must not cut somebody off at
    /// the OS level. Defaults ON because that is what was asked for; set <c>FIREWALL_BLACKLIST</c>
    /// false to keep blacklisting bot-only, e.g. where the bot does not run as root.
    /// </remarks>
    public bool FirewallBlacklistedIps { get; init; } = true;

    public static FeatureOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new FeatureOptions
        {
            LedgerDirectory = Text(configuration, "MODSAVE_PATH"),
            LogPaths = Text(configuration, "PAVLOV_LOGS"),
            StatsLogPaths = Text(configuration, "STATS_LOGS"),
            PavlovVersion = Text(configuration, "PAVLOV_VERSION"),
            RosterDirectory = Text(configuration, "FACTION_ROLES_PATH"),
            FactionsPath = Text(configuration, "FACTIONS_PATH"),
            FactionSetName = Text(configuration, "FACTION_SET"),
            IgnoredPaths = List(configuration, "IGNORE_PATHS"),
            PavlovUnits = PavlovBot.Host.Servers.ServiceControl.ParseUnits(Text(configuration, "PAVLOV_UNITS")),
            SystemctlSudo = OptionalFlag(configuration, "PAVLOV_SYSTEMCTL_SUDO"),
            /* THE FILE THE SERVER ACTUALLY READS, which is Config/blacklist.txt - the one
               the setup guide creates beside mods.txt and whitelist.txt, and the one this
               bot's own provisioner creates.

               It used to default to Config/ModSave/banlist.txt, a DIFFERENT file belonging
               to a mod. The bot synced that one while the server enforced this one, so a
               player listed here was refused by the SERVER while /banlist and /checkban
               reported nothing about him and /unban could not reach him. That is the whole
               of the bug this default fixes.

               MODSAVE_BLACKLIST_PATH IS NO LONGER READ. It was kept as a fallback so an
               existing .env would not change meaning, and that is exactly what kept the bug
               alive: every deployment that had ever set it went on syncing ModSave while the
               server enforced Config/blacklist.txt, and the fallback made the correct default
               unreachable without editing the file. It is reported at startup instead - see
               IgnoredBanFilePath - because a setting that stops working must say so.

               BLACKLIST_SYNC=false TURNS IT OFF, and there had to be a way. The ban file is
               rewritten in full from one bot's store, so two bots pointed at one install
               erase each other's bans every five minutes - which is why SECOND-BOT.md tells
               the clone not to manage it. Its instruction was to leave the path blank, and
               blank has never disabled anything: the default fills it in. */
            BanFilePath = BanFilePathsFor(configuration) is [var primary, ..] ? primary : null,
            BanFilePaths = BanFilePathsFor(configuration),
            InstallRoots = PavlovBot.Host.Storage.PavlovInstalls.Discover(
                Text(configuration, "PAVLOV_BASES"), Text(configuration, "PAVLOV_BASE_1")),

            IgnoredBanFilePath = Text(configuration, "MODSAVE_BLACKLIST_PATH"),

            // Opt-out: only an explicit MODSAVE_SYNC=off disables cross-install ModSave sync.
            ModSaveSync = OptionalFlag(configuration, "MODSAVE_SYNC") != false,
            ModSaveSyncSkipExtra = List(configuration, "MODSAVE_SYNC_SKIP_EXTRA"),

            /* Two DIFFERENT webhooks. CONNECT carries addresses and belongs in a private
               channel; JOIN is the plain public log. The port read CONNECT into the join
               feed and never read JOIN_WEBHOOK_URL at all. */
            ConnectWebhook = Text(configuration, "CONNECT_WEBHOOK_URL"),
            StaffWebhook = Text(configuration, "STAFF_WEBHOOK_URL"),
            ModLogChannel = Snowflake(configuration, "MOD_LOG_CHANNEL"),
            BotName = Text(configuration, "BOT_NAME"),
            BanLogChannel = Snowflake(configuration, "BAN_LOG_CHANNEL"),
            PoliceLogChannel = Snowflake(configuration, "POLICE_LOG_CHANNEL"),
            ArrestChannel = Snowflake(configuration, "ARREST_CHANNEL"),
            JoinWebhook = Text(configuration, "JOIN_WEBHOOK_URL"),
            KillWebhook = Text(configuration, "KILL_WEBHOOK_URL"),
            RconWebhook = Text(configuration, "RCON_WEBHOOK_URL"),

            LeaderboardChannel = Snowflake(configuration, "LEADERBOARD_CHANNEL"),
            ArrestBoardChannel = Snowflake(configuration, "ARREST_LEADERBOARD_CHANNEL"),
            WarrantBoardChannel = Snowflake(configuration, "WARRANT_BOARD_CHANNEL"),
            PlayerBoardChannel = Snowflake(configuration, "PLAYERLIST_CHANNEL"),

            PayrollAmount = Money(configuration, "PAYROLL_AMOUNT"),
            PayrollInterval = Minutes(configuration, "PAYROLL_INTERVAL_MINUTES", TimeSpan.FromMinutes(30)),
            EventRetentionDays = Int(configuration, "EVENT_RETENTION_DAYS", 90),
            PayrollFaction = Text(configuration, "PAYROLL_FACTION") ?? "NYPD",


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
                IpapiIs: Text(configuration, "IPAPIIS_KEY"),
                Sentinel: Text(configuration, "SENTINEL_API_KEY")),

            GeoapifyKey = Text(configuration, "GEOAPIFY_API_KEY"),

            /* VPN_BAN_MIN is the current name; VPN_SCREEN_BAN_MIN is what it used to be
               called, kept working so an existing .env is not silently reset to the default
               the first time somebody deploys this. */
            /* DEFAULTS ON, so this is not a silent behaviour change for anyone who has it
               working - but it is a plain switch rather than something to be discovered by
               deleting API keys. */
            VpnAutoBan = OptionalFlag(configuration, "VPN_AUTOBAN") != false,

            // Defaults ON, like VPN_AUTOBAN: a plain switch, not something discovered by
            // deleting a key. Off keeps a manual blacklist bot-only, touching no ufw rule.
            FirewallBlacklistedIps = OptionalFlag(configuration, "FIREWALL_BLACKLIST") != false,

            // ---- server monitoring: every threshold overridable, sane defaults otherwise ----
            MonitoringEnabled = OptionalFlag(configuration, "MONITORING") != false,
            MonitorAlertChannel = Snowflake(configuration, "MONITOR_ALERT_CHANNEL"),
            MonitorAlertRole = Snowflake(configuration, "MONITOR_ALERT_ROLE"),
            MonitorBoardEnabled = OptionalFlag(configuration, "MONITOR_BOARD") != false,
            MonitorSettings = new MonitorSettings(
                CheckInterval: TimeSpan.FromSeconds(Int(configuration, "MONITOR_CHECK_INTERVAL_SECONDS", 30)),
                FailureThreshold: Int(configuration, "MONITOR_FAILURE_THRESHOLD", 3),
                RecoveryThreshold: Int(configuration, "MONITOR_RECOVERY_THRESHOLD", 2),
                RconTimeout: TimeSpan.FromSeconds(Int(configuration, "MONITOR_RCON_TIMEOUT_SECONDS", 5)),
                RetryBaseDelay: TimeSpan.FromSeconds(Int(configuration, "MONITOR_RETRY_BASE_SECONDS", 2)),
                RetryMaxDelay: TimeSpan.FromSeconds(Int(configuration, "MONITOR_RETRY_MAX_SECONDS", 120)),
                LatencyWarn: TimeSpan.FromMilliseconds(Int(configuration, "MONITOR_LATENCY_WARN_MS", 250)),
                LatencyCritical: TimeSpan.FromMilliseconds(Int(configuration, "MONITOR_LATENCY_CRITICAL_MS", 750)),
                PlayerAnomalyDropFraction: Math.Clamp(Int(configuration, "MONITOR_PLAYER_DROP_PERCENT", 80) / 100.0, 0.0, 1.0),
                PlayerAnomalyWindow: TimeSpan.FromSeconds(Int(configuration, "MONITOR_PLAYER_WINDOW_SECONDS", 90)),
                LogInactivityThreshold: TimeSpan.FromMinutes(Int(configuration, "MONITOR_LOG_INACTIVITY_MINUTES", 5)),
                EscalationEvery: Int(configuration, "MONITOR_ESCALATION_EVERY", 20),
                RecoveryMessagesEnabled: OptionalFlag(configuration, "MONITOR_RECOVERY_MESSAGES") != false),

            VpnThresholds = new VpnThresholds(
                Int(configuration, "VPN_SCREEN_MIN", 1),
                Int(configuration, "VPN_CONFIRM_MIN", 1),
                Int(configuration, "VPN_BAN_MIN", Int(configuration, "VPN_SCREEN_BAN_MIN", 2))),

            VpnCacheTtl = TimeSpan.FromDays(Int(configuration, "VPN_CACHE_TTL_DAYS", 30)),

            SecurityDmIds = Snowflakes(configuration, "SECURITY_DM_IDS"),
            Owners = Snowflakes(configuration, "OWNER_IDS"),
            SuperOwners = Snowflakes(configuration, "SUPER_OWNER_IDS"),
            MasterNames = List(configuration, "MASTER_NAMES"),
            PluginDirectory = Text(configuration, "PLUGIN_DIR"),
            EnabledPlugins = List(configuration, "PLUGINS_ENABLED"),
            DisabledPlugins = List(configuration, "PLUGINS_DISABLED"),

            LeaderboardInterval = Milliseconds(configuration, "LEADERBOARD_INTERVAL_MS", TimeSpan.FromMinutes(1)),
        };
    }

    private static string? Text(IConfiguration configuration, string key) =>
        configuration[key]?.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>
    /// Every ban file to keep in sync: none when disabled, the one explicit path when set, or
    /// one per discovered install otherwise. See <see cref="BanFilePaths"/>.
    /// </summary>
    private static IReadOnlyList<string> BanFilePathsFor(IConfiguration configuration)
    {
        if (OptionalFlag(configuration, "BLACKLIST_SYNC") == false) return [];

        // An explicit BLACKLIST_PATH is honoured exactly - one file, as it always was.
        if (Text(configuration, "BLACKLIST_PATH") is { } explicitPath) return [explicitPath];

        // Otherwise every install on the box gets its own Config/blacklist.txt covered.
        var installs = PavlovBot.Host.Storage.PavlovInstalls.Discover(
            Text(configuration, "PAVLOV_BASES"),
            Text(configuration, "PAVLOV_BASE_1") ?? "/home/steam/pavlovserver");

        var paths = installs
            .Select(PavlovBot.Host.Storage.PavlovInstalls.BlacklistPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return paths.Count > 0
            ? paths
            : [PavlovBot.Host.Storage.PavlovInstalls.BlacklistPath("/home/steam/pavlovserver")];
    }

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
        $"ignored paths: {(IgnoredPaths.Count == 0
            ? "none (IGNORE_PATHS not set) - this bot may write every file it is given"
            : string.Join(", ", IgnoredPaths))}",
        $"systemd units: {string.Join(", ", PavlovUnits)}",
        $"join feed: {(JoinWebhook is null ? "off" : "on")}",
        $"kill feed: {(KillWebhook is null ? "off" : "on")}",
        $"rcon feed: {(RconWebhook is null ? "off" : "on")}",
        $"cash leaderboard: {(LeaderboardChannel is null ? "off (LEADERBOARD_CHANNEL not set)" : $"channel {LeaderboardChannel}, every {LeaderboardInterval.TotalSeconds:0}s")}",
        $"arrest board: {(ArrestBoardChannel is null ? "off (ARREST_LEADERBOARD_CHANNEL not set)" : $"channel {ArrestBoardChannel}")}",
        $"warrant board: {(WarrantBoardChannel is null ? "off (WARRANT_BOARD_CHANNEL not set)" : $"channel {WarrantBoardChannel}")}",
        $"player board: {(PlayerBoardChannel is null ? "off (PLAYERLIST_CHANNEL not set)" : $"channel {PlayerBoardChannel}")}",
        $"payroll: {(PayrollAmount <= 0
            ? "off (PAYROLL_AMOUNT not set)"
            : $"{PayrollAmount:N0} to on-duty {PayrollFaction} every {PayrollInterval.TotalMinutes:0}m")}",
        $"crash recovery: {(CrashRecovery
            ? $"on - a failed unit is restarted, up to {Servers.CrashRecovery.MaxAttempts}x per {Servers.CrashRecovery.AttemptWindow.TotalMinutes:0}m"
            : "off (CRASH_RECOVERY not set)")}",
        $"server monitor: {(!MonitoringEnabled
            ? "off (MONITORING=0)"
            : MonitorAlertChannel is null
                ? "watching, but nothing is posted - set MONITOR_ALERT_CHANNEL for the live board"
                : $"{(MonitorBoardEnabled ? "live board in" : "per-event alerts to")} channel {MonitorAlertChannel}{(MonitorAlertRole is null ? "" : $", pinging role {MonitorAlertRole}")}, checked every {MonitorSettings.CheckInterval.TotalSeconds:0}s")}",
        $"player-count channels: {(PlayerCountChannels.Count == 0 ? "off (PLAYER_COUNT_CHANNELS not set)" : $"{PlayerCountChannels.Count} configured")}",
        $"shack total channel: {(ShackTotalChannel is null ? "off (SHACK_TOTAL_CHANNEL not set)" : $"channel {ShackTotalChannel}")}",
        $"connect feed: {(ConnectWebhook is null ? "off (CONNECT_WEBHOOK_URL not set)" : "on")}",
        $"staff feed: {(StaffWebhook is null ? "off (STAFF_WEBHOOK_URL not set)" : "on")}",
        $"staff log channels: {(ModLogChannel is null && BanLogChannel is null ? "off (MOD_LOG_CHANNEL / BAN_LOG_CHANNEL not set)" : $"mod {ModLogChannel?.ToString(CultureInfo.InvariantCulture) ?? "unset"}, ban {BanLogChannel?.ToString(CultureInfo.InvariantCulture) ?? "unset"}")}",
        $"menu panel: {(MenuPanelChannel is null
            ? "off (MENU_PANEL_CHANNEL not set)"
            : MenuRoleStaff is null && MenuRoleHighStaff is null
                /* NOT "nobody qualifies" any more. /setrconroles stores a mapping too, and
                   this record only ever sees the environment - so an install configured
                   entirely through the command was being told it was broken. */
                ? $"channel {MenuPanelChannel} - no menu roles in .env; /setrconroles shows what is in force"
                : $"channel {MenuPanelChannel}")}",
        $"verification: {(VerifyChannel is null || VerifyStaffChannel is null
            ? "off (needs VERIFY_CHANNEL and VERIFY_STAFF_CHANNEL)"
            : $"panel in {VerifyChannel}, requests to {VerifyStaffChannel}" +
              (VerifiedRole is null ? " - NO VERIFIED_ROLE, approval grants nothing" : $", grants role {VerifiedRole}"))}",
        $"owners: {Owners.Count + SuperOwners.Count} configured, plus the built-in super owner",
        $"security DMs: {(SecurityAlertRecipients.Count == 0
            ? "off (SECURITY_DM_IDS is unset and no owners are configured)"
            : $"{SecurityAlertRecipients.Count} recipient(s)" +
              (SecurityDmIds.Count > 0 ? " (SECURITY_DM_IDS)" : " (the owners - SECURITY_DM_IDS is unset)"))}",

        /* Rendered THROUGH the same union the auto-ban check uses, rather than restated.
           A summary that lists the configured names alone would have said "none" on a
           deployment where one account is in fact protected - and the startup summary is
           the only place most people ever look. */
        $"master names: {string.Join(", ", OwnerGuard.WithBuiltIn(MasterNames))}",
    ];
}
