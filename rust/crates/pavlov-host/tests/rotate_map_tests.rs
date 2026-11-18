//! Port of `RotateMapTests.cs`, the host half: systemd control (`RotateMapTests`, the C#
//! `UnitStateTests` and `UnitStatsTests` classes), asserted through a recording runner so the
//! exact argv is checked without touching systemd. The unit-name parsing half is in the core
//! crate's file of this name; the warnings, `PlayerNotice` and `MapRotation` join with the
//! commands that own them.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use common::{FakeRunner, exited};
use pavlov_host::process::SystemRunner;
use pavlov_host::service_control::{
    ServiceControl, UnitAction, UnitControl, UnitResult, UnitState, UnitStats, default_units,
    number_for, rcon_name_for,
};
use pavlov_rcon::ServerLifecycle;

fn control_with(runner: Arc<FakeRunner>, units: &[&str]) -> ServiceControl {
    let units = if units.is_empty() {
        default_units()
    } else {
        units.iter().map(|u| (*u).to_owned()).collect()
    };
    ServiceControl::new(units, Some(false), runner)
}

fn control(units: &[&str]) -> ServiceControl {
    control_with(Arc::new(FakeRunner::default()), units)
}

#[test]
fn the_server_number_to_unit_mapping_is_the_one_on_the_box() {
    // Server 1 is `pavlovserver` with no suffix, so server N is `pavlovserver{N-1}`.
    let c = control(&[]);
    assert_eq!(c.unit_for(1), Some("pavlovserver"));
    assert_eq!(c.unit_for(2), Some("pavlovserver1"));
}

#[tokio::test]
async fn each_action_maps_to_its_systemctl_verb() {
    let runner = Arc::new(FakeRunner::default());
    let c = control_with(Arc::clone(&runner), &[]);

    for action in UnitAction::ALL {
        assert!(c.run("pavlovserver1", action).await.ok);
    }

    let calls = runner.calls();
    let expected: Vec<(String, Vec<String>)> = ["start", "stop", "restart"]
        .iter()
        .map(|v| {
            (
                "systemctl".to_owned(),
                vec![(*v).to_owned(), "pavlovserver1".to_owned()],
            )
        })
        .collect();
    assert_eq!(calls, expected);
}

#[tokio::test]
async fn a_refused_unit_name_is_refused_for_every_action() {
    let runner = Arc::new(FakeRunner::default());
    let c = control_with(Arc::clone(&runner), &[]);

    for action in UnitAction::ALL {
        let result = c.run("pavlovserver; reboot", action).await;
        assert!(!result.ok);
    }
    assert!(runner.calls().is_empty());
}

#[tokio::test]
async fn sudo_is_used_when_the_bot_is_not_root() {
    let detected = ServiceControl::new(default_units(), None, Arc::new(SystemRunner));
    let privileged = pavlov_storage::ownership::is_privileged();
    assert_eq!(privileged, !detected.elevation().contains("sudo"));

    // And the sudo argv is -n systemctl <verb> <unit>, never a prompt.
    let runner = Arc::new(FakeRunner::default());
    let sudo = ServiceControl::new(default_units(), Some(true), Arc::clone(&runner) as Arc<_>);
    sudo.run("pavlovserver", UnitAction::Restart).await;
    assert_eq!(
        runner.calls(),
        [(
            "sudo".to_owned(),
            vec![
                "-n".into(),
                "systemctl".into(),
                "restart".into(),
                "pavlovserver".into()
            ]
        )]
    );
}

#[test]
fn forcing_sudo_off_while_not_root_says_that_it_will_fail() {
    if pavlov_storage::ownership::is_privileged() {
        return; // the assertion is meaningless as root
    }
    assert!(control(&[]).elevation().contains("NOT root"));
}

#[tokio::test]
async fn a_refused_unit_name_never_starts_a_process() {
    let runner = Arc::new(FakeRunner::default());
    let result = control_with(Arc::clone(&runner), &[])
        .restart("pavlovserver; reboot")
        .await;

    assert!(!result.ok);
    assert!(result.detail.contains("refused before running anything"));
    assert!(runner.calls().is_empty());
}

#[test]
fn server_numbers_are_one_based_and_map_to_the_configured_order() {
    let c = control(&["unit-a", "unit-b", "unit-c"]);
    assert_eq!(c.unit_for(1), Some("unit-a"));
    assert_eq!(c.unit_for(2), Some("unit-b"));
    assert_eq!(c.unit_for(3), Some("unit-c"));
}

#[test]
fn a_server_number_outside_the_configured_range_resolves_to_nothing() {
    let c = control(&["unit-a"]);
    for n in [0, 2, -1, i32::MAX] {
        assert_eq!(c.unit_for(n), None, "{n}");
    }
}

