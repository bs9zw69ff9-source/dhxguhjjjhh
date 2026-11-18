//! Port of `ProvisioningTests.cs`, the pure half: the file text, the password and key charsets,
//! validation, slot arithmetic and the port defaults (`pavlov_core::provisioning`).
//!
//! It lives in this crate because the `.env` tests round-trip through the real dotenv parser,
//! which is here. The privileged half (SteamCMD, systemd and ufw argv, the step checklist, the
//! command shape) needs the host and command layers and joins a `provisioning_tests.rs` there.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::borrow::Cow;
use std::collections::HashSet;

use chrono::NaiveDate;
use pavlov_config::{Secret, dotenv};
use pavlov_core::provisioning::text::{
    clean_server_name, ensure_engine_ini, ensure_game_ini_setting, env_with_server,
    env_without_server, game_ini, rcon_settings, set_game_ini_value, systemd_unit,
};
use pavlov_core::provisioning::validation::{
    MAX_SERVERS, check, is_valid_password, is_valid_server_key,
};
use pavlov_core::provisioning::{
    MapEntry, ServerLayout, ServerProvisionSpec, plan, ports, target_slot,
};

struct S {
    slot: i32,
    unit: &'static str,
    dir: &'static str,
    name: &'static str,
    game: i32,
    query: i32,
    rcon: i32,
    password: String,
    max_players: i32,
    game_password: Option<&'static str>,
    maps: Option<Vec<MapEntry>>,
    server_key: Option<String>,
}

impl Default for S {
    fn default() -> Self {
        Self {
            slot: 3,
            unit: "pavlovserver2",
            dir: "/home/steam/pavlovserver2",
            name: "Mojave Authority",
            game: 7787,
            query: 8187,
            rcon: 9102,
            password: "Ab1_-.!xyzZ".into(),
            max_players: 24,
            game_password: None,
            maps: None,
            server_key: None,
        }
    }
}

fn spec(s: S) -> ServerProvisionSpec {
    ServerProvisionSpec {
        slot: s.slot,
        unit_name: s.unit.into(),
        install_dir: s.dir.into(),
        server_name: s.name.into(),
        game_port: s.game,
        query_port: s.query,
        rcon_port: s.rcon,
        rcon_password: Secret::new(s.password),
        max_players: s.max_players,
        maps: s
            .maps
            .unwrap_or_else(|| vec![MapEntry::new("datacenter", "SND")]),
        game_password: s.game_password.map(Secret::new),
        server_key: s.server_key.map(Secret::new),
    }
}

fn problems(s: &ServerProvisionSpec) -> Vec<String> {
    check(s, &HashSet::new(), &[], &[])
}

fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| (*s).to_owned()).collect()
}

fn date() -> NaiveDate {
    NaiveDate::from_ymd_opt(2026, 8, 27).unwrap()
}

fn count_lines(text: &str, starts_with: &str) -> usize {
    text.split('\n')
        .filter(|l| l.trim_start().starts_with(starts_with))
        .count()
}

// ---- config file text ----

#[test]
fn rcon_settings_is_the_two_lines_the_game_reads() {
    assert_eq!(
        rcon_settings("hunter2secret", 9102),
        "Password=hunter2secret\nPort=9102\n"
    );
}

#[test]
fn game_ini_is_the_config_that_actually_runs() {
    // GOLDEN, against the wiki's own Game.ini block. ServerName is UNQUOTED - quoting it made
    // the quotes part of the name in the browser.
    let s = spec(S {
        name: "Nuclear RP [#1]",
        max_players: 24,
        maps: Some(vec![MapEntry::new("UGC5616264", "CUSTOM")]),
        ..S::default()
    });

    let expected = "[/Script/Pavlov.DedicatedServer]\n\
                    bEnabled=true\n\
                    ServerName=Nuclear RP [#1]\n\
                    MaxPlayers=24\n\
                    bSecured=true\n\
                    bCustomServer=true\n\
                    bVerboseLogging=false\n\
                    bEnableBots=false\n\
                    bCompetitive=false #This only works for SND\n\
                    bWhitelist=false\n\
                    RefreshListTime=120\n\
                    LimitedAmmoType=0\n\
                    TickRate=90\n\
                    TimeLimit=60\n\
                    AFKTimeLimit=300\n\
                    #Password=0000\n\
                    #BalanceTableURL=\"vankruptgames/BalancingTable/main\"\n\
                    MapRotation=(MapId=\"UGC5616264\", GameMode=\"CUSTOM\")\n\
                    AdditionalMods=UGC3462586\n";

    assert_eq!(game_ini(&s), expected);
}

