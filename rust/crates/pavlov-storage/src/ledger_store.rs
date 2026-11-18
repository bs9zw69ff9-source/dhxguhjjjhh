//! Player balances in the game's own ModSave files (C# `Host/Economy/LedgerFileStore.cs`).
//!
//! The highest-stakes writes the bot makes - somebody's money - so every one passes the same
//! gate: a configured directory, a TRUSTWORTHY roster that says the player is NOT in game (the
//! server holds an online player's save in memory and overwrites anything written now), an
//! existing file (the bot never creates one; the game does when they first bank), a shape the
//! bot recognises, and `IGNORE_PATHS`. Edits change one value and keep every other byte.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, PoisonError};

use pavlov_core::economy::BalanceStore;
use pavlov_core::licensing::License;
use pavlov_core::sync::ModSaveFile;
use pavlov_core::text::case::{eq_ignore_case, fold_case};
use pavlov_rcon::OnlineRoster;

use crate::atomic_file;
use crate::game_files::GameFileGuard;

const STRIPES: usize = 64;
const UNRECOGNISED: &str =
    "their save file is not in a shape the bot recognises, so it will not rewrite it";

/// How a licence removal went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LicenseOutcome {
    /// At least one held licence was taken.
    Removed,
    /// The file has the licences, and none was held.
    NoneHeld,
    /// The file has no such licence key at all (a bare save, or another mod's layout).
    NotInFile,
    /// The gate refused, or the write failed; see the reason.
    Refused,
}

/// The licences removed, those the file knows about, and why not when refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LicenseRemoval {
    pub outcome: LicenseOutcome,
    pub removed: Vec<License>,
    pub known: Vec<License>,
    pub reason: Option<String>,
}

impl LicenseRemoval {
    fn refused(reason: impl Into<String>) -> Self {
        Self {
            outcome: LicenseOutcome::Refused,
            removed: Vec::new(),
            known: Vec::new(),
            reason: Some(reason.into()),
        }
    }
}

enum EditOutcome {
    Refused,
    Unreadable,
    Unchanged,
    Written,
    Failed,
}

/// The store over one ModSave directory.
pub struct LedgerFileStore {
    directory: Option<PathBuf>,
    online: Option<Arc<dyn OnlineRoster>>,
    guard: Arc<GameFileGuard>,
    /// Per-player writes are serialised in-process; there is no lock between processes, which
    /// is why `IGNORE_PATHS` exists.
    stripes: Vec<Mutex<()>>,
}

impl std::fmt::Debug for LedgerFileStore {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LedgerFileStore")
            .field("directory", &self.directory)
            .finish_non_exhaustive()
    }
}

fn read_save(path: &Path) -> Option<ModSaveFile> {
    ModSaveFile::parse(fs::read_to_string(path).ok().as_deref())
}

impl LedgerFileStore {
    #[must_use]
    pub fn new(
        directory: Option<PathBuf>,
        online: Option<Arc<dyn OnlineRoster>>,
        guard: Arc<GameFileGuard>,
    ) -> Self {
        Self {
            directory: directory.filter(|d| !d.as_os_str().is_empty()),
            online,
            guard,
            stripes: (0..STRIPES).map(|_| Mutex::new(())).collect(),
        }
    }

    fn stripe_for(&self, player_id: &str) -> &Mutex<()> {
        use std::hash::{Hash, Hasher};
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        fold_case(player_id.trim()).hash(&mut hasher);
        let index = usize::try_from(hasher.finish() % STRIPES as u64).unwrap_or(0);
        &self.stripes[index]
    }

    /// The file for a player. Names may contain spaces ("Butter Life.txt") and must match what
    /// the GAME writes, so only path separators and control characters are stripped.
    #[must_use]
    pub fn path_for(&self, player_id: &str) -> Option<PathBuf> {
        let directory = self.directory.as_ref()?;
        let safe: String = player_id
            .chars()
            .filter(|c| !c.is_control() && *c != '/' && *c != '\\')
            .collect();
        let safe = safe.trim();
        (!safe.is_empty()).then(|| directory.join(format!("{safe}.txt")))
    }

    fn gate(&self, player_id: &str) -> Option<String> {
        let Some(path) = self.path_for(player_id) else {
            return Some("there is no ledger directory configured".into());
        };
        if let Some(directory) = self.directory.as_ref().filter(|d| !d.is_dir()) {
            return Some(format!(
                "the ledger directory `{}` does not exist",
                directory.display()
            ));
        }
        // No roster at all is a context with no game servers - refuse rather than assume.
        let Some(online) = &self.online else {
            return Some("the bot cannot see who is online, so it will not write a ledger".into());
        };
        if !online.is_trustworthy() {
            return Some(
                "no server has a fresh player list, so the bot cannot tell whether they are online"
                    .into(),
            );
        }
        if online
            .online()
            .iter()
            .any(|p| eq_ignore_case(p, player_id.trim()))
        {
            return Some("they are currently in game. The server holds their save in memory and would overwrite anything written now - try again once they log off".into());
        }
        if !path.exists() {
            return Some("they have no ledger file yet. The bot never creates one - the game does that when they first bank".into());
        }
        None
    }

