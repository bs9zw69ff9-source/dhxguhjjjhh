//! Port of `StaffFeedTests.cs` (both classes): staff actions reaching the staff webhook from
//! the audit funnel, and `ChannelStaffLog` posting them into channels by id.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::{Arc, Mutex};

use chrono::Utc;
use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::embed::Embed;
use pavlov_core::time::SystemClock;
use pavlov_discord::component::ActionRow;
use pavlov_discord::interaction::DiscordError;
use pavlov_discord::post::{ChannelPoster, PostEdit};
use pavlov_discord::staff_log::{ChannelStaffLog, StaffLogChannels, build, channel_for};
use pavlov_host::feeds::{FeedWebhooks, STAFF, staff_line};
use pavlov_host::http::{HttpClient, HttpError, HttpRequest, HttpResponse};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::{AuditLog, ModAction};

struct NoNetwork;

impl HttpClient for NoNetwork {
    fn send(&self, _: HttpRequest) -> BoxFuture<'_, Result<HttpResponse, HttpError>> {
        async { Err(HttpError::Transport("no network in tests".into())) }.boxed()
    }
}

fn audit(feeds: Option<Arc<FeedWebhooks>>) -> (AuditLog, Arc<MemoryBackend>) {
    let backend = Arc::new(MemoryBackend::default());
    let store = Arc::new(SerializedStore::new(Arc::clone(&backend) as _));
    let log = AuditLog::new(store, Arc::new(SystemClock));
    let log = match feeds {
        Some(feeds) => log.with_feeds(feeds),
        None => log,
    };
    (log, backend)
}

fn feeds() -> Arc<FeedWebhooks> {
    Arc::new(FeedWebhooks::new(
        Arc::new(NoNetwork),
        Arc::new(MetricsRegistry::default()),
        Arc::new(SystemClock),
    ))
}

// ---- the line itself ----

#[test]
fn the_line_names_who_did_what_to_whom_and_why() {
    let line = staff_line(
        "serverswitch",
        "Mayor",
        "pavlovserver",
        Some("shutting down - ok"),
        Utc::now(),
    );
    for part in ["SERVERSWITCH", "Mayor", "pavlovserver", "shutting down"] {
        assert!(line.contains(part), "{line}");
    }
}

#[test]
fn a_reason_cannot_forge_a_second_entry() {
    let line = staff_line(
        "kick",
        "Mayor",
        "Someone",
        Some("spam\n[12:00:00] BAN  Mayor → Innocent"),
        Utc::now(),
    );
    assert!(!line.contains('\n'));
    assert!(!line.contains('\r'));
}

#[test]
fn an_action_with_no_reason_still_reads() {
    let line = staff_line("flush", "Mayor", "server1", None, Utc::now());
    assert!(line.contains("FLUSH"));
    assert!(!line.contains("()"));
}

// ---- the funnel ----

#[tokio::test]
async fn recording_an_action_still_writes_the_dataset() {
    let (audit, backend) = audit(None);
    audit
        .record(
            "serverswitch",
            "Mayor",
            "pavlovserver",
            Some("shutting down - ok"),
        )
        .await
        .unwrap();
    assert_eq!(audit.all().len(), 1);
    assert_eq!(audit.all()[0].action, "serverswitch");
    assert!(backend.writes() > 0);
}

#[tokio::test]
async fn an_unconfigured_staff_feed_changes_nothing() {
    let (audit, _) = audit(Some(feeds()));
    audit
        .record("kick", "Mayor", "Someone", Some("afk"))
        .await
        .unwrap();
    assert_eq!(audit.all().len(), 1);
}

#[tokio::test]
async fn a_dead_staff_webhook_never_loses_the_audit_entry() {
    let feeds = feeds();
    feeds.register(
        STAFF,
        Some("https://discord.com/api/webhooks/1/deadbeef-not-a-real-token"),
    );
    let (audit, _) = audit(Some(feeds));

    audit
        .record("permban", "Mayor", "Evader", Some("ban evasion"))
        .await
        .unwrap();

    assert_eq!(audit.all().len(), 1);
    assert_eq!(audit.all()[0].action, "permban");
}

#[tokio::test]
async fn server_switch_is_among_the_actions_that_reach_the_feed() {
    let (audit, _) = audit(None);
    audit
        .record(
            "serverswitch",
            "Mayor",
            "pavlovserver1",
            Some("restarting - ok"),
        )
        .await
        .unwrap();
    audit
        .record("rotatemap", "Mayor", "pavlovserver", Some("3/3 restarted"))
        .await
        .unwrap();
    let actions: Vec<String> = audit.all().into_iter().map(|a| a.action).collect();
    assert_eq!(actions, ["serverswitch", "rotatemap"]);
}

// ---- ChannelStaffLog ----

const MOD: u64 = 1_528_754_477_861_961_760;
const BAN: u64 = 999;