#[test]
fn every_map_in_the_rotation_gets_its_own_line() {
    let text = game_ini(&spec(S {
        maps: Some(vec![
            MapEntry::new("UGC1758245796", "GUN"),
            MapEntry::new("datacenter", "SND"),
        ]),
        ..S::default()
    }));

    assert!(text.contains("MapRotation=(MapId=\"UGC1758245796\", GameMode=\"GUN\")\n"));
    assert!(text.contains("MapRotation=(MapId=\"datacenter\", GameMode=\"SND\")\n"));
}

#[test]
fn the_server_key_is_written_as_server_key_equals_in_the_dedicated_server_section() {
    let text = game_ini(&spec(S {
        server_key: Some("abc-123_XYZ.+/=".into()),
        ..S::default()
    }));

    assert!(text.contains("\nServerKey=abc-123_XYZ.+/=\n"));
    // Inside the file's only section, after the wiki's block, last.
    assert!(text.starts_with("[/Script/Pavlov.DedicatedServer]\n"));
    assert_eq!(text.split('\n').filter(|l| l.starts_with('[')).count(), 1);
    assert!(text.ends_with("ServerKey=abc-123_XYZ.+/=\n"));
}

#[test]
fn without_a_server_key_there_is_no_server_key_line_at_all() {
    // Not an empty "ServerKey=": that would hand the game a blank credential.
    for key in [None, Some(String::new())] {
        let text = game_ini(&spec(S {
            server_key: key,
            ..S::default()
        }));
        assert!(!text.contains("ServerKey"));
        assert_eq!(text, game_ini(&spec(S::default())));
    }
}

#[test]
fn a_server_key_of_safe_characters_is_accepted() {
    for key in [
        "abc123",
        "0f8fad5b-d9cb-469f-a165-70867728950e",
        "A.b_c-d+e=f/g:h@i%j!k^l*m",
    ] {
        assert!(is_valid_server_key(Some(key)), "{key}");
        assert!(
            problems(&spec(S {
                server_key: Some(key.into()),
                ..S::default()
            }))
            .is_empty()
        );
    }
}

#[test]
fn a_server_key_that_could_break_the_file_is_refused_before_anything_is_written() {
    for key in [
        "has space",
        "a#comment",
        "quo\"te",
        "back`tick",
        "dollar$",
        "tab\there",
        "key\nPassword=hunter2", // would start another setting in Game.ini
        "key\r\nServerName=hijacked",
        "ünïcode",
        "trailing\n", // .NET's `$` let this one through
    ] {
        assert!(!is_valid_server_key(Some(key)), "{key:?}");
        let found = problems(&spec(S {
            server_key: Some(key.into()),
            ..S::default()
        }));
        assert_eq!(found.len(), 1, "{key:?}: {found:?}");
        assert!(found[0].contains("server key"));
    }
}

#[test]
fn a_server_key_is_at_most128_characters() {
    assert!(is_valid_server_key(Some(&"k".repeat(128))));
    assert!(!is_valid_server_key(Some(&"k".repeat(129))));
    assert!(!is_valid_server_key(Some(""))); // "no key" is None or empty on the spec, not a key
    assert!(!is_valid_server_key(None));
}

#[test]
fn the_password_line_is_commented_out_when_there_is_no_password() {
    // NOT OMITTED: an empty "Password=" locks everyone out.
    let open = game_ini(&spec(S::default()));
    assert!(open.contains("#Password=0000\n"));
    assert!(!open.contains("\nPassword="));

    let locked = game_ini(&spec(S {
        game_password: Some("0451"),
        ..S::default()
    }));
    assert!(locked.contains("\nPassword=0451\n"));
    assert!(!locked.contains("#Password=0000"));
}

