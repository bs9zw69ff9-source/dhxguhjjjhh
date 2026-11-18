//! Port of `ProvisioningTests.cs`, the host half: the privileged command lines (pinned, never
//! run), the SteamCMD lookup, the checklist and its cursor, the delete guards, and the pure
//! command helpers. The text and validation half is `pavlov-config/tests/provisioning_tests.rs`;
//! the slash-command shape joins in the Discord crate.
//!
//! BEYOND THE C#: the provisioner takes its paths, privilege and supervisor as inputs, so a
//! whole provision and a whole delete are driven here against a recording runner, a fake RCON
//! server and a temp directory. The C# could only count `run.Start(` calls in its source.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use common::{FakeRunner, exited};
use futures_util::future::BoxFuture;
use pavlov_config::Secret;
use pavlov_core::provisioning::commands::{default_map, delete_problem, parse_maps, unit_suffix};
use pavlov_core::provisioning::text::systemd_unit;
use pavlov_core::provisioning::{MapEntry, ServerProvisionSpec, ports};
use pavlov_core::servers::units::without_legacy_docker_prefix;
use pavlov_host::process::ProcessOutcome;
use pavlov_host::provisioner::{
    COPY_STEP_NAMES, DELETE_STEP_NAMES, DeleteRequest, LEGACY_SUDOERS_DROP_IN, Progress,
    ProvisionOutcome, ProvisionRequest, ProvisionStatus, ProvisionStep, Provisioner,
    ProvisionerPaths, Run, STEP_NAMES, ServerProvisioner, apt_install_argv, apt_update_argv,
    config_directories, copy_install_argv, deletable_install_problem, dependency_packages,
    docker_exists_argv, docker_remove_argv, enable_argv, holds_only_saved_data, home_from_getent,
    launch_script_argv, libcxx_link_argv, libcxx_remove_argv, optional_config_files,
    steam_client_install_path, steam_client_sdk_directory, steam_client_source,
    steam_client_update_argv, steamcmd_argv, steamcmd_candidates, steamcmd_download_argv,
    steamcmd_extract_argv, steps_for, tail, ufw_allow_argv, useradd_argv,
};
use pavlov_host::supervisor::BotSupervisor;
use pavlov_rcon::testing::FakeRconServer;
use pavlov_storage::atomic_file;

fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| (*s).to_owned()).collect()
}

fn spec(
    slot: i32,
    unit: &str,
    dir: &str,
    game: i32,
    rcon: i32,
    password: &str,
) -> ServerProvisionSpec {
    ServerProvisionSpec {
        slot,
        unit_name: unit.into(),
        install_dir: dir.into(),
        server_name: "Mojave Authority".into(),
        game_port: game,
        query_port: ports::query_port_for(game),
        rcon_port: rcon,
        rcon_password: Secret::new(password),
        max_players: 24,
        maps: vec![MapEntry::new("datacenter", "SND")],
        game_password: None,
        server_key: None,
    }
}

fn default_spec() -> ServerProvisionSpec {
    spec(
        3,
        "pavlovserver2",
        "/home/steam/pavlovserver2",
        7787,
        9102,
        "Ab1_-.!xyzZ",
    )
}

/// A progress sink that keeps every snapshot.
fn recorder() -> (Progress, Arc<Mutex<Vec<Vec<ProvisionStep>>>>) {
    let seen = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&seen);
    let progress: Progress = Arc::new(move |steps| {
        sink.lock().unwrap().push(steps);
        Box::pin(async {})
    });
    (progress, seen)
}

fn ignore() -> Progress {
    Arc::new(|_| Box::pin(async {}))
}

// ---- the privileged command lines (pinned, never run here) ----

#[test]
fn steam_cmd_argv_uses_the_pavlov_app_id_in_order() {
    // The wiki's line, and "-beta shack", NOT "-beta default": shack is the Quest build this bot
    // is built around, and the wrong branch yields a server the rest of the bot never sees.
    assert_eq!(
        steamcmd_argv("/home/steam/pavlovserver2"),
        strings(&[
            "+force_install_dir",
            "/home/steam/pavlovserver2",
            "+login",
            "anonymous",
            "+app_update",
            "622970",
            "-beta",
            "shack",
            "+exit"
        ])
    );
}

#[test]
fn enable_argv_and_ufw_rule_are_exact() {
    assert_eq!(
        enable_argv("pavlovserver2"),
        strings(&["enable", "--now", "pavlovserver2"])
    );
    // The wiki's "sudo ufw allow <port>": no protocol, so both TCP and UDP.
    assert_eq!(ufw_allow_argv(7777), strings(&["allow", "7777"]));
    assert_eq!(ufw_allow_argv(9100), strings(&["allow", "9100"]));
}

#[test]
fn useradd_argv_gives_the_steam_account_a_real_home_and_shell() {
    assert_eq!(
        useradd_argv("steam"),
        strings(&["-m", "-s", "/bin/bash", "steam"])
    );
}

// ---- command helpers ----

#[test]
fn unit_numbering_is_off_by_one_like_the_box() {
    for (slot, suffix) in [(1, ""), (2, "1"), (3, "2")] {
        assert_eq!(
            format!("pavlovserver{}", unit_suffix(slot)),
            format!("pavlovserver{suffix}")
        );
    }
}

#[test]
fn parse_maps_defaults_and_splits_modes() {
    // Naming none gets this network's own map, not a stock one nobody here plays.
    assert_eq!(
        parse_maps(None),
        vec![MapEntry::new("UGC5616264", "CUSTOM")]
    );
    assert_eq!(parse_maps(Some("   ")), vec![default_map()]);

    assert_eq!(
        parse_maps(Some("UGC123:GUN")),
        vec![MapEntry::new("UGC123", "GUN")]
    );
    assert_eq!(
        parse_maps(Some("UGC123:GUN, datacenter:SND")),
        vec![
            MapEntry::new("UGC123", "GUN"),
            MapEntry::new("datacenter", "SND")
        ]
    );
    // A bare map with no mode defaults to SND.
    assert_eq!(parse_maps(Some("sand")), vec![MapEntry::new("sand", "SND")]);
}

// ---- atomic write ----