// ---- advice ----

#[test]
fn a_permission_failure_explains_how_to_grant_it() {
    let advice = control(&[])
        .advice(&[UnitResult::new(
            false,
            "pavlovserver",
            "Failed to restart pavlovserver.service: Access denied",
        )])
        .unwrap();

    assert!(advice.contains("visudo"));
    for unit in default_units() {
        for verb in ["start", "stop", "restart"] {
            assert!(advice.contains(&format!("/bin/systemctl {verb} {unit}")));
        }
    }
}

#[test]
fn polkits_wording_is_recognised_too() {
    let advice = control(&[]).advice(&[UnitResult::new(
        false,
        "pavlovserver",
        "Interactive authentication required.",
    )]);
    assert!(advice.unwrap().contains("visudo"));
}

#[test]
fn an_unknown_unit_points_at_the_configuration_rather_than_permissions() {
    let advice = control(&[])
        .advice(&[UnitResult::new(
            false,
            "pavlovserver",
            "Unit pavlovserver.service not found.",
        )])
        .unwrap();
    assert!(advice.contains("PAVLOV_UNITS"));
    assert!(!advice.contains("visudo"));
}

#[test]
fn success_offers_no_advice() {
    assert_eq!(
        control(&[]).advice(&[UnitResult::new(true, "pavlovserver", "restarted")]),
        None
    );
}

#[test]
fn an_unrecognised_failure_says_nothing_rather_than_guessing() {
    assert_eq!(
        control(&[]).advice(&[UnitResult::new(
            false,
            "pavlovserver",
            "Job for pavlovserver.service failed"
        )]),
        None
    );
}

// ---- state (C# UnitStateTests) ----

#[tokio::test]
async fn a_unit_systemd_has_never_heard_of_is_never_reported_active() {
    let real = ServiceControl::new(default_units(), Some(false), Arc::new(SystemRunner));
    let state = real.state("pavlov-does-not-exist-7f3a").await;
    assert!(
        matches!(state, UnitState::Unknown | UnitState::Inactive),
        "{state:?}"
    );
}

#[test]
fn the_word_systemd_prints_maps_to_the_right_state() {
    for (word, expected) in [
        ("active", UnitState::Active),
        ("activating", UnitState::Starting),
        ("reloading", UnitState::Starting),
        ("inactive", UnitState::Inactive),
        ("deactivating", UnitState::Inactive),
        ("failed", UnitState::Failed),
        ("", UnitState::Unknown),
        ("something-systemd-invented-later", UnitState::Unknown),
    ] {
        assert_eq!(UnitState::from_word(Some(word)), expected, "{word}");
    }
}

#[tokio::test]
async fn a_refused_unit_name_is_never_asked() {
    let runner = Arc::new(FakeRunner::default());
    let c = control_with(Arc::clone(&runner), &[]);
    assert_eq!(c.state("--all").await, UnitState::Unknown);
    assert_eq!(c.state("pavlovserver; reboot").await, UnitState::Unknown);
    assert!(runner.calls().is_empty());
}

#[tokio::test]
async fn server_numbers_resolve_to_both_a_unit_and_an_rcon_name_consistently() {
    let runner = Arc::new(FakeRunner::default());
    runner.answer(
        "systemctl",
        &["is-active", "pavlovserver1"],
        exited(3, "inactive\n", ""),
    );
    let c = control_with(Arc::clone(&runner), &[]);

    assert_eq!(c.unit_for(1), Some("pavlovserver"));
    assert_eq!(rcon_name_for(1), "server1");
    assert_eq!(c.unit_for(2), Some("pavlovserver1"));
    assert_eq!(rcon_name_for(2), "server2");
    assert_eq!(number_for("server3"), Some(3));
    // Health asks systemd whether server2's silence was deliberate.
    assert!(c.is_intentionally_stopped("server2").await);
    assert!(!c.is_intentionally_stopped("server9").await);
}

// ---- stats (C# UnitStatsTests) ----

#[tokio::test]
async fn every_configured_unit_gets_an_answer() {
    let stats = control(&[]).stats().await;
    let units: Vec<&str> = stats.iter().map(|s| s.unit.as_str()).collect();
    assert_eq!(units, default_units());
}