#[test]
fn a_newline_in_the_server_name_cannot_split_the_line() {
    let text = game_ini(&spec(S {
        name: "Bad\nName",
        ..S::default()
    }));

    assert!(text.contains("ServerName=Bad Name\n"));
    assert_eq!(clean_server_name(Some("a\"b\u{7}c")), "abc");
    assert_eq!(clean_server_name(Some(&"x".repeat(80))).len(), 63);
    assert_eq!(clean_server_name(None), "");
}

#[test]
fn a_line_break_in_the_game_password_is_refused() {
    // Not checked in the C#; it would add a setting of its own to Game.ini.
    let found = problems(&spec(S {
        game_password: Some("0451\nbWhitelist=false"),
        ..S::default()
    }));
    assert!(found.iter().any(|p| p.contains("game password")));
}

#[test]
fn systemd_unit_is_the_wikis_file_and_nothing_else() {
    // THE WIKI'S UNIT, byte for byte, with only the install directory and -PORT filled in.
    let expected = "[Unit]\n\
                    Description=Pavlov VR dedicated server\n\
                    [Service]\n\
                    Type=simple\n\
                    WorkingDirectory=/home/steam/pavlovserver\n\
                    ExecStart=/home/steam/pavlovserver/PavlovServer.sh -PORT=7777\n\
                    RestartSec=1\n\
                    Restart=always\n\
                    User=steam\n\
                    Group=steam\n\
                    [Install]\n\
                    WantedBy = multi-user.target\n";

    let s = spec(S {
        slot: 1,
        unit: "pavlovserver",
        dir: "/home/steam/pavlovserver",
        game: 7777,
        ..S::default()
    });
    assert_eq!(systemd_unit(&s), expected);
}

#[test]
fn a_second_server_changes_only_its_directory_and_its_port() {
    let first = systemd_unit(&spec(S {
        slot: 1,
        unit: "pavlovserver",
        dir: "/home/steam/pavlovserver",
        game: 7777,
        ..S::default()
    }));
    let second = systemd_unit(&spec(S {
        slot: 2,
        unit: "pavlovserver1",
        dir: "/home/steam/pavlovserver1/",
        game: 7778,
        ..S::default()
    }));

    assert_eq!(
        second,
        first
            .replace("/home/steam/pavlovserver", "/home/steam/pavlovserver1")
            .replace("-PORT=7777", "-PORT=7778")
    );
}

// ---- editing one setting in an existing Game.ini ----

/// The shape of a real, hand-tuned file: notes, commented settings, a rotation.
const LIVE_GAME_INI: &str = "[/Script/Pavlov.DedicatedServer]\n\
                             bEnabled=true\n\
                             ServerName=Nuclear RP [#1]\n\
                             MaxPlayers=24\n\
                             bCompetitive=false #This only works for SND\n\
                             bWhitelist=false\n\
                             TickRate=60\n\
                             #Password=0000\n\
                             MapRotation=(MapId=\"UGC5616264\", GameMode=\"CUSTOM\")\n";

#[test]
fn flipping_one_setting_leaves_the_rest_of_the_file_alone() {
    let after = set_game_ini_value(LIVE_GAME_INI, "bWhitelist", "true");

    assert!(after.contains("bWhitelist=true\n"));
    assert!(!after.contains("bWhitelist=false"));
    assert!(after.contains("ServerName=Nuclear RP [#1]\n"));
    assert!(after.contains("TickRate=60\n"));
    assert!(after.contains("#Password=0000\n"));
    assert!(after.contains("MapRotation=(MapId=\"UGC5616264\", GameMode=\"CUSTOM\")\n"));
    assert_eq!(
        after,
        LIVE_GAME_INI.replace("bWhitelist=false", "bWhitelist=true")
    );
}

#[test]
fn a_note_on_the_changed_setting_survives() {
    let after = set_game_ini_value(LIVE_GAME_INI, "bCompetitive", "true");

    assert!(after.contains("bCompetitive=true #This only works for SND\n"));
}

