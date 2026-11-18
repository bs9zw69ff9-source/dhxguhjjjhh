//! Port of `FactionRenameTests.cs`: the police were renamed NYPD to GWPD. Only the NAME moved:
//! the roster files are what the game reads, and every record filed under the old name is
//! re-filed once at startup.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use chrono::DateTime;
use indexmap::IndexMap;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::factions::{FactionDefinition, FactionRegistry, FactionSet};
use pavlov_core::json::Stamp;
use pavlov_core::roles::RoleMap;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_host::economy::PayrollState;
use pavlov_host::factions::members::MemberIndex;
use pavlov_host::factions::rank_restorer::Suspensions;
use pavlov_host::factions::renames::{Renames, carry_over, rename_keys};
use pavlov_host::factions::{FactionMember, FactionMembers, RankSuspension};
use pavlov_storage::datasets::{FACTION_MEMBERS, PAYROLL_STATE, RANK_SUSPENSIONS, ROLES};

fn store() -> Arc<SerializedStore> {
    Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())))
}

fn at() -> chrono::DateTime<chrono::Utc> {
    DateTime::UNIX_EPOCH
}

fn member(faction: &str, name: &str) -> FactionMember {
    FactionMember {
        faction: faction.into(),
        name: name.into(),
        at: at(),
        by: "mod".into(),
        holds_all_ranks: false,
    }
}

fn faction(name: &str, former: &[&str]) -> FactionDefinition {
    let file = format!("{}spawn.txt", name.to_lowercase());
    FactionDefinition {
        name: name.into(),
        order: vec!["Member".into()],
        default: "Member".into(),
        rank_files: IndexMap::from([("Member".to_owned(), file.clone())]),
        spawn_file: file,
        subclasses: IndexMap::new(),
        former_names: former.iter().map(|f| (*f).to_owned()).collect(),
    }
}

#[test]
fn the_police_are_the_gwpd() {
    let names: Vec<&str> = FactionRegistry::police().names().collect();
    assert_eq!(names, ["GWPD"]);
    assert!(FactionRegistry::police().problems().is_empty());
}

#[test]
fn only_the_name_moved_not_the_files() {
    // Repointing a roster file takes the rank away, in game, from everybody listed in it.
    let gwpd = FactionRegistry::police().get(Some("GWPD")).unwrap();

    assert_eq!(gwpd.spawn_file, "policespawn.txt");
    assert_eq!(gwpd.rank_files["Cadet"], "policecadet.txt");
    assert_eq!(gwpd.rank_files["Chief of Police"], "policechief.txt");
    assert_eq!(gwpd.subclasses["Detective"], "policedetective.txt");
}

#[test]
fn the_old_name_still_finds_the_faction() {
    for name in ["NYPD", "nypd", "GWPD"] {
        assert_eq!(
            FactionRegistry::police().get(Some(name)).unwrap().name,
            "GWPD"
        );
        assert_eq!(FactionRegistry::get(Some(name)).unwrap().name, "GWPD");
    }
}

#[test]
fn the_old_name_is_never_listed() {
    assert!(!FactionRegistry::police().names().any(|n| n == "NYPD"));
}

#[test]
fn every_police_spelling_picks_the_police_set() {
    for preset in ["police", "gwpd", "nypd"] {
        assert!(std::ptr::eq(
            FactionRegistry::police(),
            FactionRegistry::preset(Some(preset)).unwrap()
        ));
    }
}

#[test]
fn a_former_name_that_is_a_faction_of_its_own_is_a_problem() {
    let set = FactionSet::of([faction("A", &["B"]), faction("B", &[])]);

    assert!(set.problems().iter().any(|p| p.contains("former name")));
    assert_eq!(set.get(Some("B")).unwrap().name, "B"); // the current name still wins
}

#[test]
fn a_former_name_claimed_twice_is_a_problem() {
    let set = FactionSet::of([faction("A", &["Old"]), faction("B", &["Old"])]);
    assert!(set.problems().iter().any(|p| p.contains("both list")));
}