#[test]
fn atomic_write_replaces_an_existing_file_whole() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("pavlov-atomic");

    atomic_file::write_text(&path, "first\n").unwrap();
    atomic_file::write_text(&path, "second\n").unwrap();

    assert_eq!(fs::read_to_string(&path).unwrap(), "second\n");
    // The temp file has been renamed away: nothing but the target is left.
    let names: Vec<_> = fs::read_dir(dir.path())
        .unwrap()
        .map(|e| e.unwrap().file_name())
        .collect();
    assert_eq!(names, ["pavlov-atomic"]);
}

// ---- the seam ----

/// The C# `StubServerProvisioner`.
#[derive(Default)]
struct StubProvisioner {
    received: Mutex<Option<ProvisionRequest>>,
}

impl Provisioner for StubProvisioner {
    fn provision<'a>(
        &'a self,
        request: &'a ProvisionRequest,
        on_progress: Progress,
    ) -> BoxFuture<'a, ProvisionOutcome> {
        Box::pin(async move {
            *self.received.lock().unwrap() = Some(request.clone());
            let steps = vec![
                ProvisionStep {
                    name: "Pre-flight checks".into(),
                    status: ProvisionStatus::Ok,
                    detail: "ok".into(),
                },
                ProvisionStep {
                    name: "SteamCMD install".into(),
                    status: ProvisionStatus::Ok,
                    detail: "installed".into(),
                },
            ];
            on_progress(steps.clone()).await;
            ProvisionOutcome {
                steps,
                restart_queued: true,
            }
        })
    }

    fn delete<'a>(&'a self, _: &'a DeleteRequest, _: Progress) -> BoxFuture<'a, ProvisionOutcome> {
        Box::pin(async {
            ProvisionOutcome {
                steps: Vec::new(),
                restart_queued: true,
            }
        })
    }
}

#[tokio::test]
async fn the_provisioner_seam_reports_progress_and_returns_an_outcome() {
    let stub = StubProvisioner::default();
    let request = ProvisionRequest::new(
        default_spec(),
        strings(&["a", "b", "c"]),
        strings(&["/a", "/b", "/c"]),
        None,
        "/tmp/.env",
    );
    let (progress, seen) = recorder();

    let seam: &dyn Provisioner = &stub;
    let outcome = seam.provision(&request, progress).await;

    let received = stub.received.lock().unwrap().clone().unwrap();
    assert_eq!(received.spec.unit_name, request.spec.unit_name);
    assert_eq!(received.env_path, request.env_path);
    assert!(!seen.lock().unwrap().is_empty());
    assert!(outcome.ok());
    assert!(outcome.restart_queued);
}

// ---- slot to unit name to install dir to unit file ----

#[test]
fn the_unit_file_gets_this_servers_own_directory_and_port() {
    // THE WHOLE CHAIN: the slot picks the unit name (off by one), the unit name picks the
    // install directory, and the unit file has to carry THAT directory and THAT game port.
    for (slot, expected_unit, expected_dir, expected_game_port) in [
        (1, "pavlovserver", "/home/steam/pavlovserver", 7777),
        (2, "pavlovserver1", "/home/steam/pavlovserver1", 7778),
        (3, "pavlovserver2", "/home/steam/pavlovserver2", 7779),
    ] {
        let unit = format!("pavlovserver{}", unit_suffix(slot));
        assert_eq!(unit, expected_unit);
        let install_dir = format!("/home/steam/{unit}");
        assert_eq!(install_dir, expected_dir);

        let text = systemd_unit(&spec(
            slot,
            &unit,
            &install_dir,
            ports::game_port(slot),
            9102,
            "pw",
        ));

        assert!(
            text.contains(&format!("WorkingDirectory={expected_dir}\n")),
            "{text}"
        );
        assert!(
            text.contains(&format!(
                "ExecStart={expected_dir}/PavlovServer.sh -PORT={expected_game_port}\n"
            )),
            "{text}"
        );
    }
}

// ---- finding SteamCMD ----

#[test]
fn the_tarball_install_location_is_among_the_candidates() {
    // THE REGRESSION: "steamcmd" was trusted to be on PATH, and the documented tarball install
    // puts nothing there.
    let candidates = steamcmd_candidates("/home/steam");

    assert!(candidates.contains(&"/home/steam/Steam/steamcmd.sh".to_owned()));
    // A distro package, if one is ever listed, wins over the tarball.
    let packaged = candidates.iter().position(|c| c == "/usr/games/steamcmd");
    let tarball = candidates
        .iter()
        .position(|c| c == "/home/steam/Steam/steamcmd.sh");
    if let (Some(packaged), Some(tarball)) = (packaged, tarball) {
        assert!(
            packaged < tarball,
            "a packaged steamcmd should be preferred over an unpacked tarball"
        );
    }
    // Every candidate is absolute - a bare name would be a PATH lookup again.
    assert!(candidates.iter().all(|c| c.starts_with('/')));
}

#[test]
fn a_trailing_slash_on_the_home_does_not_double_up() {
    assert!(
        steamcmd_candidates("/home/steam/").contains(&"/home/steam/Steam/steamcmd.sh".to_owned())
    );
}

#[test]
fn the_steam_home_is_parsed_out_of_getent_rather_than_assumed() {
    for (output, expected) in [
        (
            "steam:x:1001:1001::/home/steam:/bin/bash\n",
            Some("/home/steam"),
        ),
        (
            "steam:x:1001:1001::/srv/steam:/bin/bash",
            Some("/srv/steam"),
        ),
        ("", None),                              // getent said nothing
        ("garbage", None),                       // not a passwd line
        ("steam:x:1:1::relative:/bin/sh", None), // a home that is not absolute
    ] {
        assert_eq!(
            home_from_getent(Some(output)).as_deref(),
            expected,
            "{output:?}"
        );
    }
}