#[test]
fn a_commented_out_line_is_not_the_setting_and_is_not_edited() {
    let after = set_game_ini_value(LIVE_GAME_INI, "Password", "hunter2");

    assert!(after.contains("#Password=0000\n"));
    assert!(after.contains("\nPassword=hunter2\n"));
}

#[test]
fn a_missing_setting_is_added_under_the_section_header() {
    let after = set_game_ini_value(
        "[/Script/Pavlov.DedicatedServer]\nbEnabled=true\n",
        "bWhitelist",
        "true",
    );

    let lines: Vec<&str> = after.split('\n').collect();
    assert_eq!(lines[0], "[/Script/Pavlov.DedicatedServer]");
    assert_eq!(lines[1], "bWhitelist=true");
    assert_eq!(lines[2], "bEnabled=true");
}

#[test]
fn the_key_is_matched_whole_and_case_insensitively() {
    let after = set_game_ini_value(
        "[/Script/Pavlov.DedicatedServer]\nbwhitelist = false\nbWhitelistExtra=1\n",
        "bWhitelist",
        "true",
    );

    assert!(after.contains("bWhitelist=true\n"));
    assert!(after.contains("bWhitelistExtra=1\n"));
    assert!(!after.contains("bwhitelist = false"));
}

// ---- .env rewriting, and its round trip through the real parser ----

#[test]
fn provisioning_writes_the_triple_and_the_lists_with_the_given_host() {
    let s = spec(S {
        slot: 3,
        rcon: 9102,
        password: "Ab1_-.!@%^*+=z".into(),
        ..S::default()
    });
    let env = env_with_server(
        "",
        &s,
        "10.0.0.7",
        &strings(&["pavlovserver", "pavlovserver1", "pavlovserver2"]),
        &strings(&[
            "/home/steam/pavlovserver",
            "/home/steam/pavlovserver1",
            "/home/steam/pavlovserver2",
        ]),
        None,
        date(),
    );

    // The host is the caller's, not a hard-coded loopback.
    assert!(env.contains("RCON_HOST_3=10.0.0.7\n"));
    assert!(env.contains("RCON_PORT_3=9102\n"));
    assert!(env.contains("RCON_PASSWORD_3=Ab1_-.!@%^*+=z\n"));
    assert!(env.contains("PAVLOV_UNITS=pavlovserver,pavlovserver1,pavlovserver2\n"));
    assert!(env.contains(
        "PAVLOV_BASES=/home/steam/pavlovserver,/home/steam/pavlovserver1,/home/steam/pavlovserver2\n"
    ));
    assert!(env.contains("# pavlov-bot: server 3 (pavlovserver2) provisioned on 2026-08-27\n"));
    // And every allowed password character survives the parser.
    assert_eq!(
        dotenv::parse(&env).get("RCON_PASSWORD_3"),
        Some("Ab1_-.!@%^*+=z")
    );
}

#[test]
fn reprovisioning_the_same_slot_leaves_exactly_one_line_for_each_key() {
    // THE DUPING: every provision used to APPEND its block.
    let mut env = String::new();
    for attempt in 1..=4 {
        env = env_with_server(
            &env,
            &spec(S {
                slot: 2,
                rcon: 9200,
                password: format!("Pass{attempt}xyzzy"),
                ..S::default()
            }),
            "127.0.0.1",
            &strings(&["pavlovserver", "pavlovserver1"]),
            &strings(&["/home/steam/pavlovserver", "/home/steam/pavlovserver1"]),
            None,
            date(),
        );
    }

    for key in [
        "RCON_HOST_2=",
        "RCON_PORT_2=",
        "RCON_PASSWORD_2=",
        "PAVLOV_UNITS=",
        "PAVLOV_BASES=",
        "# pavlov-bot:",
    ] {
        assert_eq!(count_lines(&env, key), 1, "{key}");
    }
    // And the surviving one is the latest.
    assert_eq!(
        dotenv::parse(&env).get("RCON_PASSWORD_2"),
        Some("Pass4xyzzy")
    );
}

