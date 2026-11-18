//! Every dataset the bot persists, and what an empty one looks like (C# `Storage/Datasets.cs`).
//!
//! NAMES MATCH THE FILES THE NODE BOT WROTE (minus `./` and `.json`): they are the keys in the
//! live `bot.db`. A renamed dataset here would read as empty rather than failing.
//!
//! THE SEED MATTERS AS MUCH AS THE NAME: a dataset whose empty value is `[]` but is seeded `{}`
//! deserialises to the fallback on every read, right up until a write makes the wrong shape the
//! stored one. `killstats` has no seed, as in the C#: it is new in the C# bot and created on
//! first write.

pub const TEMP_BANS: &str = "tempbans";
pub const MOD_LOG: &str = "modlog";
pub const MUTES: &str = "mutes";
pub const USER_BLACKLIST: &str = "user_blacklist";
pub const USER_UNBARRED: &str = "user_unbarred";
pub const IP_FLAGS: &str = "ip_flags";
pub const IGNORED_NAMES: &str = "ignored_names";
pub const AUTOBAN_EXEMPT: &str = "autoban_exempt";
pub const NEVER_BAN: &str = "never_ban";
pub const MASTER_PARDONS: &str = "master_pardons";
pub const MASTER_GRANTS: &str = "master_grants";
pub const FIREWALL_BANS: &str = "firewall_bans";
pub const WARNINGS: &str = "warnings";
pub const BAN_RECONCILE_STATE: &str = "ban_reconcile_state";
pub const MONITOR_HISTORY: &str = "monitor_history";
pub const UNBAN_TOMBSTONES: &str = "unban_tombstones";
pub const VPN_CHECKS: &str = "vpn_checks";
pub const SERVER_LOCK: &str = "server_lock";
pub const FACTION_RANKS: &str = "faction_ranks";
pub const FACTION_CONFIG: &str = "faction_config";
pub const FACTION_AUDIT: &str = "faction_audit";
pub const FACTION_BACKUP: &str = "faction_backup";
pub const WARRANTS: &str = "warrants";
pub const ARRESTS: &str = "arrests";
pub const SENTENCES: &str = "sentences";
pub const POLICE_CONFIG: &str = "police_config";
pub const RANK_SUSPENSIONS: &str = "rank_suspensions";
pub const PLAYTIME: &str = "playtime";
pub const KILL_STATS: &str = "killstats";
pub const LAST_SEEN: &str = "lastseen";
pub const KNOWN_PLAYERS: &str = "known_players";
pub const DISCORD_LINKS: &str = "discord_links";
pub const VERIFICATIONS: &str = "verifications";
pub const FACTION_MEMBERS: &str = "faction_members";
pub const VERIFY_STATE: &str = "verify_state";
pub const DONATOR_SUSPEND: &str = "donator_suspend";
pub const WAGES: &str = "wages";
pub const PAYROLL_STATE: &str = "payroll_state";
pub const MENU_GRANTS: &str = "menu_grants";
pub const MENU_PANEL: &str = "menu_panel";
pub const MENU_ROLES: &str = "menu_roles";
pub const MENU_LINKS: &str = "menu_links";
pub const ROLES: &str = "roles";
pub const AUTO_ROTATE: &str = "autorotate";
pub const SERVER_STATS: &str = "server_stats";
pub const AUTOPOST_STATE: &str = "autopost_state";
pub const UPDATE_LOG_STATE: &str = "update_log_state";

/// Dataset -> the JSON an empty one contains, in the C# table's order.
pub const SEEDS: &[(&str, &str)] = &[
    (TEMP_BANS, r#"[]"#),
    (MOD_LOG, r#"[]"#),
    (MUTES, r#"{}"#),
    (USER_BLACKLIST, r#"[]"#),
    (USER_UNBARRED, r#"[]"#),
    (IP_FLAGS, r#"{}"#),
    (IGNORED_NAMES, r#"[]"#),
    (AUTOBAN_EXEMPT, r#"{}"#),
    (NEVER_BAN, r#"{}"#),
    (MASTER_PARDONS, r#"{}"#),
    (MASTER_GRANTS, r#"[]"#),
    (FIREWALL_BANS, r#"{}"#),
    (WARNINGS, r#"{}"#),
    (BAN_RECONCILE_STATE, r#"{}"#),
    (MONITOR_HISTORY, r#"{}"#),
    (UNBAN_TOMBSTONES, r#"{}"#),
    (VPN_CHECKS, r#"{}"#),
    (SERVER_LOCK, r#"{}"#),
    (FACTION_RANKS, r#"{}"#),
    (FACTION_CONFIG, r#"{}"#),
    (FACTION_AUDIT, r#"[]"#),
    (FACTION_BACKUP, r#"{}"#),
    (WARRANTS, r#"{}"#),
    (ARRESTS, r#"{}"#),
    (SENTENCES, r#"{}"#),
    (POLICE_CONFIG, r#"{"bailRate":1}"#),
    (RANK_SUSPENSIONS, r#"{}"#),
    (PLAYTIME, r#"{}"#),
    (LAST_SEEN, r#"{}"#),
    (KNOWN_PLAYERS, r#"{}"#),
    (DISCORD_LINKS, r#"{}"#),
    (VERIFICATIONS, r#"{}"#),
    (FACTION_MEMBERS, r#"{}"#),
    (VERIFY_STATE, r#"{}"#),
    (DONATOR_SUSPEND, r#"{}"#),
    (WAGES, r#"[]"#),
    (PAYROLL_STATE, r#"{}"#),
    (MENU_GRANTS, r#"{}"#),
    (MENU_PANEL, r#"{}"#),
    (MENU_ROLES, r#"{}"#),
    (MENU_LINKS, r#"{}"#),
    (
        ROLES,
        r#"{"modRole":null,"adminRole":null,"factionLeaderRole":null,"policeRole":null,"factionRoles":{}}"#,
    ),
    (AUTO_ROTATE, r#"{}"#),
    (SERVER_STATS, r#"{}"#),
    (AUTOPOST_STATE, r#"{}"#),
    (UPDATE_LOG_STATE, r#"{}"#),
];

/// Every seeded dataset name (C# `Datasets.All`).
pub fn all() -> impl Iterator<Item = &'static str> {
    SEEDS.iter().map(|(name, _)| *name)
}

/// The seed for a dataset, `None` for a name that has none.
#[must_use]
pub fn seed(name: &str) -> Option<&'static str> {
    SEEDS.iter().find(|(n, _)| *n == name).map(|(_, s)| *s)
}