#[test]
fn the_bootstrap_downloads_to_a_file_rather_than_through_a_shell_pipe() {
    // The documented one-liner pipes curl into tar, which needs a shell. Flags and URL kept
    // exactly; only the pipe becomes a download plus a separate tar.
    let download = steamcmd_download_argv("/home/steam/Steam/steamcmd_linux.tar.gz");

    assert!(download.contains(
        &"https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz".to_owned()
    ));
    assert!(download.contains(&"-sqL".to_owned()));
    assert!(download.contains(&"-o".to_owned()));
    assert!(download.contains(&"/home/steam/Steam/steamcmd_linux.tar.gz".to_owned()));
    assert!(!download.iter().any(|a| a.contains('|')));

    assert_eq!(
        steamcmd_extract_argv(
            "/home/steam/Steam/steamcmd_linux.tar.gz",
            "/home/steam/Steam"
        ),
        strings(&[
            "-xzf",
            "/home/steam/Steam/steamcmd_linux.tar.gz",
            "-C",
            "/home/steam/Steam"
        ])
    );
}

// ---- reporting a failure ----

#[test]
fn failure_output_keeps_the_end_where_the_reason_is() {
    // THE REGRESSION: the FIRST 300 characters were kept, so the operator saw the SteamCMD
    // banner and never the error. Tools put the reason last.
    let output = format!("{}\nERROR! the part that actually matters", "x".repeat(900));

    let excerpt = tail(Some(&output), 600);

    assert!(excerpt.contains("ERROR! the part that actually matters"));
    assert!(excerpt.starts_with('…'));
    assert!(
        excerpt.chars().count() <= 601,
        "the excerpt has to stay well inside Discord's embed limits"
    );
}

#[test]
fn short_output_is_passed_through_whole() {
    assert_eq!(
        tail(Some("useradd: user 'steam' already exists"), 600),
        "useradd: user 'steam' already exists"
    );
    assert_eq!(tail(None, 600), "");
    assert_eq!(tail(Some("  trimmed\n"), 600), "trimmed");
}

// ---- copying an existing install ----

#[test]
fn the_copy_takes_the_sources_contents_so_it_cannot_nest_one_level_down() {
    // "cp -r src dst" BECOMES dst or lands as dst/src depending on whether dst exists; the "/."
    // makes it the contents either way.
    assert_eq!(
        copy_install_argv("/home/steam/pavlovserver", "/home/steam/pavlovserver1"),
        strings(&[
            "-r",
            "/home/steam/pavlovserver/.",
            "/home/steam/pavlovserver1"
        ])
    );
    // A trailing slash on the source must not produce "pavlovserver//.".
    assert_eq!(
        copy_install_argv("/home/steam/pavlovserver/", "/home/steam/pavlovserver2"),
        strings(&[
            "-r",
            "/home/steam/pavlovserver/.",
            "/home/steam/pavlovserver2"
        ])
    );
}

#[test]
fn copying_swaps_the_two_steam_cmd_steps_for_one_copy_step() {
    let download = steps_for(false);
    let copy = steps_for(true);

    assert!(download.contains(&"SteamCMD install"));
    assert!(download.contains(&"SteamCMD (locate or bootstrap)"));
    assert!(copy.contains(&"Copy an existing install"));
    assert!(!copy.contains(&"SteamCMD install"));
    assert!(!copy.contains(&"SteamCMD (locate or bootstrap)"));

    // One step shorter, identical either side of the swap, steamclient.so right after the step
    // that makes SteamCMD available.
    assert_eq!(download.len() - 1, copy.len());
    let at = download
        .iter()
        .position(|s| *s == "SteamCMD (locate or bootstrap)")
        .unwrap();
    assert_eq!(download[..at], copy[..at]);
    assert_eq!(download[at + 1], "steamclient.so (SteamCMD app 1007)");
    assert_eq!(copy[at + 1], "steamclient.so (SteamCMD app 1007)");
    assert_eq!(download[at + 3..], copy[at + 2..]);
}

// ---- the directories and files the guide creates ----

#[test]
fn the_guides_directories_and_optional_files_are_all_created() {
    assert_eq!(
        config_directories("/home/steam/pavlovserver1"),
        strings(&[
            "/home/steam/pavlovserver1/Pavlov/Saved/Logs",
            "/home/steam/pavlovserver1/Pavlov/Saved/Config/LinuxServer",
            "/home/steam/pavlovserver1/Pavlov/Saved/maps",
        ])
    );
    assert_eq!(
        optional_config_files("/home/steam/pavlovserver1"),
        strings(&[
            "/home/steam/pavlovserver1/Pavlov/Saved/Config/mods.txt",
            "/home/steam/pavlovserver1/Pavlov/Saved/Config/blacklist.txt",
            "/home/steam/pavlovserver1/Pavlov/Saved/Config/whitelist.txt",
        ])
    );
}

// ---- deleting a server ----

#[test]
fn only_the_highest_server_can_be_deleted() {
    // Servers are positional as well as indexed: a gap re-maps which unit is which server.
    let units = strings(&["a", "b", "c"]);
    let bases = strings(&["/a", "/b", "/c"]);

    assert_eq!(delete_problem(3, &[1, 2, 3], &units, &bases), None);

    let middle = delete_problem(2, &[1, 2, 3], &units, &bases).unwrap();
    assert!(middle.contains("only server 3 can be deleted"), "{middle}");

    // SERVER 1 REACHES THE EXPLANATION rather than being refused by Discord's option range.
    let first = delete_problem(1, &[1, 2, 3], &units, &bases).unwrap();
    assert!(first.contains("only server 3 can be deleted"), "{first}");
}

#[test]
fn a_server_number_that_does_not_exist_says_so() {
    let problem =
        delete_problem(5, &[1, 2], &strings(&["a", "b"]), &strings(&["/a", "/b"])).unwrap();
    assert!(problem.contains("no server 5"), "{problem}");
}

#[test]
fn the_last_remaining_server_including_server_one_can_be_deleted() {
    // The bot starts with none configured, so the restart after it is safe.
    assert_eq!(
        delete_problem(1, &[1], &strings(&["a"]), &strings(&["/a"])),
        None
    );
}

#[test]
fn deleting_needs_the_lists_to_line_up_with_the_rcon_slots() {
    let problem =
        delete_problem(2, &[1, 2], &strings(&["only-one"]), &strings(&["/a", "/b"])).unwrap();
    assert!(problem.contains("exactly 2 entries"), "{problem}");
}

