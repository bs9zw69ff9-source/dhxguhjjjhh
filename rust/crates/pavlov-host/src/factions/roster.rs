//! Reading and writing the game's roster files, with the membership rules enforced (C#
//! `Factions/RosterService.cs`).
//!
//! The rosters are plain `.txt` files the game reads LIVE, one name per line. Two consequences
//! shape everything here:
//!
//! - THE STORAGE CANNOT EXPRESS THE RULES. Nothing stops a name appearing in six files, so one
//!   faction per player and one rank are enforced here, before anything is written.
//!   `MembershipRules` (pavlov-core) holds the decisions; this holds the I/O.
//! - A BAD WRITE IS LIVE IMMEDIATELY. Every write goes through `RosterWriteGuard`, which refuses
//!   one that would delete more than a handful of entries, and a pre-write copy is kept outside
//!   the game tree so a bad write is recoverable by hand.
//!
//! Every read-modify-write is serialised per FILE: two commands editing one roster would
//! otherwise write over each other and silently drop a member.

use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, PoisonError};

use indexmap::IndexMap;
use pavlov_core::data::RosterWriteGuard;
use pavlov_core::factions::{
    FactionDefinition, FactionRegistry, FactionSet, MembershipDecision, MembershipOutcome,
    MembershipRules,
};
use pavlov_core::text::case::{
    IgnoreCaseMap, IgnoreCaseSet, cmp_ignore_case, eq_ignore_case, fold_case,
};
use pavlov_storage::atomic_file;
use pavlov_storage::game_files::{GameFileGuard, create_owned_like_parent};

/// Where a player sits. `rank` is the highest rank whose file holds them.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Membership {
    /// The faction's current name; look the definition up in [`RosterService::factions`].
    pub faction: String,
    pub player: String,
    pub rank: String,
}

/// What creating the missing roster files at startup did. A count was not enough: zero created
/// is the answer when the feature is off, when everything was there, and when nothing could be
/// written, and an operator needs to be told which.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RosterFileReport {
    /// One line, safe to log as-is.
    pub summary: String,
    pub expected: usize,
    pub created: usize,
    pub present: usize,
    /// `"file: reason"` for each one that could not be created.
    pub failed: Vec<String>,
}

/// The result of emptying a faction's rosters.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RosterWipe {
    pub outcome: MembershipOutcome,
    /// Distinct members cleared, not rows deleted.
    pub removed: usize,
    /// Files that could not be read or written, and so still hold members.
    pub failed: Vec<String>,
}

/// Whether a player keeps every rank at or below their own (the hold-ranks preference).
pub type HoldsAllRanks = Arc<dyn Fn(&str) -> bool + Send + Sync>;

fn decision(
    outcome: MembershipOutcome,
    rank: Option<&str>,
    conflict: Option<&str>,
) -> MembershipDecision {
    MembershipDecision {
        outcome,
        rank: rank.map(str::to_owned),
        conflict: conflict.map(str::to_owned),
    }
}

fn holds(list: &[String], player: &str) -> bool {
    list.iter().any(|p| eq_ignore_case(p, player))
}

fn without(list: &[String], player: &str) -> Vec<String> {
    list.iter()
        .filter(|p| !eq_ignore_case(p, player))
        .cloned()
        .collect()
}

/// Every roster file a faction owns: ranks, sub-classes and the spawn file. The spawn file MUST
/// be in any removal: a member left in it reads as removed everywhere staff look while the game
/// still lets them play. Distinct, because a ladderless faction names one file twice.
fn files_of(faction: &FactionDefinition) -> Vec<String> {
    let mut seen = IgnoreCaseSet::new();
    faction
        .rank_files
        .values()
        .chain(faction.subclasses.values())
        .chain(std::iter::once(&faction.spawn_file))
        .filter(|f| seen.insert(f.as_str()))
        .cloned()
        .collect()
}

/// The service.
pub struct RosterService {
    directory: Option<PathBuf>,
    backup_directory: PathBuf,
    factions: FactionSet,
    guard: Arc<GameFileGuard>,
    holds_all_ranks: Option<HoldsAllRanks>,
    gates: Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
}

