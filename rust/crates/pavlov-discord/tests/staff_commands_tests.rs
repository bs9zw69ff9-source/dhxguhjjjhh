//! `/staffactivity`, `/banlist`, `/flush` and `/stripmenu`. `/flush` and `/stripmenu` are
//! checked against the fake RCON server's recorded commands. (The C# has no test file.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use futures_util::future::BoxFuture;
use indexmap::IndexMap;
use pavlov_core::moderation::BanRecord;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::staff::{FlushCommand, StripMenuCommand, activity, ban_list};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::bans::{BanService, MasterNames};
use pavlov_host::moderation::{AuditLog, ModAction};
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;

const OWNER: u64 = 1;

struct Protects(&'static str);
impl MasterNames for Protects {
    fn is_master(&self, name: &str) -> bool {
        name == self.0
    }
    fn is_exempt(&self, _: &str) -> bool {
        false
    }
    fn is_protected(&self, _: &str) -> bool {
        false
    }
    fn exempt<'a>(&'a self, _: &'a str, _: Option<Duration>) -> BoxFuture<'a, ()> {
        Box::pin(async {})
    }
}

async fn live(reply: Option<&str>) -> (FakeRconServer, Arc<RconRegistry>) {
    let fake = FakeRconServer::start().await.unwrap();
    if let Some(reply) = reply {
        let reply = reply.to_owned();
        fake.configure(|b| b.exact_reply = Some(reply));
    }
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

fn action(kind: &str, hours: i64) -> ModAction {
    ModAction {
        action: kind.into(),
        moderator: "mod1".into(),
        player: "p".into(),
        reason: Some("x".into()),
        at: Utc::now() - TimeDelta::hours(hours),
    }
}

#[test]
fn activity_groups_by_kind_most_frequent_first() {
    let embed = activity(
        "mod1",
        &[action("kick", 1), action("ban", 2), action("Kick", 3)],
    );
    let body = embed.description.as_deref().unwrap();
    assert!(body.starts_with("`kick` × 2"), "kinds fold case: {body}");
    assert!(embed.field_named("Most recent").is_some());
}

#[test]
fn the_ban_list_puts_permanent_first_then_soonest_to_lapse() {
    let ban = |name: &str, permanent: bool, hours: i64| BanRecord {
        permanent,
        expires: (!permanent).then(|| Utc::now() + TimeDelta::hours(hours)),
        ..BanRecord::for_player(name)
    };
    let embed = ban_list(&[
        ban("later", false, 48),
        ban("forever", true, 0),
        ban("soon", false, 1),
    ]);
    let body = embed.description.as_deref().unwrap();
    let order: Vec<usize> = ["forever", "soon", "later"]
        .iter()
        .map(|n| body.find(n).unwrap())
        .collect();
    assert!(order.windows(2).all(|w| w[0] < w[1]), "{body}");
}

#[tokio::test(flavor = "multi_thread")]
async fn flush_never_picks_a_master_and_only_kicks() {
    let (fake, rcon) = live(Some(
        r#"{"Successful":true,"PlayerList":[{"Username":"Boss","UniqueId":"1"},{"Username":"Rando","UniqueId":"2"}]}"#,
    ))
    .await;
    rcon.refresh_rosters().await;
    let store = common::commands::store();
    let masters: Arc<dyn MasterNames> = Arc::new(Protects("Boss"));
    let command = FlushCommand {
        bans: Arc::new(BanService::new(
            Arc::clone(&rcon),
            Arc::clone(&store),
            Arc::clone(&masters),
            Arc::new(SystemClock),
        )),
        rcon,
        masters,
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
        access: common::commands::access(&store, &[OWNER]),
    };
    let invocation = Invocation::new("flush", Caller::user(OWNER)).with(InvokedOption::value(
        "server",
        OptionKind::String,
        OptionValue::String("server1".into()),
    ));
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    let kicks: Vec<String> = fake
        .commands()
        .into_iter()
        .filter(|c| c.starts_with("Kick") || c.starts_with("Ban"))
        .collect();
    assert_eq!(kicks.len(), 1, "{kicks:?}");
    assert!(
        kicks[0].starts_with("Kick ") && !kicks[0].contains("Boss"),
        "{kicks:?}"
    );
}

#[tokio::test(flavor = "multi_thread")]
async fn stripmenu_sends_removemenu_and_drops_the_grant() {
    let (fake, rcon) = live(None).await;
    let store = common::commands::store();
    let mut grants = IndexMap::new();
    grants.insert("77".to_owned(), "Officer".to_owned());
    store.write("menu_grants", &grants).await.unwrap();
    let command = StripMenuCommand {
        rcon,
        store: Arc::clone(&store),
        access: common::commands::access(&store, &[OWNER]),
    };
    let invocation = Invocation::new("stripmenu", Caller::user(OWNER)).with(InvokedOption::value(
        "member",
        OptionKind::User,
        OptionValue::User(77),
    ));
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    let sent = fake.commands();
    assert!(sent.iter().any(|c| c.starts_with("RemoveMenu")), "{sent:?}");
    assert!(
        !sent.iter().any(|c| c.starts_with("StripMenu")),
        "RCON+ has no StripMenu: {sent:?}"
    );
    let left: IndexMap<String, String> = store.read("menu_grants", IndexMap::new()).unwrap();
    assert!(left.is_empty());
}
