//! The command runner's order of events (C# `DiscordGateway.HandleSlashCommandAsync`). The C#
//! had no tests for it (it lived inside a class that needs a live socket client); these pin
//! the order the C# comments argue for.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use common::{RecordingResponder, Sent};
use futures_util::future::BoxFuture;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::embed::Embed;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::CommandSpec;
use pavlov_discord::command_log::CommandLog;
use pavlov_discord::component::ActionRow;
use pavlov_discord::dispatcher::InteractionDispatcher;
use pavlov_discord::interaction::{DiscordError, IntegrationOwners, Invocation, Reply};
use pavlov_discord::post::{ChannelPoster, PostEdit};
use pavlov_discord::runner::{AccessPrimer, CommandRunner, RunOutcome, RunnerParts};
use pavlov_discord::slash::{CommandContext, CommandError, SlashCommand};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::CommandBlacklist;
use pavlov_host::recent_errors::RecentErrors;

const GUILD: u64 = 999;
const BARRED: u64 = 678_362_059_905_171_471;

#[derive(Clone, Copy, PartialEq)]
enum Behaviour {
    Succeed,
    Fail,
    Hang,
}

struct Probe {
    name: &'static str,
    ephemeral: bool,
    behaviour: Behaviour,
    runs: AtomicUsize,
}

impl Probe {
    fn new(name: &'static str, behaviour: Behaviour) -> Arc<Self> {
        Arc::new(Self {
            name,
            ephemeral: false,
            behaviour,
            runs: AtomicUsize::new(0),
        })
    }
}

impl SlashCommand for Probe {
    fn name(&self) -> &str {
        self.name
    }
    fn build(&self) -> CommandSpec {
        CommandSpec::new(self.name, "probe")
    }
    fn ephemeral(&self) -> bool {
        self.ephemeral
    }
    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            self.runs.fetch_add(1, Ordering::SeqCst);
            match self.behaviour {
                Behaviour::Succeed => context.reply(Reply::text("done")).await,
                Behaviour::Fail => Err(CommandError("rcon: /home/steam/secret path".into())),
                Behaviour::Hang => {
                    tokio::time::sleep(Duration::from_secs(3600)).await;
                    Ok(())
                }
            }
        })
    }
}

#[derive(Default)]
struct Primer {
    primed: Mutex<Vec<u64>>,
}

impl AccessPrimer for Primer {
    fn prime(&self, user_id: u64) -> BoxFuture<'_, ()> {
        self.primed.lock().unwrap().push(user_id);
        Box::pin(async {})
    }
}

#[derive(Default)]
struct Poster {
    sent: Mutex<Vec<Embed>>,
}

impl ChannelPoster for Poster {
    fn edit(&self, _: u64, _: u64, _: Embed, _: Vec<ActionRow>) -> BoxFuture<'_, PostEdit> {
        Box::pin(async { PostEdit::Failed })
    }
    fn send(
        &self,
        _: u64,
        embed: Embed,
        _: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        self.sent.lock().unwrap().push(embed);
        Box::pin(async { Ok(Some(1)) })
    }
}

struct Rig {
    runner: CommandRunner,
    metrics: Arc<MetricsRegistry>,
    errors: Arc<RecentErrors>,
    primer: Arc<Primer>,
    poster: Arc<Poster>,
    dispatcher: Arc<InteractionDispatcher>,
}

fn rig(commands: Vec<Arc<dyn SlashCommand>>) -> Rig {
    let metrics = Arc::new(MetricsRegistry::default());
    let errors = Arc::new(RecentErrors::new());
    let primer = Arc::new(Primer::default());
    let poster = Arc::new(Poster::default());
    let dispatcher = Arc::new(InteractionDispatcher::new());
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::default())));
    let blacklist = Arc::new(CommandBlacklist::new(store, Arc::new(|_| false), &[BARRED]));
    let runner = CommandRunner::new(RunnerParts {
        commands,
        blacklist: Some(blacklist),
        command_log: Some(Arc::new(CommandLog::new(
            Arc::clone(&poster) as Arc<dyn ChannelPoster>,
            5,
        ))),
        dispatcher: Arc::clone(&dispatcher),
        metrics: Arc::clone(&metrics),
        errors: Arc::clone(&errors),
        primer: Some(Arc::clone(&primer) as Arc<dyn AccessPrimer>),
        guild_installed: Arc::new(|_| true),
        budget: Duration::from_millis(100),
    });
    Rig {
        runner,
        metrics,
        errors,
        primer,
        poster,
        dispatcher,
    }
}

fn in_guild(command: &str, caller: Caller) -> Invocation {
    Invocation {
        guild_id: Some(GUILD),
        channel_id: 42,
        owners: IntegrationOwners {
            guild_install: Some(1),
            user_install: None,
        },
        ..Invocation::new(command, caller)
    }
}

#[tokio::test]
async fn a_command_is_deferred_publicly_then_run() {
    let probe = Probe::new("health", Behaviour::Succeed);
    let r = rig(vec![probe.clone()]);
    let responder = RecordingResponder::default();

    let outcome = r
        .runner
        .run(
            &in_guild("health", Caller::member(7, GUILD, &[1])),
            &responder,
        )
        .await;

    assert_eq!(outcome, RunOutcome::Completed);
    assert_eq!(responder.sent()[0], Sent::Defer { ephemeral: false });
    assert_eq!(probe.runs.load(Ordering::SeqCst), 1);
    assert!(r.metrics.render().contains("command_duration_ms"));
}

