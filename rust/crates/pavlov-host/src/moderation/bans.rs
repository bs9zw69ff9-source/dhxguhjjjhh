//! Issuing, enforcing and lifting bans (C# `Moderation/BanService.cs`).
//!
//! A BAN LIVES IN THE PLAYER'S SAVE (owner, 2026-11-19): `<ModSave>/<name>.json` gets
//! `"ban": { "banned": true, "reason": ..., "unban_date": "MM/DD/YY" }` (blank date:
//! permanent), and the mod refuses them. The bot sends NO RCON `Ban` or `Unban` and never
//! writes `blacklist.txt` any more; entries already in that file are left alone.
//!
//! - A player with no save yet (never joined) cannot be written: they are kicked on sight,
//!   and the sweep writes the ban the moment the game has created their file.
//! - The sweep re-writes every active ban that is not in its save. The mod rewrites an online
//!   player's save from memory, so a ban written while they were in game can be lost; this
//!   is what puts it back.
//! - `bot.db` (`temp_bans`) stays the record of who is banned, why, and until when; the save
//!   is where it is enforced. Lifting (by /unban or expiry) clears the save's block.
//!
//! THE NAME IS THE TARGET. The save is `<name>.json` and Shack's Kick acts on the display
//! name, so every ban, kick and lift names the same thing by construction. The account id is
//! bookkeeping for evasion flags and evidence, and a kick fallback only when no name exists.

use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Duration;

use futures_util::future::{BoxFuture, join_all};
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::{BanRecord, BanRules};
use pavlov_core::sync::{SaveBan, UNBAN_DATE_FORMAT};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::IgnoreCaseSet;
use pavlov_core::time::Clock;
use pavlov_storage::datasets::TEMP_BANS;
use pavlov_storage::ledger_store::{LedgerFileStore, SaveBanWrite};

use crate::registry::RconRegistry;

/// After a lift: long enough for anything lingering (a flag, a stale roster) to clear.
pub const LIFT_EXEMPTION: Duration = Duration::from_secs(60 * 60);
/// The reason written into a save when the record carries none.
const NO_REASON: &str = "No reason given";

/// What a ban, kick or lift did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EnforcementResult {
    /// Servers that accepted the kick (0 when no kick was sent).
    pub servers: usize,
    /// The name kicked, after sanitising.
    pub target: Option<String>,
    /// Whether this was a ban or a lift, which only takes effect in the save. A plain kick
    /// is not.
    pub writes_save: bool,
    /// The save write, for a ban or a lift. `None` when none was asked for, or no ModSave
    /// directory is configured.
    pub save: Option<SaveBanWrite>,
}

impl EnforcementResult {
    /// It took effect: the save holds the ban (or the lift), or, for a plain kick, a server
    /// accepted it. A ban whose save could not be written has NOT landed, kick or no kick.
    #[must_use]
    pub fn landed(&self) -> bool {
        if self.writes_save {
            self.save.as_ref().is_some_and(SaveBanWrite::holds)
        } else {
            self.servers > 0
        }
    }

    /// Where a ban stands, in a few words, for feeds, alerts and short replies.
    #[must_use]
    pub fn summary(&self) -> &'static str {
        match &self.save {
            Some(save) if save.holds() => "written to their save",
            Some(SaveBanWrite::NoSave) => {
                "no save yet - kicked, and written when the game creates their save"
            }
            Some(_) => "NOT written to their save - kicked; the sweep retries every 30 seconds",
            None => "recorded only - MODSAVE_PATH is not set, so there is no save to write",
        }
    }

    /// The ban waits for the game to create the player's save; they are kicked meanwhile.
    #[must_use]
    pub fn pending(&self) -> bool {
        self.save == Some(SaveBanWrite::NoSave)
    }
}

