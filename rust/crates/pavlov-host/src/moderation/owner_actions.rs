//! What `/configure`'s owner panel does (C# `Moderation/OwnerActions.cs`): blacklisting,
//! the never-ban list, the command bar list, the tracking ignore list, whitelist snapshots and
//! the destructive wipes. Each returns words for the panel; none of them throws at it.

use std::net::IpAddr;
use std::path::PathBuf;
use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::evasion::{AccountRecord, FlagSet};
use pavlov_core::moderation::{BanRecord, StaffTier};
use pavlov_core::text::case::{IgnoreCaseMap, eq_ignore_case};
use pavlov_storage::datasets::{
    IGNORED_NAMES, IP_FLAGS, KNOWN_PLAYERS, LAST_SEEN, MENU_GRANTS, PLAYTIME, TEMP_BANS,
};
use serde_json::Value;

use super::command_blacklist::{BarOutcome, CommandBlacklist};
use super::master_names::StoredMasterNames;
use crate::factions::whitelist_backup::{WhitelistBackup, WhitelistBackupResult};
use crate::firewall::Firewall;
use crate::logs::IpTrackingService;

/// What an action did: whether it worked, a sentence, and any list to show under it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OwnerActionResult {
    pub ok: bool,
    pub detail: String,
    pub lines: Vec<String>,
}

impl OwnerActionResult {
    #[must_use]
    pub fn done(detail: impl Into<String>) -> Self {
        Self {
            ok: true,
            detail: detail.into(),
            lines: Vec::new(),
        }
    }

    #[must_use]
    pub fn refused(detail: impl Into<String>) -> Self {
        Self {
            ok: false,
            detail: detail.into(),
            lines: Vec::new(),
        }
    }

    #[must_use]
    pub fn list(detail: impl Into<String>, lines: Vec<String>) -> Self {
        Self {
            ok: true,
            detail: detail.into(),
            lines,
        }
    }
}

/// Lifts a ban by player name (the ban service's unban). `Err` carries why it did not.
pub type LiftBan = Arc<dyn Fn(String) -> BoxFuture<'static, Result<(), String>> + Send + Sync>;

/// What the actions are wired to; everything but the store and the tracker is optional, and an
/// action whose piece is missing says so rather than pretending.
#[derive(Default)]
pub struct OwnerActionParts {
    pub ledger_directory: Option<PathBuf>,
    pub masters: Option<Arc<StoredMasterNames>>,
    pub lift_ban: Option<LiftBan>,
    pub firewall: Option<Arc<dyn Firewall>>,
    pub whitelists: Option<Arc<WhitelistBackup>>,
    pub blacklist: Option<Arc<CommandBlacklist>>,
}

impl std::fmt::Debug for OwnerActionParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OwnerActionParts")
            .field("ledger_directory", &self.ledger_directory)
            .finish_non_exhaustive()
    }
}

const NO_BLACKLIST: &str = "The command blacklist is not available.";
const NO_NEVER_BAN: &str = "The never-ban list is not available.";
const NO_SNAPSHOTS: &str = "Whitelist snapshots are not wired up on this bot.";

fn flag_count(flags: &FlagSet) -> usize {
    flags.ips.len() + flags.manual_ips.len() + flags.names.len() + flags.ids.len()
}

/// A Discord user id, bare or as a mention.
fn user_id(input: &str) -> Option<u64> {
    let trimmed = input
        .trim()
        .trim_start_matches(['<', '@', '!'])
        .trim_end_matches('>');
    if trimmed.is_empty() || !trimmed.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    trimmed.parse::<u64>().ok().filter(|id| *id > 0)
}

fn display(account: &AccountRecord) -> String {
    account
        .name()
        .map_or_else(|| account.id.clone(), str::to_owned)
}

/// The owner actions.
pub struct OwnerActions {
    store: Arc<SerializedStore>,
    tracking: Arc<IpTrackingService>,
    parts: OwnerActionParts,
}

impl std::fmt::Debug for OwnerActions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OwnerActions").finish_non_exhaustive()
    }
}

impl OwnerActions {
    #[must_use]
    pub fn new(
        store: Arc<SerializedStore>,
        tracking: Arc<IpTrackingService>,
        parts: OwnerActionParts,
    ) -> Self {
        Self {
            store,
            tracking,
            parts,
        }
    }

    // ---- address enforcement ----