    /// Why a balance write would be refused, or `None` when it would go ahead.
    #[must_use]
    pub fn refusal(&self, player_id: &str) -> Option<String> {
        if let Some(refused) = self.gate(player_id) {
            return Some(refused);
        }
        let shaped = self
            .path_for(player_id)
            .and_then(|p| read_save(&p))
            .and_then(|s| s.with_money(0));
        shaped.is_none().then(|| UNRECOGNISED.to_owned())
    }

    /// Whether a balance write would go ahead.
    #[must_use]
    pub fn can_write(&self, player_id: &str) -> bool {
        self.refusal(player_id).is_none()
    }

    /// Take these licences away (write them `false`), touching nothing else.
    #[must_use]
    pub fn remove_licenses(&self, player_id: &str, licenses: &[License]) -> LicenseRemoval {
        if let Some(refused) = self.gate(player_id) {
            return LicenseRemoval::refused(refused);
        }
        let mut held = Vec::new();
        let mut known = Vec::new();
        let outcome = self.edit(player_id, |save| {
            known = licenses
                .iter()
                .copied()
                .filter(|l| save.get(l.key).is_some())
                .collect();
            held = known
                .iter()
                .copied()
                .filter(|l| save.is_true(l.key))
                .collect();
            if held.is_empty() {
                return None;
            }
            held.iter().try_fold(save.clone(), |edited, license| {
                edited.with(license.key, "false").ok()
            })
        });
        match outcome {
            EditOutcome::Written => LicenseRemoval {
                outcome: LicenseOutcome::Removed,
                removed: held,
                known,
                reason: None,
            },
            EditOutcome::Unchanged if known.is_empty() => LicenseRemoval {
                outcome: LicenseOutcome::NotInFile,
                removed: Vec::new(),
                known: Vec::new(),
                reason: Some("their save file has no such license in it".into()),
            },
            EditOutcome::Unchanged => LicenseRemoval {
                outcome: LicenseOutcome::NoneHeld,
                removed: Vec::new(),
                known,
                reason: None,
            },
            EditOutcome::Unreadable => LicenseRemoval::refused(UNRECOGNISED),
            EditOutcome::Refused | EditOutcome::Failed => LicenseRemoval::refused(
                self.gate(player_id)
                    .or_else(|| {
                        self.path_for(player_id)
                            .and_then(|p| self.guard.problem(Some(&p.to_string_lossy())))
                    })
                    .unwrap_or_else(|| "the save file did not accept the write".into()),
            ),
        }
    }

    fn edit(
        &self,
        player_id: &str,
        change: impl FnOnce(&ModSaveFile) -> Option<ModSaveFile>,
    ) -> EditOutcome {
        if self.gate(player_id).is_some() {
            return EditOutcome::Refused;
        }
        let Some(path) = self.path_for(player_id) else {
            return EditOutcome::Refused;
        };
        // Two bots against one install both rewriting a save lose updates; IGNORE_PATHS is the
        // way one of them stands aside.
        if self.guard.problem(Some(&path.to_string_lossy())).is_some() {
            return EditOutcome::Refused;
        }
        let _held = self
            .stripe_for(player_id)
            .lock()
            .unwrap_or_else(PoisonError::into_inner);
        let Some(save) = read_save(&path) else {
            return EditOutcome::Unreadable;
        };
        let Some(edited) = change(&save) else {
            return EditOutcome::Unchanged;
        };
        // TEMP THEN RENAME: the game reads these files, and must never see half of one.
        match atomic_file::write_text(&path, &edited.to_string()) {
            Ok(()) => EditOutcome::Written,
            Err(error) => {
                tracing::warn!(path = %path.display(), %error, "ledger write failed");
                EditOutcome::Failed
            }
        }
    }
}

impl BalanceStore for LedgerFileStore {
    /// `None` means UNKNOWN; returning 0 would let a transient read error wipe a balance.
    fn read(&self, player_id: &str) -> Option<i64> {
        self.path_for(player_id)
            .and_then(|p| read_save(&p))
            .and_then(|s| s.money())
    }

    fn write(&self, player_id: &str, balance: i64) -> bool {
        matches!(
            self.edit(player_id, |save| save.with_money(balance)),
            EditOutcome::Written
        )
    }
}
