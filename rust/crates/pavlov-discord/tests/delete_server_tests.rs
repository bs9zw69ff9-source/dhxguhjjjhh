//! `/deleteserver`'s rules: only the highest server, the last one allowed, and the lists must
//! match the slots. (The C# keeps `Problem` internal and untested; the provisioner's delete
//! steps are tested in pavlov-host.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_discord::commands::delete_server::{checklist, problem};
use pavlov_host::provisioner::{ProvisionStatus, ProvisionStep};

fn s(items: &[&str]) -> Vec<String> {
    items.iter().map(|i| (*i).to_owned()).collect()
}

#[test]
fn only_the_highest_server_can_be_deleted() {
    let units = s(&["pavlovserver", "pavlovserver1", "pavlovserver2"]);
    let bases = s(&["/a", "/b", "/c"]);
    assert!(problem(3, &[1, 2, 3], &units, &bases).is_none());
    assert!(
        problem(2, &[1, 2, 3], &units, &bases)
            .unwrap()
            .starts_with("only server 3 can be deleted")
    );
    assert!(
        problem(5, &[1, 2, 3], &units, &bases)
            .unwrap()
            .contains("there is no server 5")
    );
}

#[test]
fn the_last_server_standing_may_go() {
    assert!(problem(1, &[1], &s(&["pavlovserver"]), &s(&["/a"])).is_none());
}

#[test]
fn the_lists_must_match_the_slots_first() {
    let reason = problem(2, &[1, 2], &s(&["pavlovserver"]), &s(&["/a", "/b"])).unwrap();
    assert!(reason.contains("(they list 1 and 2)"), "{reason}");
}

#[test]
fn the_checklist_reads_running_until_every_step_lands() {
    let step = |status| ProvisionStep {
        name: "Stop the service".into(),
        status,
        detail: String::new(),
    };
    assert!(checklist(2, "pavlovserver1", &[]).description.as_deref() == Some("Starting…"));
    let running = checklist(2, "u", &[step(ProvisionStatus::Running)]);
    let done = checklist(2, "u", &[step(ProvisionStatus::Ok)]);
    let failed = checklist(2, "u", &[step(ProvisionStatus::Failed)]);
    assert_ne!(running.color, done.color);
    assert_ne!(done.color, failed.color);
}