#[test]
fn a_zero_or_negative_slot_is_never_deletable() {
    // Rust-only: the C# int comparison refused these, and an unsigned cast must not let one by.
    assert!(delete_problem(0, &[], &[], &[]).is_none_or(|p| p.contains("only server")));
    assert!(delete_problem(-1, &[], &[], &[]).is_some());
    assert!(delete_problem(0, &[1], &strings(&["a"]), &strings(&["/a"])).is_some());
}

#[test]
fn a_recursive_delete_refuses_anything_that_is_not_one_servers_install() {
    // The one guard worth having in front of "rm -rf" as root.
    for (path, deletable) in [
        ("/home/steam/pavlovserver1", true),
        ("/", false),
        ("/home", false),
        ("/home/steam", false),           // the steam account's whole home
        ("/home/steam/../../etc", false), // traversal
        ("relative/path", false),
        ("", false),
    ] {
        assert_eq!(
            deletable_install_problem(Some(path)).is_none(),
            deletable,
            "{path}"
        );
    }
    assert!(deletable_install_problem(None).is_some());
}

// ---- steam's sudo grant: only ever removed ----

#[test]
fn the_legacy_sudo_grant_is_the_bots_own_drop_in() {
    assert_eq!(LEGACY_SUDOERS_DROP_IN, "/etc/sudoers.d/pavlov-steam-full");
    assert_eq!(
        ProvisionerPaths::default().legacy_sudoers_drop_in,
        Path::new(LEGACY_SUDOERS_DROP_IN)
    );
}

// ---- the wiki's dependency and post-installation steps ----

#[test]
fn dependencies_are_the_wikis_package_line_for_this_release() {
    for (os_release, gcc) in [
        ("ID=ubuntu\nVERSION_ID=\"24.04\"\n", "lib32gcc-s1"),
        ("ID=ubuntu\nVERSION_ID=\"22.10\"\n", "lib32gcc-s1"),
        ("ID=ubuntu\nVERSION_ID=\"22.04\"\n", "lib32gcc1"),
        ("ID=debian\nVERSION_ID=\"12\"\n", "lib32gcc-s1"),
        (
            "ID=pop\nID_LIKE=\"ubuntu debian\"\nVERSION_ID=\"22.04\"\n",
            "lib32gcc-s1",
        ),
    ] {
        assert_eq!(
            dependency_packages(Some(os_release)),
            Some(strings(&["gdb", "curl", gcc, "libc++-dev", "unzip"])),
            "{os_release:?}"
        );
    }
}

#[test]
fn a_system_that_is_not_debian_like_gets_no_package_step() {
    // The wiki's Rocky/RHEL line switches SELinux to permissive for the whole machine.
    for os_release in [
        "ID=rocky\nID_LIKE=\"rhel centos fedora\"\n",
        "ID=alpine\n",
        "",
    ] {
        assert_eq!(
            dependency_packages(Some(os_release)),
            None,
            "{os_release:?}"
        );
    }
    assert_eq!(dependency_packages(None), None);
}

#[test]
fn apt_runs_non_interactively_through_env() {
    assert_eq!(
        apt_update_argv(),
        strings(&["DEBIAN_FRONTEND=noninteractive", "apt-get", "update"])
    );
    assert_eq!(
        apt_install_argv(&strings(&["gdb", "curl"])),
        strings(&[
            "DEBIAN_FRONTEND=noninteractive",
            "apt-get",
            "install",
            "-y",
            "gdb",
            "curl"
        ])
    );
}

#[test]
fn post_install_commands_are_the_wikis_path_for_path() {
    assert_eq!(
        steam_client_update_argv(),
        strings(&["+login", "anonymous", "+app_update", "1007", "+quit"])
    );
    assert_eq!(
        steam_client_source("/home/steam/Steam/steamcmd.sh"),
        "/home/steam/Steam/steamapps/common/Steamworks SDK Redist/linux64/steamclient.so"
    );
    assert_eq!(
        steam_client_sdk_directory("/home/steam/"),
        "/home/steam/.steam/sdk64"
    );
    assert_eq!(
        steam_client_install_path("/home/steam/pavlovserver1"),
        "/home/steam/pavlovserver1/Pavlov/Binaries/Linux/steamclient.so"
    );
    assert_eq!(
        libcxx_remove_argv(),
        strings(&["-f", "/usr/lib/x86_64-linux-gnu/libc++.so"])
    );
    assert_eq!(
        libcxx_link_argv(),
        strings(&[
            "-s",
            "/usr/lib/x86_64-linux-gnu/libc++.so.1",
            "/usr/lib/x86_64-linux-gnu/libc++.so"
        ])
    );
    assert_eq!(
        launch_script_argv("/home/steam/pavlovserver"),
        strings(&["+x", "/home/steam/pavlovserver/PavlovServer.sh"])
    );
}

#[test]
fn only_an_empty_folder_or_one_holding_just_saved_data_may_be_provisioned_into() {
    let root = tempfile::tempdir().unwrap();
    assert!(holds_only_saved_data(root.path()).unwrap());

    fs::create_dir_all(root.path().join("Pavlov/Saved/Config")).unwrap();
    assert!(holds_only_saved_data(root.path()).unwrap());

    fs::create_dir_all(root.path().join("Pavlov/Binaries")).unwrap();
    assert!(!holds_only_saved_data(root.path()).unwrap());

    let other = tempfile::tempdir().unwrap();
    fs::create_dir_all(other.path().join("Pavlov/Saved")).unwrap();
    fs::write(other.path().join("PavlovServer.sh"), "x").unwrap();
    assert!(!holds_only_saved_data(other.path()).unwrap());
}

#[test]
fn deleting_also_looks_for_and_removes_a_legacy_docker_container_by_the_unit_name() {
    assert_eq!(
        docker_exists_argv("pavlovserver2"),
        strings(&[
            "container",
            "inspect",
            "--format",
            "{{.Id}}",
            "pavlovserver2"
        ])
    );
    assert_eq!(
        docker_remove_argv("pavlovserver2"),
        strings(&["rm", "-f", "pavlovserver2"])
    );
    // A "docker:" entry in PAVLOV_UNITS names the same container once the prefix is dropped.
    assert_eq!(
        without_legacy_docker_prefix("docker:pavlovserver2"),
        "pavlovserver2"
    );
}

