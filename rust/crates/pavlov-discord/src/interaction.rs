//! A slash command as it arrived, and the way to answer it, as plain values and one trait.
//!
//! [`Invocation`] is the C# `SocketSlashCommand`'s data: the command, its (possibly nested)
//! options, the caller and where they are. [`Responder`] is how a command answers; the gateway
//! adapter implements it over Discord's REST calls, and the tests implement it by recording.

use std::collections::HashMap;
use std::fmt;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;

use crate::caller::Caller;
use crate::command::{Choice, OptionKind};
use crate::component::ActionRow;

/// An option's value.
#[derive(Debug, Clone, PartialEq)]
pub enum OptionValue {
    String(String),
    Integer(i64),
    Number(f64),
    Boolean(bool),
    User(u64),
    Channel(u64),
    Role(u64),
    Mentionable(u64),
    Attachment(Attachment),
}

/// An uploaded file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Attachment {
    pub filename: String,
    pub url: String,
    pub size: u64,
    pub content_type: Option<String>,
}

/// One option: a value, or a subcommand carrying its own options.
#[derive(Debug, Clone, PartialEq)]
pub struct InvokedOption {
    pub name: String,
    pub kind: OptionKind,
    pub value: Option<OptionValue>,
    pub options: Vec<InvokedOption>,
}

impl InvokedOption {
    #[must_use]
    pub fn value(name: impl Into<String>, kind: OptionKind, value: OptionValue) -> Self {
        Self {
            name: name.into(),
            kind,
            value: Some(value),
            options: Vec::new(),
        }
    }

    #[must_use]
    pub fn sub(name: impl Into<String>, options: Vec<InvokedOption>) -> Self {
        Self {
            name: name.into(),
            kind: OptionKind::SubCommand,
            value: None,
            options,
        }
    }
}

/// A user an option resolved to: what Discord sent alongside the id.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ResolvedUser {
    pub id: u64,
    pub username: String,
    pub global_name: Option<String>,
    pub bot: bool,
}

/// Which installs an interaction came through (Discord's `authorizing_integration_owners`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct IntegrationOwners {
    /// The guild install's owner, when the interaction came through it.
    pub guild_install: Option<u64>,
    /// The user install's owner, when it came through the caller's own install.
    pub user_install: Option<u64>,
}

impl IntegrationOwners {
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.guild_install.is_none() && self.user_install.is_none()
    }
}

/// A slash command as it arrived.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Invocation {
    pub command: String,
    pub options: Vec<InvokedOption>,
    pub caller: Caller,
    pub username: String,
    pub avatar_url: Option<String>,
    pub guild_id: Option<u64>,
    pub guild_name: Option<String>,
    pub channel_id: u64,
    pub resolved_users: HashMap<u64, ResolvedUser>,
    /// Role and channel names Discord resolved alongside their ids.
    pub resolved_roles: HashMap<u64, String>,
    pub resolved_channels: HashMap<u64, String>,
    pub owners: IntegrationOwners,
}

impl Invocation {
    /// A command with no options, from `caller`.
    #[must_use]
    pub fn new(command: impl Into<String>, caller: Caller) -> Self {
        Self {
            command: command.into(),
            caller,
            ..Self::default()
        }
    }

    #[must_use]
    pub fn with(mut self, option: InvokedOption) -> Self {
        self.options.push(option);
        self
    }

    /// The subcommand (or group) chosen, if the command has them.
    #[must_use]
    pub fn subcommand(&self) -> Option<&str> {
        self.options
            .iter()
            .find(|o| o.kind.is_subcommand())
            .map(|o| o.name.as_str())
    }

    /// The subcommand path, outermost first: `["group", "sub"]`.
    #[must_use]
    pub fn subcommand_path(&self) -> Vec<&str> {
        let mut path = Vec::new();
        let mut level = &self.options;
        while let Some(sub) = level.iter().find(|o| o.kind.is_subcommand()) {
            path.push(sub.name.as_str());
            level = &sub.options;
        }
        path
    }

    /// The value options at the deepest level (inside the chosen subcommand).
    #[must_use]
    pub fn values(&self) -> &[InvokedOption] {
        let mut level = &self.options;
        while let Some(sub) = level.iter().find(|o| o.kind.is_subcommand()) {
            level = &sub.options;
        }
        level
    }

    /// A value option by name.
    #[must_use]
    pub fn get(&self, name: &str) -> Option<&OptionValue> {
        self.values()
            .iter()
            .find(|o| o.name == name)
            .and_then(|o| o.value.as_ref())
    }

    #[must_use]
    pub fn string(&self, name: &str) -> Option<&str> {
        match self.get(name)? {
            OptionValue::String(s) => Some(s),
            _ => None,
        }
    }

    #[must_use]
    pub fn integer(&self, name: &str) -> Option<i64> {
        match self.get(name)? {
            OptionValue::Integer(i) => Some(*i),
            OptionValue::Number(n) => Some(*n as i64),
            _ => None,
        }
    }

