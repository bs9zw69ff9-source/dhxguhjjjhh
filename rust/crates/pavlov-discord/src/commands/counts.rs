//! `/counts` (C# `CountsCommand.cs`): run the player-count channel update now and say what
//! happened. It runs the SAME code the five-minute timer runs, not a simulation of it.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;

use crate::access::{Access, RequiredAccess};
use crate::command::CommandSpec;
use crate::interaction::Reply;
use crate::player_count::{PlayerCountChannels, Targets, missing_half};
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// What the command is wired to.
pub struct CountsCommand {
    pub counts: Arc<PlayerCountChannels>,
    pub targets: Targets,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for CountsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CountsCommand").finish_non_exhaustive()
    }
}

/// The run's report, with the likely causes of a failure and the half not configured.
#[must_use]
pub fn report(targets: &Targets, lines: &[String]) -> Embed {
    let failed = lines.iter().filter(|r| r.contains("FAILED")).count();
    let body: Vec<String> = lines
        .iter()
        .map(|r| format!("{} {r}", theme::DOT))
        .collect();
    let body = body.join("\n");
    let mut embed = if failed > 0 {
        theme::failure(
            &format!(
                "{failed} of {} channel(s) could not be updated",
                lines.len()
            ),
            Some(&body),
        )
        .field(
            "Most likely",
            "**needs Manage Channels** — on each voice channel, not just the server.\n\
             **not visible** — wrong id, or the bot cannot see that channel.",
            false,
        )
    } else {
        theme::success("Player-count channels updated", Some(&body))
    };
    // A report cannot mention a channel that was never configured, so half a setup would
    // otherwise show a green tick over half the feature.
    if let Some((title, text)) = missing_half(targets) {
        embed = embed.field(title, text, false);
    }
    embed.footer("Discord allows 2 renames per 10 minutes per channel")
}

impl SlashCommand for CountsCommand {
    fn name(&self) -> &str {
        "counts"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("counts", "Mod - Update the player-count voice channels now")
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if !self.access.allows(RequiredAccess::Mod, caller) {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Mod)),
                )
            } else if self.targets.server_channels.is_empty()
                && self.targets.total_channel.is_none()
            {
                theme::warning(
                    "No count channels configured",
                    Some(
                        "Set these, then restart. Channel ids go in server order.\n\
                         ```\nPLAYER_COUNT_CHANNELS=111,222,333\nSHACK_TOTAL_CHANNEL=444\n```",
                    ),
                )
            } else {
                report(&self.targets, &self.counts.run(&self.targets).await)
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