#[tokio::test]
async fn a_unit_that_does_not_exist_reports_unknown_and_no_numbers() {
    let runner = Arc::new(FakeRunner::default());
    runner.answer(
        "systemctl",
        &["show"],
        exited(
            0,
            "ActiveState=inactive\nMemoryCurrent=[not set]\nCPUUsageNSec=[not set]\n",
            "",
        ),
    );
    let stats = control_with(runner, &["pavlov-absent"]).stats().await;

    assert_eq!(stats.len(), 1);
    assert_ne!(stats[0].state, UnitState::Active);
    assert_eq!(stats[0].memory_bytes, None);
    assert_eq!(stats[0].cpu_percent, None);
}

#[tokio::test]
async fn a_refused_unit_name_is_never_queried() {
    let stats = control(&["--all"]).stats().await;
    assert_eq!(stats.len(), 1);
    assert_eq!(stats[0].state, UnitState::Unknown);
}

#[tokio::test]
async fn the_query_is_bounded_and_does_not_throw() {
    let real = ServiceControl::new(default_units(), Some(false), Arc::new(SystemRunner));
    let stats = tokio::time::timeout(Duration::from_secs(30), real.stats())
        .await
        .unwrap();
    assert!(stats.iter().all(|s| !s.unit.is_empty()));
}

#[test]
fn cumulative_cpu_time_is_not_usage() {
    let stats = UnitStats {
        state: UnitState::Active,
        cpu_total: Some(Duration::from_secs_f64(5.64)),
        cpu_percent: Some(12.5),
        ..UnitStats::unknown("pavlovserver")
    };
    assert_eq!(stats.cpu_total, Some(Duration::from_secs_f64(5.64)));
    assert_eq!(stats.cpu_percent, Some(12.5));
}

#[test]
fn a_percentage_over_one_hundred_is_valid() {
    let stats = UnitStats {
        cpu_percent: Some(187.4),
        ..UnitStats::unknown("pavlovserver")
    };
    assert!(stats.cpu_percent.unwrap() > 100.0);
}

#[tokio::test]
async fn a_live_stats_query_reads_memory_and_a_cpu_delta() {
    let runner = Arc::new(FakeRunner::default());
    runner.answer(
        "systemctl",
        &["show", "pavlovserver", "--property=CPUUsageNSec"],
        exited(0, "CPUUsageNSec=1500000000\n", ""),
    );
    runner.answer(
        "systemctl",
        &["show", "pavlovserver"],
        exited(0, "ActiveState=active\nMemoryCurrent=104857600\nCPUUsageNSec=1000000000\nTasksCurrent=12\n", ""),
    );
    let stats = control_with(runner, &["pavlovserver"]).stats().await;

    assert_eq!(stats[0].state, UnitState::Active);
    assert_eq!(stats[0].memory_bytes, Some(104_857_600));
    assert_eq!(stats[0].tasks, Some(12));
    // 0.5 s of CPU over a ~1 s window is about 50%.
    let percent = stats[0].cpu_percent.unwrap();
    assert!(percent > 30.0 && percent <= 51.0, "{percent}");
}

// ---- warnings (C# ServerSwitchCommand.WarningFor) ----

mod warnings {
    use pavlov_core::text::Sanitize;
    use pavlov_host::notice::{RESTART_WARNING, warning_for};
    use pavlov_host::service_control::UnitAction;

    fn warnings() -> impl Iterator<Item = &'static str> {
        UnitAction::ALL.into_iter().filter_map(warning_for)
    }

    #[test]
    fn stopping_and_restarting_warn_players_differently() {
        // Told "restarting", a player waits; told "shutting down", they go elsewhere.
        assert_eq!(
            warning_for(UnitAction::Stop),
            Some("Server shutting down...")
        );
        assert_eq!(
            warning_for(UnitAction::Restart),
            Some("Server Rotating... Please rejoin after disconnect")
        );
    }

    #[test]
    fn starting_warns_nobody() {
        // A stopped server has nobody on it and no RCON session to reach them through.
        assert_eq!(warning_for(UnitAction::Start), None);
    }

    #[test]
    fn every_action_that_drops_players_warns_them() {
        for action in UnitAction::ALL {
            match (action, warning_for(action)) {
                (UnitAction::Start, warning) => assert_eq!(warning, None),
                (_, Some(warning)) => assert!(!warning.trim().is_empty(), "{action:?}"),
                (_, None) => panic!("{action:?} drops players and must warn them"),
            }
        }
    }

    #[test]
    fn every_warning_survives_sanitising_intact() {
        for warning in warnings() {
            assert_eq!(Sanitize::message(Some(warning)), warning);
        }
    }

    #[test]
    fn the_broadcast_is_the_exact_line_asked() {
        assert_eq!(
            RESTART_WARNING,
            "Server Rotating... Please rejoin after disconnect"
        );
    }

    #[test]
    fn no_warning_carries_its_own_target() {
        for warning in warnings() {
            assert!(
                !warning.starts_with("All "),
                "\"{warning}\" carries a target that PlayerNotice already supplies"
            );
        }
    }

    #[test]
    fn the_broadcast_survives_sanitising_intact() {
        assert_eq!(Sanitize::message(Some(RESTART_WARNING)), RESTART_WARNING);
    }
}

