//! Port of `CommandLogTests.cs`: what each slash command attempt becomes in
//! `COMMAND_LOG_CHANNEL`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::command_log::{self, CommandInvocation, CommandLog, MAX_LINE_LENGTH};
use pavlov_discord::component::ActionRow;
use pavlov_discord::interaction::{DiscordError, Invocation, InvokedOption, OptionValue};
use pavlov_discord::post::{ChannelPoster, PostEdit};

#[derive(Default)]
struct FakeTarget {
    sent: Mutex<Vec<(u64, Embed)>>,
    fails: bool,
}

impl ChannelPoster for FakeTarget {
    fn edit(&self, _: u64, _: u64, _: Embed, _: Vec<ActionRow>) -> BoxFuture<'_, PostEdit> {
        panic!("the command log never edits")
    }

    fn send(
        &self,
        channel_id: u64,
        embed: Embed,
        _: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        let fails = self.fails;
        if !fails {
            self.sent.lock().unwrap().push((channel_id, embed));
        }
        Box::pin(async move {
            if fails {
                Err(DiscordError("channel gone".into()))
            } else {
                Ok(Some(1))
            }
        })
    }
}

fn purge_at(line: &str, channel: Option<u64>, guild: Option<&str>) -> CommandInvocation {
    CommandInvocation {
        user_id: 7,
        user_name: "fki6".into(),
        avatar_url: Some("https://cdn.example/a.png".into()),
        command: "purge".into(),
        line: line.into(),
        channel_id: channel,
        guild_name: guild.map(str::to_owned),
        at: DateTime::<Utc>::UNIX_EPOCH,
    }
}

fn purge(line: &str) -> CommandInvocation {
    purge_at(line, Some(42), Some("Nuclear Roleplay"))
}

fn leaf(name: &str, kind: OptionKind, value: OptionValue) -> InvokedOption {
    InvokedOption::value(name, kind, value)
}

fn description(embed: &Embed) -> &str {
    embed.description.as_deref().unwrap()
}

#[test]
fn leaf_options_are_key_colon_value() {
    let invocation = Invocation::new("purge", Caller::user(1)).with(leaf(
        "count",
        OptionKind::Integer,
        OptionValue::Integer(6),
    ));
    assert_eq!(command_log::describe(&invocation), "/purge count:6");
}

#[test]
fn no_options_is_just_the_name() {
    assert_eq!(
        command_log::describe(&Invocation::new("health", Caller::user(1))),
        "/health"
    );
}

#[test]
fn sub_commands_and_groups_are_words() {
    let group = InvokedOption {
        kind: OptionKind::SubCommandGroup,
        ..InvokedOption::sub(
            "faction",
            vec![InvokedOption::sub(
                "set",
                vec![
                    leaf(
                        "player",
                        OptionKind::String,
                        OptionValue::String("Bob".into()),
                    ),
                    leaf(
                        "rank",
                        OptionKind::String,
                        OptionValue::String("Sergeant".into()),
                    ),
                ],
            )],
        )
    };
    let invocation = Invocation::new("config", Caller::user(1)).with(group);
    assert_eq!(
        command_log::describe(&invocation),
        "/config faction set player:Bob rank:Sergeant"
    );
}

#[test]
fn numbers_use_the_invariant_culture() {
    // Rust formatting has no culture: a decimal point, always.
    let invocation = Invocation::new("pay", Caller::user(1)).with(leaf(
        "amount",
        OptionKind::Number,
        OptionValue::Number(1.5),
    ));
    assert_eq!(command_log::describe(&invocation), "/pay amount:1.5");
}

#[test]
fn embed_names_the_caller_the_command_and_the_channel() {
    let embed = command_log::build(&purge("/purge count:6"));

    assert_eq!(embed.author.as_deref(), Some("fki6"));
    assert!(description(&embed).contains("<@7> used `/purge` command in <#42>"));
    assert!(description(&embed).contains("/purge count:6"));
    assert!(
        embed
            .footer
            .as_deref()
            .unwrap()
            .contains("Nuclear Roleplay")
    );
    assert_eq!(embed.timestamp, Some(DateTime::<Utc>::UNIX_EPOCH));
}

#[test]
fn typed_markdown_cannot_format_the_log() {
    let embed = command_log::build(&purge("/say text:**everyone** `x`"));
    assert!(!description(&embed).contains("**everyone**"));
}

#[test]
fn unicode_is_kept_as_typed() {
    let embed = command_log::build(&purge_at(
        "/say text:caf\u{e9} \u{2622}",
        Some(42),
        Some("Nuclear Roleplay\u{2122}"),
    ));
    assert!(description(&embed).contains("caf\u{e9} \u{2622}"));
    assert!(
        embed
            .footer
            .as_deref()
            .unwrap()
            .contains("Nuclear Roleplay\u{2122}")
    );
}

#[test]
fn a_wall_of_text_is_capped() {
    let embed = command_log::build(&purge(&format!("/say text:{}", "a".repeat(5000))));
    assert!(description(&embed).chars().count() < MAX_LINE_LENGTH + 200);
    assert!(description(&embed).ends_with('…'));
}

#[tokio::test]
async fn posts_to_the_configured_channel() {
    let target = Arc::new(FakeTarget::default());
    let log = CommandLog::new(
        Arc::clone(&target) as Arc<dyn ChannelPoster>,
        1_553_558_500_309_729_330,
    );

    log.post(&purge("/purge count:6")).await;

    let sent = target.sent.lock().unwrap();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].0, 1_553_558_500_309_729_330);
}

#[tokio::test]
async fn a_delivery_failure_never_reaches_the_command() {
    let target = Arc::new(FakeTarget {
        fails: true,
        ..FakeTarget::default()
    });
    // `post` returns nothing to fail with: the type is the guarantee, the call the check.
    CommandLog::new(target, 1)
        .post(&purge("/purge count:6"))
        .await;
}