    #[must_use]
    pub fn number(&self, name: &str) -> Option<f64> {
        match self.get(name)? {
            OptionValue::Number(n) => Some(*n),
            OptionValue::Integer(i) => Some(*i as f64),
            _ => None,
        }
    }

    #[must_use]
    pub fn boolean(&self, name: &str) -> Option<bool> {
        match self.get(name)? {
            OptionValue::Boolean(b) => Some(*b),
            _ => None,
        }
    }

    /// A user, channel, role or mentionable option's id.
    #[must_use]
    pub fn id(&self, name: &str) -> Option<u64> {
        match self.get(name)? {
            OptionValue::User(id)
            | OptionValue::Channel(id)
            | OptionValue::Role(id)
            | OptionValue::Mentionable(id) => Some(*id),
            _ => None,
        }
    }

    #[must_use]
    pub fn attachment(&self, name: &str) -> Option<&Attachment> {
        match self.get(name)? {
            OptionValue::Attachment(a) => Some(a),
            _ => None,
        }
    }

    /// The resolved user for a user option.
    #[must_use]
    pub fn user(&self, name: &str) -> Option<&ResolvedUser> {
        self.resolved_users.get(&self.id(name)?)
    }
}

/// A file sent with a reply.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileUpload {
    pub filename: String,
    pub bytes: Vec<u8>,
}

/// What a command answers with. Mentions are always suppressed: names in replies are player
/// text, and a reply must never ping anybody.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct Reply {
    pub content: Option<String>,
    pub embeds: Vec<Embed>,
    pub components: Vec<ActionRow>,
    pub files: Vec<FileUpload>,
}

impl Reply {
    #[must_use]
    pub fn text(content: impl Into<String>) -> Self {
        Self {
            content: Some(content.into()),
            ..Self::default()
        }
    }

    #[must_use]
    pub fn embed(embed: Embed) -> Self {
        Self {
            embeds: vec![embed],
            ..Self::default()
        }
    }

    #[must_use]
    pub fn with_components(mut self, rows: Vec<ActionRow>) -> Self {
        self.components = rows;
        self
    }
}

/// A Discord call that failed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DiscordError(pub String);

impl fmt::Display for DiscordError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for DiscordError {}

/// How an interaction is answered.
pub trait Responder: Send + Sync {
    /// Acknowledge now (inside Discord's three seconds) and answer later.
    fn defer(&self, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Answer at once, instead of deferring.
    fn respond(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Replace the deferred or first answer.
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// [`edit_original`](Self::edit_original), returning the message's id when the adapter
    /// knows it (paged replies key their state on it).
    fn edit_original_message(
        &self,
        reply: Reply,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        Box::pin(async move { self.edit_original(reply).await.map(|()| None) })
    }
    /// Another message after the first.
    fn followup(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Whether the interaction has been acknowledged.
    fn has_responded(&self) -> bool;
}

/// How an autocomplete request is answered.
pub trait Suggester: Send + Sync {
    /// Offer these choices (at most 25; an empty list offers nothing).
    fn suggest(&self, choices: Vec<Choice>) -> BoxFuture<'_, Result<(), DiscordError>>;
}

/// A button press or select-menu choice as it arrived.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ComponentInvocation {
    pub custom_id: String,
    pub caller: Caller,
    pub username: String,
    pub guild_id: Option<u64>,
    pub channel_id: u64,
    /// The message the component is on.
    pub message_id: u64,
    /// That message's first embed title, for handlers that keep their state in it.
    pub message_title: Option<String>,
    /// What a select menu chose (empty for a button).
    pub values: Vec<String>,
    /// A MODAL SUBMIT's text inputs, by their custom id (empty for a button or a select). A
    /// modal opened from a message carries that message's id, so a handler finds its state.
    pub fields: Vec<(String, String)>,
}

impl ComponentInvocation {
    /// A modal text input's value.
    #[must_use]
    pub fn field(&self, custom_id: &str) -> Option<&str> {
        self.fields
            .iter()
            .find(|(id, _)| id == custom_id)
            .map(|(_, v)| v.as_str())
    }
}

/// How a component interaction is answered.
pub trait ComponentResponder: Send + Sync {
    /// Replace the message the component is on.
    fn update(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Answer with a new message.
    fn respond(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Edit the message again after [`update`](Self::update) or
    /// [`acknowledge`](Self::acknowledge), for work that outlasts Discord's three seconds.
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Acknowledge without changing anything (Discord's deferred update).
    fn acknowledge(&self) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Open a modal. Only on an interaction NOT yet acknowledged: a modal cannot open on one.
    fn show_modal(&self, modal: crate::component::Modal)
    -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Acknowledge with a NEW reply to come ("thinking..."), for work past three seconds; the
    /// answer then goes through [`followup`](Self::followup).
    fn defer_reply(&self, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>>;
    /// Another message after the interaction was acknowledged.
    fn followup(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>>;
}