    /// Blacklist an address or a username, told apart without asking. A manual ADDRESS is
    /// also denied at ufw: an exact address a human typed is a deliberate choice, unlike an
    /// auto-ban, which never touches the firewall. Non-fatal: the flag is written either way.
    pub async fn blacklist(&self, target: &str) -> OwnerActionResult {
        let target = target.trim().to_owned();
        if target.is_empty() {
            return OwnerActionResult::refused("Nothing to blacklist - the value was empty.");
        }
        let is_address = target.parse::<IpAddr>().is_ok();
        let flagged = target.clone();
        if let Err(error) = self
            .tracking
            .edit_flags(move |flags| {
                if is_address {
                    flags.manual_ips.insert(flagged);
                } else {
                    flags.names.insert(flagged);
                }
            })
            .await
        {
            return OwnerActionResult::refused(format!(
                "The blacklist could not be saved. ({error})"
            ));
        }
        let firewall_note = match (&self.parts.firewall, is_address) {
            (Some(firewall), true) => {
                let result = firewall.deny(&target).await;
                Some(if result.ok {
                    "🧱 Also denied at the OS firewall (ufw).".to_owned()
                } else {
                    format!(
                        "⚠️ Blacklisted, but the firewall rule did not apply: {}",
                        result.detail
                    )
                })
            }
            _ => None,
        };
        let matched: Vec<String> = if is_address {
            self.tracking
                .accounts_with_address(&target)
                .iter()
                .map(display)
                .collect()
        } else {
            self.tracking
                .account_by_name(&target)
                .iter()
                .map(display)
                .collect()
        };
        let kind = if is_address { "address" } else { "username" };
        let summary = if matched.is_empty() {
            format!(
                "{kind} `{target}` blacklisted. No account on record matches it yet; future connections will be caught."
            )
        } else {
            format!(
                "{kind} `{target}` blacklisted. It matches **{}** known account(s), listed below - ban them with `/permban`.",
                matched.len()
            )
        };
        let detail = match firewall_note {
            Some(note) => format!("{summary}\n{note}"),
            None => summary,
        };
        OwnerActionResult::list(
            detail,
            matched.iter().map(|n| format!("• **{n}**")).collect(),
        )
    }

    /// Everything on the blacklist. EMPTY IS TWO ANSWERS: a row that will not parse reads as
    /// an empty set, so the raw bytes are checked and a corrupt row is said out loud.
    #[must_use]
    pub fn view_blacklist(&self) -> OwnerActionResult {
        let flags = self.tracking.load_flags();
        let mut lines: Vec<String> = Vec::new();
        lines.extend(
            flags
                .manual_ips
                .iter()
                .map(|ip| format!("🚫 address `{ip}` *(set by an owner)*")),
        );
        lines.extend(
            flags
                .ips
                .iter()
                .map(|ip| format!("🚫 address `{ip}` *(from a ban)*")),
        );
        lines.extend(flags.names.iter().map(|n| format!("🚫 username **{n}**")));
        lines.extend(flags.ids.iter().map(|id| format!("🚫 account `{id}`")));
        if !lines.is_empty() {
            return OwnerActionResult::list(
                format!("**{}** blacklist entr(ies).", lines.len()),
                lines,
            );
        }
        let raw = self
            .store
            .read_raw(IP_FLAGS)
            .ok()
            .flatten()
            .unwrap_or_default();
        let raw = raw.trim();
        if raw.chars().count() > 2 {
            return OwnerActionResult::done(format!(
                "**The blacklist could not be read.**\n\n`{IP_FLAGS}` holds {} characters that did not parse, so this list \
                 is empty while the matcher may still be banning on what is in there. That is a corrupt or \
                 foreign-format row.\n\n**Clear every flag** on this panel rewrites it from scratch and fixes it.",
                raw.chars().count()
            ));
        }
        OwnerActionResult::done(
            "Nothing is blacklisted.\n\nThis is only what an owner blacklisted by hand. VPN screening and the \
             game's own ban list are separate - `/checkban <name>` says which one caught somebody.",
        )
    }

    /// Accounts sharing a CONFIRMED address with this name's account.
    #[must_use]
    pub fn alts(&self, name: &str) -> OwnerActionResult {
        let name = name.trim();
        if name.is_empty() {
            return OwnerActionResult::refused("No name given.");
        }
        let Some(account) = self.tracking.account_by_name(name) else {
            return OwnerActionResult::done(format!(
                "No account on record uses the name **{name}**."
            ));
        };
        let alts = self.tracking.alts_of(&account.id);
        if alts.is_empty() {
            return OwnerActionResult::done(format!(
                "**{name}** shares no confirmed address with any other account."
            ));
        }
        OwnerActionResult::list(
            format!(
                "**{name}** shares a confirmed address with **{}** account(s).",
                alts.len()
            ),
            alts.iter()
                .map(|a| format!("• **{}** — `{}`", display(a), a.id))
                .collect(),
        )
    }