// ---- the checklist itself: real step list, real cursor ----

#[test]
fn the_real_step_list_removes_the_legacy_sudo_grant_right_after_the_account() {
    // steam is never GRANTED sudo - the step after the account only removes an old grant.
    assert_eq!(
        STEP_NAMES,
        [
            "Pre-flight checks",
            "Operating system and dependencies",
            "Steam user account",
            "Remove legacy steam sudo grant",
            "SteamCMD (locate or bootstrap)",
            "steamclient.so (SteamCMD app 1007)",
            "SteamCMD install",
            "libc++ and the launch script",
            "Server config (RconSettings.txt, Game.ini)",
            "Restore kept player data",
            "systemd unit (write, daemon-reload, enable --now)",
            "Firewall (ufw)",
            "RCON reachability",
            "Wire into the bot (.env)",
            "Engine.ini (net.MaxPlayersOverride)",
            "Restart the bot",
        ]
    );
}

const SOURCE: &str = include_str!("../src/provisioner.rs");

/// The body of one method of `provisioner.rs`, from its signature to the next marker.
fn method_source(signature: &str, next: &str) -> &'static str {
    let start = SOURCE
        .find(signature)
        .unwrap_or_else(|| panic!("could not find {signature}"));
    let end = SOURCE[start + signature.len()..]
        .find(next)
        .map(|i| start + signature.len() + i)
        .unwrap_or_else(|| panic!("could not find the end of {signature}"));
    &SOURCE[start..end]
}

fn starts(body: &str) -> usize {
    body.matches("run.start(").count()
}

#[test]
fn every_step_the_code_starts_is_on_the_checklist() {
    // THE SHIPPED BUG (C#): a conditional start with no checklist entry shifted every later step
    // and indexed past the end. provision starts one copy-only and two SteamCMD-only steps on
    // its two branches, so it holds one more start than either checklist minus the restart.
    let provision = starts(method_source(
        "pub async fn provision(",
        "pub async fn delete(",
    ));
    let delete = starts(method_source(
        "pub async fn delete(",
        "async fn remove_legacy_container(",
    ));
    let restart = starts(method_source("async fn restart(", "\n    }\n"));

    assert_eq!(restart, 1);
    assert_eq!(STEP_NAMES.len(), provision + restart - 1);
    assert_eq!(COPY_STEP_NAMES.len(), provision + restart - 2);
    assert_eq!(DELETE_STEP_NAMES.len(), delete + restart);
}

fn new_run() -> (Run, Arc<Mutex<Vec<Vec<ProvisionStep>>>>) {
    let (progress, seen) = recorder();
    (Run::new(&["one", "two", "three"], progress), seen)
}

#[tokio::test]
async fn start_advances_to_the_next_step_without_an_index() {
    let (mut run, snapshots) = new_run();

    run.start("first").await;
    run.ok("first done").await;
    run.start("second").await;
    run.fail("second failed").await;

    let last = snapshots.lock().unwrap().last().cloned().unwrap();
    assert_eq!(last[0].status, ProvisionStatus::Ok);
    assert_eq!(last[0].detail, "first done");
    assert_eq!(last[1].status, ProvisionStatus::Failed);
    assert_eq!(last[1].detail, "second failed");
    assert_eq!(last[2].status, ProvisionStatus::Pending);
}

#[tokio::test]
async fn abort_skips_everything_after_the_current_step_and_leaves_the_failure_alone() {
    let (mut run, _) = new_run();

    run.start("first").await;
    run.ok("fine").await;
    run.start("second").await;
    run.fail("boom").await;
    let outcome = run.abort().await;

    assert_eq!(outcome.steps[0].status, ProvisionStatus::Ok);
    assert_eq!(outcome.steps[1].status, ProvisionStatus::Failed); // NOT overwritten to Skipped
    assert_eq!(outcome.steps[2].status, ProvisionStatus::Skipped);
    assert!(!outcome.ok());
    assert!(!outcome.restart_queued);
}

#[tokio::test]
async fn every_change_emits_a_snapshot_of_all_steps() {
    let (mut run, snapshots) = new_run();

    run.start("go").await;

    let snapshots = snapshots.lock().unwrap();
    assert_eq!(snapshots.len(), 1);
    assert_eq!(snapshots[0].len(), 3);
    assert_eq!(snapshots[0][0].status, ProvisionStatus::Running);
}

#[tokio::test]
async fn a_start_past_the_end_of_the_checklist_is_reported_not_a_panic() {
    // Rust-only: the C# shipped "Index was outside the bounds of the array" here.
    let (mut run, _) = new_run();
    for _ in 0..4 {
        run.start("step").await;
        run.ok("done").await;
    }
    assert!(run.finish(false).ok());
}

// ---- whole runs against a recording runner (Rust-only) ----

struct Machine {
    root: tempfile::TempDir,
    runner: Arc<FakeRunner>,
}

impl Machine {
    fn new() -> Self {
        let root = tempfile::tempdir().unwrap();
        for dir in ["systemd", "sudoers.d", "libcxx", "home/steam/Steam"] {
            fs::create_dir_all(root.path().join(dir)).unwrap();
        }
        fs::write(root.path().join("libcxx/libc++.so.1"), "").unwrap();
        fs::write(
            root.path().join("os-release"),
            "ID=ubuntu\nVERSION_ID=\"24.04\"\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"\n",
        )
        .unwrap();
        let runner = Arc::new(FakeRunner::default());
        let home = root.path().join("home/steam");
        runner.answer(
            "getent",
            &["passwd", "steam"],
            exited(
                0,
                &format!("steam:x:1001:1001::{}:/bin/bash\n", home.display()),
                "",
            ),
        );
        Self { root, runner }
    }

    fn path(&self, rel: &str) -> PathBuf {
        self.root.path().join(rel)
    }

    fn text(&self, rel: &str) -> String {
        self.path(rel).to_string_lossy().into_owned()
    }

