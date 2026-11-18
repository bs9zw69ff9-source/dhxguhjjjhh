//! Player balances and licences in the game's own saves, `<MODSAVE_PATH>/<playername>.json`
//! (C# `Host/Economy/LedgerFileStore.cs`, moved to the JSON saves on 2026-11-18).
//!
//! The highest-stakes writes the bot makes - somebody's money - so every one passes the same
//! gate: a configured directory that exists, an existing save (the bot never creates one; the
//! game does), a shape the bot recognises ([`PlayerSave`]), and `IGNORE_PATHS`. An edit changes
//! only the field it is about, stamps `updated`, and keeps every other field.
//!
//! THERE IS NO ONLINE GATE (owner, 2026-11-18). The mod syncs a player's in-game state to the
//! file live and reads it back, so a save may be edited while they are in game. The C# refused
//! then, because the old mod held the save in memory and overwrote anything written.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, PoisonError};

use pavlov_core::economy::BalanceStore;
use pavlov_core::licensing::License;
use pavlov_core::sync::{PlayerSave, UPDATED_FORMAT};
use pavlov_core::text::case::{eq_ignore_case, fold_case};

use crate::atomic_file;
use crate::game_files::GameFileGuard;

const STRIPES: usize = 64;
const EXTENSION: &str = "json";
const UNRECOGNISED: &str =
    "their save file is not in a shape the bot recognises, so it will not rewrite it";

/// How a licence change went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LicenseOutcome {
    /// At least one licence changed.
    Changed,
    /// Every one named was already in the asked state.
    AlreadySo,
    /// The gate refused, or the write failed; see the reason.
    Refused,
}

/// The licences changed, and why not when refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LicenseChange {
    pub outcome: LicenseOutcome,
    pub changed: Vec<License>,
    pub reason: Option<String>,
}