    /// Un-flag an address, and undo the ufw deny a manual block created.
    pub async fn clear_address(&self, ip: &str) -> OwnerActionResult {
        let ip = ip.trim().to_owned();
        if ip.is_empty() {
            return OwnerActionResult::refused("No address given.");
        }
        let before = self.tracking.load_flags();
        let was_manual = before.manual_ips.contains(&ip);
        if !was_manual && !before.ips.contains(&ip) {
            return OwnerActionResult::done(format!("`{ip}` was not flagged. Nothing changed."));
        }
        let cleared = ip.clone();
        if let Err(error) = self
            .tracking
            .edit_flags(move |flags| {
                flags.ips.shift_remove(&cleared);
                flags.manual_ips.shift_remove(&cleared);
            })
            .await
        {
            return OwnerActionResult::refused(format!("The flag could not be cleared. ({error})"));
        }
        let done = format!("`{ip}` un-flagged. Anyone on that address can connect again.");
        match (&self.parts.firewall, was_manual) {
            (Some(firewall), true) => {
                let result = firewall.undeny(&ip).await;
                let note = if result.ok {
                    "🧱 The OS firewall (ufw) rule was removed too.".to_owned()
                } else {
                    format!(
                        "⚠️ The bot flag is gone, but the firewall rule may remain: {}",
                        result.detail
                    )
                };
                OwnerActionResult::done(format!("{done}\n{note}"))
            }
            _ => OwnerActionResult::done(done),
        }
    }

    pub async fn clear_flagged_names(&self) -> OwnerActionResult {
        let before = self.tracking.load_flags();
        if before.names.is_empty() {
            return OwnerActionResult::done("No usernames were flagged.");
        }
        match self
            .tracking
            .edit_flags(|flags| flags.names = Default::default())
            .await
        {
            Ok(()) => OwnerActionResult::done(format!(
                "**{}** username flag(s) cleared. Address and account flags are untouched.",
                before.names.len()
            )),
            Err(error) => OwnerActionResult::refused(format!("Nothing was cleared. ({error})")),
        }
    }

    /// Every flag AND every account record the tracker holds. Bans are untouched.
    pub async fn wipe_all_ip_data(&self) -> OwnerActionResult {
        let flags = self.tracking.load_flags();
        let accounts = self.count_map(KNOWN_PLAYERS);
        let written = async {
            self.store.write(IP_FLAGS, &FlagSet::default()).await?;
            self.store
                .write(KNOWN_PLAYERS, &IgnoreCaseMap::<Value>::new())
                .await
        };
        if let Err(error) = written.await {
            return OwnerActionResult::refused(format!("The wipe did not complete. ({error})"));
        }
        OwnerActionResult::done(format!(
            "Wiped **{accounts}** account record(s) and **{}** flag(s). Bans themselves are untouched - \
             this only clears what the evasion tracker knows.",
            flag_count(&flags)
        ))
    }

    pub async fn clear_all_flags(&self) -> OwnerActionResult {
        let count = flag_count(&self.tracking.load_flags());
        match self.store.write(IP_FLAGS, &FlagSet::default()).await {
            Ok(()) => OwnerActionResult::done(format!(
                "**{count}** address, account and name flag(s) cleared. Ban records are unchanged."
            )),
            Err(error) => OwnerActionResult::refused(format!("Nothing was cleared. ({error})")),
        }
    }

    // ---- the never-ban list ----

    /// Never auto-ban this player again, and lift what is already on them. The protection is
    /// written either way: it is the half that stops this recurring.
    pub async fn protect_player(&self, player: &str) -> OwnerActionResult {
        let player = player.trim();
        if player.is_empty() {
            return OwnerActionResult::refused("No player name given.");
        }
        let Some(masters) = &self.parts.masters else {
            return OwnerActionResult::refused(NO_NEVER_BAN);
        };
        if let Err(error) = masters.protect(player).await {
            return OwnerActionResult::refused(format!(
                "The never-ban list could not be saved. ({error})"
            ));
        }
        let mut lifted = false;
        if let Some(lift) = &self.parts.lift_ban {
            if let Err(error) = lift(player.to_owned()).await {
                return OwnerActionResult::done(format!(
                    "**{player}** will never be auto-banned again. The existing ban could NOT be lifted \
                     ({error}) - run `/unban {player}` to clear it."
                ));
            }
            lifted = true;
        }
        let tail = if lifted {
            " Any ban on them has been lifted and their evasion flags cleared."
        } else {
            " Run `/unban` if they are still banned."
        };
        OwnerActionResult::done(format!(
            "**{player}** will never be auto-banned again - not by evasion matching, not by VPN screening, \
             not by the enforcement sweep.{tail}\n\nA moderator can still ban them by hand."
        ))
    }