#[test]
fn deleting_removes_the_slots_lines_outright_rather_than_blanking_them() {
    let env = env_with_server(
        "",
        &spec(S {
            slot: 2,
            rcon: 9200,
            ..S::default()
        }),
        "10.0.0.7",
        &strings(&["pavlovserver", "pavlovserver1"]),
        &strings(&["/a", "/b"]),
        None,
        date(),
    );
    assert!(env.contains("RCON_HOST_2"));

    let after = env_without_server(
        &env,
        2,
        &strings(&["pavlovserver"]),
        &strings(&["/a"]),
        date(),
    );

    assert!(!after.contains("RCON_HOST_2"));
    assert!(!after.contains("RCON_PORT_2"));
    assert!(!after.contains("RCON_PASSWORD_2"));
    assert!(!after.contains("10.0.0.7"));

    let parsed = dotenv::parse(&after);
    assert!(!parsed.contains_key("RCON_HOST_2"));
    assert_eq!(parsed.get("PAVLOV_UNITS"), Some("pavlovserver"));
}

#[test]
fn everything_the_operator_wrote_survives_the_rewrite() {
    let existing = "# the bot's own identity - do not share\n\
                    DISCORD_TOKEN=abc.def.ghi\n\
                    \n\
                    # why this is 40 and not 24\n\
                    PAYROLL_AMOUNT=40\n\
                    RCON_HOST_1=127.0.0.1\n";

    let after = env_with_server(
        existing,
        &spec(S {
            slot: 2,
            rcon: 9200,
            ..S::default()
        }),
        "127.0.0.1",
        &strings(&["pavlovserver", "pavlovserver1"]),
        &strings(&["/a", "/b"]),
        None,
        date(),
    );

    assert!(after.starts_with(existing.trim_end()));
    assert!(after.contains("# the bot's own identity - do not share"));
    assert!(after.contains("DISCORD_TOKEN=abc.def.ghi"));
    assert!(after.contains("# why this is 40 and not 24"));
    assert!(after.contains("PAYROLL_AMOUNT=40"));
    // Another server's slot is not this one's business.
    assert!(after.contains("RCON_HOST_1=127.0.0.1"));
}

#[test]
fn a_key_is_cleared_whichever_form_it_was_written_in() {
    let existing = "export RCON_HOST_2=1.1.1.1\nrcon_port_2: 9200\nRCON_PASSWORD_2=old\n";

    let after = env_without_server(existing, 2, &[], &[], date());

    assert!(!after.contains("1.1.1.1"));
    assert!(!after.contains("9200"));
    assert!(!after.contains("old"));
}

#[test]
fn removing_a_slot_leaves_no_trace_of_it_and_keeps_the_others() {
    let existing = "RCON_HOST_3=127.0.0.1\nRCON_PORT_3=9300\nRCON_PASSWORD_3=secret\n\
                    RCON_HOST_1=127.0.0.1\n\
                    PAVLOV_UNITS=pavlovserver,pavlovserver1,pavlovserver2\n";

    let after = env_without_server(
        existing,
        3,
        &strings(&["pavlovserver", "pavlovserver1"]),
        &strings(&["/home/steam/pavlovserver", "/home/steam/pavlovserver1"]),
        date(),
    );
    let parsed = dotenv::parse(&after);

    assert!(!parsed.contains_key("RCON_HOST_3"));
    assert!(!parsed.contains_key("RCON_PORT_3"));
    assert!(!parsed.contains_key("RCON_PASSWORD_3"));
    assert!(!after.contains("secret"));
    assert_eq!(parsed.get("RCON_HOST_1"), Some("127.0.0.1"));
    assert_eq!(
        parsed.get("PAVLOV_UNITS"),
        Some("pavlovserver,pavlovserver1")
    );
    assert_eq!(
        parsed.get("PAVLOV_BASES"),
        Some("/home/steam/pavlovserver,/home/steam/pavlovserver1")
    );
}

// ---- password charset ----