/// The ban block a record is written into a save as. The date is the server's local date the
/// ban ends on, in the mod's `MM/DD/YY`; blank is permanent. The field holds no time, so a
/// ban shorter than a day is lifted by the bot at its exact time, not by the date.
#[must_use]
pub fn save_ban_for(ban: &BanRecord) -> SaveBan {
    let unban_date = match (ban.permanent, ban.expires) {
        (false, Some(expires)) => expires
            .with_timezone(&chrono::Local)
            .format(UNBAN_DATE_FORMAT)
            .to_string(),
        _ => String::new(),
    };
    SaveBan {
        reason: ban
            .reason
            .as_deref()
            .map(str::trim)
            .filter(|r| !r.is_empty())
            .unwrap_or(NO_REASON)
            .to_owned(),
        unban_date,
    }
}

/// The names automation never touches (C# `IMasterNames`).
pub trait MasterNames: Send + Sync {
    fn is_master(&self, name: &str) -> bool;
    fn is_exempt(&self, name: &str) -> bool;
    fn is_protected(&self, name: &str) -> bool;
    fn exempt<'a>(&'a self, name: &'a str, duration: Option<Duration>) -> BoxFuture<'a, ()>;
}

/// The evasion flags a ban created (C# `IBanEvidence`).
pub trait BanEvidence: Send + Sync {
    fn account_id_for(&self, name: &str) -> Option<String>;
    fn clear_flags<'a>(&'a self, account_id: &'a str) -> BoxFuture<'a, Result<(), String>>;
}

fn summarise(reply: &str) -> String {
    let text = reply.split_whitespace().collect::<Vec<_>>().join(" ");
    if text.is_empty() {
        "(no response)".into()
    } else {
        text.chars().take(140).collect()
    }
}

/// The ban service.
pub struct BanService {
    rcon: Arc<RconRegistry>,
    store: Arc<SerializedStore>,
    masters: Arc<dyn MasterNames>,
    clock: Arc<dyn Clock>,
    evidence: Option<Arc<dyn BanEvidence>>,
    /// Where bans are written. Without it, a ban is only recorded and kicked.
    saves: Option<Arc<LedgerFileStore>>,
}

impl std::fmt::Debug for BanService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BanService").finish_non_exhaustive()
    }
}

impl BanService {
    #[must_use]
    pub fn new(
        rcon: Arc<RconRegistry>,
        store: Arc<SerializedStore>,
        masters: Arc<dyn MasterNames>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        Self {
            rcon,
            store,
            masters,
            clock,
            evidence: None,
            saves: None,
        }
    }

    #[must_use]
    pub fn with_evidence(mut self, evidence: Arc<dyn BanEvidence>) -> Self {
        self.evidence = Some(evidence);
        self
    }

    /// The ModSave store bans are written into.
    #[must_use]
    pub fn with_saves(mut self, saves: Arc<LedgerFileStore>) -> Self {
        self.saves = Some(saves);
        self
    }

    /// Valid records only. A corrupt entry enforces nothing either way; failing the whole
    /// list for it would unban everybody.
    #[must_use]
    pub fn load_bans(&self) -> Vec<BanRecord> {
        let all = match self.store.read::<Vec<BanRecord>>(TEMP_BANS, Vec::new()) {
            Ok(all) => all,
            Err(error) => {
                tracing::error!(%error, "could not read the ban list");
                return Vec::new();
            }
        };
        let total = all.len();
        let valid: Vec<BanRecord> = all.into_iter().filter(BanRecord::is_valid).collect();
        if valid.len() != total {
            tracing::error!(
                bad = total - valid.len(),
                total,
                "ban records are unusable and enforce nothing until repaired"
            );
        }
        valid
    }

    #[must_use]
    pub fn active_bans(&self) -> Vec<BanRecord> {
        let now = self.clock.now();
        self.load_bans()
            .into_iter()
            .filter(|b| b.is_active_at(now))
            .collect()
    }

    #[must_use]
    pub fn server_count(&self) -> usize {
        self.rcon.servers().len()
    }

