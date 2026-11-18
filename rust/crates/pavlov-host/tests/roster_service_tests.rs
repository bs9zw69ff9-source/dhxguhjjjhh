//! Port of `RosterServiceTests.cs`. The rosters are plain text the game reads LIVE, with no
//! transaction and no undo, so every one of these is about not corrupting a file in use.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use indexmap::IndexMap;
use pavlov_core::data::RosterWriteGuard;
use pavlov_core::factions::{FactionDefinition, FactionSet, MembershipOutcome};
use pavlov_host::factions::RosterService;
use pavlov_storage::game_files::GameFileGuard;

fn nypd() -> FactionDefinition {
    pavlov_core::factions::testing::faction(Some("NYPD"))
        .unwrap()
        .clone()
}

/// A faction with no ladder: one nominal rank, one file that is also the spawn file.
fn spawn_only(name: &str) -> FactionDefinition {
    let file = format!("{}spawn.txt", name.to_lowercase());
    FactionDefinition {
        name: name.into(),
        order: vec!["Member".into()],
        default: "Member".into(),
        rank_files: IndexMap::from([("Member".to_owned(), file.clone())]),
        spawn_file: file,
        subclasses: IndexMap::new(),
        former_names: Vec::new(),
    }
}

fn gambino() -> FactionDefinition {
    spawn_only("Gambino")
}

fn with_mafias() -> FactionSet {
    FactionSet::of([gambino(), spawn_only("Colombo"), nypd()])
}

struct Fixture {
    dir: tempfile::TempDir,
    rosters: RosterService,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let rosters = Self::service(dir.path())
            .with_factions(pavlov_core::factions::testing::ladder().clone());
        Self { dir, rosters }
    }

    fn service(dir: &Path) -> RosterService {
        RosterService::new(Some(dir.to_path_buf()), dir.join("_bak"))
    }

    fn path(&self) -> &Path {
        self.dir.path()
    }

    fn backups(&self) -> PathBuf {
        self.path().join("_bak")
    }

    fn seed(&self, file: &str, names: &[&str]) {
        std::fs::write(self.path().join(file), format!("{}\n", names.join("\n"))).unwrap();
    }

    fn contents(&self, file: &str) -> Vec<String> {
        self.rosters.read(file).unwrap_or_default()
    }

    fn has(&self, file: &str, name: &str) -> bool {
        self.contents(file).iter().any(|n| n == name)
    }
}

fn players(n: usize) -> Vec<String> {
    (0..n).map(|i| format!("Player{i}")).collect()
}

fn refs(names: &[String]) -> Vec<&str> {
    names.iter().map(String::as_str).collect()
}

#[tokio::test]
async fn joining_lands_at_the_default_rank() {
    let f = Fixture::new();
    let decision = f.rosters.join(&nypd(), "Alice").await;

    assert!(decision.is_allowed());
    assert_eq!(decision.rank.as_deref(), Some("Cadet"));
    assert!(f.has("policecadet.txt", "Alice"));
}

#[tokio::test]
async fn a_busy_entry_rank_still_takes_the_join() {
    // Rank caps are gone, so the file simply grows; asserted on the ROSTER the game reads.
    let f = Fixture::new();
    let many = players(50);
    f.seed("policecadet.txt", &refs(&many));

    let decision = f.rosters.join(&nypd(), "Alice").await;

    assert!(decision.is_allowed());
    assert!(f.has("policecadet.txt", "Alice"));
    assert_eq!(f.contents("policecadet.txt").len(), 51);
}

#[tokio::test]
async fn a_promotion_moves_them_between_files_rather_than_adding_a_second_rank() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;
    let decision = f.rosters.change_rank(&nypd(), "Alice", 1).await;

    assert_eq!(decision.rank.as_deref(), Some("Patrolman"));
    assert!(f.has("policepatrolman.txt", "Alice"));
    assert!(!f.has("policecadet.txt", "Alice"));
}

#[tokio::test]
async fn a_promotion_cleans_up_a_stale_duplicate_rank() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice"]);
    f.seed("policesergeant.txt", &["Alice"]);

    // Found at their HIGHEST rank, so the promotion is from Sergeant.
    let decision = f.rosters.change_rank(&nypd(), "Alice", 1).await;
    assert_eq!(decision.rank.as_deref(), Some("Lieutenant"));

    let mentions: usize = nypd()
        .rank_files
        .values()
        .map(|file| f.contents(file).iter().filter(|n| *n == "Alice").count())
        .sum();
    assert_eq!(mentions, 1);
    assert!(f.has("policelieutenant.txt", "Alice"));
}

