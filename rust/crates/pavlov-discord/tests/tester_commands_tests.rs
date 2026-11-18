//! `/tester` and `/testmode` against real install directories: per-install edits, the
//! empty-whitelist trap, the Game.ini edit and the restart that makes it take effect. (The C#
//! has no test file for these commands.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::path::Path;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::servers::installs::{game_ini_path, whitelist_path};
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::tester::{TestModeCommand, TesterCommand};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::notice::PlayerNotice;
use pavlov_host::registry::RconRegistry;
use pavlov_host::service_control::{UnitAction, UnitControl, UnitResult, UnitState};
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::whitelist::WhitelistFile;

const OWNER: u64 = 1;

struct Rig {
    dir: tempfile::TempDir,
    installs: Vec<String>,
}

fn rig(whitelists: &[&str]) -> Rig {
    let dir = tempfile::tempdir().unwrap();
    let installs: Vec<String> = whitelists
        .iter()
        .enumerate()
        .map(|(i, contents)| {
            let root = dir.path().join(format!(
                "pavlovserver{}",
                if i == 0 { String::new() } else { i.to_string() }
            ));
            let root = root.to_string_lossy().into_owned();
            for file in [whitelist_path(&root), game_ini_path(&root)] {
                std::fs::create_dir_all(Path::new(&file).parent().unwrap()).unwrap();
            }
            std::fs::write(whitelist_path(&root), contents).unwrap();
            std::fs::write(
                game_ini_path(&root),
                "[/Script/Pavlov.DedicatedServer]\nbWhitelist=false\n",
            )
            .unwrap();
            root
        })
        .collect();
    Rig { dir, installs }
}

fn whitelist(rig: &Rig) -> Arc<WhitelistFile> {
    Arc::new(WhitelistFile::new(
        Arc::new(GameFileGuard::none()),
        rig.dir.path().join("bak"),
    ))
}

fn text(name: &str, value: &str) -> InvokedOption {
    InvokedOption::value(name, OptionKind::String, OptionValue::String(value.into()))
}

async fn run(command: &dyn SlashCommand, invocation: &Invocation) -> String {
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    responder.last_text()
}

fn tester(rig: &Rig) -> TesterCommand {
    let store = common::commands::store();
    TesterCommand {
        whitelist: whitelist(rig),
        access: common::commands::access(&store, &[OWNER]),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
        installs: rig.installs.clone(),
    }
}

#[tokio::test]
async fn add_writes_every_install_and_check_finds_the_one_missing() {
    let rig = rig(&["Alice\n", ""]);
    let command = tester(&rig);
    let check = Invocation::new("tester", Caller::user(OWNER))
        .with(InvokedOption::sub("check", vec![text("playerid", "alice")]));
    let before = run(&command, &check).await;
    assert!(before.contains("missing from 1 server(s)"), "{before}");

    let add = Invocation::new("tester", Caller::user(OWNER))
        .with(InvokedOption::sub("add", vec![text("playerid", "Bob")]));
    let reply = run(&command, &add).await;
    assert!(reply.contains("whitelisted on 2 server(s)"), "{reply}");
    for install in &rig.installs {
        assert!(
            std::fs::read_to_string(whitelist_path(install))
                .unwrap()
                .contains("Bob")
        );
    }
}

struct Systemd {
    ran: Mutex<Vec<(String, UnitAction)>>,
}

impl UnitControl for Systemd {
    fn units(&self) -> &[String] {
        static UNITS: std::sync::OnceLock<Vec<String>> = std::sync::OnceLock::new();
        UNITS.get_or_init(|| vec!["pavlovserver".into()])
    }
    fn state<'a>(&'a self, _: &'a str) -> BoxFuture<'a, UnitState> {
        Box::pin(async { UnitState::Active })
    }
    fn run<'a>(&'a self, unit: &'a str, action: UnitAction) -> BoxFuture<'a, UnitResult> {
        self.ran.lock().unwrap().push((unit.to_owned(), action));
        Box::pin(async move { UnitResult::new(true, unit, "") })
    }
}

fn test_mode(rig: &Rig) -> (Arc<Systemd>, TestModeCommand) {
    let systemd = Arc::new(Systemd {
        ran: Mutex::default(),
    });
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    let store = common::commands::store();
    let command = TestModeCommand {
        services: Arc::clone(&systemd) as Arc<dyn UnitControl>,
        notice: Arc::new(PlayerNotice::new(rcon)),
        whitelist: whitelist(rig),
        guard: Arc::new(GameFileGuard::none()),
        access: common::commands::access(&store, &[OWNER]),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
        installs: rig.installs.clone(),
        grace: Duration::ZERO,
    };
    (systemd, command)
}

fn toggle(enabled: bool) -> Invocation {
    Invocation::new("testmode", Caller::user(OWNER))
        .with(InvokedOption::value(
            "server",
            OptionKind::Integer,
            OptionValue::Integer(1),
        ))
        .with(InvokedOption::value(
            "enabled",
            OptionKind::Boolean,
            OptionValue::Boolean(enabled),
        ))
}

#[tokio::test]
async fn an_empty_whitelist_cannot_be_locked_to() {
    let rig = rig(&[""]);
    let (systemd, command) = test_mode(&rig);
    assert!(
        run(&command, &toggle(true))
            .await
            .contains("The whitelist is empty")
    );
    assert!(systemd.ran.lock().unwrap().is_empty());
    assert!(
        std::fs::read_to_string(game_ini_path(&rig.installs[0]))
            .unwrap()
            .contains("bWhitelist=false")
    );
}

#[tokio::test]
async fn turning_it_on_writes_game_ini_and_restarts() {
    let rig = rig(&["Alice\n"]);
    let (systemd, command) = test_mode(&rig);
    let reply = run(&command, &toggle(true)).await;
    assert!(reply.contains("Test mode on"), "{reply}");
    assert!(
        std::fs::read_to_string(game_ini_path(&rig.installs[0]))
            .unwrap()
            .contains("bWhitelist=true")
    );
    assert_eq!(
        *systemd.ran.lock().unwrap(),
        [("pavlovserver".to_owned(), UnitAction::Restart)]
    );
}

#[tokio::test]
async fn no_change_means_no_restart() {
    let rig = rig(&["Alice\n"]);
    let (systemd, command) = test_mode(&rig);
    assert!(run(&command, &toggle(false)).await.contains("Already off"));
    assert!(systemd.ran.lock().unwrap().is_empty());
}