#[tokio::test]
async fn an_ephemeral_command_defers_privately() {
    let probe = Arc::new(Probe {
        ephemeral: true,
        ..Arc::into_inner(Probe::new("lookup", Behaviour::Succeed)).unwrap()
    });
    let r = rig(vec![probe]);
    let responder = RecordingResponder::default();

    r.runner
        .run(&in_guild("lookup", Caller::user(7)), &responder)
        .await;

    assert_eq!(responder.sent()[0], Sent::Defer { ephemeral: true });
}

#[tokio::test]
async fn a_user_install_in_a_foreign_guild_defers_privately() {
    let r = rig(vec![Probe::new("server", Behaviour::Succeed)]);
    let responder = RecordingResponder::default();
    let invocation = Invocation {
        owners: IntegrationOwners {
            guild_install: None,
            user_install: Some(7),
        },
        ..in_guild("server", Caller::user(7))
    };

    r.runner.run(&invocation, &responder).await;

    assert_eq!(responder.sent()[0], Sent::Defer { ephemeral: true });
}

#[tokio::test]
async fn a_barred_caller_is_refused_before_any_deferral_and_nothing_runs() {
    let probe = Probe::new("permban", Behaviour::Succeed);
    let r = rig(vec![probe.clone()]);
    let responder = RecordingResponder::default();

    let outcome = r
        .runner
        .run(&in_guild("permban", Caller::user(BARRED)), &responder)
        .await;

    assert_eq!(outcome, RunOutcome::Barred);
    assert!(matches!(
        responder.sent()[..],
        [Sent::Respond {
            ephemeral: true,
            ..
        }]
    ));
    assert_eq!(probe.runs.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn the_attempt_is_logged_even_when_it_is_refused() {
    let r = rig(vec![]);
    r.runner
        .run(
            &in_guild("permban", Caller::user(BARRED)),
            &RecordingResponder::default(),
        )
        .await;
    assert!(r.dispatcher.drain(Duration::from_secs(5)).await);

    let sent = r.poster.sent.lock().unwrap();
    assert_eq!(sent.len(), 1);
    assert!(sent[0].description.as_deref().unwrap().contains("/permban"));
}

#[tokio::test]
async fn an_unknown_command_is_told_so_privately() {
    let r = rig(vec![]);
    let responder = RecordingResponder::default();

    let outcome = r
        .runner
        .run(&in_guild("gone", Caller::user(7)), &responder)
        .await;

    assert_eq!(outcome, RunOutcome::Unknown);
    let Sent::Respond { reply, ephemeral } = &responder.sent()[0] else {
        panic!("expected a reply");
    };
    assert!(*ephemeral);
    assert_eq!(
        reply.content.as_deref(),
        Some("That command is no longer available.")
    );
}

#[tokio::test]
async fn a_failed_acknowledgement_runs_nothing() {
    let probe = Probe::new("health", Behaviour::Succeed);
    let r = rig(vec![probe.clone()]);

    let outcome = r
        .runner
        .run(
            &in_guild("health", Caller::user(7)),
            &RecordingResponder::failing(),
        )
        .await;

    assert_eq!(outcome, RunOutcome::NotAcknowledged);
    assert_eq!(probe.runs.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn a_failure_is_recorded_and_the_caller_gets_only_the_id() {
    let r = rig(vec![Probe::new("ban", Behaviour::Fail)]);
    let responder = RecordingResponder::default();

    let RunOutcome::Failed { correlation_id } = r
        .runner
        .run(&in_guild("ban", Caller::user(7)), &responder)
        .await
    else {
        panic!("expected a failure");
    };

    let text = responder.last_text();
    assert_eq!(
        text,
        format!("That didn't work. The error has been logged (`{correlation_id}`).")
    );
    assert!(!text.contains("/home/steam"));
    let recorded = r.errors.find(Some(&correlation_id)).unwrap();
    assert_eq!(recorded.operation, "/ban");
    assert_eq!(recorded.user_id, 7);
    assert!(
        r.metrics
            .render()
            .contains("command_errors_total{command=\"ban\"} 1")
    );
}

#[tokio::test]
async fn a_hung_command_is_abandoned_at_its_budget() {
    let r = rig(vec![Probe::new("serverswitch", Behaviour::Hang)]);
    let responder = RecordingResponder::default();

    let outcome = r
        .runner
        .run(&in_guild("serverswitch", Caller::user(7)), &responder)
        .await;

    let RunOutcome::TimedOut { correlation_id } = outcome else {
        panic!("expected a timeout, got {outcome:?}");
    };
    assert!(responder.last_text().contains(&format!(
        "That took too long and was stopped (`{correlation_id}`)"
    )));
}

#[tokio::test]
async fn only_a_caller_with_no_roles_is_primed() {
    let r = rig(vec![Probe::new("health", Behaviour::Succeed)]);

    r.runner
        .run(
            &in_guild("health", Caller::member(7, GUILD, &[1])),
            &RecordingResponder::default(),
        )
        .await;
    r.runner
        .run(
            &in_guild("health", Caller::member(8, GUILD, &[])),
            &RecordingResponder::default(),
        )
        .await;
    r.runner
        .run(
            &in_guild("health", Caller::user(9)),
            &RecordingResponder::default(),
        )
        .await;

    assert_eq!(*r.primer.primed.lock().unwrap(), [8, 9]);
}

#[test]
fn correlation_ids_are_eight_hex_digits_and_differ() {
    let a = pavlov_discord::runner::new_correlation_id();
    let b = pavlov_discord::runner::new_correlation_id();
    assert_eq!(a.len(), 8);
    assert!(a.chars().all(|c| c.is_ascii_hexdigit()));
    assert_ne!(a, b);
}
