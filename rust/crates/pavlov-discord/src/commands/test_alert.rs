//! `/testalert` (C# `TestAlertCommand.cs`): send a sample security alert and say who actually
//! received it.
//!
//! A DM to somebody with DMs closed, or who shares no server with the bot, is ACCEPTED by
//! Discord and never delivered, so "configured" and "works" are different claims. This goes
//! through the SAME sink the detectors use, so an empty recipient list or a sink never
//! attached shows here instead of a private test path quietly passing.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::time::Clock;
use pavlov_host::moderation::alerts::{
    AlertDelivery, SecurityAlert, SecurityAlertKind, SecurityAlerts,
};

use crate::access::{Access, RequiredAccess};
use crate::command::CommandSpec;
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// What the command is wired to.
pub struct TestAlertCommand {
    pub alerts: Arc<SecurityAlerts>,
    /// `SECURITY_DM_IDS`, or the owners when it is unset.
    pub recipients: Vec<u64>,
    pub access: Arc<Access>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for TestAlertCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TestAlertCommand")
            .field("recipients", &self.recipients.len())
            .finish_non_exhaustive()
    }
}

/// The report on who the sample reached.
#[must_use]
pub fn report(results: &[AlertDelivery]) -> Embed {
    let delivered = results.iter().filter(|r| r.delivered).count();
    let lines: Vec<String> = results
        .iter()
        .map(|r| {
            if r.delivered {
                format!("{} <@{}> — delivered", theme::OK, r.user)
            } else {
                format!(
                    "{} <@{}> — {}",
                    theme::BAD,
                    r.user,
                    r.problem.as_deref().unwrap_or("not delivered")
                )
            }
        })
        .collect();
    let body = lines.join("\n");
    if delivered == results.len() {
        return theme::success(
            &format!("Test alert delivered to {delivered} of {}", results.len()),
            Some(&body),
        );
    }
    theme::failure(&format!("Test alert reached {delivered} of {}", results.len()), Some(&body)).field(
        "Why a DM does not arrive",
        "**Closed DMs** — Privacy Settings → *Direct Messages* must allow messages from server members, for a \
         server this bot is in.\n**No shared server** — the bot can only DM somebody it shares a server with.",
        false,
    )
}

impl TestAlertCommand {
    async fn run(&self) -> Embed {
        if !self.alerts.enabled() || self.recipients.is_empty() {
            return theme::warning(
                "Nobody is set to receive security alerts",
                Some(
                    "Set `SECURITY_DM_IDS` to the Discord user id(s) that should get them and restart.\n\n\
                     ```\nSECURITY_DM_IDS=307052224087851009\n```\n\
                     With it unset the owners get them, and with no owners configured either, nothing is sent.",
                ),
            );
        }
        let sample = SecurityAlert {
            kind: SecurityAlertKind::Evasion,
            player: "TestSubject".into(),
            headline: "This is a test. No player matched anything.".into(),
            action: "Nothing - `/testalert` sent this to prove the alerts reach you.".into(),
            account_id: Some("0002913af4f445df86da1be6a2a01728".into()),
            // RFC 5737 documentation address: never a real player's.
            ip: Some("203.0.113.9".into()),
            server: Some("server1".into()),
            alts: vec!["TestSubject_alt".into()],
            at: Some(self.clock.now()),
        };
        report(&self.alerts.post(&sample).await)
    }
}

impl SlashCommand for TestAlertCommand {
    fn name(&self) -> &str {
        "testalert"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "testalert",
            "Owner - Send a test security alert and report who received it",
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::Owner, caller) {
                self.run().await
            } else {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Owner)),
                )
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