#[tokio::test]
async fn a_demotion_into_a_busy_rank_goes_through() {
    let f = Fixture::new();
    f.seed("policesergeant.txt", &["Alice"]);
    let many = players(15);
    f.seed("policecorporal.txt", &refs(&many));

    let decision = f.rosters.change_rank(&nypd(), "Alice", -1).await;

    assert!(decision.is_allowed());
    assert!(f.has("policecorporal.txt", "Alice"));
    assert!(!f.has("policesergeant.txt", "Alice"));
}

#[tokio::test]
async fn leaving_clears_every_file_including_sub_classes() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice", "Bob"]);
    f.seed("policesergeant.txt", &["Alice"]);
    f.seed("policedetective.txt", &["Alice"]);

    assert!(f.rosters.leave(&nypd(), "Alice").await.is_allowed());

    assert!(!f.has("policecadet.txt", "Alice"));
    assert!(!f.has("policesergeant.txt", "Alice"));
    assert!(!f.has("policedetective.txt", "Alice"));
    assert!(f.has("policecadet.txt", "Bob"));
}

#[tokio::test]
async fn removing_somebody_who_is_not_there_is_refused_rather_than_silently_succeeding() {
    let f = Fixture::new();
    assert_eq!(
        f.rosters.leave(&nypd(), "Nobody").await.outcome,
        MembershipOutcome::NotWhitelisted
    );
}

#[tokio::test]
async fn a_write_that_would_wipe_a_roster_is_refused() {
    // An empty roster is VALID to the game; the guard is on the size of the change.
    let f = Fixture::new();
    let many = players(20);
    f.seed("policecadet.txt", &refs(&many));

    assert!(!f.rosters.write("policecadet.txt", Vec::new(), false).await);
    assert_eq!(f.contents("policecadet.txt").len(), 20);
}

#[tokio::test]
async fn a_deliberate_wipe_is_allowed_with_allow_bulk() {
    let f = Fixture::new();
    let many = players(20);
    f.seed("policecadet.txt", &refs(&many));

    assert!(f.rosters.write("policecadet.txt", Vec::new(), true).await);
    assert!(f.contents("policecadet.txt").is_empty());
}

#[tokio::test]
async fn a_pre_write_backup_is_kept_outside_the_game_tree() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice"]);
    f.rosters.join(&nypd(), "Bob").await;

    let backup = f.backups().join("policecadet.txt.bak");
    assert!(backup.exists());
    assert!(std::fs::read_to_string(backup).unwrap().contains("Alice"));
}

#[tokio::test]
async fn a_sub_class_is_additive_and_the_member_keeps_their_rank() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;
    assert!(
        f.rosters
            .change_subclass(&nypd(), "Alice", "Detective", false)
            .await
            .is_allowed()
    );

    assert!(f.has("policedetective.txt", "Alice"));
    assert!(f.has("policecadet.txt", "Alice"));
}

#[tokio::test]
async fn subclasses_async_reports_who_holds_what() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;
    f.rosters.join(&nypd(), "Bob").await;
    f.rosters
        .change_subclass(&nypd(), "Alice", "Detective", false)
        .await;

    let held = f.rosters.subclasses(&nypd());

    assert_eq!(held.get("Alice").map(String::as_str), Some("Detective"));
    assert!(!held.contains_key("Bob"));
}

#[tokio::test]
async fn only_one_sub_class_at_a_time() {
    let f = Fixture::new();
    f.rosters
        .change_subclass(&nypd(), "Alice", "Detective", false)
        .await;
    let decision = f
        .rosters
        .change_subclass(&nypd(), "Alice", "Vice Officer", false)
        .await;

    assert_eq!(decision.outcome, MembershipOutcome::AlreadyHasSubclass);
    assert_eq!(decision.conflict.as_deref(), Some("Detective"));
}

#[tokio::test]
async fn narcotics_bureau_is_assignable_and_still_only_one_sub_class() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;

    assert!(
        f.rosters
            .change_subclass(&nypd(), "Alice", "Narcotics Bureau", false)
            .await
            .is_allowed()
    );
    assert!(f.has("policenarcotics.txt", "Alice"));
    assert!(f.has("policecadet.txt", "Alice"));

    let second = f
        .rosters
        .change_subclass(&nypd(), "Alice", "Detective", false)
        .await;
    assert_eq!(second.outcome, MembershipOutcome::AlreadyHasSubclass);
    assert_eq!(second.conflict.as_deref(), Some("Narcotics Bureau"));
}