    pub async fn unprotect_player(&self, player: &str) -> OwnerActionResult {
        let player = player.trim();
        if player.is_empty() {
            return OwnerActionResult::refused("No player name given.");
        }
        let Some(masters) = &self.parts.masters else {
            return OwnerActionResult::refused(NO_NEVER_BAN);
        };
        if !masters.protected().contains_key(player) {
            return OwnerActionResult::done(format!("**{player}** was not on the never-ban list."));
        }
        match masters.unprotect(player).await {
            Ok(_) => OwnerActionResult::done(format!("**{player}** can be auto-banned again.")),
            Err(error) => OwnerActionResult::refused(format!(
                "The never-ban list could not be saved. ({error})"
            )),
        }
    }

    #[must_use]
    pub fn protected_players(&self) -> OwnerActionResult {
        let Some(masters) = &self.parts.masters else {
            return OwnerActionResult::refused(NO_NEVER_BAN);
        };
        let entries = masters.protected();
        if entries.is_empty() {
            return OwnerActionResult::done("Nobody is on the never-ban list.");
        }
        let mut sorted: Vec<(&str, i64)> =
            entries.iter().map(|(k, v)| (k, v.0.timestamp())).collect();
        sorted.sort_by(|a, b| pavlov_core::text::case::cmp_ignore_case(a.0, b.0));
        OwnerActionResult::list(
            format!("**{}** player(s) can never be auto-banned.", entries.len()),
            sorted
                .iter()
                .map(|(name, at)| format!("✅ **{name}** — since <t:{at}:R>"))
                .collect(),
        )
    }

    // ---- the command bar list ----

    /// Bar a Discord user from every command, by id or mention: a bare id is what an owner has
    /// once the person has left the server, which is exactly when barring them matters.
    pub async fn bar_user(&self, input: &str) -> OwnerActionResult {
        let Some(id) = user_id(input) else {
            return OwnerActionResult::refused(format!("`{input}` is not a Discord user id."));
        };
        let Some(blacklist) = &self.parts.blacklist else {
            return OwnerActionResult::refused(NO_BLACKLIST);
        };
        let result = blacklist.bar(id).await;
        match result.outcome {
            BarOutcome::Barred => {
                OwnerActionResult::done(format!("`{id}` is barred from every bot command."))
            }
            BarOutcome::AlreadyBarred => {
                OwnerActionResult::done(format!("`{id}` is already barred."))
            }
            BarOutcome::Owner => OwnerActionResult::refused(format!(
                "`{id}` is an owner. Owners can never be barred from the bot."
            )),
            _ => OwnerActionResult::refused(format!(
                "`{id}` was NOT barred: the bar list could not be saved. ({})",
                result.error.unwrap_or_default()
            )),
        }
    }

    pub async fn unbar_user(&self, input: &str) -> OwnerActionResult {
        let Some(id) = user_id(input) else {
            return OwnerActionResult::refused(format!("`{input}` is not a Discord user id."));
        };
        let Some(blacklist) = &self.parts.blacklist else {
            return OwnerActionResult::refused(NO_BLACKLIST);
        };
        let result = blacklist.unbar(id).await;
        match result.outcome {
            BarOutcome::Unbarred => {
                OwnerActionResult::done(format!("`{id}` may use bot commands again."))
            }
            BarOutcome::NotBarred => {
                OwnerActionResult::done(format!("`{id}` was not barred. Nothing changed."))
            }
            _ => OwnerActionResult::refused(format!(
                "`{id}` is STILL barred: the change could not be saved. ({})",
                result.error.unwrap_or_default()
            )),
        }
    }

