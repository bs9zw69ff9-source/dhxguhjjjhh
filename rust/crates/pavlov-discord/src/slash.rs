//! What a slash command is (C# `ISlashCommand`), and `COMMANDS_DISABLED` applied to whole
//! commands and to single subcommands (C# `SubcommandFilter.cs`).

use std::fmt;
use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::skin::theme;
use pavlov_core::text::case::{IgnoreCaseMap, IgnoreCaseSet, eq_ignore_case};

use crate::command::CommandSpec;
use crate::interaction::{DiscordError, Invocation, Reply, Responder};

/// Why a command failed. The caller is shown a correlation id, never this text: it can carry
/// paths and internal detail that do not belong in a public channel.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommandError(pub String);

impl fmt::Display for CommandError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for CommandError {}

impl From<DiscordError> for CommandError {
    fn from(error: DiscordError) -> Self {
        Self(format!("discord: {error}"))
    }
}

impl From<pavlov_core::data::StoreError> for CommandError {
    fn from(error: pavlov_core::data::StoreError) -> Self {
        Self(format!("store: {error}"))
    }
}

/// What a command is handed: the invocation, and how to answer it.
#[derive(Clone, Copy)]
pub struct CommandContext<'a> {
    pub invocation: &'a Invocation,
    pub responder: &'a dyn Responder,
}

impl fmt::Debug for CommandContext<'_> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("CommandContext")
            .field("command", &self.invocation.command)
            .finish_non_exhaustive()
    }
}

impl CommandContext<'_> {
    /// Replace the deferred answer.
    ///
    /// # Errors
    /// When Discord refuses the edit.
    pub async fn reply(&self, reply: Reply) -> Result<(), CommandError> {
        Ok(self.responder.edit_original(reply).await?)
    }
}

/// A slash command.
pub trait SlashCommand: Send + Sync {
    fn name(&self) -> &str;
    fn build(&self) -> CommandSpec;
    /// Whether every reply is private. A command that shows a player's addresses or a staff
    /// tool's state must be.
    fn ephemeral(&self) -> bool {
        false
    }
    /// Run it. The interaction is ALREADY DEFERRED: answer with [`CommandContext::reply`].
    fn handle<'a>(&'a self, context: CommandContext<'a>)
    -> BoxFuture<'a, Result<(), CommandError>>;
}

/// `COMMANDS_DISABLED`, split into whole commands (`arrest`) and single subcommands
/// (`whitelist playtime`). Case and spacing do not matter.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DisabledCommandList {
    pub commands: IgnoreCaseSet,
    pub subcommands: IgnoreCaseMap<IgnoreCaseSet>,
}

impl DisabledCommandList {
    #[must_use]
    pub fn parse<S: AsRef<str>>(entries: &[S]) -> Self {
        let mut list = Self::default();
        for entry in entries {
            let words: Vec<&str> = entry.as_ref().split_whitespace().collect();
            match words.as_slice() {
                [command] => {
                    list.commands.insert(*command);
                }
                [command, sub] => {
                    let mut set = list.subcommands.remove(command).unwrap_or_default();
                    set.insert(*sub);
                    list.subcommands.insert(*command, set);
                }
                _ => {}
            }
        }
        list
    }
}

/// A command with some of its subcommands switched off: they are not registered, and a stale
/// client that still sends one is told so rather than running it.
pub struct SubcommandFilter {
    inner: Arc<dyn SlashCommand>,
    disabled: IgnoreCaseSet,
}

impl fmt::Debug for SubcommandFilter {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("SubcommandFilter")
            .field("command", &self.inner.name())
            .finish_non_exhaustive()
    }
}

impl SlashCommand for SubcommandFilter {
    fn name(&self) -> &str {
        self.inner.name()
    }

    fn ephemeral(&self) -> bool {
        self.inner.ephemeral()
    }

    fn build(&self) -> CommandSpec {
        let mut spec = self.inner.build();
        spec.options
            .retain(|o| !(o.kind.is_subcommand() && self.disabled.contains(&o.name)));
        spec
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            if let Some(sub) = context
                .invocation
                .subcommand()
                .filter(|s| self.disabled.contains(s))
            {
                let embed = theme::denied(
                    "Not available",
                    Some(&format!(
                        "`/{} {sub}` is switched off on this bot.",
                        self.name()
                    )),
                );
                return context.reply(Reply::embed(embed)).await;
            }
            self.inner.handle(context).await
        })
    }
}

/// The subcommand names a command offers.
#[must_use]
pub fn subcommands_of(command: &dyn SlashCommand) -> IgnoreCaseSet {
    command.build().subcommands().into_iter().collect()
}

/// Apply `COMMANDS_DISABLED`: whole commands dropped, subcommands filtered (a command left with
/// none dropped whole), an untouched command returned as it was. Entries naming nothing are
/// returned in `unknown`, so a typo is reported rather than looking like an ignored setting.
#[must_use]
pub fn apply(
    all: &[Arc<dyn SlashCommand>],
    list: &DisabledCommandList,
) -> (Vec<Arc<dyn SlashCommand>>, Vec<String>) {
    let known = |name: &str| all.iter().any(|c| eq_ignore_case(c.name(), name));
    let mut unknown: Vec<String> = list
        .commands
        .iter()
        .filter(|name| !known(name))
        .map(str::to_owned)
        .collect();

    let mut kept: Vec<Arc<dyn SlashCommand>> = Vec::new();
    for command in all {
        if list.commands.contains(command.name()) {
            continue;
        }
        let Some(off) = list.subcommands.get(command.name()) else {
            kept.push(Arc::clone(command));
            continue;
        };
        let offered = subcommands_of(command.as_ref());
        unknown.extend(
            off.iter()
                .filter(|s| !offered.contains(s))
                .map(|s| format!("{} {s}", command.name())),
        );
        if !offered.is_empty() && offered.iter().all(|s| off.contains(s)) {
            continue;
        }
        kept.push(Arc::new(SubcommandFilter {
            inner: Arc::clone(command),
            disabled: off.clone(),
        }));
    }
    for (name, subs) in list.subcommands.iter().filter(|(name, _)| !known(name)) {
        unknown.extend(subs.iter().map(|s| format!("{name} {s}")));
    }
    let mut seen = IgnoreCaseSet::new();
    unknown.retain(|u| seen.insert(u.as_str()));
    (kept, unknown)
}
