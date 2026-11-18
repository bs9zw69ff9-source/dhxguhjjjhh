//! `/givemenu`, `/unlinkname` and the menu panel: the grant goes over RCON+ and is recorded
//! with its binding, nothing is recorded when nothing landed, and the panel's blacklist role
//! beats even an owner. (The C# has no test file for these; `MenuLinkTests` covers the decision.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use indexmap::IndexMap;
use pavlov_config::{Config, FeatureOptions};
use pavlov_core::data::SerializedStore;
use pavlov_core::moderation::MenuRoleMap;
use pavlov_core::servers::{DetectedServer, HostProbe};
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::menu::{
    GiveMenuCommand, MenuAccess, MenuPanel, PanelTier, UnlinkNameCommand,
};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::confirmations::RconConfirmations;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;

const OWNER: u64 = 1;
const MEMBER: u64 = 77;

struct EmptyBox;
impl HostProbe for EmptyBox {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        Vec::new()
    }
    fn installs_beside(&self, _: &str, _: &str) -> std::io::Result<Vec<String>> {
        Ok(Vec::new())
    }
    fn directory_exists(&self, _: &str) -> bool {
        false
    }
}

async fn rcon(reachable: bool) -> (Option<FakeRconServer>, Arc<RconRegistry>) {
    let fake = FakeRconServer::start().await.unwrap();
    let mut options = fake.options();
    options.name = "server1".into();
    options.command_spacing = Duration::ZERO;
    options.read_cache_duration = Duration::ZERO;
    options.command_timeout = Duration::from_millis(500);
    options.max_attempts = 1;
    let fake = if reachable { Some(fake) } else { None };
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[options],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    (fake, rcon)
}

fn menus(rcon: Arc<RconRegistry>, store: &Arc<SerializedStore>) -> Arc<MenuAccess> {
    Arc::new(MenuAccess {
        rcon,
        store: Arc::clone(store),
        confirmations: Arc::new(RconConfirmations::default()),
        clock: Arc::new(SystemClock),
    })
}

fn give(name: &str) -> Invocation {
    Invocation::new("givemenu", Caller::user(OWNER))
        .with(InvokedOption::value(
            "member",
            OptionKind::User,
            OptionValue::User(MEMBER),
        ))
        .with(InvokedOption::value(
            "playerid",
            OptionKind::String,
            OptionValue::String(name.into()),
        ))
        .with(InvokedOption::value(
            "menu",
            OptionKind::String,
            OptionValue::String("highstaff".into()),
        ))
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

#[tokio::test(flavor = "multi_thread")]
async fn a_grant_sends_givemenu_and_records_the_grant_and_binding() {
    let (fake, rcon) = rcon(true).await;
    let store = common::commands::store();
    let command = GiveMenuCommand {
        menus: menus(rcon, &store),
        access: common::commands::access(&store, &[OWNER]),
    };
    let reply = run(&command, &give("Officer")).await;
    assert!(reply.contains("High Staff menu granted"), "{reply}");
    assert!(
        fake.unwrap()
            .commands()
            .iter()
            .any(|c| c.starts_with("GiveMenu Officer "))
    );
    let grants: IndexMap<String, String> = store.read("menu_grants", IndexMap::new()).unwrap();
    assert_eq!(grants.get("77").map(String::as_str), Some("Officer"));
    assert!(
        store
            .read_raw("menu_links")
            .unwrap()
            .unwrap()
            .contains("\"inGameName\": \"Officer\"")
    );

    // The same name again, holding the menu, is a release: the grant goes, the binding stays.
    assert!(
        run(&command, &give("Officer"))
            .await
            .contains("Menu access removed")
    );
    let grants: IndexMap<String, String> = store.read("menu_grants", IndexMap::new()).unwrap();
    assert!(grants.is_empty());
    assert!(
        store
            .read_raw("menu_links")
            .unwrap()
            .unwrap()
            .contains("Officer")
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn nothing_landed_means_nothing_recorded() {
    let (_gone, rcon) = rcon(false).await;
    let store = common::commands::store();
    let command = GiveMenuCommand {
        menus: menus(rcon, &store),
        access: common::commands::access(&store, &[OWNER]),
    };
    let reply = run(&command, &give("Officer")).await;
    assert!(reply.contains("Not granted"), "{reply}");
    assert!(store.read_raw("menu_grants").unwrap().is_none());
    assert!(store.read_raw("menu_links").unwrap().is_none());
}

#[tokio::test]
async fn unlinking_breaks_the_binding() {
    let store = common::commands::store();
    let (_fake, rcon) = rcon(true).await;
    menus(rcon, &store).bind("77", "Officer").await.unwrap();
    let command = UnlinkNameCommand {
        store: Arc::clone(&store),
        access: common::commands::access(&store, &[OWNER]),
    };
    let invocation = Invocation::new("unlinkname", Caller::user(OWNER)).with(InvokedOption::value(
        "member",
        OptionKind::User,
        OptionValue::User(MEMBER),
    ));
    assert!(run(&command, &invocation).await.contains("Binding broken"));
    assert!(
        !store
            .read_raw("menu_links")
            .unwrap()
            .unwrap()
            .contains("Officer")
    );
}

#[tokio::test]
async fn the_blacklist_role_beats_even_an_owner() {
    let store = common::commands::store();
    store
        .write(
            "menu_roles",
            &MenuRoleMap {
                high_staff: Some(10),
                staff: Some(20),
                blacklist: Some(99),
            },
        )
        .await
        .unwrap();
    let (_fake, rcon) = rcon(true).await;
    let panel = MenuPanel {
        menus: menus(rcon, &store),
        features: Arc::new(FeatureOptions::bind(
            &Config::from_pairs(Vec::<(&str, &str)>::new()),
            &EmptyBox,
        )),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
        access: common::commands::access(&store, &[OWNER]),
    };
    assert_eq!(
        panel.tier_of(&Caller::member(OWNER, 5, &[99])),
        Some(PanelTier::Blacklisted)
    );
    assert_eq!(
        panel.tier_of(&Caller::user(OWNER)),
        Some(PanelTier::Menu("highstaff")),
        "an owner, member or not"
    );
    assert_eq!(
        panel.tier_of(&Caller::member(3, 5, &[20])),
        Some(PanelTier::Menu("staff"))
    );
    assert_eq!(
        panel.tier_of(&Caller::member(3, 5, &[10, 20])),
        Some(PanelTier::Menu("highstaff")),
        "highest wins"
    );
    assert_eq!(panel.tier_of(&Caller::member(3, 5, &[])), None);
}