impl std::fmt::Debug for RosterService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RosterService")
            .field("directory", &self.directory)
            .finish_non_exhaustive()
    }
}

impl RosterService {
    /// Rosters in `directory` (`None`: the feature is off), pre-write copies in `backup_directory`.
    #[must_use]
    pub fn new(directory: Option<PathBuf>, backup_directory: PathBuf) -> Self {
        Self {
            directory: directory.filter(|d| !d.as_os_str().is_empty()),
            backup_directory,
            factions: FactionRegistry::default_set().clone(),
            guard: Arc::new(GameFileGuard::none()),
            holds_all_ranks: None,
            gates: Mutex::new(HashMap::new()),
        }
    }

    #[must_use]
    pub fn with_factions(mut self, factions: FactionSet) -> Self {
        self.factions = factions;
        self
    }

    #[must_use]
    pub fn with_guard(mut self, guard: Arc<GameFileGuard>) -> Self {
        self.guard = guard;
        self
    }

    /// The hold-ranks preference, read through a callback because only one caller has the
    /// member index it lives in. Unset is one rank file per member.
    #[must_use]
    pub fn with_holds_all_ranks(mut self, holds: HoldsAllRanks) -> Self {
        self.holds_all_ranks = Some(holds);
        self
    }

    /// The factions this bot runs.
    #[must_use]
    pub fn factions(&self) -> &FactionSet {
        &self.factions
    }