#[tokio::test]
async fn records_under_the_old_name_are_refiled() {
    let store = store();
    let members: MemberIndex = IndexMap::from([
        ("1".to_owned(), member("NYPD", "Officer")),
        ("2".to_owned(), member("Gambino", "Tony")),
    ]);
    store.write(FACTION_MEMBERS, &members).await.unwrap();
    let mut suspensions = Suspensions::new();
    suspensions.insert(
        "officer",
        RankSuspension {
            player: "Officer".into(),
            faction: "NYPD".into(),
            restore_to: "Sergeant".into(),
            suspended_by: "admin".into(),
            until: at(),
        },
    );
    store.write(RANK_SUSPENSIONS, &suspensions).await.unwrap();
    let mut roles = RoleMap::default();
    roles.faction_roles.insert("NYPD", 55);
    store.write(ROLES, &roles).await.unwrap();
    let mut payroll = PayrollState::default();
    payroll.last_paid.insert("NYPD", Stamp(at()));
    payroll.previous.insert("NYPD", vec!["Officer".into()]);
    store.write(PAYROLL_STATE, &payroll).await.unwrap();

    assert_eq!(carry_over(&store, FactionRegistry::police()).await, 4);

    let index = FactionMembers::new(Arc::clone(&store));
    assert_eq!(index.of(1).unwrap().faction, "GWPD");
    assert_eq!(index.of(2).unwrap().faction, "Gambino");
    assert_eq!(index.in_faction("GWPD").len(), 1);

    let suspensions: Suspensions = store.read(RANK_SUSPENSIONS, Suspensions::new()).unwrap();
    assert_eq!(suspensions.get("officer").unwrap().faction, "GWPD");

    let roles: RoleMap = store.read(ROLES, RoleMap::default()).unwrap();
    assert_eq!(roles.role_for(Some("GWPD")), Some(55));
    assert!(roles.faction_roles.keys().all(|k| k != "NYPD"));

    let state: PayrollState = store.read(PAYROLL_STATE, PayrollState::default()).unwrap();
    assert_eq!(state.last_paid.get("GWPD"), Some(&Stamp(at())));
    assert_eq!(state.previous.get("GWPD").unwrap(), &["Officer".to_owned()]);
    assert!(state.last_paid.keys().all(|k| k != "NYPD"));
}

#[tokio::test]
async fn the_second_run_changes_nothing() {
    let store = store();
    let members: MemberIndex = IndexMap::from([("1".to_owned(), member("NYPD", "Officer"))]);
    store.write(FACTION_MEMBERS, &members).await.unwrap();

    assert_eq!(carry_over(&store, FactionRegistry::police()).await, 1);
    assert_eq!(carry_over(&store, FactionRegistry::police()).await, 0);
}

#[tokio::test]
async fn a_bot_with_no_renames_writes_nothing() {
    let store = store();
    let members: MemberIndex = IndexMap::from([("1".to_owned(), member("NYPD", "Officer"))]);
    store.write(FACTION_MEMBERS, &members).await.unwrap();
    let before = store.read_raw(FACTION_MEMBERS).unwrap();

    let unrenamed = FactionSet::of([FactionDefinition {
        name: "Rangers".into(),
        order: vec!["Recruit".into(), "Ranger".into()],
        default: "Recruit".into(),
        rank_files: IndexMap::from([
            ("Recruit".to_owned(), "rangerrecruit.txt".to_owned()),
            ("Ranger".to_owned(), "ranger.txt".to_owned()),
        ]),
        spawn_file: "rangerspawn.txt".into(),
        subclasses: IndexMap::new(),
        former_names: Vec::new(),
    }]);

    assert_eq!(carry_over(&store, &unrenamed).await, 0);
    assert_eq!(store.read_raw(FACTION_MEMBERS).unwrap(), before);
    assert_eq!(store.read_raw(ROLES).unwrap(), None);
}

#[test]
fn a_role_already_set_under_the_new_name_is_kept() {
    // Set after the rename, so set deliberately: it beats the one inherited from NYPD.
    let mut map = IgnoreCaseMap::new();
    map.insert("NYPD", 1_u64);
    map.insert("GWPD", 2_u64);
    let mut renames = Renames::new();
    for (old, now) in FactionRegistry::police().renames() {
        renames.insert(old, now);
    }

    rename_keys(&mut map, &renames);

    assert_eq!(map.get("GWPD"), Some(&2));
    assert!(map.keys().all(|k| k != "NYPD"));
}