impl LicenseChange {
    fn refused(reason: impl Into<String>) -> Self {
        Self {
            outcome: LicenseOutcome::Refused,
            changed: Vec::new(),
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

/// The local time `updated` is stamped with.
pub type Stamp = Arc<dyn Fn() -> String + Send + Sync>;

/// The host's local time in the mod's format: the mod writes the server's own clock.
#[must_use]
pub fn local_stamp() -> Stamp {
    Arc::new(|| chrono::Local::now().format(UPDATED_FORMAT).to_string())
}

/// The store over one ModSave directory.
pub struct LedgerFileStore {
    directory: Option<PathBuf>,
    guard: Arc<GameFileGuard>,
    stamp: Stamp,
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

fn read_save(path: &Path) -> Option<PlayerSave> {
    PlayerSave::parse(fs::read_to_string(path).ok().as_deref())
}

/// What a name may contribute to a file name: path separators and control characters go.
fn safe_name(player_id: &str) -> Option<String> {
    let safe: String = player_id
        .chars()
        .filter(|c| !c.is_control() && *c != '/' && *c != '\\')
        .collect();
    let safe = safe.trim();
    (!safe.is_empty() && safe != "." && safe != "..").then(|| safe.to_owned())
}

impl LedgerFileStore {
    #[must_use]
    pub fn new(directory: Option<PathBuf>, guard: Arc<GameFileGuard>) -> Self {
        Self {
            directory: directory.filter(|d| !d.as_os_str().is_empty()),
            guard,
            stamp: local_stamp(),
            stripes: (0..STRIPES).map(|_| Mutex::new(())).collect(),
        }
    }

    /// A fixed `updated` stamp (tests).
    #[must_use]
    pub fn with_stamp(mut self, stamp: Stamp) -> Self {
        self.stamp = stamp;
        self
    }

    /// The directory, when configured.
    #[must_use]
    pub fn directory(&self) -> Option<&Path> {
        self.directory.as_deref()
    }

    fn stripe_for(&self, player_id: &str) -> &Mutex<()> {
        use std::hash::{Hash, Hasher};
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        fold_case(player_id.trim()).hash(&mut hasher);
        let index = usize::try_from(hasher.finish() % STRIPES as u64).unwrap_or(0);
        &self.stripes[index]
    }

    /// The save for a player: `<name>.json`, or, when that exact file is not there, the one
    /// file whose name matches ignoring case. The disk is case-sensitive and the bot's copy of
    /// a name can differ in case from the game's. Two files differing only by case is
    /// ambiguous, so neither is picked and the exact path is returned.
    #[must_use]
    pub fn path_for(&self, player_id: &str) -> Option<PathBuf> {
        let directory = self.directory.as_ref()?;
        let name = safe_name(player_id)?;
        let exact = directory.join(format!("{name}.{EXTENSION}"));
        if exact.exists() {
            return Some(exact);
        }
        let wanted = format!("{name}.{EXTENSION}");
        let matches: Vec<PathBuf> = fs::read_dir(directory)
            .map(|entries| {
                entries
                    .flatten()
                    .filter(|e| {
                        e.file_name()
                            .to_str()
                            .is_some_and(|n| eq_ignore_case(n, &wanted))
                    })
                    .map(|e| e.path())
                    .collect()
            })
            .unwrap_or_default();
        match matches.as_slice() {
            [only] => Some(only.clone()),
            _ => Some(exact),
        }
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
        if !path.exists() {
            return Some("they have no save file yet. The bot never creates one - the game does that the first time they join".into());
        }
        None
    }

    /// Why a balance write would be refused, or `None` when it would go ahead.
    #[must_use]
    pub fn refusal(&self, player_id: &str) -> Option<String> {
        if let Some(refused) = self.gate(player_id) {
            return Some(refused);
        }
        let readable = self.path_for(player_id).and_then(|p| read_save(&p));
        readable.is_none().then(|| UNRECOGNISED.to_owned())
    }

    /// Whether a balance write would go ahead.
    #[must_use]
    pub fn can_write(&self, player_id: &str) -> bool {
        self.refusal(player_id).is_none()
    }

    /// Give (`held = true`) or take (`false`) these licences, touching nothing else.
    #[must_use]
    pub fn set_licenses(&self, player_id: &str, licenses: &[License], held: bool) -> LicenseChange {
        if let Some(refused) = self.gate(player_id) {
            return LicenseChange::refused(refused);
        }
        let mut changed = Vec::new();
        let outcome = self.edit(player_id, |save| {
            changed = licenses
                .iter()
                .copied()
                .filter(|l| save.license(l.key).unwrap_or(false) != held)
                .collect();
            if changed.is_empty() {
                return None;
            }
            Some(
                changed
                    .iter()
                    .fold(save.clone(), |edited, l| edited.with_license(l.key, held)),
            )
        });
        match outcome {
            EditOutcome::Written => LicenseChange {
                outcome: LicenseOutcome::Changed,
                changed,
                reason: None,
            },
            EditOutcome::Unchanged => LicenseChange {
                outcome: LicenseOutcome::AlreadySo,
                changed: Vec::new(),
                reason: None,
            },
            EditOutcome::Unreadable => LicenseChange::refused(UNRECOGNISED),
            EditOutcome::Refused | EditOutcome::Failed => LicenseChange::refused(
                self.gate(player_id)
                    .or_else(|| {
                        self.path_for(player_id)
                            .and_then(|p| self.guard.problem(Some(&p.to_string_lossy())))
                    })
                    .unwrap_or_else(|| "the save file did not accept the write".into()),
            ),
        }
    }

    /// Take these licences away.
    #[must_use]
    pub fn remove_licenses(&self, player_id: &str, licenses: &[License]) -> LicenseChange {
        self.set_licenses(player_id, licenses, false)
    }

    /// Give these licences.
    #[must_use]
    pub fn give_licenses(&self, player_id: &str, licenses: &[License]) -> LicenseChange {
        self.set_licenses(player_id, licenses, true)
    }

    fn edit(
        &self,
        player_id: &str,
        change: impl FnOnce(&PlayerSave) -> Option<PlayerSave>,
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
        let edited = edited.touched(&(self.stamp)());
        // TEMP THEN RENAME: the game reads these files, and must never see half of one.
        match atomic_file::write_text(&path, &edited.render()) {
            Ok(()) => EditOutcome::Written,
            Err(error) => {
                tracing::warn!(path = %path.display(), %error, "save write failed");
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
            self.edit(player_id, |save| Some(save.with_money(balance))),
            EditOutcome::Written
        )
    }
}

/// Every save in a ModSave directory, as (file stem, save). Other files - other mods' JSON,
/// the role lists - are skipped because they do not parse as a save.
#[must_use]
pub fn all_saves(directory: &Path) -> Vec<(String, PathBuf, PlayerSave)> {
    let Ok(entries) = fs::read_dir(directory) else {
        return Vec::new();
    };
    let mut saves: Vec<(String, PathBuf, PlayerSave)> = entries
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.is_file() && p.extension().and_then(|e| e.to_str()) == Some(EXTENSION))
        .filter_map(|p| {
            let stem = p.file_stem()?.to_str()?.to_owned();
            let save = read_save(&p)?;
            Some((stem, p, save))
        })
        .collect();
    saves.sort_by(|a, b| a.0.cmp(&b.0));
    saves
}