    /// Whether the roster directory is configured AND exists.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.directory.as_deref().is_some_and(Path::is_dir)
    }

    fn path_for(&self, file: &str) -> Option<PathBuf> {
        self.directory.as_ref().map(|d| d.join(file))
    }

    fn gate(&self, file: &str) -> Arc<tokio::sync::Mutex<()>> {
        Arc::clone(
            self.gates
                .lock()
                .unwrap_or_else(PoisonError::into_inner)
                .entry(fold_case(file))
                .or_default(),
        )
    }

    /// Every roster file the factions own, de-duplicated and sorted so the startup line reads
    /// the same every time.
    #[must_use]
    pub fn roster_files_of(factions: &FactionSet) -> Vec<String> {
        let mut seen = IgnoreCaseSet::new();
        let mut files: Vec<String> = factions
            .all()
            .flat_map(|f| {
                f.rank_files
                    .values()
                    .chain(f.subclasses.values())
                    .chain(std::iter::once(&f.spawn_file))
            })
            .filter(|f| !f.trim().is_empty())
            .filter(|f| seen.insert(f.as_str()))
            .cloned()
            .collect();
        files.sort_by(|a, b| cmp_ignore_case(a, b));
        files
    }

    /// Create any roster file the factions expect but the install does not have: EMPTY, and
    /// only when missing, so a roster with members is never touched. The directory itself is
    /// created only inside a real install (see [`creatable_problem`](Self::creatable_problem)).
    #[must_use]
    pub fn ensure_roster_files(&self) -> RosterFileReport {
        let expected = Self::roster_files_of(&self.factions);
        let report = |summary: String| RosterFileReport {
            summary,
            expected: expected.len(),
            created: 0,
            present: 0,
            failed: Vec::new(),
        };
        let Some(directory) = &self.directory else {
            return report("off (FACTION_ROLES_PATH not set)".into());
        };
        let shown = directory.display();

        let mut created_directory = false;
        if !directory.is_dir() {
            if let Some(refusal) = self.creatable_problem(directory) {
                return report(format!(
                    "off (FACTION_ROLES_PATH is {shown}, which does not exist - {refusal})"
                ));
            }
            if let Err(error) = create_owned_like_parent(directory) {
                return report(format!(
                    "off (FACTION_ROLES_PATH is {shown}, which does not exist and could not be created: {error})"
                ));
            }
            created_directory = true;
            tracing::warn!(directory = %shown, "created the missing roster directory");
        }

        let (mut created, mut present, mut failed) = (0, 0, Vec::new());
        for file in &expected {
            let path = directory.join(file);
            if path.exists() {
                present += 1;
                continue;
            }
            if let Some(problem) = self.guard.problem(path.to_str()) {
                failed.push(format!("{file}: {problem}"));
                continue;
            }
            // Through the atomic writer so a NEW roster takes its directory's owner.
            match atomic_file::write_text(&path, "") {
                Ok(()) => created += 1,
                Err(error) => failed.push(format!("{file}: {error}")),
            }
        }

        let mut summary = format!(
            "{created} created, {present} already there, of {} in {shown}{}",
            expected.len(),
            if created_directory {
                " (directory created)"
            } else {
                ""
            }
        );
        if !failed.is_empty() {
            summary.push_str(&format!(" - {} COULD NOT BE CREATED", failed.len()));
        }
        RosterFileReport {
            summary,
            expected: expected.len(),
            created,
            present,
            failed,
        }
    }

    /// Why a missing roster directory must not be created, or `None` when it may: only below an
    /// existing `.../Pavlov/Saved/Config`. A path that does not reach one is a typo.
    #[must_use]
    pub fn creatable_problem(&self, directory: &Path) -> Option<String> {
        let full = std::path::absolute(directory).unwrap_or_else(|_| directory.to_path_buf());
        let full = full.to_string_lossy();
        let full = full.trim_end_matches('/');
        const MARKER: &str = "/Pavlov/Saved/Config";
        let Some(at) = full.find(MARKER) else {
            return Some(
                "it is not inside a Pavlov install's Pavlov/Saved/Config, so it was not created"
                    .into(),
            );
        };
        let config = &full[..at + MARKER.len()];
        if !Path::new(config).is_dir() {
            return Some(format!(
                "{config} does not exist either - is the server installed?"
            ));
        }
        let probe = format!("{full}/placeholder.txt");
        self.guard
            .is_ignored(Some(&probe))
            .then(|| "it is in IGNORE_PATHS - another bot or process owns it".into())
    }

    /// Every `.txt` in the roster folder, plus any roster file the factions expect that is not
    /// there yet. Hand-made files are included: a snapshot is of the folder, not the registry.
    #[must_use]
    pub fn list_roster_files(&self) -> Vec<String> {
        let Some(directory) = self.directory.as_ref().filter(|d| d.is_dir()) else {
            return Vec::new();
        };
        let on_disk = std::fs::read_dir(directory)
            .map(|entries| {
                entries
                    .filter_map(Result::ok)
                    .filter(|e| e.path().is_file())
                    .filter_map(|e| e.file_name().into_string().ok())
                    .filter(|n| n.ends_with(".txt"))
                    .collect::<Vec<_>>()
            })
            .unwrap_or_default();
        let mut seen = IgnoreCaseSet::new();
        let mut files: Vec<String> = on_disk
            .into_iter()
            .chain(Self::roster_files_of(&self.factions))
            .filter(|f| seen.insert(f.as_str()))
            .collect();
        files.sort_by(|a, b| cmp_ignore_case(a, b));
        files
    }

    /// Read a roster. `None` means UNREADABLE, which is not the same as empty: an empty roster
    /// is a valid state, and writing "cannot read" back as empty wipes a faction.
    #[must_use]
    pub fn read(&self, file: &str) -> Option<Vec<String>> {
        if !self.enabled() {
            return None;
        }
        let path = self.path_for(file)?;
        match std::fs::read_to_string(&path) {
            Ok(text) => Some(
                text.lines()
                    .map(str::trim)
                    .filter(|l| !l.is_empty())
                    .map(str::to_owned)
                    .collect(),
            ),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Some(Vec::new()),
            Err(error) => {
                tracing::error!(file, %error, "cannot read the roster");
                None
            }
        }
    }

    /// Replace a roster. `allow_bulk` skips the destruction guard: ONLY for an operation that
    /// legitimately rewrites a whole roster, a restore or a deliberate wipe.
    pub async fn write(&self, file: &str, lines: Vec<String>, allow_bulk: bool) -> bool {
        self.update(file, move |_| Some(lines), allow_bulk).await
    }

    /// A read-modify-write under the file's gate. `change` sees the current contents (`None`:
    /// unreadable) and returns the new ones, or `None` to write nothing. True when written.
    async fn update(
        &self,
        file: &str,
        change: impl FnOnce(Option<&[String]>) -> Option<Vec<String>>,
        allow_bulk: bool,
    ) -> bool {
        if !self.enabled() {
            return false;
        }
        let Some(path) = self.path_for(file) else {
            return false;
        };
        let gate = self.gate(file);
        let _held = gate.lock().await;

        let current = self.read(file);
        let Some(lines) = change(current.as_deref()) else {
            return false;
        };
        if !allow_bulk {
            let verdict = RosterWriteGuard::evaluate(current.as_deref(), Some(&lines), true, false);
            if !verdict.is_allowed() {
                tracing::error!(
                    file,
                    reason = verdict.explanation.as_deref().unwrap_or(""),
                    "REFUSED a roster write - the roster is untouched"
                );
                return false;
            }
        }
        if let Some(current) = &current {
            self.backup(file, current);
        }
        // Only into a directory that already exists, and never an IGNORE_PATHS file.
        if let Some(problem) = self.guard.problem(path.to_str()) {
            tracing::warn!(path = %path.display(), %problem, "not writing the roster");
            return false;
        }
        let mut text = lines.join("\n");
        text.push('\n');
        match atomic_file::write_text_async(path, text).await {
            Ok(()) => true,
            Err(error) => {
                tracing::error!(file, %error, "roster write failed");
                false
            }
        }
    }

    /// One rolling pre-write copy per roster, OUTSIDE the game tree: a `.bak` beside the live
    /// files is one glob away from being read as a roster. Best effort, and reported when it
    /// fails, so an unwritable backup directory is not discovered only when somebody needs it.
    fn backup(&self, file: &str, contents: &[String]) {
        let result = std::fs::create_dir_all(&self.backup_directory).and_then(|()| {
            let mut text = contents.join("\n");
            text.push('\n');
            std::fs::write(self.backup_directory.join(format!("{file}.bak")), text)
        });
        if let Err(error) = result {
            tracing::warn!(
                file,
                directory = %self.backup_directory.display(),
                %error,
                "could not write the roster backup"
            );
        }
    }

    /// Put each name on every roster file, where it is not already. FOR MASTER NAMES ONLY: it
    /// deliberately breaks one-faction-one-rank. Additions only, so the guard passes; an
    /// unreadable roster is skipped and reported, never written.
    ///
    /// How many (file, name) entries were added, and the files that could not be updated.
    pub async fn ensure_on_every_roster(&self, names: &[String]) -> (usize, Vec<String>) {
        if !self.enabled() || names.is_empty() {
            return (0, Vec::new());
        }
        let (mut added, mut failed) = (0, Vec::new());
        for file in Self::roster_files_of(&self.factions) {
            let mut unreadable = false;
            let mut missing_count = 0;
            let written = self
                .update(
                    &file,
                    |current| {
                        let Some(current) = current else {
                            unreadable = true;
                            return None;
                        };
                        let mut seen = IgnoreCaseSet::new();
                        let missing: Vec<String> = names
                            .iter()
                            .filter(|n| !holds(current, n))
                            .filter(|n| seen.insert(n.as_str()))
                            .cloned()
                            .collect();
                        missing_count = missing.len();
                        (!missing.is_empty()).then(|| [current, missing.as_slice()].concat())
                    },
                    false,
                )
                .await;
            if written {
                added += missing_count;
            } else if unreadable || missing_count > 0 {
                failed.push(file);
            }
        }
        (added, failed)
    }

    /// The names on EVERY roster file, which only a master grant ever does. Empty when the
    /// rosters are off, there are fewer than two files, or any file cannot be read.
    #[must_use]
    pub fn names_on_every_roster(&self) -> Vec<String> {
        if !self.enabled() {
            return Vec::new();
        }
        let files = Self::roster_files_of(&self.factions);
        if files.len() < 2 {
            return Vec::new();
        }
        let mut common: Option<IndexMap<String, String>> = None;
        for file in &files {
            let Some(lines) = self.read(file) else {
                return Vec::new();
            };
            let names: IndexMap<String, String> =
                lines.into_iter().map(|l| (fold_case(&l), l)).collect();
            common = Some(match common {
                None => names,
                Some(mut kept) => {
                    kept.retain(|key, _| names.contains_key(key));
                    kept
                }
            });
            if common.as_ref().is_some_and(IndexMap::is_empty) {
                return Vec::new();
            }
        }
        common
            .map(|c| c.into_values().collect())
            .unwrap_or_default()
    }

    /// Take each name off every roster file: the reverse of
    /// [`ensure_on_every_roster`](Self::ensure_on_every_roster), for a former master.
    pub async fn remove_from_every_roster(&self, names: &[String]) -> (usize, Vec<String>) {
        if !self.enabled() || names.is_empty() {
            return (0, Vec::new());
        }
        let (mut removed, mut failed) = (0, Vec::new());
        for file in Self::roster_files_of(&self.factions) {
            let mut unreadable = false;
            let mut dropped = 0;
            let written = self
                .update(
                    &file,
                    |current| {
                        let Some(current) = current else {
                            unreadable = true;
                            return None;
                        };
                        let kept: Vec<String> = current
                            .iter()
                            .filter(|line| !holds(names, line.trim()))
                            .cloned()
                            .collect();
                        dropped = current.len() - kept.len();
                        (dropped > 0).then_some(kept)
                    },
                    false,
                )
                .await;
            if written {
                removed += dropped;
            } else if unreadable || dropped > 0 {
                failed.push(file);
            }
        }
        (removed, failed)
    }

    /// Where a player sits, at their HIGHEST rank (the one that governs what they can do in
    /// game), or `None` when they are on no roster.
    #[must_use]
    pub fn find(&self, player: &str) -> Option<Membership> {
        for faction in self.factions.all() {
            for rank in faction.order.iter().rev() {
                let Some(file) = faction.rank_files.get(rank) else {
                    continue;
                };
                if self.read(file).is_some_and(|r| holds(&r, player)) {
                    return Some(Membership {
                        faction: faction.name.clone(),
                        player: player.to_owned(),
                        rank: rank.clone(),
                    });
                }
            }
        }
        None
    }

    /// Every member of a faction, once, at their highest rank.
    #[must_use]
    pub fn roster(&self, faction: &FactionDefinition) -> Vec<Membership> {
        let mut seen: IndexMap<String, Membership> = IndexMap::new();
        for rank in &faction.order {
            let Some(file) = faction.rank_files.get(rank) else {
                continue;
            };
            for player in self.read(file).unwrap_or_default() {
                // Later ranks are higher, so they win.
                seen.insert(
                    fold_case(&player),
                    Membership {
                        faction: faction.name.clone(),
                        player,
                        rank: rank.clone(),
                    },
                );
            }
        }
        seen.into_values().collect()
    }

    /// Which sub-class each member holds, by in-game name (ignoring case). Each file is read
    /// once; a name in two (a hand-edit) resolves to the last read rather than failing.
    #[must_use]
    pub fn subclasses(&self, faction: &FactionDefinition) -> IgnoreCaseMap<String> {
        let mut held = IgnoreCaseMap::new();
        for (name, file) in &faction.subclasses {
            for player in self.read(file).unwrap_or_default() {
                held.insert(player, name.clone());
            }
        }
        held
    }

    /// Everyone on every roster, by in-game name (ignoring case): the bulk form of
    /// [`find`](Self::find), reading each rank file once. Same answer as `find`: the highest
    /// rank wins within a faction, the first faction wins across them.
    #[must_use]
    pub fn affiliations(&self) -> IgnoreCaseMap<Membership> {
        let mut all: IgnoreCaseMap<Membership> = IgnoreCaseMap::new();
        for faction in self.factions.all() {
            for rank in &faction.order {
                let Some(file) = faction.rank_files.get(rank) else {
                    continue;
                };
                for player in self.read(file).unwrap_or_default() {
                    if all
                        .get(&player)
                        .is_some_and(|held| !eq_ignore_case(&held.faction, &faction.name))
                    {
                        continue;
                    }
                    let membership = Membership {
                        faction: faction.name.clone(),
                        player: player.clone(),
                        rank: rank.clone(),
                    };
                    all.insert(player, membership);
                }
            }
        }
        all
    }

    /// Add a player to a faction at its default rank, and to its SPAWN file, which is what
    /// actually lets them play as it.
    pub async fn join(&self, faction: &FactionDefinition, player: &str) -> MembershipDecision {
        let current: Vec<String> = self.find(player).map(|m| m.faction).into_iter().collect();
        let decided = MembershipRules::join(Some(faction), &current);
        if !decided.is_allowed() {
            return decided;
        }
        let rank = decided.rank.clone().unwrap_or_default();
        let Some(file) = faction.rank_files.get(&rank) else {
            return decision(MembershipOutcome::NoSuchRank, Some(&rank), None);
        };
        // Unreadable is not an unknown faction: they have different fixes.
        let Some(roster) = self.read(file) else {
            return decision(MembershipOutcome::RosterUnavailable, Some(&rank), None);
        };
        let mut next = roster;
        next.push(player.to_owned());
        if !self.write(file, next, false).await {
            return decision(MembershipOutcome::WriteFailed, Some(&rank), None);
        }
        if self.ensure_listed(&faction.spawn_file, player).await {
            decided
        } else {
            decision(MembershipOutcome::WriteFailed, Some(&rank), None)
        }
    }

    /// Add a player to a roster if they are not already on it. True when they are on it after.
    /// Idempotent: a ladderless faction's join writes the same file twice.
    async fn ensure_listed(&self, file: &str, player: &str) -> bool {
        let Some(roster) = self.read(file) else {
            return false;
        };
        if holds(&roster, player) {
            return true;
        }
        let mut next = roster;
        next.push(player.to_owned());
        self.write(file, next, false).await
    }

    /// Empty every roster file a faction owns: THE ONLY CALLER THAT SETS `allow_bulk`. File by
    /// file, since plain text has no transaction; the caller is told which files resisted.
    pub async fn wipe(&self, faction: &FactionDefinition) -> RosterWipe {
        if !self.enabled() {
            return RosterWipe {
                outcome: MembershipOutcome::RosterUnavailable,
                removed: 0,
                failed: Vec::new(),
            };
        }
        // By name, not by row: rank file plus spawn file is one person.
        let mut removed = IgnoreCaseSet::new();
        let mut failed = Vec::new();
        for file in files_of(faction) {
            let Some(roster) = self.read(&file) else {
                failed.push(file);
                continue;
            };
            if roster.is_empty() {
                continue;
            }
            if self.write(&file, Vec::new(), true).await {
                for name in roster {
                    removed.insert(name);
                }
            } else {
                failed.push(file);
            }
        }
        RosterWipe {
            outcome: if failed.is_empty() {
                MembershipOutcome::Allowed
            } else {
                MembershipOutcome::WriteFailed
            },
            removed: removed.len(),
            failed,
        }
    }

    /// Remove a player from every file of a faction. "Not whitelisted" ONLY when the files said
    /// so: an unreadable file or a refused write may still hold them.
    pub async fn leave(&self, faction: &FactionDefinition, player: &str) -> MembershipDecision {
        if !self.enabled() {
            return decision(MembershipOutcome::RosterUnavailable, None, None);
        }
        let (mut removed, mut blocked) = (false, false);
        for file in files_of(faction) {
            let Some(roster) = self.read(&file) else {
                blocked = true;
                continue;
            };
            let next = without(&roster, player);
            if next.len() == roster.len() {
                continue;
            }
            if self.write(&file, next, false).await {
                removed = true;
            } else {
                blocked = true;
            }
        }
        if removed {
            MembershipDecision::allow(None)
        } else if blocked {
            decision(MembershipOutcome::RosterUnavailable, None, None)
        } else {
            decision(MembershipOutcome::NotWhitelisted, None, None)
        }
    }

    /// Move a member one step up (+1) or down (-1).
    pub async fn change_rank(
        &self,
        faction: &FactionDefinition,
        player: &str,
        direction: i32,
    ) -> MembershipDecision {
        // Before `find`: with no directory it finds nothing and the rules would blame the member.
        if !self.enabled() {
            return decision(MembershipOutcome::RosterUnavailable, None, None);
        }
        let current = self.find(player);
        let decided = MembershipRules::change_rank(
            Some(faction),
            current.as_ref().map(|m| m.rank.as_str()),
            direction,
        );
        if !decided.is_allowed() {
            return decided;
        }
        self.apply_rank(faction, player, decided).await
    }

    /// Put a member at a named rank, through the SAME write as a promotion.
    pub async fn set_rank(
        &self,
        faction: &FactionDefinition,
        player: &str,
        rank: &str,
    ) -> MembershipDecision {
        if !self.enabled() {
            return decision(MembershipOutcome::RosterUnavailable, None, None);
        }
        let current = self.find(player);
        let decided = MembershipRules::set_rank(
            Some(faction),
            current.as_ref().map(|m| m.rank.as_str()),
            Some(rank),
        );
        if !decided.is_allowed() {
            return decided;
        }
        self.apply_rank(faction, player, decided).await
    }

    /// Write an allowed rank decision. Every rank file that is not theirs is stripped first (a
    /// stale duplicate would otherwise survive as a second rank). "Theirs" is the target rank,
    /// or for a member holding ranks the target and everything below it. Then the spawn entry
    /// is repaired on the way past.
    async fn apply_rank(
        &self,
        faction: &FactionDefinition,
        player: &str,
        decided: MembershipDecision,
    ) -> MembershipDecision {
        let holds_lower = self.holds_all_ranks.as_ref().is_some_and(|h| h(player));
        let Some(target) = faction.index_of(decided.rank.as_deref()) else {
            return decided;
        };

        for (rank, file) in &faction.rank_files {
            let index = faction.index_of(Some(rank));
            let keep = match index {
                Some(i) if holds_lower => i <= target,
                Some(i) => i == target,
                None => false,
            };
            if keep {
                continue;
            }
            let Some(roster) = self.read(file) else {
                continue;
            };
            if holds(&roster, player) {
                self.write(file, without(&roster, player), false).await;
            }
        }

        if holds_lower {
            // Lowest first, so an interrupted run leaves the ranks below before the one above.
            for rank in faction.order.iter().take(target + 1) {
                if let Some(file) = faction.rank_files.get(rank) {
                    self.ensure_listed(file, player).await;
                }
            }
        } else if let Some(file) = faction.rank_files.get(&faction.order[target]) {
            self.ensure_listed(file, player).await;
        }

        // SELF-HEALING: a promotion cannot leave somebody holding a rank they cannot spawn into.
        self.ensure_listed(&faction.spawn_file, player).await;
        decided
    }

    /// Assign or remove a sub-class, which is additive to the member's rank.
    pub async fn change_subclass(
        &self,
        faction: &FactionDefinition,
        player: &str,
        subclass: &str,
        removing: bool,
    ) -> MembershipDecision {
        if !self.enabled() {
            return decision(MembershipOutcome::RosterUnavailable, None, None);
        }
        let held: Vec<String> = faction
            .subclasses
            .iter()
            .filter(|(_, file)| self.read(file).is_some_and(|r| holds(&r, player)))
            .map(|(name, _)| name.clone())
            .collect();
        let decided = MembershipRules::change_subclass(Some(faction), subclass, &held, removing);
        if !decided.is_allowed() {
            return decided;
        }
        let Some(file) = faction.subclasses.get(subclass) else {
            return decision(MembershipOutcome::NoSuchSubclass, None, Some(subclass));
        };
        let roster = self.read(file).unwrap_or_default();
        let next = if removing {
            without(&roster, player)
        } else {
            let mut next = roster;
            next.push(player.to_owned());
            next
        };
        // NOT NoSuchSubclass: the rules confirmed it exists, so this is a write failure.
        if self.write(file, next, false).await {
            decided
        } else {
            decision(MembershipOutcome::WriteFailed, None, Some(subclass))
        }
    }
}
