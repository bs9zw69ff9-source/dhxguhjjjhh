//! `/configure`: every action is reachable, owner access is re-checked on every press, a
//! destructive action needs its word, and a confirmed one is audited. (The C# has no test file
//! for the panel; `OwnerActionsTests` covers the actions.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashSet;
use std::sync::{Arc, Mutex};

use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::commands::config_panel::{ACTIONS, ConfigPanel, confirmed, find, panel};
use pavlov_discord::component::{ActionRow, Modal};
use pavlov_discord::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply};
use pavlov_discord::paged::ComponentHandler;
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::owner_actions::{OwnerActionParts, OwnerActions};

const OWNER: u64 = 1;

#[derive(Default)]
struct Press {
    said: Mutex<Vec<Reply>>,
    followups: Mutex<Vec<Reply>>,
    modals: Mutex<Vec<Modal>>,
}

impl ComponentResponder for Press {
    fn update(&self, _: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn respond(&self, reply: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.said.lock().unwrap().push(reply);
        Box::pin(async { Ok(()) })
    }
    fn edit_original(&self, _: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn acknowledge(&self) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn show_modal(&self, modal: Modal) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.modals.lock().unwrap().push(modal);
        Box::pin(async { Ok(()) })
    }
    fn defer_reply(&self, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn followup(&self, reply: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.followups.lock().unwrap().push(reply);
        Box::pin(async { Ok(()) })
    }
}

fn panel_for_tests() -> (Arc<AuditLog>, ConfigPanel) {
    let store = common::commands::store();
    let clock = Arc::new(SystemClock);
    let tracking = Arc::new(IpTrackingService::new(
        Arc::clone(&store),
        Arc::new(MetricsRegistry::default()),
        clock.clone(),
    ));
    let audit = Arc::new(AuditLog::new(Arc::clone(&store), clock));
    let config = ConfigPanel {
        actions: Arc::new(OwnerActions::new(
            Arc::clone(&store),
            tracking,
            OwnerActionParts::default(),
        )),
        access: common::commands::access(&store, &[OWNER]),
        audit: Arc::clone(&audit),
    };
    (audit, config)
}

fn press(
    parts: &[&str],
    user: u64,
    values: &[&str],
    fields: &[(&str, &str)],
) -> ComponentInvocation {
    ComponentInvocation {
        custom_id: ComponentId::encode("cfg", parts).unwrap(),
        caller: Caller::user(user),
        username: "boss".into(),
        values: values.iter().map(|v| (*v).to_owned()).collect(),
        fields: fields
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect(),
        ..ComponentInvocation::default()
    }
}

#[test]
fn every_action_is_on_the_panel_once_and_every_id_fits() {
    let reply = panel();
    let ActionRow::Select(menu) = &reply.components[0] else {
        panic!()
    };
    assert_eq!(menu.options.len(), ACTIONS.len());
    assert!(menu.options.len() <= 25);
    let keys: HashSet<&str> = ACTIONS.iter().map(|a| a.key).collect();
    assert_eq!(keys.len(), ACTIONS.len());
    for a in &ACTIONS {
        ComponentId::encode("cfg", &["submit", a.key]).unwrap();
    }
}

#[test]
fn the_word_must_match_and_a_wordless_action_needs_none() {
    let wipe = find(Some("wipemoney")).unwrap();
    assert!(confirmed(wipe, Some(" wipe ")));
    assert!(!confirmed(wipe, Some("WIPE IT")));
    assert!(!confirmed(wipe, None));
    assert!(confirmed(find(Some("viewbl")).unwrap(), None));
}

#[tokio::test]
async fn a_non_owner_is_refused_on_every_press() {
    let (_audit, config) = panel_for_tests();
    let r = Press::default();
    config
        .handle(&press(&["menu"], 99, &["viewbl"], &[]), &r)
        .await;
    let said = r.said.lock().unwrap();
    assert!(
        said[0].embeds[0]
            .title
            .as_deref()
            .unwrap()
            .contains("Not allowed")
    );
    assert!(r.followups.lock().unwrap().is_empty());
}

#[tokio::test]
async fn a_destructive_action_opens_a_modal_and_a_wrong_word_cancels_it() {
    let (audit, config) = panel_for_tests();
    let r = Press::default();
    config
        .handle(&press(&["menu"], OWNER, &["clearallbans"], &[]), &r)
        .await;
    assert_eq!(r.modals.lock().unwrap()[0].inputs[0].custom_id, "confirm");
    config
        .handle(
            &press(
                &["submit", "clearallbans"],
                OWNER,
                &[],
                &[("confirm", "nope")],
            ),
            &r,
        )
        .await;
    assert!(
        r.said.lock().unwrap()[0].embeds[0]
            .title
            .as_deref()
            .unwrap()
            .contains("Cancelled")
    );
    assert!(audit.all().is_empty(), "nothing ran, nothing audited");
}

#[tokio::test]
async fn a_confirmed_destructive_action_runs_and_is_audited() {
    let (audit, config) = panel_for_tests();
    let r = Press::default();
    config
        .handle(
            &press(
                &["submit", "cleartempbans"],
                OWNER,
                &[],
                &[("confirm", "CLEAR")],
            ),
            &r,
        )
        .await;
    assert_eq!(r.followups.lock().unwrap().len(), 1);
    assert_eq!(audit.all()[0].action, "configure-cleartempbans");
}
