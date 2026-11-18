//! Port of `BotSupervisorTests.cs`: `/provisionserver` and `/deleteserver` restart the bot
//! through whatever is supervising it.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_host::supervisor::{BotSupervisor, SupervisorKind, unit_from_cgroup};

const SYSTEMD_CGROUP: &str = "0::/system.slice/pavlov-bot.service\n";

fn env(vars: &'static [(&'static str, &'static str)]) -> impl Fn(&str) -> Option<String> {
    move |key| {
        vars.iter()
            .find(|(k, _)| *k == key)
            .map(|(_, v)| (*v).to_owned())
    }
}

fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| (*s).to_owned()).collect()
}

#[test]
fn a_systemd_service_restarts_its_own_unit_without_blocking() {
    // The live bot: moved off pm2, and /deleteserver then said "restart the bot yourself".
    let supervisor = BotSupervisor::detect(env(&[("INVOCATION_ID", "abc")]), Some(SYSTEMD_CGROUP));

    assert_eq!(
        supervisor,
        BotSupervisor {
            kind: SupervisorKind::Systemd,
            name: "pavlov-bot".into()
        }
    );
    let (file, args) = supervisor.restart_command().unwrap();
    assert_eq!(file, "systemctl");
    assert_eq!(args, strings(&["restart", "--no-block", "pavlov-bot"]));
}

#[test]
fn cgroup_v1_is_read_from_the_systemd_hierarchy() {
    let v1 = "12:cpu,cpuacct:/system.slice/other.service\n1:name=systemd:/system.slice/pavlov-bot.service\n";
    assert_eq!(
        unit_from_cgroup(Some(v1)).as_deref(),
        Some("pavlov-bot.service")
    );
}

#[test]
fn pm2_wins_even_inside_a_systemd_unit() {
    // pm2's daemon runs from pm2-root.service; restarting that would bounce every pm2 app.
    let supervisor = BotSupervisor::detect(
        env(&[
            ("pm_id", "0"),
            ("name", "pavlov-bot-legacy"),
            ("INVOCATION_ID", "abc"),
        ]),
        Some("0::/system.slice/pm2-root.service\n"),
    );

    assert_eq!(
        supervisor,
        BotSupervisor {
            kind: SupervisorKind::Pm2,
            name: "pavlov-bot-legacy".into()
        }
    );
    let (file, args) = supervisor.restart_command().unwrap();
    assert_eq!(file, "pm2");
    assert_eq!(args, strings(&["restart", "pavlov-bot-legacy"]));
}

#[test]
fn a_shell_inside_a_service_cgroup_is_not_the_service() {
    assert_eq!(
        BotSupervisor::detect(env(&[]), Some(SYSTEMD_CGROUP)).kind,
        SupervisorKind::None
    );
}

#[test]
fn a_login_session_is_unmanaged() {
    let supervisor = BotSupervisor::detect(
        env(&[("INVOCATION_ID", "abc")]),
        Some("0::/user.slice/user-0.slice/session-4.scope\n"),
    );

    assert_eq!(supervisor.kind, SupervisorKind::None);
    assert_eq!(supervisor.restart_command(), None);
}

#[test]
fn the_unit_override_is_validated_like_any_unit_name() {
    for (vars, expected) in [
        (
            &[
                ("INVOCATION_ID", "abc"),
                ("PAVLOV_SYSTEMD_UNIT", "pavlov-bot.service"),
            ],
            Some("pavlov-bot"),
        ),
        (
            &[
                ("INVOCATION_ID", "abc"),
                ("PAVLOV_SYSTEMD_UNIT", "-evil.service"),
            ],
            None,
        ),
        (
            &[
                ("INVOCATION_ID", "abc"),
                ("PAVLOV_SYSTEMD_UNIT", "pavlov-bot.timer"),
            ],
            None,
        ),
    ] {
        let supervisor = BotSupervisor::detect(env(vars), None);
        let name = (supervisor.kind == SupervisorKind::Systemd).then_some(supervisor.name.as_str());
        assert_eq!(name, expected, "{vars:?}");
    }
}