#[test]
fn password_charset_is_what_survives_both_file_formats() {
    for (password, valid) in [
        ("Abcdef12", true),       // exactly 8, mixed
        ("A1_-.!@%^*+=zZ", true), // every allowed symbol
        ("short1", false),        // under 8
        ("has space1", false),
        ("has#hash1", false), // '#' would start a comment in .env
        ("has\"quote1", false),
        ("has`tick1", false),
        ("has$dollar1", false),
        ("Abcdef12\n", false), // .NET's `$` let this one through
    ] {
        assert_eq!(is_valid_password(Some(password)), valid, "{password:?}");
    }
}

// ---- validation ----

#[test]
fn a_good_spec_has_no_problems() {
    let found = check(
        &spec(S::default()),
        &HashSet::from([9100, 9101]),
        &strings(&["pavlovserver", "pavlovserver1"]),
        &strings(&["/a", "/b"]),
    );
    assert!(found.is_empty(), "{found:?}");
}

#[test]
fn three_ports_must_all_differ() {
    let found = problems(&spec(S {
        game: 7777,
        query: 7777,
        rcon: 9102,
        ..S::default()
    }));
    assert!(
        found
            .iter()
            .any(|p| p.contains("different from each other"))
    );
}

#[test]
fn a_new_port_colliding_with_an_existing_rcon_port_is_rejected() {
    let found = check(
        &spec(S {
            rcon: 9100,
            ..S::default()
        }),
        &HashSet::from([9100]),
        &[],
        &[],
    );
    assert!(found.iter().any(|p| p.contains("already an RCON port")));
}

#[test]
fn out_of_range_ports_are_rejected() {
    let found = problems(&spec(S {
        game: 0,
        query: 70000,
        rcon: -1,
        ..S::default()
    }));
    assert_eq!(found.iter().filter(|p| p.contains("valid port")).count(), 3);
}

#[test]
fn a_unit_or_dir_already_in_use_is_rejected() {
    let found = check(
        &spec(S {
            unit: "pavlovserver1",
            dir: "/home/steam/pavlovserver1",
            ..S::default()
        }),
        &HashSet::new(),
        &strings(&["pavlovserver1"]),
        &strings(&["/home/steam/pavlovserver1"]),
    );

    assert!(
        found
            .iter()
            .any(|p| p.contains("already configured") && p.contains("unit"))
    );
    assert!(
        found
            .iter()
            .any(|p| p.contains("already configured") && p.contains("directory"))
    );
}

#[test]
fn install_dir_must_be_an_absolute_traversal_free_path() {
    for (dir, ok) in [
        ("/home/steam/pavlovserver2", true),
        ("relative/path", false),
        ("-rf", false), // would be read as an option by mkdir/chown
        ("/home/steam/../../etc/x", false),
        ("/home/steam/bad name", false),
        ("/home/steam/x\n", false),
    ] {
        let found = problems(&spec(S {
            dir,
            ..S::default()
        }));
        assert_eq!(
            !found.iter().any(|p| p.contains("install directory")),
            ok,
            "{dir:?}"
        );
    }
}

#[test]
fn bad_map_tokens_and_capacity_and_empty_name_are_rejected() {
    let found = problems(&spec(S {
        name: "   ",
        max_players: 500,
        maps: Some(vec![MapEntry::new("has space", "GUN\"")]),
        ..S::default()
    }));

    assert!(found.iter().any(|p| p.contains("Max players")));
    assert!(found.iter().any(|p| p.contains("empty after cleaning")));
    assert!(found.iter().any(|p| p.contains("Map id")));
    assert!(found.iter().any(|p| p.contains("Game mode")));
}

#[test]
fn capacity_stops_at_shacks_hard_limit_rather_than_the_pc_one() {
    for (max_players, allowed) in [(1, true), (24, true), (25, false), (50, false), (0, false)] {
        let found = problems(&spec(S {
            max_players,
            ..S::default()
        }));
        assert_eq!(
            !found.iter().any(|p| p.contains("Max players")),
            allowed,
            "{max_players}"
        );
    }
}

// ---- which slot gets built ----