    #[must_use]
    pub fn barred_users(&self) -> OwnerActionResult {
        let Some(blacklist) = &self.parts.blacklist else {
            return OwnerActionResult::refused(NO_BLACKLIST);
        };
        let barred = blacklist.entries();
        if barred.is_empty() {
            return OwnerActionResult::done("No Discord user is barred.");
        }
        OwnerActionResult::list(
            format!("**{}** barred Discord user(s).", barred.len()),
            barred
                .iter()
                .map(|u| {
                    let env = if u.from_environment {
                        " *(BLACKLIST_IDS)*"
                    } else {
                        ""
                    };
                    format!("• <@{}> — `{}`{env}", u.id, u.id)
                })
                .collect(),
        )
    }

    // ---- the tracking ignore list ----

    fn ignored(&self) -> Vec<String> {
        self.store
            .read(IGNORED_NAMES, Vec::new())
            .unwrap_or_default()
    }

    /// Stop recording a name's addresses. Existing records are kept.
    pub async fn ignore(&self, name: &str) -> OwnerActionResult {
        let name = name.trim();
        if name.is_empty() {
            return OwnerActionResult::refused("No name given.");
        }
        let mut ignored = self.ignored();
        if ignored.iter().any(|n| eq_ignore_case(n, name)) {
            return OwnerActionResult::done(format!("**{name}** is already ignored."));
        }
        ignored.push(name.to_owned());
        if let Err(error) = self.store.write(IGNORED_NAMES, &ignored).await {
            return OwnerActionResult::refused(format!(
                "The ignore list could not be saved. ({error})"
            ));
        }
        self.tracking.untrack(name);
        OwnerActionResult::done(format!(
            "**{name}**'s addresses will no longer be recorded. Existing records are kept - clear those with a full wipe."
        ))
    }

    pub async fn unignore(&self, name: &str) -> OwnerActionResult {
        let name = name.trim();
        if name.is_empty() {
            return OwnerActionResult::refused("No name given.");
        }
        let ignored = self.ignored();
        if !ignored.iter().any(|n| eq_ignore_case(n, name)) {
            return OwnerActionResult::done(format!("**{name}** was not ignored."));
        }
        let kept: Vec<String> = ignored
            .into_iter()
            .filter(|n| !eq_ignore_case(n, name))
            .collect();
        if let Err(error) = self.store.write(IGNORED_NAMES, &kept).await {
            return OwnerActionResult::refused(format!(
                "The ignore list could not be saved. ({error})"
            ));
        }
        self.tracking.retrack(name);
        OwnerActionResult::done(format!("**{name}** is tracked again."))
    }

    #[must_use]
    pub fn ignored_names(&self) -> OwnerActionResult {
        let ignored = self.ignored();
        if ignored.is_empty() {
            return OwnerActionResult::done("No name is ignored.");
        }
        OwnerActionResult::list(
            format!("**{}** ignored name(s).", ignored.len()),
            ignored.iter().map(|n| format!("• **{n}**")).collect(),
        )
    }

    /// Re-apply the stored ignore list at startup, so it survives a restart.
    pub fn restore_ignore_list(&self) {
        for name in self.ignored() {
            self.tracking.untrack(&name);
        }
    }

    // ---- whitelist snapshots ----

    fn snapshot_result(result: WhitelistBackupResult) -> OwnerActionResult {
        if result.ok {
            OwnerActionResult::done(result.message)
        } else {
            OwnerActionResult::refused(result.message)
        }
    }

    pub async fn save_whitelists(&self) -> OwnerActionResult {
        let Some(backup) = &self.parts.whitelists else {
            return OwnerActionResult::refused(NO_SNAPSHOTS);
        };
        match backup.save().await {
            Ok(result) => Self::snapshot_result(result),
            Err(error) => {
                OwnerActionResult::refused(format!("The snapshot could not be saved. ({error})"))
            }
        }
    }

    pub async fn load_whitelists(&self) -> OwnerActionResult {
        let Some(backup) = &self.parts.whitelists else {
            return OwnerActionResult::refused(NO_SNAPSHOTS);
        };
        Self::snapshot_result(backup.load().await)
    }

    // ---- destructive wipes ----

    /// Delete every player ledger. Failures are COUNTED: a file the game holds open keeps that
    /// player's money while everyone else loses theirs.
    #[must_use]
    pub fn wipe_money(&self) -> OwnerActionResult {
        let Some(directory) = self.parts.ledger_directory.as_ref().filter(|d| d.is_dir()) else {
            return OwnerActionResult::refused(
                "MODSAVE_PATH is not set, or does not exist. Nothing was deleted.",
            );
        };
        let Ok(entries) = std::fs::read_dir(directory) else {
            return OwnerActionResult::refused(
                "MODSAVE_PATH could not be read. Nothing was deleted.",
            );
        };
        let (mut deleted, mut failed) = (0, 0);
        for path in entries.flatten().map(|e| e.path()) {
            if path.extension().and_then(|e| e.to_str()) != Some("txt") {
                continue;
            }
            match std::fs::remove_file(&path) {
                Ok(()) => deleted += 1,
                Err(_) => failed += 1,
            }
        }
        OwnerActionResult::done(if failed == 0 {
            format!("Deleted **{deleted}** player ledger(s).")
        } else {
            format!(
                "Deleted **{deleted}** player ledger(s). **{failed}** could not be deleted - most likely held open by the game server."
            )
        })
    }