    /// The sanitized RCON target, or `None` (logged) when nothing usable is left.
    fn target(name: &str, unique_id: Option<&str>, what: &str) -> Option<String> {
        let source = if name.is_empty() {
            unique_id.unwrap_or_default()
        } else {
            name
        };
        let target = Sanitize::id(Some(source));
        if target.is_empty() {
            tracing::error!(
                name,
                unique_id,
                "{what} has no usable RCON target: the identifier sanitized to empty"
            );
            return None;
        }
        if source != target {
            // Loud: the alternative is a ban that appears to succeed.
            tracing::warn!(source, target = %target, "RCON target rewritten (the sanitizer stripped characters) - if this does not land, the mismatch is why");
        }
        Some(target)
    }

    /// The ban `name`'s save holds right now, whoever wrote it (the bot, the mod, or a hand
    /// edit). `None` when it holds none or no ModSave directory is configured.
    #[must_use]
    pub fn save_ban(&self, name: &str) -> Option<SaveBan> {
        self.saves.as_ref()?.ban(name)
    }

    /// Whether bans can be written at all (a ModSave directory is configured).
    #[must_use]
    pub fn writes_saves(&self) -> bool {
        self.saves.as_ref().is_some_and(|s| s.directory().is_some())
    }

    /// The newest active record for `name`.
    fn active_record(&self, name: &str) -> Option<BanRecord> {
        self.active_bans()
            .into_iter()
            .filter(|b| BanRules::same_player(Some(&b.player_id), Some(name)))
            .max_by_key(|b| b.at)
    }

    /// Write `ban` into its player's save (logged when something changed or failed).
    fn write_ban(&self, ban: &BanRecord) -> Option<SaveBanWrite> {
        let saves = self.saves.as_ref()?;
        let outcome = saves.set_ban(&ban.player_id, Some(&save_ban_for(ban)));
        match &outcome {
            SaveBanWrite::Written => {
                tracing::info!(player = %ban.player_id, "ban written to their save");
            }
            SaveBanWrite::Refused(reason) => {
                tracing::error!(player = %ban.player_id, reason, "ban NOT written to their save - the game will not refuse them");
            }
            SaveBanWrite::AlreadySo | SaveBanWrite::NoSave => {}
        }
        Some(outcome)
    }

    /// `Kick <target>` on every server; the number that accepted. Not verified: a refused kick
    /// means they are not on that server.
    async fn kick_everywhere(&self, target: &str) -> usize {
        let command = format!("Kick {target}");
        let accepted = AtomicUsize::new(0);
        join_all(self.rcon.servers().iter().map(|server| {
            let (command, accepted) = (&command, &accepted);
            async move {
                match self.rcon.send(server, command).await {
                    Ok(reply) => {
                        accepted.fetch_add(1, Ordering::SeqCst);
                        tracing::info!(server, command, reply = %summarise(&reply), "sent");
                    }
                    Err(error) => tracing::warn!(server, command, %error, "FAILED"),
                }
            }
        }))
        .await;
        accepted.into_inner()
    }

    /// Ban (write the active record into their save) and/or kick on every server. With no
    /// save to write yet, they are kicked whatever `kick` says: that is the only way to keep
    /// them out until the game creates the file.
    pub async fn hard_enforce(
        &self,
        name: &str,
        unique_id: Option<&str>,
        ban: bool,
        kick: bool,
    ) -> EnforcementResult {
        let target = Self::target(name, unique_id, "enforce");
        let save = if ban {
            match self.active_record(name) {
                Some(record) => self.write_ban(&record),
                None => {
                    tracing::error!(
                        name,
                        "asked to enforce a ban with no active record - nothing to write"
                    );
                    Some(SaveBanWrite::Refused(
                        "there is no active ban record".into(),
                    ))
                }
            }
        } else {
            None
        };
        let must_kick = kick || save == Some(SaveBanWrite::NoSave);
        let servers = match (&target, must_kick) {
            (Some(target), true) => self.kick_everywhere(target).await,
            _ => 0,
        };
        if save == Some(SaveBanWrite::NoSave) {
            tracing::warn!(
                name,
                kicked_on = servers,
                "no save yet - kicked; the ban is written once the game creates their file"
            );
        }
        EnforcementResult {
            servers,
            target,
            writes_save: ban,
            save,
        }
    }

