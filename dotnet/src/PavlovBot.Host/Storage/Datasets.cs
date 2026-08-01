namespace PavlovBot.Host.Storage;

/// <summary>
/// Every dataset the bot persists, and what an empty one looks like.
/// </summary>
/// <remarks>
/// Names match the Node bot's file names exactly (minus the <c>./</c> and <c>.json</c>), so
/// the two share one <c>bot.db</c> during the migration. A renamed dataset here would read
/// as empty rather than failing - a bot that silently forgets every ban - so the names are
/// not a detail to tidy up.
///
/// The SEED matters as much as the name: a dataset whose natural empty value is <c>[]</c>
/// but which is seeded <c>{}</c> deserialises to the fallback on every read, which looks
/// like it works right up until a write turns the wrong shape into the stored one.
/// </remarks>
public static class Datasets
{
    // ---- moderation ----
    public const string TempBans = "tempbans";
    public const string ModLog = "modlog";
    public const string Mutes = "mutes";
    /// <summary>
    /// Discord user ids barred from every command. AN ARRAY OF STRINGS - the Node bot's
    /// format, and it reads this exact dataset (<c>index.js</c>, <c>BLACKLIST_IDS</c>).
    /// </summary>
    public const string UserBlacklist = "user_blacklist";
    public const string UserUnbarred = "user_unbarred";

    /// <summary>
    /// IP, name and account-id flags for ban-evasion matching.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="UserBlacklist"/>, and it has to be. Both bots share one
    /// database, and this data used to live under that name in C# while the Node bot used
    /// the same name for its array of barred Discord ids. Each bot's write destroyed the
    /// other's data: C# writing an object left Node calling <c>.map</c> on it, and Node
    /// writing an array left C# reading no flags at all - which silently turns off
    /// ban-evasion auto-banning, the failure that announces itself least.
    /// </remarks>
    public const string IpFlags = "ip_flags";

    /// <summary>
    /// In-game names whose addresses are never recorded. An array of strings.
    /// </summary>
    /// <remarks>
    /// Persisted rather than in-memory: an ignore list that empties on restart is worse
    /// than none, because it silently starts collecting the addresses of the accounts
    /// somebody deliberately asked it not to.
    /// </remarks>
    public const string IgnoredNames = "ignored_names";
    public const string AutobanExempt = "autoban_exempt";
    public const string BanReconcileState = "ban_reconcile_state";
    public const string VpnChecks = "vpn_checks";
    public const string ServerLock = "server_lock";

    // ---- factions ----
    public const string FactionRanks = "faction_ranks";
    public const string FactionConfig = "faction_config";
    public const string FactionAudit = "faction_audit";
    public const string FactionBackup = "faction_backup";

    // ---- police ----
    public const string Warrants = "warrants";
    public const string Arrests = "arrests";
    public const string Sentences = "sentences";
    public const string PoliceConfig = "police_config";
    public const string RankSuspensions = "rank_suspensions";

    // ---- players ----
    public const string Playtime = "playtime";
    public const string LastSeen = "lastseen";
    public const string KnownPlayers = "known_players";
    public const string DiscordLinks = "discord_links";
    public const string Verifications = "verifications";
    public const string VerifyState = "verify_state";
    public const string DonatorSuspend = "donator_suspend";
    public const string Wages = "wages";

    // ---- menus ----
    public const string MenuGrants = "menu_grants";
    public const string MenuPanel = "menu_panel";
    public const string MenuRoles = "menu_roles";
    public const string MenuLinks = "menu_links";

    // ---- bot config and surfaces ----
    public const string Roles = "roles";
    public const string AutoRotate = "autorotate";
    public const string ServerStats = "server_stats";
    public const string AutopostState = "autopost_state";
    public const string UpdateLogState = "update_log_state";

    /// <summary>Dataset -> the JSON an empty one contains.</summary>
    public static readonly IReadOnlyDictionary<string, string> Seeds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TempBans] = "[]",
        [ModLog] = "[]",
        [Mutes] = "{}",
        [UserBlacklist] = "[]",
        [UserUnbarred] = "[]",
        [IpFlags] = "{}",
        [IgnoredNames] = "[]",
        [AutobanExempt] = "{}",
        [BanReconcileState] = "{}",
        [VpnChecks] = "{}",
        [ServerLock] = "{}",

        [FactionRanks] = "{}",
        [FactionConfig] = "{}",
        [FactionAudit] = "[]",
        [FactionBackup] = "{}",

        [Warrants] = "{}",
        [Arrests] = "{}",
        [Sentences] = "{}",
        [PoliceConfig] = """{"bailRate":1}""",
        [RankSuspensions] = "{}",

        [Playtime] = "{}",
        [LastSeen] = "{}",
        [KnownPlayers] = "{}",
        [DiscordLinks] = "{}",
        [Verifications] = "{}",
        [VerifyState] = "{}",
        [DonatorSuspend] = "{}",
        [Wages] = "[]",

        [MenuGrants] = "{}",
        [MenuPanel] = "{}",
        [MenuRoles] = "{}",
        [MenuLinks] = "{}",

        [Roles] = """{"modRoleId":"","adminRoleId":"","factionLeaderRoleId":"","policeRoleId":"","gambinoRoleId":"","colomboRoleId":"","nypdRoleId":""}""",
        [AutoRotate] = "{}",
        [ServerStats] = "{}",
        [AutopostState] = "{}",
        [UpdateLogState] = "{}",
    };

    public static IReadOnlyCollection<string> All { get; } = Seeds.Keys.ToList();
}