    fn count_map(&self, key: &str) -> usize {
        self.store
            .read::<IgnoreCaseMap<Value>>(key, IgnoreCaseMap::new())
            .map_or(0, |m| m.len())
    }

    /// Account records, playtime and last-seen. Bans, flags, warrants and whitelists stay.
    pub async fn wipe_player_data(&self) -> OwnerActionResult {
        let accounts = self.count_map(KNOWN_PLAYERS);
        let playtime = self.count_map(PLAYTIME);
        let last_seen = self.count_map(LAST_SEEN);
        for key in [KNOWN_PLAYERS, PLAYTIME, LAST_SEEN] {
            if let Err(error) = self.store.write(key, &IgnoreCaseMap::<Value>::new()).await {
                return OwnerActionResult::refused(format!(
                    "The wipe did not complete at `{key}`. ({error})"
                ));
            }
        }
        OwnerActionResult::done(format!(
            "Cleared **{accounts}** account record(s), **{playtime}** playtime entr(ies) and **{last_seen}** last-seen entr(ies).\n\
             Bans, blacklist flags, warrants, whitelists and donators were **not** touched."
        ))
    }

    pub async fn clear_temp_bans(&self) -> OwnerActionResult {
        self.clear_bans(
            |b| !b.permanent,
            "temporary ban(s)",
            "Permanent bans were kept.",
        )
        .await
    }

    /// The bot's record only: lifting the native bans too would unban everyone everywhere.
    pub async fn clear_all_bans(&self) -> OwnerActionResult {
        self.clear_bans(
            |_| true,
            "ban record(s)",
            "Native server bans are untouched - lift those with `/unban`.",
        )
        .await
    }

    /// One locked read-modify-write, so a ban issued meanwhile is not wiped with the rest. A
    /// Master Owner's ban is never bulk-cleared.
    async fn clear_bans(
        &self,
        clear: fn(&BanRecord) -> bool,
        what: &str,
        note: &str,
    ) -> OwnerActionResult {
        let master = |b: &BanRecord| b.tier == Some(StaffTier::MasterOwner);
        let counts = std::sync::Mutex::new((0usize, 0usize));
        let saved = self
            .store
            .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                let kept = bans.iter().filter(|b| clear(b) && master(b)).count();
                let before = bans.len();
                bans.retain(|b| !(clear(b) && !master(b)));
                let removed = before - bans.len();
                *counts
                    .lock()
                    .unwrap_or_else(std::sync::PoisonError::into_inner) = (removed, kept);
                (removed > 0).then_some(bans)
            })
            .await;
        match saved {
            Ok(result) if result.ok || result.is_vetoed() => {}
            Ok(result) => {
                return OwnerActionResult::refused(format!(
                    "The ban list could not be updated, so nothing was cleared. ({})",
                    result.error.unwrap_or_default()
                ));
            }
            Err(error) => {
                return OwnerActionResult::refused(format!(
                    "The ban list could not be updated, so nothing was cleared. ({error})"
                ));
            }
        }
        let (removed, kept) = *counts
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        let protected = if kept > 0 {
            format!(
                "\n**{kept}** Master Owner ban(s) were kept - only a Master Owner can lift those, with `/unban`."
            )
        } else {
            String::new()
        };
        OwnerActionResult::done(format!("**{removed}** {what} cleared. {note}{protected}"))
    }

    /// Every menu grant. Name bindings survive, or everybody could re-claim on an alt.
    pub async fn strip_all_menus(&self) -> OwnerActionResult {
        let count = self.count_map(MENU_GRANTS);
        match self
            .store
            .write(MENU_GRANTS, &IgnoreCaseMap::<Value>::new())
            .await
        {
            Ok(()) => OwnerActionResult::done(format!(
                "**{count}** menu grant(s) removed. Name bindings are unchanged."
            )),
            Err(error) => OwnerActionResult::refused(format!("Nothing was removed. ({error})")),
        }
    }
}
