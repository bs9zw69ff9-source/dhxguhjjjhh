//! Test doubles for the Discord crate: a responder and a suggester that record what they were
//! asked to send, optionally failing like a Discord outage.

#![allow(dead_code, clippy::unwrap_used)]

use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};

use futures_util::future::BoxFuture;
use pavlov_discord::command::Choice;
use pavlov_discord::interaction::{DiscordError, Reply, Responder, Suggester};

/// One call made to the responder.
#[derive(Debug, Clone, PartialEq)]
pub enum Sent {
    Defer { ephemeral: bool },
    Respond { reply: Reply, ephemeral: bool },
    Edit { reply: Reply },
    Followup { reply: Reply, ephemeral: bool },
}

#[derive(Debug, Default)]
pub struct RecordingResponder {
    pub sent: Mutex<Vec<Sent>>,
    pub fails: bool,
    responded: AtomicBool,
}

impl RecordingResponder {
    pub fn failing() -> Self {
        Self {
            fails: true,
            ..Self::default()
        }
    }

    pub fn sent(&self) -> Vec<Sent> {
        self.sent.lock().unwrap().clone()
    }

    fn record(&self, sent: Sent) -> BoxFuture<'_, Result<(), DiscordError>> {
        let fails = self.fails;
        if !fails {
            self.responded.store(true, Ordering::SeqCst);
            self.sent.lock().unwrap().push(sent);
        }
        Box::pin(async move {
            if fails {
                Err(DiscordError("Discord is down".into()))
            } else {
                Ok(())
            }
        })
    }

    /// The last reply's text and every embed's title and description, for assertions.
    pub fn last_text(&self) -> String {
        let reply = match self.sent().last() {
            Some(
                Sent::Respond { reply, .. } | Sent::Edit { reply } | Sent::Followup { reply, .. },
            ) => reply.clone(),
            _ => return String::new(),
        };
        let mut text = reply.content.unwrap_or_default();
        for embed in &reply.embeds {
            text.push('\n');
            text.push_str(embed.title.as_deref().unwrap_or(""));
            text.push('\n');
            text.push_str(embed.description.as_deref().unwrap_or(""));
            for field in &embed.fields {
                text.push_str(&format!("\n{}: {}", field.name, field.value));
            }
        }
        text
    }
}

impl Responder for RecordingResponder {
    fn defer(&self, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.record(Sent::Defer { ephemeral })
    }
    fn respond(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.record(Sent::Respond { reply, ephemeral })
    }
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.record(Sent::Edit { reply })
    }
    fn followup(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.record(Sent::Followup { reply, ephemeral })
    }
    fn has_responded(&self) -> bool {
        self.responded.load(Ordering::SeqCst)
    }
}

#[derive(Debug, Default)]
pub struct RecordingSuggester {
    pub offered: Mutex<Vec<Vec<Choice>>>,
}

impl Suggester for RecordingSuggester {
    fn suggest(&self, choices: Vec<Choice>) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.offered.lock().unwrap().push(choices);
        Box::pin(async { Ok(()) })
    }
}
pub mod commands;
pub mod directory;