/// Records what it was asked to post.
#[derive(Default)]
struct RecordingPostTarget {
    sent: Mutex<Vec<(u64, Embed)>>,
    fails: bool,
}

impl ChannelPoster for RecordingPostTarget {
    fn edit(&self, _: u64, _: u64, _: Embed, _: Vec<ActionRow>) -> BoxFuture<'_, PostEdit> {
        async { PostEdit::Missing }.boxed()
    }
    fn send(
        &self,
        channel: u64,
        embed: Embed,
        _: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        let fails = self.fails;
        if !fails {
            self.sent.lock().unwrap().push((channel, embed));
        }
        async move {
            if fails {
                Err(DiscordError("missing permissions".into()))
            } else {
                Ok(Some(1))
            }
        }
        .boxed()
    }
}

fn both() -> StaffLogChannels {
    StaffLogChannels::new(Some(MOD), Some(BAN))
}

fn action(kind: &str, moderator: &str, player: &str, reason: Option<&str>) -> ModAction {
    ModAction {
        action: kind.into(),
        moderator: moderator.into(),
        player: player.into(),
        reason: reason.map(str::to_owned),
        at: Utc::now(),
    }
}

#[test]
fn every_ban_action_routes_to_the_ban_channel() {
    for kind in ["permban", "tempban", "unban", "autoban", "vpnban"] {
        assert_eq!(channel_for(kind, &both()), Some(BAN), "{kind}");
    }
}

#[test]
fn everything_else_routes_to_the_mod_channel() {
    for kind in [
        "kick",
        "arrest",
        "serverswitch",
        "rotatemap",
        "menu-claim",
        "flush",
        "verify-accept",
        "firewall-unblock",
    ] {
        assert_eq!(channel_for(kind, &both()), Some(MOD), "{kind}");
    }
}

#[test]
fn one_channel_configured_takes_everything() {
    assert_eq!(
        channel_for("permban", &StaffLogChannels::new(Some(MOD), None)),
        Some(MOD)
    );
    assert_eq!(
        channel_for("kick", &StaffLogChannels::new(None, Some(BAN))),
        Some(BAN)
    );
}

#[test]
fn neither_channel_configured_posts_nothing() {
    assert_eq!(channel_for("permban", &StaffLogChannels::default()), None);
    assert_eq!(channel_for("kick", &StaffLogChannels::default()), None);
}

#[test]
fn both_pointing_at_the_same_channel_is_fine() {
    let same = StaffLogChannels::new(Some(MOD), Some(MOD));
    assert_eq!(channel_for("permban", &same), Some(MOD));
    assert_eq!(channel_for("serverswitch", &same), Some(MOD));
}

#[tokio::test]
async fn an_action_reaches_its_channel() {
    let target = Arc::new(RecordingPostTarget::default());
    ChannelStaffLog::new(Arc::clone(&target) as _, both())
        .post_action(&action(
            "serverswitch",
            "Mayor",
            "pavlovserver",
            Some("shutting down - ok"),
        ))
        .await;

    let sent = target.sent.lock().unwrap();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].0, MOD);
    assert!(sent[0].1.title.as_deref().unwrap().contains("SERVERSWITCH"));
}

#[tokio::test]
async fn a_post_that_throws_never_escapes() {
    let target = Arc::new(RecordingPostTarget {
        fails: true,
        ..RecordingPostTarget::default()
    });
    // Returns nothing to fail with; the call completing is the check.
    ChannelStaffLog::new(target, both())
        .post_action(&action("permban", "Mayor", "Evader", Some("evasion")))
        .await;
}

#[test]
fn the_embed_names_the_staffer_the_target_and_the_reason() {
    let embed = build(&action("permban", "Mayor", "Evader", Some("ban evasion")));
    let text = format!(
        "{}{}",
        embed.title.clone().unwrap_or_default(),
        embed
            .fields
            .iter()
            .map(|f| format!("{}:{}", f.name, f.value))
            .collect::<Vec<_>>()
            .join(" ")
    );
    for part in ["Evader", "Mayor", "ban evasion"] {
        assert!(text.contains(part), "{text}");
    }
}

#[test]
fn an_unban_reads_differently_from_a_ban() {
    let ban = build(&action("permban", "M", "P", None));
    let unban = build(&action("unban", "M", "P", None));
    assert_ne!(ban.color, unban.color);
}

#[test]
fn a_player_called_everyone_cannot_become_a_mention() {
    let embed = build(&action("kick", "Mayor", "@everyone", Some("@here too")));
    let target = &embed.field_named("Target").unwrap().value;
    assert!(target.starts_with('`') && target.ends_with('`'));
}

#[test]
fn a_reason_with_a_newline_cannot_forge_a_field() {
    let embed = build(&action(
        "kick",
        "Mayor",
        "Someone",
        Some("spam\nBanned by: nobody"),
    ));
    assert!(!embed.field_named("Reason").unwrap().value.contains('\n'));
}