#[tokio::test]
async fn leaving_clears_the_narcotics_sub_class_too() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;
    f.rosters
        .change_subclass(&nypd(), "Alice", "Narcotics Bureau", false)
        .await;

    f.rosters.leave(&nypd(), "Alice").await;

    assert!(!f.has("policenarcotics.txt", "Alice"));
}

#[tokio::test]
async fn a_member_is_reported_at_their_highest_rank() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice"]);
    f.seed("policecaptain.txt", &["Alice"]);

    assert_eq!(f.rosters.find("Alice").unwrap().rank, "Captain");
}

#[tokio::test]
async fn the_roster_lists_every_member_once() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice", "Bob"]);
    f.seed("policesergeant.txt", &["Alice"]);

    let roster = f.rosters.roster(&nypd());
    assert_eq!(roster.len(), 2);
    assert_eq!(
        roster.iter().find(|m| m.player == "Alice").unwrap().rank,
        "Sergeant"
    );
}

#[test]
fn an_unconfigured_directory_disables_everything_safely() {
    let disabled = RosterService::new(None, PathBuf::from("/nonexistent/_bak"))
        .with_factions(pavlov_core::factions::testing::ladder().clone());
    assert!(!disabled.enabled());
    assert!(disabled.read("policecadet.txt").is_none());
}

#[tokio::test]
async fn with_no_roster_directory_every_change_reports_the_roster_not_the_faction() {
    let disabled = RosterService::new(None, PathBuf::from("/nonexistent/_bak"));
    let nypd = nypd();
    let subclass = nypd.subclasses.keys().next().unwrap().clone();

    assert_eq!(
        disabled.join(&nypd, "Alice").await.outcome,
        MembershipOutcome::RosterUnavailable
    );
    assert_eq!(
        disabled.leave(&nypd, "Alice").await.outcome,
        MembershipOutcome::RosterUnavailable
    );
    assert_eq!(
        disabled.change_rank(&nypd, "Alice", 1).await.outcome,
        MembershipOutcome::RosterUnavailable
    );
    assert_eq!(
        disabled
            .change_subclass(&nypd, "Alice", &subclass, false)
            .await
            .outcome,
        MembershipOutcome::RosterUnavailable
    );
}

#[tokio::test]
async fn a_missing_directory_is_reported_as_unavailable_rather_than_created() {
    let f = Fixture::new();
    let missing = f.path().join("no-such-directory");
    let service = RosterService::new(Some(missing.clone()), f.backups());

    assert!(!service.enabled());
    assert_eq!(
        service.join(&nypd(), "Alice").await.outcome,
        MembershipOutcome::RosterUnavailable
    );
    assert!(!missing.exists());
}

#[tokio::test]
async fn joining_writes_the_spawn_file_as_well_as_the_rank_file() {
    // The port gap: the rank file without the spawn file lets nobody spawn.
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;

    assert!(f.has("policecadet.txt", "Alice"));
    assert!(f.has(&nypd().spawn_file, "Alice"));
}

#[tokio::test]
async fn leaving_clears_the_spawn_file_too() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;

    f.rosters.leave(&nypd(), "Alice").await;

    assert!(!f.has(&nypd().spawn_file, "Alice"));
    assert!(!f.has("policecadet.txt", "Alice"));
}

#[tokio::test]
async fn setting_a_rank_moves_them_in_one_write_and_leaves_no_stale_entry() {
    let f = Fixture::new();
    let nypd = nypd();
    f.rosters.join(&nypd, "Alice").await;

    let decision = f.rosters.set_rank(&nypd, "Alice", "Sergeant").await;

    assert!(decision.is_allowed());
    assert_eq!(decision.rank.as_deref(), Some("Sergeant"));
    assert!(f.has(&nypd.rank_files["Sergeant"], "Alice"));
    assert!(!f.has("policecadet.txt", "Alice"));
    assert!(f.has(&nypd.spawn_file, "Alice"));
}

#[tokio::test]
async fn setting_a_rank_downwards_strips_what_is_now_above_them() {
    let f = Fixture::new();
    let nypd = nypd();
    f.rosters.join(&nypd, "Alice").await;
    f.rosters.set_rank(&nypd, "Alice", "Sergeant").await;

    f.rosters.set_rank(&nypd, "Alice", "Patrolman").await;

    assert!(f.has("policepatrolman.txt", "Alice"));
    assert!(!f.has(&nypd.rank_files["Sergeant"], "Alice"));
}

#[tokio::test]
async fn setting_a_rank_the_faction_does_not_have_writes_nothing() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;

    let decision = f.rosters.set_rank(&nypd(), "Alice", "Centurion").await;

    assert_eq!(decision.outcome, MembershipOutcome::NoSuchRank);
    assert!(f.has("policecadet.txt", "Alice"));
}

