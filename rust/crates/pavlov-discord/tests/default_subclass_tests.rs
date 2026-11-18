//! Port of `DefaultSubclassTests.cs`: the GWPD's sub-classes, written into their own files
//! without touching rank or spawn access, one at a time.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::factions::{FactionDefinition, MembershipOutcome};
use pavlov_core::text::case::eq_ignore_case;
use pavlov_discord::commands::subclass::subclass_choices;
use pavlov_host::factions::RosterService;

struct Rig {
    dir: tempfile::TempDir,
    rosters: RosterService,
}

fn gwpd() -> &'static FactionDefinition {
    pavlov_core::factions::testing::ladder()
        .get(Some("GWPD"))
        .unwrap()
}

impl Rig {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let rosters = RosterService::new(Some(dir.path().to_path_buf()), dir.path().join("_bak"))
            .with_factions(pavlov_core::factions::testing::ladder().clone());
        Self { dir, rosters }
    }

    fn lines(&self, file: &str) -> Vec<String> {
        self.rosters.read(file).unwrap_or_default()
    }

    fn holds(&self, file: &str, player: &str) -> bool {
        self.lines(file).iter().any(|l| eq_ignore_case(l, player))
    }

    async fn joined(&self) -> String {
        let name = "Officer12";
        assert!(self.rosters.join(gwpd(), name).await.is_allowed());
        name.to_owned()
    }
}

#[test]
fn every_subclass_exists_on_the_default_set_with_its_own_file() {
    let g = gwpd();
    assert_eq!(g.subclasses["Vice Officer"], "policevice.txt");
    assert_eq!(g.subclasses["Detective"], "policedetective.txt");
    assert_eq!(
        g.subclasses["Tactical Response Unit"],
        "policetacticalresponse.txt"
    );
    assert_eq!(g.subclasses["Narcotics Bureau"], "policenarcotics.txt");
    for file in g.subclasses.values() {
        assert!(
            !g.rank_files.values().any(|r| eq_ignore_case(r, file)),
            "{file}"
        );
    }
}

#[tokio::test]
async fn assigning_detective_writes_the_name_into_the_detective_file() {
    let r = Rig::new();
    let player = r.joined().await;
    assert!(
        r.rosters
            .change_subclass(gwpd(), &player, "Detective", false)
            .await
            .is_allowed()
    );
    assert!(r.holds("policedetective.txt", &player));
    assert!(!r.holds("policevice.txt", &player));
}

#[tokio::test]
async fn assigning_vice_writes_the_name_into_the_vice_file() {
    let r = Rig::new();
    let player = r.joined().await;
    assert!(
        r.rosters
            .change_subclass(gwpd(), &player, "Vice Officer", false)
            .await
            .is_allowed()
    );
    assert!(r.holds("policevice.txt", &player));
    assert!(!r.holds("policedetective.txt", &player));
}

#[tokio::test]
async fn a_subclass_does_not_touch_their_rank_or_their_spawn_access() {
    let r = Rig::new();
    let player = r.joined().await;
    r.rosters
        .change_subclass(gwpd(), &player, "Detective", false)
        .await;

    assert!(r.holds(&gwpd().rank_files[gwpd().lowest()], &player));
    assert!(r.holds(&gwpd().spawn_file, &player));
    let membership = r.rosters.find(&player).unwrap();
    assert_eq!(membership.faction, "GWPD");
    assert_eq!(membership.rank, gwpd().lowest());
}

#[tokio::test]
async fn one_subclass_at_a_time() {
    let r = Rig::new();
    let player = r.joined().await;
    r.rosters
        .change_subclass(gwpd(), &player, "Detective", false)
        .await;

    let second = r
        .rosters
        .change_subclass(gwpd(), &player, "Vice Officer", false)
        .await;

    assert!(!second.is_allowed());
    assert_eq!(second.outcome, MembershipOutcome::AlreadyHasSubclass);
    assert_eq!(second.conflict.as_deref(), Some("Detective"));
    assert!(!r.holds("policevice.txt", &player));
}

#[tokio::test]
async fn removing_one_frees_them_for_the_other() {
    let r = Rig::new();
    let player = r.joined().await;
    r.rosters
        .change_subclass(gwpd(), &player, "Detective", false)
        .await;

    assert!(
        r.rosters
            .change_subclass(gwpd(), &player, "Detective", true)
            .await
            .is_allowed()
    );
    assert!(!r.holds("policedetective.txt", &player));
    assert!(
        r.rosters
            .change_subclass(gwpd(), &player, "Vice Officer", false)
            .await
            .is_allowed()
    );
    assert!(r.holds("policevice.txt", &player));
    assert!(r.holds(&gwpd().spawn_file, &player));
}

#[tokio::test]
async fn removing_one_they_do_not_hold_changes_nothing() {
    let r = Rig::new();
    let player = r.joined().await;
    let decision = r
        .rosters
        .change_subclass(gwpd(), &player, "Detective", true)
        .await;
    assert_eq!(decision.outcome, MembershipOutcome::NoChange);
    assert!(r.lines("policedetective.txt").is_empty());
}

#[tokio::test]
async fn a_subclass_the_faction_does_not_define_is_refused() {
    let r = Rig::new();
    let player = r.joined().await;
    let decision = r
        .rosters
        .change_subclass(gwpd(), &player, "Sniper", false)
        .await;
    assert_eq!(decision.outcome, MembershipOutcome::NoSuchSubclass);
    let sniper = std::fs::read_dir(r.dir.path()).unwrap().any(|e| {
        e.unwrap()
            .file_name()
            .to_string_lossy()
            .to_lowercase()
            .contains("sniper")
    });
    assert!(!sniper);
}

#[test]
fn every_subclass_appears_in_the_picker_labelled_with_its_faction() {
    let choices = subclass_choices(pavlov_core::factions::testing::ladder());
    let gwpd: Vec<String> = choices
        .iter()
        .filter(|c| c.faction == "GWPD")
        .map(|c| c.label())
        .collect();
    assert_eq!(
        gwpd,
        [
            "GWPD - Detective",
            "GWPD - Narcotics Bureau",
            "GWPD - Tactical Response Unit",
            "GWPD - Vice Officer"
        ]
    );
    assert!(choices.iter().any(|c| c.value() == "GWPD:Detective"));
}

#[test]
fn every_subclass_file_is_created_at_startup() {
    let r = Rig::new();
    let report = r.rosters.ensure_roster_files();
    assert!(report.failed.is_empty());
    for file in gwpd().subclasses.values() {
        assert!(r.dir.path().join(file).exists(), "{file}");
    }
}
