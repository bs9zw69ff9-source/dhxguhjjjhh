//! `/rotatemap`, `/guildinvites` and `/alts`: the parts the C# keeps private and untested,
//! pinned here so the port cannot drift. (`GuildInvitesTests` covers the invite rendering.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::evasion::AccountRecord;
use pavlov_core::security::OwnerGuard;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::{ChoiceValue, problems};
use pavlov_discord::commands::alts::{self, MAX_ROWS};
use pavlov_discord::commands::guild_invites::GuildInvitesCommand;
use pavlov_discord::commands::rotate_map::{self, RotateMapCommand};
use pavlov_discord::guild_directory::{GuildDirectory, GuildInvite};
use pavlov_discord::interaction::{DiscordError, Invocation};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::notice::{RotationOutcome, RotationResult};
use pavlov_host::registry::RconRegistry;

const OWNER: u64 = 7;

fn account(id: &str, ips: &[&str], seen_hours_ago: i64) -> AccountRecord {
    AccountRecord {
        id: id.into(),
        confirmed_ips: ips.iter().map(|s| (*s).to_owned()).collect(),
        names: vec![format!("{id}_name")],
        last_seen: Some(Utc::now() - TimeDelta::hours(seen_hours_ago)),
        ..AccountRecord::default()
    }
}

#[test]
fn zero_is_every_server_and_an_unknown_number_is_none() {
    assert_eq!(rotate_map::targets(0, 3), [1, 2, 3]);
    assert_eq!(rotate_map::targets(2, 3), [2]);
    assert!(rotate_map::targets(4, 3).is_empty());
    assert!(rotate_map::targets(-1, 3).is_empty());
}

#[test]
fn the_server_choices_follow_the_configured_units() {
    let store = common::commands::store();
    let command = RotateMapCommand {
        rcon: Arc::new(RconRegistry::with_system_clock(
            &[],
            Duration::from_secs(60),
            Arc::new(MetricsRegistry::default()),
        )),
        unit_count: 2,
        access: common::commands::access(&store, &[OWNER]),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
    };
    let spec = command.build();
    assert!(problems(&spec).is_empty());
    let values: Vec<ChoiceValue> = spec.options[0]
        .choices
        .iter()
        .map(|c| c.value.clone())
        .collect();
    assert_eq!(
        values,
        [
            ChoiceValue::Integer(0),
            ChoiceValue::Integer(1),
            ChoiceValue::Integer(2)
        ]
    );
}

#[test]
fn an_unconfirmed_rotation_is_not_counted_as_done() {
    let results = [
        RotationResult {
            number: 1,
            outcome: RotationOutcome::Rotated,
            detail: "map rotated".into(),
        },
        RotationResult {
            number: 2,
            outcome: RotationOutcome::Unconfirmed,
            detail: "x".repeat(900),
        },
    ];
    let embed = rotate_map::report(&results);
    assert!(
        embed
            .title
            .as_deref()
            .unwrap()
            .ends_with("Rotated 1 of 2 server(s)")
    );
    let second = embed
        .fields
        .iter()
        .find(|f| f.name.ends_with("Server 2"))
        .unwrap();
    assert!(second.value.chars().count() < 420, "the reply is truncated");
}

#[derive(Default)]
struct Directory {
    invites: Vec<GuildInvite>,
    accepts: bool,
    sent_to: Mutex<Vec<u64>>,
}

impl GuildDirectory for Directory {
    fn invite_to_every_guild(&self, _: Duration, _: u32) -> BoxFuture<'_, Vec<GuildInvite>> {
        Box::pin(async move { self.invites.clone() })
    }

    fn send_direct_message(
        &self,
        user_id: u64,
        _: Embed,
    ) -> BoxFuture<'_, Result<bool, DiscordError>> {
        self.sent_to.lock().unwrap().push(user_id);
        Box::pin(async move { Ok(self.accepts) })
    }
}

async fn run_invites(accepts: bool) -> (Arc<Directory>, String) {
    let store = common::commands::store();
    let directory = Arc::new(Directory {
        invites: vec![GuildInvite {
            guild_id: 1,
            guild_name: "Home".into(),
            member_count: 10,
            url: Some("https://discord.gg/abc".into()),
            problem: None,
        }],
        accepts,
        ..Directory::default()
    });
    let command = GuildInvitesCommand {
        directory: Arc::clone(&directory) as Arc<dyn GuildDirectory>,
        access: common::commands::access(&store, &[OWNER]),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock))),
    };
    let responder = common::RecordingResponder::default();
    let invocation = Invocation::new("guildinvites", Caller::user(OWNER));
    command
        .handle(CommandContext {
            invocation: &invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    (directory, responder.last_text())
}

#[tokio::test]
async fn the_invites_go_to_the_super_owner_and_nobody_else() {
    let (directory, text) = run_invites(true).await;
    assert_eq!(
        *directory.sent_to.lock().unwrap(),
        [OwnerGuard::SUPER_OWNER_ID]
    );
    assert!(text.contains("Sent 1 server(s)"), "{text}");
}

#[tokio::test]
async fn a_closed_inbox_puts_the_list_in_the_reply_instead() {
    let (_, text) = run_invites(false).await;
    assert!(text.contains("Could not DM the super owner"), "{text}");
    assert!(text.contains("https://discord.gg/abc"));
}

#[test]
fn no_alts_is_said_plainly() {
    let embed = alts::report(&account("me", &["1.1.1.1"], 1), &[]);
    assert!(
        embed
            .title
            .as_deref()
            .unwrap()
            .contains("No shared addresses")
    );
}

#[test]
fn the_most_overlapping_account_is_listed_first_with_the_caveat() {
    let subject = account("me", &["1.1.1.1", "2.2.2.2"], 1);
    let linked = [
        account("household", &["1.1.1.1"], 1),
        account("sameperson", &["1.1.1.1", "2.2.2.2"], 50),
    ];
    let embed = alts::report(&subject, &linked);
    let body = embed.description.as_deref().unwrap();
    assert!(body.find("sameperson").unwrap() < body.find("household").unwrap());
    assert!(
        embed
            .footer
            .as_deref()
            .is_some_and(|f| f.contains("evidence, not proof"))
    );
}

#[test]
fn more_than_the_row_limit_says_how_many_are_hidden() {
    let subject = account("me", &["1.1.1.1"], 1);
    let linked: Vec<AccountRecord> = (0..MAX_ROWS + 2)
        .map(|i| account(&format!("a{i}"), &["1.1.1.1"], 1))
        .collect();
    let embed = alts::report(&subject, &linked);
    let hidden = embed.fields.iter().find(|f| f.name == "Not shown").unwrap();
    assert!(hidden.value.starts_with("2 more"));
}
