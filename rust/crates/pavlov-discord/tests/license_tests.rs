//! Port of `LicenseTests.cs`, the Discord half: the cash board reads keyed saves, `/license`'s
//! shape, and that it lands in the police log. The save-file editing is tested in
//! `pavlov-storage`, the licences themselves in `pavlov-core`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use pavlov_core::time::SystemClock;
use pavlov_discord::boards::Boards;
use pavlov_discord::commands::license::{AUDIT_ACTION, GIVE_ACTION, LicenseCommand};
use pavlov_discord::slash::{SlashCommand, subcommands_of};
use pavlov_discord::staff_log::is_police_action;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::registry::RconRegistry;

#[test]
fn the_cash_board_counts_keyed_saves() {
    let dir = tempfile::tempdir().unwrap();
    std::fs::write(
        dir.path().join("Alice.json"),
        "{\n\t\"money\": 300,\n\t\"drivers_license\": false,\n\t\"firearm_license\": false\n}",
    )
    .unwrap();
    std::fs::write(dir.path().join("Bob.json"), "{\"money\": 100}").unwrap();
    // The old text saves are not read any more.
    std::fs::write(dir.path().join("Carol.txt"), "5000").unwrap();
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    let boards = Boards::new(
        common::commands::store(),
        rcon,
        Some(dir.path().to_path_buf()),
        None,
        Arc::new(SystemClock),
    );

    let cash = boards.read_cash().unwrap();

    assert_eq!(cash.total, 400);
    assert_eq!(cash.top[0].0, "Alice");
}

#[test]
fn it_is_license_remove_with_a_choice_of_license() {
    let store = common::commands::store();
    let command = LicenseCommand {
        saves: None,
        access: common::commands::access(&store, &[]),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
    };
    let built = command.build();

    assert_eq!(built.name, "license");
    let subs: Vec<&str> = built.options.iter().map(|o| o.name.as_str()).collect();
    assert_eq!(subs, ["give", "remove"], "give was added beside remove");
    let remove = &built.options[1];
    assert_eq!(remove.name, "remove");
    let license = remove.options.iter().find(|o| o.name == "license").unwrap();
    let values: Vec<String> = license
        .choices
        .iter()
        .map(|c| c.value.to_string())
        .collect();
    assert_eq!(values, ["drivers", "firearm", "all"]);
    assert!(subcommands_of(&command).contains("remove"));
    let give = &built.options[0];
    let give_values: Vec<String> = give
        .options
        .iter()
        .find(|o| o.name == "license")
        .unwrap()
        .choices
        .iter()
        .map(|c| c.value.to_string())
        .collect();
    assert_eq!(give_values, ["drivers", "firearm", "all"]);
}

#[test]
fn it_lands_in_the_police_log() {
    assert!(is_police_action(AUDIT_ACTION));
    assert!(is_police_action(GIVE_ACTION));
}
