//! `/errors` (C# `ErrorsCommand.cs`): the last few command failures, by the id the caller was
//! shown. Owner only and always ephemeral: a trace names internal types and paths.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::recent_errors::{CAPACITY, RecentErrors};

use crate::access::{Access, RequiredAccess};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Discord's hard limit on an embed field value.
const FIELD_LIMIT: usize = 1024;
/// How many of the innermost trace lines are shown.
const TRACE_FRAMES: usize = 14;

/// What the command is wired to.
pub struct ErrorsCommand {
    pub errors: Arc<RecentErrors>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for ErrorsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ErrorsCommand").finish_non_exhaustive()
    }
}

/// The END of a trace, not the start: the lines nearest the failure name it, the outer ones
/// are the dispatcher and the same for every failure.
#[must_use]
pub fn innermost(trace: &str) -> String {
    let frames: Vec<&str> = trace.split('\n').collect();
    let joined = frames[frames.len().saturating_sub(TRACE_FRAMES)..].join("\n");
    let trimmed = joined.trim();
    let limit = FIELD_LIMIT - 20;
    let count = trimmed.chars().count();
    if count > limit {
        trimmed.chars().skip(count - limit).collect()
    } else {
        trimmed.to_owned()
    }
}

impl ErrorsCommand {
    #[must_use]
    pub fn list(&self) -> Embed {
        let all = self.errors.all();
        if all.is_empty() {
            return theme::success(
                "No recent failures",
                Some(
                    "Nothing has thrown since the last restart.\n\nThis list is in memory, so a restart clears it.",
                ),
            );
        }
        let lines: Vec<String> = all
            .iter()
            .map(|e| {
                format!(
                    "{} `{}` **{}** - {} <t:{}:R>",
                    theme::DOT,
                    e.correlation_id,
                    Sanitize::code(Some(&e.operation)),
                    Sanitize::code(Some(&e.kind)),
                    e.at.timestamp()
                )
            })
            .collect();
        theme::notice(
            &format!("{} recent failure(s)", all.len()),
            Some(&lines.join("\n")),
        )
        .field(
            "Next",
            "`/errors id:<the id>` for the message and stack trace.",
            false,
        )
    }

    #[must_use]
    pub fn detail(&self, id: &str) -> Embed {
        let Some(error) = self.errors.find(Some(id)) else {
            return theme::failure(
                "No such error",
                Some(&format!(
                    "Nothing recorded under `{}`. Only the last {CAPACITY} are kept, and a restart clears them.\n\n\
                     Run `/errors` on its own to see what is left.",
                    Sanitize::code(Some(id))
                )),
            );
        };
        theme::failure(
            &format!("{} - {}", error.operation, error.kind),
            Some(&Sanitize::code(Some(&error.message))),
        )
        .field("When", format!("<t:{}:F>", error.at.timestamp()), true)
        .field("Caller", format!("<@{}>", error.user_id), true)
        .field(
            "Trace (innermost frames)",
            format!("```\n{}\n```", innermost(&error.trace)),
            false,
        )
    }
}

impl SlashCommand for ErrorsCommand {
    fn name(&self) -> &str {
        "errors"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "errors",
            "Owner - Recent command failures, with the stack trace",
        )
        .option(OptionSpec::new(
            OptionKind::String,
            "id",
            "The id from the failure message. Omit to list recent failures.",
        ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if !self.access.allows(RequiredAccess::Owner, caller) {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Owner)),
                )
            } else {
                match context
                    .invocation
                    .string("id")
                    .map(str::trim)
                    .filter(|i| !i.is_empty())
                {
                    None => self.list(),
                    Some(id) => self.detail(id),
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