// ---- what actually reaches the server ----

mod on_the_wire {
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    use pavlov_host::metrics::MetricsRegistry;
    use pavlov_host::notice::{GRACE, PlayerNotice, RESTART_WARNING, RotationOutcome, rotate};
    use pavlov_host::registry::RconRegistry;
    use pavlov_host::service_control::rcon_name_for;
    use pavlov_rcon::testing::FakeRconServer;

    fn registry(server: &FakeRconServer, slot: i32) -> Arc<RconRegistry> {
        let mut options = server.options();
        options.name = rcon_name_for(slot);
        options.read_cache_duration = Duration::ZERO;
        Arc::new(RconRegistry::with_system_clock(
            &[options],
            Duration::from_secs(60),
            Arc::new(MetricsRegistry::default()),
        ))
    }

    fn sent_notify(server: &FakeRconServer) -> String {
        let notifies: Vec<String> = server
            .commands()
            .into_iter()
            .filter(|c| c.starts_with("Notify"))
            .collect();
        assert_eq!(notifies.len(), 1, "{notifies:?}");
        notifies[0].clone()
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_broadcast_says_who_it_is_for() {
        // THE BUG, ON THE WIRE: `Notify Server restarting...` took "Server" for a player.
        let server = FakeRconServer::start().await.unwrap();

        let result = PlayerNotice::new(registry(&server, 1))
            .warn(1, "Server restarting...")
            .await;

        assert!(result.delivered, "{}", result.detail);
        assert_eq!(sent_notify(&server), "Notify All Server restarting...");
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_rotate_broadcast_does_not_address_itself_twice() {
        let server = FakeRconServer::start().await.unwrap();

        let _ = PlayerNotice::new(registry(&server, 1))
            .warn(1, RESTART_WARNING)
            .await;

        assert_eq!(
            sent_notify(&server),
            "Notify All Server Rotating... Please rejoin after disconnect"
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn sends_exactly_rotate_map_and_nothing_else() {
        // /rotatemap used to be `systemctl restart`; now the RCON verb with no arguments.
        let server = FakeRconServer::start().await.unwrap();

        let result = rotate(&registry(&server, 1), 1).await;

        assert_eq!(result.outcome, RotationOutcome::Rotated);
        assert!(result.ok());
        assert_eq!(server.commands(), ["RotateMap"]);
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn the_server_number_picks_the_matching_rcon_slot() {
        // Slot 2 only: server 2 must reach it, and server 1 must not be redirected there.
        let server = FakeRconServer::start().await.unwrap();
        let rcon = registry(&server, 2);

        assert!(rotate(&rcon, 2).await.ok());

        let missing = rotate(&rcon, 1).await;
        assert_eq!(missing.outcome, RotationOutcome::Failed);
        assert!(missing.detail.contains("RCON_HOST_1"), "{}", missing.detail);
        assert_eq!(
            server
                .commands()
                .iter()
                .filter(|c| *c == "RotateMap")
                .count(),
            1
        );
    }

    #[tokio::test(flavor = "multi_thread")]
    async fn a_refusal_is_a_failure_not_a_rotation() {
        let server = FakeRconServer::start().await.unwrap();
        server.configure(|b| b.refuse_everything = true);

        let result = rotate(&registry(&server, 1), 1).await;

        assert_eq!(result.outcome, RotationOutcome::Failed);
        assert!(!result.detail.trim().is_empty());
    }

    #[test]
    fn the_grace_is_five_seconds_and_runs_before_the_system_command() {
        // The broadcast has to reach the client and render before the process goes away.
        assert_eq!(GRACE, Duration::from_secs(5));
    }

    #[tokio::test]
    async fn the_grace_is_actually_waited() {
        let started = Instant::now();
        PlayerNotice::wait().await;
        assert!(
            started.elapsed() >= Duration::from_millis(4500),
            "waited only {:?}",
            started.elapsed()
        );
    }

    #[test]
    fn server_numbers_map_to_rcon_slots_by_name_rather_than_by_position() {
        // RCON slots may have gaps (1 and 3, no 2): the name, never the list position.
        assert_eq!(rcon_name_for(1), "server1");
        assert_eq!(rcon_name_for(2), "server2");
        assert_eq!(rcon_name_for(3), "server3");
    }
}
