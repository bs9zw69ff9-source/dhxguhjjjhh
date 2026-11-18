//! Every slash command anyone runs, posted to `COMMAND_LOG_CHANNEL` (C# `CommandLog.cs`).
//!
//! NOT THE STAFF LOG. That records what an action DID, once it succeeded. This records what
//! was TYPED, by whom and where, on arrival: before the bar list, the permission checks or the
//! handler have had a say, so a refused, failed or timed-out command is logged exactly like
//! one that worked. Posting is a REST call, so the runner hands it to the dispatcher instead
//! of awaiting it in front of the three-second acknowledgement.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use pavlov_core::embed::{Embed, palette};
use pavlov_core::text::Sanitize;

use crate::interaction::{Invocation, InvokedOption, OptionValue};
use crate::post::ChannelPoster;

/// Long enough for any real invocation; short enough that a pasted wall of text in a string
/// option cannot push the embed past Discord's limits.
pub const MAX_LINE_LENGTH: usize = 1000;

const HAMMER: &str = "🔨";

/// One slash command as it was typed, captured the moment it arrived.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommandInvocation {
    pub user_id: u64,
    pub user_name: String,
    pub avatar_url: Option<String>,
    /// The top-level command name, without the slash.
    pub command: String,
    /// The full invocation, e.g. `/purge count:6`.
    pub line: String,
    pub channel_id: Option<u64>,
    /// The server it was run in, or `None` for a DM.
    pub guild_name: Option<String>,
    pub at: DateTime<Utc>,
}

impl CommandInvocation {
    /// Capture an invocation as it arrived.
    #[must_use]
    pub fn capture(invocation: &Invocation, at: DateTime<Utc>) -> Self {
        Self {
            user_id: invocation.caller.id,
            user_name: invocation.username.clone(),
            avatar_url: invocation.avatar_url.clone(),
            command: invocation.command.clone(),
            line: describe(invocation),
            channel_id: (invocation.channel_id != 0).then_some(invocation.channel_id),
            guild_name: invocation.guild_name.clone(),
            at,
        }
    }
}

/// The invocation as the user typed it: `/name group sub key:value key:value`. Subcommands
/// are WORDS, the way the picker shows them; users, roles and channels render by name, since a
/// mention in this line would ping.
#[must_use]
pub fn describe(invocation: &Invocation) -> String {
    let mut line = format!("/{}", invocation.command);
    append(&mut line, &invocation.options, invocation);
    line
}

fn append(line: &mut String, options: &[InvokedOption], invocation: &Invocation) {
    for option in options {
        line.push(' ');
        line.push_str(&option.name);
        if option.kind.is_subcommand() {
            append(line, &option.options, invocation);
            continue;
        }
        line.push(':');
        line.push_str(&render(option, invocation));
    }
}

fn render(option: &InvokedOption, invocation: &Invocation) -> String {
    let name_of = |map: &std::collections::HashMap<u64, String>, id: &u64| {
        map.get(id).cloned().unwrap_or_else(|| id.to_string())
    };
    let user = |id: &u64| {
        invocation
            .resolved_users
            .get(id)
            .map(|u| format!("@{}", u.username))
    };
    match &option.value {
        None => String::new(),
        Some(OptionValue::String(s)) => s.clone(),
        Some(OptionValue::Integer(i)) => i.to_string(),
        Some(OptionValue::Number(n)) => n.to_string(),
        // .NET's bool.ToString().
        Some(OptionValue::Boolean(b)) => if *b { "True" } else { "False" }.to_owned(),
        Some(OptionValue::User(id)) => user(id).unwrap_or_else(|| format!("@{id}")),
        Some(OptionValue::Role(id)) => format!("@{}", name_of(&invocation.resolved_roles, id)),
        Some(OptionValue::Channel(id)) => {
            format!("#{}", name_of(&invocation.resolved_channels, id))
        }
        Some(OptionValue::Mentionable(id)) => {
            user(id).unwrap_or_else(|| format!("@{}", name_of(&invocation.resolved_roles, id)))
        }
        Some(OptionValue::Attachment(file)) => file.filename.clone(),
    }
}

/// Typed text made inert in a description: markdown escaped, line breaks flattened. NOT the
/// RCON sanitiser, which strips non-ASCII and stops at 200 characters: right for a broadcast,
/// wrong for a record of what somebody actually typed.
#[must_use]
pub fn escape(text: &str) -> String {
    let mut escaped = String::with_capacity(text.len() + 16);
    for c in text.chars() {
        match c {
            '\\' | '*' | '_' | '~' | '`' | '|' | '[' | ']' | '>' | '#' => {
                escaped.push('\\');
                escaped.push(c);
            }
            '\r' | '\n' => escaped.push(' '),
            _ => escaped.push(c),
        }
    }
    escaped
}

/// The embed one invocation becomes. The line is the one part a user controls end to end, so
/// it is escaped and capped; the author and channel are ids Discord renders.
#[must_use]
pub fn build(invocation: &CommandInvocation) -> Embed {
    let line = if invocation.line.chars().count() > MAX_LINE_LENGTH {
        let mut cut: String = invocation.line.chars().take(MAX_LINE_LENGTH).collect();
        cut.push('…');
        cut
    } else {
        invocation.line.clone()
    };
    let place = match invocation.channel_id {
        Some(channel) => format!(" in <#{channel}>"),
        None => " in a DM".to_owned(),
    };
    let mut embed = Embed::new()
        .color(palette::BLUE)
        .author(invocation.user_name.clone())
        .description(format!(
            "{HAMMER} <@{}> used `/{}` command{place}\n{}",
            invocation.user_id,
            Sanitize::code(Some(&invocation.command)),
            escape(&line)
        ));
    if let Some(avatar) = &invocation.avatar_url {
        embed = embed.author_icon(avatar.clone());
    }
    // Footers are not markdown, so the server name goes in as it is.
    let guild = invocation
        .guild_name
        .as_deref()
        .filter(|g| !g.is_empty())
        .unwrap_or("Direct message");
    // AFTER brand, which clears the timestamp. It renders in each viewer's own time zone.
    embed.brand(Some(guild)).timestamp(invocation.at)
}

/// The command log.
pub struct CommandLog {
    target: Arc<dyn ChannelPoster>,
    channel_id: u64,
}

impl std::fmt::Debug for CommandLog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CommandLog")
            .field("channel_id", &self.channel_id)
            .finish_non_exhaustive()
    }
}

impl CommandLog {
    #[must_use]
    pub fn new(target: Arc<dyn ChannelPoster>, channel_id: u64) -> Self {
        Self { target, channel_id }
    }

    /// Post one invocation. NEVER fails: a log line that could not be delivered must not
    /// touch the command, so a failure is logged and dropped here.
    pub async fn post(&self, invocation: &CommandInvocation) {
        if let Err(error) = self
            .target
            .send(self.channel_id, build(invocation), Vec::new())
            .await
        {
            tracing::warn!(
                command = %invocation.command,
                channel = self.channel_id,
                %error,
                "could not post to the command log channel"
            );
        }
    }
}
