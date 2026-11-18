//! `/setpin`, `/removepin` and `/vpncheck`. The PIN commands are checked against the fake RCON
//! server's recorded commands: clearing must send a BARE `SetPin`. (The C# has no test file.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::admin::{
    RemovePinCommand, SetPinCommand, generate_pin, is_valid_pin, probe_report,
};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_host::vpn::DetectorProbe;
use pavlov_rcon::testing::FakeRconServer;

const OWNER: u64 = 1;

async fn live() -> (FakeRconServer, Arc<RconRegistry>) {
    let fake = FakeRconServer::start().await.unwrap();
    let mut options = fake.options();
    options.name = "server1".into();
    options.command_spacing = Duration::ZERO;
    options.read_cache_duration = Duration::ZERO;
    options.max_attempts = 1;
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    (fake, rcon)
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

#[test]
fn a_pin_is_one_to_four_digits_with_no_leading_zero() {
    for good in ["1", "42", "999", "9999"] {
        assert!(is_valid_pin(good), "{good}");
    }
    for bad in ["", "0", "0123", "12345", "12a", "-1", " 12"] {
        assert!(!is_valid_pin(bad), "{bad}");
    }
    for _ in 0..200 {
        let pin = generate_pin().unwrap();
        assert!(is_valid_pin(&pin) && pin.len() == 4, "{pin}");
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn setpin_sends_the_pin_and_removepin_sends_a_bare_setpin() {
    let (fake, rcon) = live().await;
    let store = common::commands::store();
    let access = common::commands::access(&store, &[OWNER]);
    let set = SetPinCommand {
        rcon: Arc::clone(&rcon),
        access: Arc::clone(&access),
    };
    let reply = run(
        &set,
        &Invocation::new("setpin", Caller::user(OWNER))
            .with(text("server", "server1"))
            .with(text("pin", "4321")),
    )
    .await;
    assert!(reply.contains("requires the PIN **4321**"), "{reply}");
    let clear = RemovePinCommand { rcon, access };
    run(
        &clear,
        &Invocation::new("removepin", Caller::user(OWNER)).with(text("server", "server1")),
    )
    .await;
    assert_eq!(
        fake.commands(),
        ["SetPin 4321", "SetPin"],
        "never `SetPin 0`, which leaves it locked"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn a_bad_pin_is_refused_before_anything_is_sent() {
    let (fake, rcon) = live().await;
    let store = common::commands::store();
    let set = SetPinCommand {
        rcon,
        access: common::commands::access(&store, &[OWNER]),
    };
    let reply = run(
        &set,
        &Invocation::new("setpin", Caller::user(OWNER))
            .with(text("server", "server1"))
            .with(text("pin", "0123")),
    )
    .await;
    assert!(reply.contains("That PIN will not work"));
    assert!(fake.commands().is_empty());
}

#[test]
fn a_failed_detector_says_why() {
    let probe = |name: &str, tier: i32, configured: bool, answered: bool, error: Option<&str>| {
        DetectorProbe {
            name: name.into(),
            tier,
            answered,
            flagged: None,
            detail: answered.then(|| "clean".into()),
            duration_ms: 120.0,
            error: error.map(Into::into),
            configured,
        }
    };
    let embed = probe_report(
        "8.8.8.8",
        &[
            probe("b", 2, true, false, Some("HTTP 401: bad key")),
            probe("a", 1, true, true, None),
            probe("c", 1, false, false, None),
        ],
        Some("only one tier"),
    );
    let body = embed.description.as_deref().unwrap();
    assert!(body.starts_with("**1** of **2** configured"), "{body}");
    assert!(body.contains("HTTP 401: bad key"));
    assert!(body.contains("no API key set - not checked"));
    assert!(
        body.find("**a**").unwrap() < body.find("**b**").unwrap(),
        "tier 1 first"
    );
    assert!(
        embed
            .fields
            .iter()
            .any(|f| f.name.ends_with("Configuration"))
    );
}