    fn provisioner(&self, privileged: bool) -> ServerProvisioner {
        ServerProvisioner::with(
            Arc::clone(&self.runner) as _,
            ProvisionerPaths {
                unit_directory: self.path("systemd"),
                legacy_sudoers_drop_in: self.path("sudoers.d/pavlov-steam-full"),
                os_release: self.path("os-release"),
                libcxx_directory: self.path("libcxx"),
            },
            privileged,
            BotSupervisor::unmanaged(),
        )
    }

    fn calls(&self) -> Vec<Vec<String>> {
        self.runner
            .calls()
            .into_iter()
            .map(|(file, argv)| std::iter::once(file).chain(argv).collect())
            .collect()
    }
}

fn line(parts: &[&str]) -> Vec<String> {
    strings(parts)
}

#[tokio::test]
async fn a_whole_download_provision_runs_the_wikis_commands_in_order() {
    let machine = Machine::new();
    let rcon = FakeRconServer::with_password("Ab1xyz").await.unwrap();
    let install = machine.text("home/steam/pavlovserver2");
    // Old saved data, which is the one thing a provision may build over (and hands to steam).
    fs::create_dir_all(machine.path("home/steam/pavlovserver2/Pavlov/Saved/Config/LinuxServer"))
        .unwrap();
    fs::write(
        machine.path("home/steam/pavlovserver2/Pavlov/Saved/Config/LinuxServer/Engine.ini"),
        "",
    )
    .unwrap();
    fs::write(machine.path("home/steam/Steam/steamcmd.sh"), "").unwrap();
    let client = machine.path("home/steam/Steam/steamapps/common/Steamworks SDK Redist/linux64");
    fs::create_dir_all(&client).unwrap();
    fs::write(client.join("steamclient.so"), "").unwrap();
    fs::write(
        machine.path("sudoers.d/pavlov-steam-full"),
        "steam ALL=(ALL) NOPASSWD:ALL\n",
    )
    .unwrap();
    fs::write(machine.path(".env"), "DISCORD_TOKEN=x\n").unwrap();
    machine
        .runner
        .answer("id", &["steam"], exited(1, "", "id: 'steam': no such user"));

    let spec = spec(
        3,
        "pavlovserver2",
        &install,
        7779,
        i32::from(rcon.port()),
        "Ab1xyz",
    );
    let request = ProvisionRequest::new(
        spec,
        strings(&["pavlovserver", "pavlovserver1", "pavlovserver2"]),
        strings(&["/a", "/b", &install]),
        None,
        machine.path(".env"),
    );
    let (progress, seen) = recorder();

    let outcome = machine
        .provisioner(true)
        .provision(&request, progress)
        .await;

    let failed: Vec<_> = outcome
        .steps
        .iter()
        .filter(|s| s.status != ProvisionStatus::Ok)
        .collect();
    assert!(failed.is_empty(), "{failed:#?}");
    assert!(outcome.ok());
    assert!(outcome.changed_env());
    assert!(!outcome.restart_queued); // unmanaged: "restart the bot yourself"
    assert_eq!(outcome.steps.len(), STEP_NAMES.len());
    assert!(seen.lock().unwrap().len() > STEP_NAMES.len());

    let steamcmd = machine.text("home/steam/Steam/steamcmd.sh");
    let sdk = machine.text("home/steam/.steam/sdk64");
    let source = client.join("steamclient.so").to_string_lossy().into_owned();
    let config = format!("{install}/Pavlov/Saved/Config");
    let libcxx = machine.text("libcxx");
    let port = rcon.port().to_string();
    let expected: Vec<Vec<String>> = vec![
        line(&["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "update"]),
        line(&[
            "env",
            "DEBIAN_FRONTEND=noninteractive",
            "apt-get",
            "install",
            "-y",
            "gdb",
            "curl",
            "lib32gcc-s1",
            "libc++-dev",
            "unzip",
        ]),
        line(&["id", "steam"]),
        line(&["useradd", "-m", "-s", "/bin/bash", "steam"]),
        line(&["chown", "-R", "steam:steam", &install]),
        line(&["getent", "passwd", "steam"]),
        line(&["getent", "passwd", "steam"]),
        line(&[
            "sudo",
            "-u",
            "steam",
            &steamcmd,
            "+login",
            "anonymous",
            "+app_update",
            "1007",
            "+quit",
        ]),
        line(&["sudo", "-u", "steam", "mkdir", "-p", &sdk]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "mkdir",
            "-p",
            &format!("{install}/Pavlov/Binaries/Linux"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "cp",
            &source,
            &format!("{sdk}/steamclient.so"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "cp",
            &source,
            &format!("{install}/Pavlov/Binaries/Linux/steamclient.so"),
        ]),
        line(&["sudo", "-u", "steam", "mkdir", "-p", &install]),
        line(&[
            "sudo",
            "-u",
            "steam",
            &steamcmd,
            "+force_install_dir",
            &install,
            "+login",
            "anonymous",
            "+app_update",
            "622970",
            "-beta",
            "shack",
            "+exit",
        ]),
        line(&["rm", "-f", &format!("{libcxx}/libc++.so")]),
        line(&[
            "ln",
            "-s",
            &format!("{libcxx}/libc++.so.1"),
            &format!("{libcxx}/libc++.so"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "chmod",
            "+x",
            &format!("{install}/PavlovServer.sh"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "mkdir",
            "-p",
            &format!("{install}/Pavlov/Saved/Logs"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "mkdir",
            "-p",
            &format!("{install}/Pavlov/Saved/Config/LinuxServer"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "mkdir",
            "-p",
            &format!("{install}/Pavlov/Saved/maps"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "touch",
            &format!("{config}/mods.txt"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "touch",
            &format!("{config}/blacklist.txt"),
        ]),
        line(&[
            "sudo",
            "-u",
            "steam",
            "touch",
            &format!("{config}/whitelist.txt"),
        ]),
        line(&["chown", "steam", &format!("{config}/RconSettings.txt")]),
        line(&["chown", "steam", &format!("{config}/LinuxServer/Game.ini")]),
        line(&["systemctl", "daemon-reload"]),
        line(&["systemctl", "enable", "--now", "pavlovserver2"]),
        line(&["ufw", "allow", "7779"]),
        line(&["ufw", "allow", "8179"]),
        line(&["ufw", "allow", &port]),
        line(&[
            "chown",
            "steam",
            &format!("{config}/LinuxServer/Engine.ini"),
        ]),
        line(&["systemctl", "restart", "pavlovserver2"]),
    ];
    assert_eq!(machine.calls(), expected);

    // The files it writes itself.
    assert!(
        !machine.path("sudoers.d/pavlov-steam-full").exists(),
        "the legacy grant is removed"
    );
    let unit = fs::read_to_string(machine.path("systemd/pavlovserver2.service")).unwrap();
    assert!(
        unit.contains(&format!("WorkingDirectory={install}\n")),
        "{unit}"
    );
    let rcon_settings = fs::read_to_string(format!("{config}/RconSettings.txt")).unwrap();
    assert!(rcon_settings.contains("Password=Ab1xyz"), "{rcon_settings}");
    assert!(
        rcon_settings.contains(&format!("Port={port}")),
        "{rcon_settings}"
    );
    assert!(
        fs::read_to_string(format!("{config}/LinuxServer/Game.ini"))
            .unwrap()
            .contains("datacenter")
    );
    assert!(
        fs::read_to_string(format!("{config}/LinuxServer/Engine.ini"))
            .unwrap()
            .contains("net.MaxPlayersOverride=32")
    );
    let env = fs::read_to_string(machine.path(".env")).unwrap();
    assert!(env.contains("DISCORD_TOKEN=x"), "{env}");
    assert!(env.contains(&format!("RCON_PORT_3={port}")), "{env}");
    // And it asked the new server, over RCON, whether it is up.
    assert!(
        rcon.commands().iter().any(|c| c == "ServerInfo"),
        "{:?}",
        rcon.commands()
    );
}

#[tokio::test]
async fn an_unprivileged_bot_refuses_before_running_anything() {
    let machine = Machine::new();
    let request = ProvisionRequest::new(
        default_spec(),
        Vec::new(),
        Vec::new(),
        None,
        machine.path(".env"),
    );

    let outcome = machine
        .provisioner(false)
        .provision(&request, ignore())
        .await;

    assert_eq!(outcome.steps[0].status, ProvisionStatus::Failed);
    assert!(
        outcome.steps[0].detail.contains("not running as root"),
        "{}",
        outcome.steps[0].detail
    );
    assert!(
        outcome.steps[1..]
            .iter()
            .all(|s| s.status == ProvisionStatus::Skipped)
    );
    assert!(machine.calls().is_empty());
    assert!(!machine.path(".env").exists());
}

#[tokio::test]
async fn an_install_folder_holding_more_than_saved_data_is_never_built_over() {
    let machine = Machine::new();
    let install = machine.text("home/steam/pavlovserver2");
    fs::create_dir_all(machine.path("home/steam/pavlovserver2/Pavlov/Binaries")).unwrap();
    let spec = spec(3, "pavlovserver2", &install, 7779, 9300, "pw");
    let request = ProvisionRequest::new(spec, Vec::new(), Vec::new(), None, machine.path(".env"));

    let outcome = machine
        .provisioner(true)
        .provision(&request, ignore())
        .await;

    assert!(
        outcome.steps[0]
            .detail
            .contains("refusing to install over it"),
        "{}",
        outcome.steps[0].detail
    );
    assert!(machine.calls().is_empty());
}

#[tokio::test]
async fn an_existing_unit_is_refused_unless_the_slot_is_being_rebuilt() {
    let machine = Machine::new();
    fs::write(machine.path("systemd/pavlovserver2.service"), "[Unit]\n").unwrap();
    let mut request = ProvisionRequest::new(
        default_spec(),
        Vec::new(),
        Vec::new(),
        None,
        machine.path(".env"),
    );

    let refused = machine
        .provisioner(true)
        .provision(&request, ignore())
        .await;
    assert!(
        refused.steps[0]
            .detail
            .contains("refusing to overwrite an existing unit"),
        "{}",
        refused.steps[0].detail
    );

    request.rebuilding_existing_slot = true;
    machine.runner.answer(
        "env",
        &[],
        ProcessOutcome::not_started("No such file or directory"),
    );
    let rebuilt = machine
        .provisioner(true)
        .provision(&request, ignore())
        .await;
    // Past the pre-flight check this time (and then stopped by the fake's missing apt-get).
    assert_eq!(rebuilt.steps[0].status, ProvisionStatus::Ok);
    assert!(
        rebuilt.steps[1].detail.contains("could not run apt-get"),
        "{}",
        rebuilt.steps[1].detail
    );
}

#[tokio::test]
async fn a_failed_steamcmd_reports_what_steamcmd_said_and_stops() {
    let machine = Machine::new();
    let install = machine.text("home/steam/pavlovserver2");
    fs::write(machine.path("home/steam/Steam/steamcmd.sh"), "").unwrap();
    let client = machine.path("home/steam/Steam/steamapps/common/Steamworks SDK Redist/linux64");
    fs::create_dir_all(&client).unwrap();
    fs::write(client.join("steamclient.so"), "").unwrap();
    let steamcmd = machine.text("home/steam/Steam/steamcmd.sh");
    machine.runner.answer(
        "sudo",
        &["-u", "steam", &steamcmd, "+force_install_dir"],
        exited(
            8,
            "",
            "Error! App '622970' state is 0x202 after update job.",
        ),
    );
    let request = ProvisionRequest::new(
        spec(3, "pavlovserver2", &install, 7779, 9300, "pw"),
        Vec::new(),
        Vec::new(),
        None,
        machine.path(".env"),
    );

    let outcome = machine
        .provisioner(true)
        .provision(&request, ignore())
        .await;

    let install_step = outcome
        .steps
        .iter()
        .find(|s| s.name == "SteamCMD install")
        .unwrap();
    assert_eq!(install_step.status, ProvisionStatus::Failed);
    assert!(
        install_step
            .detail
            .contains("SteamCMD exited 8: Error! App '622970' state is 0x202"),
        "{}",
        install_step.detail
    );
    let after = outcome
        .steps
        .iter()
        .skip_while(|s| s.name != "SteamCMD install")
        .skip(1);
    assert!(
        after
            .into_iter()
            .all(|s| s.status == ProvisionStatus::Skipped)
    );
    assert!(!machine.path("systemd/pavlovserver2.service").exists());
    assert!(!machine.path(".env").exists());
}

#[tokio::test]
async fn copying_an_install_skips_the_download_and_still_fetches_steamclient() {
    let machine = Machine::new();
    let source = machine.text("home/steam/pavlovserver");
    fs::create_dir_all(&source).unwrap();
    let install = machine.text("home/steam/pavlovserver1");
    fs::write(machine.path("home/steam/Steam/steamcmd.sh"), "").unwrap();
    let mut request = ProvisionRequest::new(
        spec(2, "pavlovserver1", &install, 7778, 9200, "pw"),
        Vec::new(),
        Vec::new(),
        None,
        machine.path(".env"),
    );
    request.copy_from_install_dir = Some(source.clone());

    let outcome = machine
        .provisioner(true)
        .provision(&request, ignore())
        .await;

    assert_eq!(outcome.steps.len(), COPY_STEP_NAMES.len());
    assert_eq!(outcome.steps[4].name, "Copy an existing install");
    assert_eq!(
        outcome.steps[4].status,
        ProvisionStatus::Ok,
        "{}",
        outcome.steps[4].detail
    );
    let calls = machine.calls();
    assert!(calls.contains(&line(&[
        "sudo",
        "-u",
        "steam",
        "cp",
        "-r",
        &format!("{source}/."),
        &install
    ])));
    assert!(
        !calls
            .iter()
            .any(|c| c.contains(&"+force_install_dir".to_owned())),
        "a copy never downloads"
    );
    // steamclient.so: SteamCMD is located for it, then asked for app 1007 - which the fake
    // "ran" without producing the file, so the step fails on the missing file, by name.
    let client = &outcome.steps[5];
    assert_eq!(client.name, "steamclient.so (SteamCMD app 1007)");
    assert_eq!(client.status, ProvisionStatus::Failed);
    assert!(
        client.detail.contains("steamclient.so is not there"),
        "{}",
        client.detail
    );
}

#[tokio::test]
async fn a_whole_delete_keeps_the_player_data_and_unwires_the_slot() {
    let machine = Machine::new();
    let install = machine.text("home/steam/pavlovserver2");
    let roster = machine
        .path("home/steam/pavlovserver2/Pavlov/Saved/Config/ModSave/FactionRoles/officer.txt");
    fs::create_dir_all(roster.parent().unwrap()).unwrap();
    fs::write(&roster, "PlayerOne\n").unwrap();
    fs::write(machine.path("systemd/pavlovserver2.service"), "[Unit]\n").unwrap();
    fs::write(
        machine.path(".env"),
        "RCON_HOST_1=127.0.0.1\nRCON_HOST_3=127.0.0.1\nRCON_PORT_3=9300\nRCON_PASSWORD_3=hunter2\nPAVLOV_UNITS=a,b,pavlovserver2\n",
    )
    .unwrap();
    machine.runner.answer(
        "docker",
        &[],
        ProcessOutcome::not_started("No such file or directory"),
    );
    let request = DeleteRequest {
        slot: 3,
        unit_name: "pavlovserver2".into(),
        install_dir: install.clone(),
        final_pavlov_units: strings(&["a", "b"]),
        final_pavlov_bases: strings(&["/a", "/b"]),
        env_path: machine.path(".env"),
    };

    let outcome = machine.provisioner(true).delete(&request, ignore()).await;

    assert!(outcome.ok(), "{:#?}", outcome.steps);
    assert!(outcome.changed_env());
    assert_eq!(
        machine.calls(),
        vec![
            line(&["systemctl", "disable", "--now", "pavlovserver2"]),
            line(&[
                "docker",
                "container",
                "inspect",
                "--format",
                "{{.Id}}",
                "pavlovserver2"
            ]),
            line(&["systemctl", "daemon-reload"]),
            line(&["rm", "-rf", &install]),
        ]
    );
    assert!(!machine.path("systemd/pavlovserver2.service").exists());
    let kept =
        machine.path("home/steam/.pavlov-preserved/pavlovserver2/ModSave/FactionRoles/officer.txt");
    assert_eq!(fs::read_to_string(kept).unwrap(), "PlayerOne\n");
    let env = fs::read_to_string(machine.path(".env")).unwrap();
    assert!(!env.contains("RCON_PORT_3"), "{env}");
    assert!(!env.contains("hunter2"), "{env}");
    assert!(env.contains("RCON_HOST_1=127.0.0.1"), "{env}");
}

#[tokio::test]
async fn a_legacy_docker_container_is_removed_on_delete() {
    let machine = Machine::new();
    machine.runner.answer(
        "docker",
        &["container", "inspect"],
        exited(0, "abc123\n", ""),
    );
    let request = DeleteRequest {
        slot: 1,
        unit_name: "pavlovserver".into(),
        install_dir: machine.text("home/steam/pavlovserver"),
        final_pavlov_units: Vec::new(),
        final_pavlov_bases: Vec::new(),
        env_path: machine.path(".env"),
    };

    let outcome = machine.provisioner(true).delete(&request, ignore()).await;

    assert!(
        machine
            .calls()
            .contains(&line(&["docker", "rm", "-f", "pavlovserver"]))
    );
    assert!(
        outcome.steps[1]
            .detail
            .contains("Docker container pavlovserver stopped and removed too"),
        "{}",
        outcome.steps[1].detail
    );
}

#[tokio::test]
async fn a_delete_refuses_a_path_that_is_not_one_servers_install() {
    let machine = Machine::new();
    let request = DeleteRequest {
        slot: 1,
        unit_name: "pavlovserver".into(),
        install_dir: "/home".into(),
        final_pavlov_units: Vec::new(),
        final_pavlov_bases: Vec::new(),
        env_path: machine.path(".env"),
    };

    let outcome = machine.provisioner(true).delete(&request, ignore()).await;

    let step = outcome
        .steps
        .iter()
        .find(|s| s.name == "Delete the install directory")
        .unwrap();
    assert_eq!(step.status, ProvisionStatus::Failed);
    assert!(
        !machine.calls().iter().any(|c| c[0] == "rm"),
        "rm -rf must never run"
    );
}