#[tokio::test]
async fn joining_then_placing_lands_them_at_the_rank_and_nowhere_else() {
    let f = Fixture::new();
    let nypd = nypd();
    f.rosters.join(&nypd, "Alice").await;
    let placed = f.rosters.set_rank(&nypd, "Alice", "Sergeant").await;

    assert!(placed.is_allowed());
    assert_eq!(placed.rank.as_deref(), Some("Sergeant"));
    assert!(f.has(&nypd.rank_files["Sergeant"], "Alice"));
    assert!(!f.has("policecadet.txt", "Alice"));
    assert!(f.has(&nypd.spawn_file, "Alice"));
}

#[tokio::test]
async fn placing_at_the_join_rank_is_a_no_change_rather_than_a_rewrite() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;

    let placed = f.rosters.set_rank(&nypd(), "Alice", "Cadet").await;

    assert_eq!(placed.outcome, MembershipOutcome::NoChange);
    assert!(f.has("policecadet.txt", "Alice"));
}

fn ranks_through(faction: &FactionDefinition, top: &str) -> Vec<String> {
    let at = faction.index_of(Some(top)).unwrap();
    faction.order[..=at].to_vec()
}

#[tokio::test]
async fn placing_honours_hold_all_ranks_so_the_order_of_the_two_writes_matters() {
    // The index write has to come BEFORE the placement, or hold_ranks looks broken.
    let f = Fixture::new();
    let recorded = Arc::new(AtomicBool::new(false));
    let flag = Arc::clone(&recorded);
    let holding = Fixture::service(f.path())
        .with_holds_all_ranks(Arc::new(move |_| flag.load(Ordering::Relaxed)));
    let nypd = nypd();

    holding.join(&nypd, "Alice").await;
    recorded.store(true, Ordering::Relaxed);
    holding.set_rank(&nypd, "Alice", "Sergeant").await;

    for rank in ranks_through(&nypd, "Sergeant") {
        assert!(
            f.has(&nypd.rank_files[&rank], "Alice"),
            "missing from {rank}"
        );
    }
}

#[tokio::test]
async fn setting_a_rank_leaves_a_subclass_alone() {
    let f = Fixture::new();
    let nypd = nypd();
    f.rosters.join(&nypd, "Alice").await;
    f.rosters
        .change_subclass(&nypd, "Alice", "Detective", false)
        .await;

    f.rosters.set_rank(&nypd, "Alice", "Sergeant").await;

    assert!(f.has(&nypd.subclasses["Detective"], "Alice"));
    assert!(f.has(&nypd.rank_files["Sergeant"], "Alice"));
}

#[tokio::test]
async fn setting_a_rank_honours_hold_all_ranks() {
    let f = Fixture::new();
    let holding = Fixture::service(f.path()).with_holds_all_ranks(Arc::new(|name| name == "Alice"));
    let nypd = nypd();

    holding.join(&nypd, "Alice").await;
    holding.set_rank(&nypd, "Alice", "Sergeant").await;

    for rank in ranks_through(&nypd, "Sergeant") {
        assert!(
            f.has(&nypd.rank_files[&rank], "Alice"),
            "missing from {rank}"
        );
    }
    assert!(!f.has(&nypd.rank_files[nypd.highest()], "Alice"));
}

#[tokio::test]
async fn setting_a_rank_restores_a_missing_spawn_entry() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice"]);

    f.rosters.set_rank(&nypd(), "Alice", "Sergeant").await;

    assert!(f.has(&nypd().spawn_file, "Alice"));
}

#[tokio::test]
async fn promotion_keeps_the_member_in_the_spawn_file() {
    let f = Fixture::new();
    f.rosters.join(&nypd(), "Alice").await;

    let decision = f.rosters.change_rank(&nypd(), "Alice", 1).await;

    assert!(decision.is_allowed());
    assert!(f.has("policepatrolman.txt", "Alice"));
    assert!(f.has(&nypd().spawn_file, "Alice"));
}

#[tokio::test]
async fn promotion_restores_a_missing_spawn_entry() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice"]); // on the ladder, absent from the spawn file

    f.rosters.change_rank(&nypd(), "Alice", 1).await;

    assert!(f.has(&nypd().spawn_file, "Alice"));
}

#[tokio::test]
async fn a_ladderless_faction_is_written_once_not_twice() {
    let f = Fixture::new();
    let service = Fixture::service(f.path()).with_factions(with_mafias());
    let spawn_only = gambino();

    service.join(&spawn_only, "Alice").await;

    assert_eq!(service.read(&spawn_only.spawn_file).unwrap(), ["Alice"]);
}

