//! `/feeds` (C# `FeedsCommand.cs`): whether the join, connect, kill, RCON and staff webhooks
//! are working. `test:true` posts to each, because a URL can be present, well-formed and
//! belong to a deleted channel; and a sample CONNECTION CARD goes to `connect`, since cards
//! take a different path from lines and a line-only test would miss a rejected embed.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use indexmap::IndexMap;
use pavlov_core::cards::connect::{ConnectCardInput, build as connect_card};
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::time::{Clock, EasternTime};
use pavlov_host::feeds::{self, FeedWebhooks};

use crate::access::{Access, RequiredAccess};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// One feed: its label, its setting and what it carries. The join/connect split is spelled
/// out because getting it backwards publishes player addresses.
pub const FEEDS: [(&str, &str, &str); 5] = [
    (
        feeds::JOIN,
        "JOIN_WEBHOOK_URL",
        "Join and leave lines. No addresses.",
    ),
    (
        feeds::CONNECT,
        "CONNECT_WEBHOOK_URL",
        "Connection cards WITH IP ADDRESSES, plus auto-ban notices. Private channel only.",
    ),
    (feeds::KILL, "KILL_WEBHOOK_URL", "Kill lines."),
    (
        feeds::RCON,
        "RCON_WEBHOOK_URL",
        "Every RCON command from Pavlov.log. Private channel only.",
    ),
    (
        feeds::STAFF,
        "STAFF_WEBHOOK_URL",
        "Every staff action. Private channel only.",
    ),
];

/// What the command is wired to.
pub struct FeedsCommand {
    pub feeds: Arc<FeedWebhooks>,
    pub access: Arc<Access>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for FeedsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FeedsCommand").finish_non_exhaustive()
    }
}

fn mark(status: &str) -> &'static str {
    if status.starts_with("delivering") {
        theme::OK
    } else if status.starts_with("not configured") {
        theme::DOT
    } else if status.starts_with("configured") || status.starts_with("open") {
        theme::WARN
    } else {
        theme::BAD
    }
}

/// The status report, from the feeds' own status lines.
#[must_use]
pub fn report(status: &IndexMap<String, String>, tested: bool) -> Embed {
    let lines: Vec<String> = FEEDS
        .iter()
        .map(|(label, variable, carries)| {
            let state = status.get(*label).map_or("not registered", String::as_str);
            format!(
                "{} **{label}** — `{variable}`\n{} {state}\n{} *{carries}*",
                mark(state),
                theme::DOT,
                theme::DOT
            )
        })
        .collect();
    let embed = theme::notice("Feed webhooks", Some(&lines.join("\n\n")));
    if tested {
        embed.field(
            "Test sent",
            "A line to every feed and a sample card to `connect`. Anything still *configured* did not accept it.",
            false,
        )
    } else {
        embed.footer("/feeds test:true posts a test line to each feed")
    }
}

impl FeedsCommand {
    fn test(&self) {
        let now = self.clock.now();
        let stamp = EasternTime::stamp(Some(now));
        for (label, _, _) in FEEDS {
            self.feeds.post(
                label,
                &format!("[{stamp}] Feed test from /feeds - if you can read this, {label} works"),
            );
        }
        // RFC 5737 documentation address: never a real player's.
        let card = connect_card(&ConnectCardInput {
            name: "feed-test",
            account_id: "0000000000000000",
            ip: Some("203.0.113.1"),
            confident_ip: true,
            server: "test",
            account: None,
            alts: &[],
            vpn: None,
            flagged: false,
            master: false,
            at: now,
        });
        self.feeds.post_embed(feeds::CONNECT, card);
    }
}

impl SlashCommand for FeedsCommand {
    fn name(&self) -> &str {
        "feeds"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "feeds",
            "Mod - Check whether the join, connect, kill and money webhooks are working",
        )
        .option(OptionSpec::new(
            OptionKind::Boolean,
            "test",
            "Post a test line to every configured feed",
        ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::Mod, caller) {
                let tested = context.invocation.boolean("test") == Some(true);
                if tested {
                    self.test();
                }
                report(&self.feeds.status(), tested)
            } else {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Mod)),
                )
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
