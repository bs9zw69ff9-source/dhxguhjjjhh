//! `/serverinfo` and `/factionstats`. (The C# has no test file for either; `/serverinfo` is
//! checked against the fake RCON server's recorded commands, not its format strings.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use pavlov_core::factions::{FactionActivity, FactionRegistry, MemberActivity};
use pavlov_discord::caller::Caller;
use pavlov_discord::commands::faction_stats::{inactive, overview};
use pavlov_discord::commands::server_info::{
    MAX_NAMES_SHOWN, ServerInfoCommand, ServerView, embeds, view_embed,
};
use pavlov_discord::interaction::Invocation;
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::testing::FakeRconServer;

fn online(name: &str, players: usize, max: Option<i32>) -> ServerView {
    let roster: Vec<String> = (0..players).map(|i| format!("p{i:02}")).collect();
    ServerView {
        server: name.into(),
        online: true,
        display_name: name.into(),
        map: "Datacenter".into(),
        mode: "TDM".into(),
        player_count: roster.len(),
        max_players: max,
        roster,
    }
}

#[test]
fn an_unreachable_server_is_reported_not_dropped() {
    let views = [online("one", 2, Some(24)), ServerView::unreachable("two")];
    let all = embeds(&views);
    assert_eq!(all.len(), 2);
    assert_eq!(all[1].description.as_deref(), Some("Offline"));
    assert_eq!(
        all[0].field_named("All servers").unwrap().value,
        "**2/24** online across 2 server(s)."
    );
}

#[test]
fn a_long_roster_is_cut_with_a_count() {
    let embed = view_embed(&online("one", MAX_NAMES_SHOWN + 5, None));
    let body = embed.description.as_deref().unwrap();
    assert!(body.contains("**20** playing"), "{body}");
    assert!(body.ends_with(" and 5 more"), "{body}");
}

#[test]
fn a_backtick_in_a_name_cannot_break_the_code_span() {
    let mut view = online("one", 0, Some(10));
    view.roster = vec!["evil`**bold**".into()];
    view.player_count = 1;
    assert!(
        view_embed(&view)
            .description
            .as_deref()
            .unwrap()
            .contains("`evil'**bold**`")
    );
}

#[test]
fn only_an_explicit_false_reads_as_down() {
    let up = ServerView::from_replies(
        "s",
        Some(r#"{"PlayerList":[{"Username":"Bob","UniqueId":"1"}]}"#),
        Some("{}"),
    );
    assert!(up.online);
    assert_eq!(up.roster, ["Bob"]);
    assert_eq!(up.map, "*Unknown*");
    let down = ServerView::from_replies("s", Some(r#"{"Successful":false}"#), None);
    assert!(!down.online);
}

#[tokio::test(flavor = "multi_thread")]
async fn serverinfo_sends_refreshlist_and_serverinfo_to_each_server() {
    let fake = FakeRconServer::start().await.unwrap();
    fake.configure(|b| {
        b.exact_reply = Some(
            r#"{"Successful":true,"ServerInfo":{"ServerName":"Precinct","MapLabel":"Station","GameMode":"SND","PlayerCount":"1/16"},"PlayerList":[{"Username":"Alice","UniqueId":"1"}]}"#
                .into(),
        );
    });
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
    let responder = common::RecordingResponder::default();
    let invocation = Invocation::new("serverinfo", Caller::user(1));
    ServerInfoCommand { rcon }
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();

    let mut sent = fake.commands();
    sent.sort();
    assert_eq!(sent, ["RefreshList", "ServerInfo"]);
    let text = responder.last_text();
    assert!(text.contains("Precinct"), "{text}");
    assert!(text.contains("**1/16** playing"), "{text}");
}

#[test]
fn never_seen_members_are_kept_apart_from_idle_ones() {
    let faction = FactionRegistry::default_set()
        .all()
        .find(|f| f.has_ranks())
        .cloned()
        .unwrap();
    let rank = faction.order.first().cloned().unwrap_or_default();
    let member = |player: &str, days: Option<i64>| MemberActivity {
        player: player.into(),
        rank: rank.clone(),
        minutes: 0,
        last_seen: days.map(|d| Utc::now() - TimeDelta::days(d)),
    };
    let report = FactionActivity::report(
        &faction,
        &[
            member("Idle", Some(90)),
            member("Ghost", None),
            member("Active", Some(0)),
        ],
        Utc::now(),
    );
    let embed = inactive(&report);
    let idle = embed
        .fields
        .iter()
        .find(|f| f.name.starts_with("Idle"))
        .unwrap();
    let ghost = embed
        .fields
        .iter()
        .find(|f| f.name.starts_with("No record"))
        .unwrap();
    assert!(idle.value.contains("Idle") && !idle.value.contains("Ghost"));
    assert!(ghost.value.contains("Ghost") && ghost.value.contains("**Not** the same as inactive"));
    assert!(
        overview(&report, &faction)
            .footer
            .as_deref()
            .is_some_and(|f| f.contains("Active: seen in"))
    );
}