fn seed_wipe_set(f: &Fixture) {
    f.seed("policecadet.txt", &["Alice"]);
    f.seed("policecaptain.txt", &["Bob"]);
    f.seed("policevice.txt", &["Alice"]); // a sub-class, held alongside a rank
    f.seed(&nypd().spawn_file, &["Alice", "Bob"]);
}

#[tokio::test]
async fn wiping_clears_every_file_the_faction_owns() {
    let f = Fixture::new();
    seed_wipe_set(&f);

    let result = f.rosters.wipe(&nypd()).await;

    assert_eq!(result.outcome, MembershipOutcome::Allowed);
    for file in [
        "policecadet.txt",
        "policecaptain.txt",
        "policevice.txt",
        &nypd().spawn_file,
    ] {
        assert!(f.contents(file).is_empty(), "{file} still holds members");
    }
}

#[tokio::test]
async fn the_wipe_counts_members_rather_than_rows() {
    let f = Fixture::new();
    seed_wipe_set(&f);

    assert_eq!(f.rosters.wipe(&nypd()).await.removed, 2);
}

#[tokio::test]
async fn the_wipe_is_not_blocked_by_the_bulk_deletion_guard() {
    let f = Fixture::new();
    let many = players(RosterWriteGuard::BULK_DROP_LIMIT * 4);
    f.seed("policecadet.txt", &refs(&many));
    f.seed(&nypd().spawn_file, &refs(&many));

    let result = f.rosters.wipe(&nypd()).await;

    assert_eq!(result.outcome, MembershipOutcome::Allowed);
    assert_eq!(result.removed, many.len());
    assert!(f.contents("policecadet.txt").is_empty());
}

#[tokio::test]
async fn wiping_backs_up_each_roster_first() {
    // The confirmation prompt promises this undo.
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice", "Bob"]);

    f.rosters.wipe(&nypd()).await;

    let backup = std::fs::read_to_string(f.backups().join("policecadet.txt.bak")).unwrap();
    assert!(backup.contains("Alice"));
    assert!(backup.contains("Bob"));
}

#[tokio::test]
async fn wiping_one_faction_leaves_the_others_alone() {
    let f = Fixture::new();
    let other = gambino();
    f.seed("policecadet.txt", &["Alice"]);
    f.seed(&other.spawn_file, &["Bob"]);

    f.rosters.wipe(&nypd()).await;

    assert!(f.has(&other.spawn_file, "Bob"));
}

#[tokio::test]
async fn wiping_with_no_roster_directory_is_reported_rather_than_claimed() {
    let f = Fixture::new();
    let service = RosterService::new(Some(f.path().join("nope")), f.backups());

    let result = service.wipe(&nypd()).await;

    assert_eq!(result.outcome, MembershipOutcome::RosterUnavailable);
    assert_eq!(result.removed, 0);
}

#[tokio::test]
async fn an_ignored_path_is_not_written_even_though_the_roster_is_otherwise_usable() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice"]);
    let guard = Arc::new(GameFileGuard::new(&[f.path().to_str().unwrap().to_owned()]));
    let guarded = Fixture::service(f.path()).with_guard(guard);

    assert!(
        !guarded
            .write("policecadet.txt", vec!["Alice".into(), "Bob".into()], false)
            .await
    );
    // Reads are deliberately unaffected, and the file still says what it did.
    assert_eq!(guarded.read("policecadet.txt").unwrap(), ["Alice"]);
}

#[tokio::test]
async fn an_unguarded_roster_service_still_writes() {
    let f = Fixture::new();
    f.seed("policecadet.txt", &["Alice"]);

    assert!(
        f.rosters
            .write("policecadet.txt", vec!["Alice".into(), "Bob".into()], false)
            .await
    );
    assert!(f.has("policecadet.txt", "Bob"));
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn concurrent_edits_to_one_roster_do_not_lose_members() {
    let f = Arc::new(Fixture::new());
    f.seed("policecadet.txt", &["Existing"]);

    let tasks: Vec<_> = (0..5)
        .map(|i| {
            let f = Arc::clone(&f);
            tokio::spawn(async move {
                let mut current = f.rosters.read("policecadet.txt").unwrap_or_default();
                current.push(format!("Player{i}"));
                f.rosters.write("policecadet.txt", current, false).await;
            })
        })
        .collect();
    for task in tasks {
        task.await.unwrap();
    }

    assert!(f.has("policecadet.txt", "Existing"));
}