    fn untouchable(&self, name: &str) -> bool {
        self.masters.is_master(name)
            || self.masters.is_protected(name)
            || self.masters.is_exempt(name)
    }

    /// Keep every active ban in force: write any that its save does not hold (a pending ban
    /// whose file now exists, or one the game overwrote), and kick every banned player who is
    /// online. The number kicked.
    pub async fn enforce_sweep(&self) -> usize {
        let active = self.active_bans();
        for ban in &active {
            if !self.untouchable(&ban.player_id) {
                let _ = self.write_ban(ban);
            }
        }
        let banned: IgnoreCaseSet = active.into_iter().map(|b| b.player_id).collect();
        let mut handled = IgnoreCaseSet::default();
        let mut removed = 0;
        for server in self.rcon.servers() {
            for player in self.rcon.roster(&server).players {
                let name = player.name.clone();
                if name.is_empty() || self.untouchable(&name) || !handled.insert(name.clone()) {
                    continue;
                }
                if !banned.contains(&name) {
                    continue;
                }
                let uid = (!player.unique_id.is_empty()).then_some(player.unique_id.as_str());
                tracing::warn!(name = %name, uid = uid.unwrap_or("unknown"), server, "ENFORCING - online but matches a ban record");
                let Some(target) = Self::target(&name, uid, "kick") else {
                    continue;
                };
                if self.kick_everywhere(&target).await > 0 {
                    removed += 1;
                }
            }
        }
        removed
    }

    /// End a ban: the ONE path both /unban and expiry take, so they do the same things - remove
    /// every record, clear the evasion flags, exempt briefly, and clear the ban from their save.
    pub async fn lift(&self, name: &str, unique_id: Option<&str>) -> EnforcementResult {
        if let Err(error) = self
            .store
            .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                // EVERY record: a second one would re-catch them on the next sweep.
                let before = bans.len();
                bans.retain(|b| !BanRules::same_player(Some(&b.player_id), Some(name)));
                (bans.len() != before).then_some(bans)
            })
            .await
        {
            tracing::error!(name, %error, "could not remove the ban record");
        }

        let account = unique_id
            .map(str::to_owned)
            .or_else(|| self.evidence.as_ref().and_then(|e| e.account_id_for(name)));
        if let (Some(account), Some(evidence)) = (&account, &self.evidence) {
            /* LOUD, and it does not abort the lift: the native unban is what the player waits
            on. But surviving flags WILL re-ban them, so this line is the only warning. */
            if let Err(error) = evidence.clear_flags(account).await {
                tracing::error!(name, account, %error, "could not clear the evasion flags - they may be auto-banned again on their next connection; clear them by hand with /configure");
            }
        }

        self.masters.exempt(name, Some(LIFT_EXEMPTION)).await;

        // No save means nothing in a file holds the ban: as lifted as it gets.
        let save = self
            .saves
            .as_ref()
            .map(|saves| match saves.set_ban(name, None) {
                SaveBanWrite::NoSave => SaveBanWrite::AlreadySo,
                other => other,
            });
        match &save {
            Some(SaveBanWrite::Written) => tracing::info!(name, "ban cleared from their save"),
            Some(SaveBanWrite::Refused(reason)) => {
                tracing::error!(
                    name,
                    reason,
                    "the ban could NOT be cleared from their save - the game may still refuse them"
                );
            }
            _ => {}
        }
        EnforcementResult {
            servers: 0,
            target: Self::target(name, account.as_deref(), "lift"),
            writes_save: true,
            save,
        }
    }

    /// Lift every expired temp ban through [`lift`](Self::lift). The bans lifted.
    pub async fn process_expired(&self) -> Vec<BanRecord> {
        let now = self.clock.now();
        let expired: Vec<BanRecord> = self
            .load_bans()
            .into_iter()
            .filter(|b| !b.permanent && b.expires.is_some_and(|e| e <= now))
            .collect();
        for ban in &expired {
            let result = self.lift(&ban.player_id, ban.unique_id.as_deref()).await;
            tracing::info!(player = %ban.player_id, servers = result.servers, "expired ban lifted");
        }
        expired
    }
}
