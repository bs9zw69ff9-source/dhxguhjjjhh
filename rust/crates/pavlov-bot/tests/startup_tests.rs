//! The startup checks (C# the head and tail of `Program.Main`): each fatal problem says what
//! to change, a missing factions file with a set named falls back with a note, and the
//! warnings fire on what they read. The C# had no test file for these; `FactionSourceTests`
//! (ported in pavlov-host) covers the helpers they use.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::io;
use std::path::Path;

use pavlov_bot::startup::{Environment, settle, warnings};
use pavlov_config::{Config, FeatureOptions};
use pavlov_core::servers::{DetectedServer, HostProbe};

struct EmptyBox;

impl HostProbe for EmptyBox {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        Vec::new()
    }
    fn installs_beside(&self, _: &str, _: &str) -> io::Result<Vec<String>> {
        Ok(Vec::new())
    }
    fn directory_exists(&self, _: &str) -> bool {
        false
    }
}

fn config(extra: &[(&str, &str)]) -> Config {
    let mut pairs = vec![
        ("DISCORD_TOKEN", "x"),
        ("CLIENT_ID", "1"),
        ("GUILD_ID", "1"),
    ];
    pairs.extend_from_slice(extra);
    Config::from_pairs(pairs)
}

fn fatal(extra: &[(&str, &str)]) -> String {
    settle(&config(extra), Path::new("/srv/bot"), &EmptyBox)
        .unwrap_err()
        .lines
        .join("\n")
}

#[test]
fn a_minimal_configuration_settles_and_notes_that_no_server_is_configured() {
    let settled = settle(&config(&[]), Path::new("/srv/bot"), &EmptyBox).unwrap();
    assert!(settled.notes.iter().any(|n| n.contains("No RCON servers")));
    assert!(settled.factions.names().count() > 0);
}

#[test]
fn a_missing_token_is_reported_with_every_other_problem() {
    let problems = settle(
        &Config::from_pairs([("RCON_HOST_1", "127.0.0.1")]),
        Path::new("/srv/bot"),
        &EmptyBox,
    )
    .unwrap_err()
    .lines;
    assert_eq!(problems[0], "Configuration is not usable:");
    assert!(problems.iter().any(|p| p.contains("DISCORD_TOKEN")));
    assert!(problems.len() > 2, "all of them at once: {problems:?}");
}

#[test]
fn the_fallout_theme_has_its_own_error_and_an_unknown_one_lists_the_skins() {
    assert!(fatal(&[("THEME", "fallout")]).contains("the fallout theme was removed"));
    let unknown = fatal(&[("THEME", "neon")]);
    assert!(unknown.contains("not a built-in theme"));
    assert!(unknown.contains("police"));
    assert!(settle(&config(&[("THEME", "police")]), Path::new("/"), &EmptyBox).is_ok());
}

#[test]
fn a_faction_set_that_is_not_a_set_is_fatal_never_a_fallback() {
    assert!(fatal(&[("FACTION_SET", "fallout")]).contains("the fallout factions were removed"));
    assert!(fatal(&[("FACTION_SET", "falout")]).contains("not a built-in set"));
}

#[test]
fn a_missing_factions_file_falls_back_to_a_named_set_with_a_note() {
    let dir = tempfile::tempdir().unwrap();
    let missing = dir.path().join("factions.json");
    let missing = missing.to_str().unwrap();

    let settled = settle(
        &config(&[("FACTIONS_PATH", missing), ("FACTION_SET", "police")]),
        dir.path(),
        &EmptyBox,
    )
    .unwrap();
    assert!(settled.notes.iter().any(|n| n.contains("does not exist")));

    let without_a_set = fatal(&[("FACTIONS_PATH", missing)]);
    assert!(without_a_set.contains("FACTIONS_PATH is set but unusable"));
}

#[test]
fn a_malformed_factions_file_stops_the_bot() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("factions.json");
    std::fs::write(&file, "{ not json").unwrap();
    let message = fatal(&[
        ("FACTIONS_PATH", file.to_str().unwrap()),
        ("FACTION_SET", "police"),
    ]);
    assert!(message.contains("unusable"));
}

#[test]
fn rosters_inside_ignore_paths_are_refused() {
    let message = fatal(&[
        (
            "FACTION_ROLES_PATH",
            "/home/steam/pavlovserver/Pavlov/Saved/ModSave/FactionRoles",
        ),
        (
            "IGNORE_PATHS",
            "/home/steam/pavlovserver/Pavlov/Saved/ModSave",
        ),
    ]);
    assert!(message.contains("inside IGNORE_PATHS"));
}

fn features(pairs: &[(&str, &str)]) -> FeatureOptions {
    FeatureOptions::bind(&Config::from_pairs(pairs.iter().copied()), &EmptyBox)
}

#[test]
fn an_env_file_others_can_read_is_warned_about_and_a_private_one_is_not() {
    let owners = features(&[("OWNER_IDS", "5"), ("MASTER_NAMES", "boss")]);
    let env = |mode| Environment {
        env_mode: Some(mode),
        env_path: "/srv/bot/.env".into(),
        ..Environment::default()
    };
    assert!(
        warnings(&owners, &env(0o100_644))
            .iter()
            .any(|w| w.contains("chmod 600"))
    );
    assert!(warnings(&owners, &env(0o100_600)).is_empty());
}

#[test]
fn root_plugins_and_missing_owners_are_each_warned_about() {
    let bare = features(&[("PLUGINS_ENABLED", "a")]);
    let said = warnings(
        &bare,
        &Environment {
            running_as_root: true,
            stray_plugins: true,
            ..Environment::default()
        },
    );
    assert!(said.iter().any(|w| w.contains("ROOT")));
    assert!(said.iter().any(|w| w.contains("Plugins are not supported")));
    assert!(
        said.iter()
            .any(|w| w.contains("OWNER_IDS and SUPER_OWNER_IDS"))
    );
    assert!(said.iter().any(|w| w.contains("MASTER_NAMES")));
}

#[test]
fn the_retired_menu_panel_settings_are_named_as_doing_nothing() {
    let said = warnings(
        &features(&[
            ("MENU_PANEL_CHANNEL", "5"),
            ("OWNER_IDS", "1"),
            ("MASTER_NAMES", "m"),
        ]),
        &Environment::default(),
    );
    assert!(
        said.iter().any(|w| w.contains("MENU_PANEL_CHANNEL")),
        "{said:?}"
    );
}