#[test]
fn a_configured_slot_with_no_install_is_the_one_to_build() {
    assert_eq!(target_slot(1, &HashSet::new()), 1);
    assert_eq!(target_slot(3, &HashSet::from([2, 3])), 1);
    assert_eq!(target_slot(3, &HashSet::from([1, 3])), 2);
}

#[test]
fn with_every_configured_server_really_installed_the_next_slot_is_a_new_one() {
    assert_eq!(target_slot(1, &HashSet::from([1])), 2);
    assert_eq!(target_slot(3, &HashSet::from([1, 2, 3])), 4);
    assert_eq!(target_slot(0, &HashSet::new()), 1);
}

fn layout(indices: &[i32], units: &[&str], bases: &[&str]) -> ServerLayout {
    ServerLayout {
        rcon_indices: indices.to_vec(),
        units: strings(units),
        bases: strings(bases),
    }
}

#[test]
fn filling_in_an_existing_slot_replaces_its_entry_rather_than_appending() {
    let planned = plan(
        &layout(&[1, 2], &["stale", "pavlovserver1"], &["/stale", "/b"]),
        1,
        "pavlovserver",
        "/home/steam/pavlovserver",
        MAX_SERVERS,
    )
    .unwrap();

    assert_eq!(planned.slot, 1);
    assert_eq!(planned.final_units, ["pavlovserver", "pavlovserver1"]);
    assert_eq!(planned.final_bases, ["/home/steam/pavlovserver", "/b"]);
}

#[test]
fn filling_in_still_needs_the_lists_to_line_up_with_the_rcon_slots() {
    let found = plan(
        &layout(&[1, 2], &["only-one"], &["/a", "/b"]),
        1,
        "pavlovserver",
        "/home/steam/pavlovserver",
        MAX_SERVERS,
    )
    .unwrap_err();

    assert!(found.iter().any(|p| p.contains("exactly 2 entries")));
}

// ---- slot allocation and alignment ----

#[test]
fn a_contiguous_layout_takes_the_next_slot_and_appends_to_both_lists() {
    let planned = plan(
        &layout(&[2, 1], &["pavlovserver", "pavlovserver1"], &["/a", "/b"]),
        3,
        "pavlovserver2",
        "/c",
        MAX_SERVERS,
    )
    .unwrap();

    assert_eq!(planned.slot, 3);
    assert_eq!(
        planned.final_units,
        ["pavlovserver", "pavlovserver1", "pavlovserver2"]
    );
    assert_eq!(planned.final_bases, ["/a", "/b", "/c"]);
}

#[test]
fn a_gapped_rcon_layout_is_refused() {
    let found = plan(
        &layout(&[1, 3], &["a", "b"], &["/a", "/b"]),
        3,
        "u",
        "/d",
        MAX_SERVERS,
    )
    .unwrap_err();

    assert!(found.iter().any(|p| p.contains("gapless")));
}

#[test]
fn lists_that_do_not_match_the_rcon_count_are_refused() {
    // Two RCON servers but only one unit listed: a positional append would misalign.
    let found = plan(
        &layout(&[1, 2], &["only-one"], &["/a", "/b"]),
        3,
        "u",
        "/d",
        MAX_SERVERS,
    )
    .unwrap_err();

    assert!(
        found
            .iter()
            .any(|p| p.contains("PAVLOV_UNITS must list exactly 2"))
    );
}

#[test]
fn an_empty_box_can_still_grow_its_first_server() {
    let planned = plan(
        &layout(&[], &[], &[]),
        1,
        "pavlovserver",
        "/home/steam/pavlovserver",
        MAX_SERVERS,
    )
    .unwrap();

    assert_eq!(planned.slot, 1);
    assert_eq!(planned.final_units, ["pavlovserver"]);
    assert_eq!(planned.final_bases, ["/home/steam/pavlovserver"]);
}

#[test]
fn running_out_of_slots_is_refused() {
    let indices: Vec<i32> = (1..=MAX_SERVERS).collect();
    let names: Vec<String> = indices.iter().map(|i| format!("u{i}")).collect();
    let bases: Vec<String> = indices.iter().map(|i| format!("/b{i}")).collect();
    let full = ServerLayout {
        rcon_indices: indices,
        units: names,
        bases,
    };

    let found = plan(&full, MAX_SERVERS + 1, "u10", "/b10", MAX_SERVERS).unwrap_err();

    assert!(found.iter().any(|p| p.contains("no room")));
}

// ---- per-slot port defaults ----

#[test]
fn default_ports_step_per_slot_so_a_second_server_is_not_a_copy_of_the_first() {
    for (slot, game, query, rcon) in [
        (1, 7777, 8177, 9100),
        (2, 7778, 8178, 9200),
        (3, 7779, 8179, 9300),
        (9, 7785, 8185, 9900),
    ] {
        assert_eq!(ports::game_port(slot), game);
        assert_eq!(ports::query_port_for(ports::game_port(slot)), query);
        assert_eq!(ports::rcon_port(slot), rcon);
    }
}

#[test]
fn the_query_port_follows_the_game_port_even_when_it_is_chosen_by_hand() {
    assert_eq!(ports::query_port_for(7800), 8200);
    assert_eq!(ports::QUERY_PORT_OFFSET, 8177 - 7777);
}

#[test]
fn every_default_port_stays_inside_the_valid_range() {
    for slot in 1..=MAX_SERVERS {
        let s = spec(S {
            slot,
            game: ports::game_port(slot),
            query: ports::query_port_for(ports::game_port(slot)),
            rcon: ports::rcon_port(slot),
            ..S::default()
        });
        assert!(problems(&s).is_empty(), "slot {slot}");
    }
}

// ---- an existing Game.ini: only ServerKey, only when absent ----

#[test]
fn server_key_is_added_to_an_existing_game_ini_without_touching_anything_else() {
    let updated = ensure_game_ini_setting(LIVE_GAME_INI, "ServerKey", "k3y");

    assert_eq!(updated, format!("{LIVE_GAME_INI}ServerKey=k3y\n"));
}

#[test]
fn engine_ini_gets_the_console_variables_block_at_the_bottom() {
    assert_eq!(
        ensure_engine_ini(""),
        "[ConsoleVariables]\nnet.MaxPlayersOverride=32\n"
    );

    let generated = "[/Script/Engine.GameSession]\nMaxPlayers=10\n";
    assert_eq!(
        ensure_engine_ini(generated),
        format!("{generated}\n[ConsoleVariables]\nnet.MaxPlayersOverride=32\n")
    );
    assert_eq!(
        ensure_engine_ini("MaxPlayers=10"),
        "MaxPlayers=10\n\n[ConsoleVariables]\nnet.MaxPlayersOverride=32\n"
    );
}

#[test]
fn engine_ini_adds_the_key_to_an_existing_console_variables_section_and_never_twice() {
    let once = ensure_engine_ini("[ConsoleVariables]\nfoo=1\n").into_owned();

    assert_eq!(
        once,
        "[ConsoleVariables]\nfoo=1\nnet.MaxPlayersOverride=32\n"
    );
    assert!(matches!(ensure_engine_ini(&once), Cow::Borrowed(_)));

    // Inside the section, ahead of the next one and the blank line that closes it.
    assert_eq!(
        ensure_engine_ini("[ConsoleVariables]\nfoo=1\n\n[Other]\nx=1\n"),
        "[ConsoleVariables]\nfoo=1\nnet.MaxPlayersOverride=32\n\n[Other]\nx=1\n"
    );
}

#[test]
fn an_existing_server_key_is_never_replaced() {
    let ini = format!("{LIVE_GAME_INI}ServerKey=theirs\n");

    assert!(matches!(
        ensure_game_ini_setting(&ini, "ServerKey", "mine"),
        Cow::Borrowed(_)
    ));
}

#[test]
fn a_commented_out_server_key_does_not_count() {
    let updated = ensure_game_ini_setting(
        "[/Script/Pavlov.DedicatedServer]\n#ServerKey=old\n",
        "ServerKey",
        "new",
    );

    assert!(updated.contains("\nServerKey=new\n"));
}
