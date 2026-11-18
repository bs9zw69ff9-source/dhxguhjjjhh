//! A long list as pages, turned with buttons (C# `Paged.cs`). The pages are kept in memory
//! by message id, so a restart or eviction expires a list and says so rather than leaving
//! buttons that do nothing; only whoever ran the command may turn its pages.

use std::collections::HashMap;
use std::sync::{Mutex, PoisonError};

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;

use crate::component::{ActionRow, Button, ButtonStyle};
use crate::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply, Responder};
use crate::prefixes::PAGED;

/// How many lists are remembered before the oldest are dropped.
pub const MAX_CACHED: usize = 200;

#[derive(Debug, Clone)]
struct Session {
    title: String,
    pages: Vec<String>,
    owner: u64,
    at: DateTime<Utc>,
}

/// The pager.
#[derive(Debug, Default)]
pub struct Paged {
    sessions: Mutex<HashMap<u64, Session>>,
}

fn render(title: &str, pages: &[String], index: usize) -> Embed {
    theme::notice(
        &format!("{title} — page {}/{}", index + 1, pages.len()),
        Some(&pages[index]),
    )
    .brand(None)
}

fn button(label: &str, step: &str, style: ButtonStyle, disabled: bool) -> Button {
    let id = ComponentId::encode(PAGED, &[step]).unwrap_or_else(|_| PAGED.to_owned());
    let button = Button::new(style, label, id);
    if disabled { button.disabled() } else { button }
}

/// The four page buttons for page `index` of `count`.
#[must_use]
pub fn buttons(index: usize, count: usize) -> Vec<ActionRow> {
    let last = count.saturating_sub(1);
    vec![ActionRow::Buttons(vec![
        button("First", "first", ButtonStyle::Secondary, index == 0),
        button("Prev", "prev", ButtonStyle::Primary, index == 0),
        button("Next", "next", ButtonStyle::Primary, index >= last),
        button("Last", "last", ButtonStyle::Secondary, index >= last),
    ])]
}

/// The page a message is on, read from its title (`... — page 2/5`).
#[must_use]
pub fn current_page(title: Option<&str>, count: usize) -> usize {
    let Some(title) = title else {
        return 0;
    };
    let Some(at) = title.rfind("page ") else {
        return 0;
    };
    title[at + 5..]
        .split('/')
        .next()
        .and_then(|n| n.trim().parse::<usize>().ok())
        .map_or(0, |page| {
            page.saturating_sub(1).min(count.saturating_sub(1))
        })
}

impl Paged {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    fn sessions(&self) -> std::sync::MutexGuard<'_, HashMap<u64, Session>> {
        self.sessions.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Send `pages` as the deferred reply: one page plain, more with buttons.
    ///
    /// # Errors
    /// When Discord refuses the edit.
    pub async fn send(
        &self,
        responder: &dyn Responder,
        owner: u64,
        title: &str,
        pages: Vec<String>,
    ) -> Result<(), DiscordError> {
        if pages.len() <= 1 {
            let body = pages.first().map_or("*Nothing to show.*", String::as_str);
            return responder
                .edit_original(Reply::embed(theme::notice(title, Some(body)).brand(None)))
                .await;
        }
        let reply = Reply::embed(render(title, &pages, 0)).with_components(buttons(0, pages.len()));
        if let Some(message) = responder.edit_original_message(reply).await? {
            let mut sessions = self.sessions();
            sessions.insert(
                message,
                Session {
                    title: title.to_owned(),
                    pages,
                    owner,
                    at: Utc::now(),
                },
            );
            if sessions.len() > MAX_CACHED {
                let mut by_age: Vec<(u64, DateTime<Utc>)> =
                    sessions.iter().map(|(k, s)| (*k, s.at)).collect();
                by_age.sort_by_key(|(_, at)| *at);
                for (key, _) in by_age.into_iter().take(sessions.len() - MAX_CACHED) {
                    sessions.remove(&key);
                }
            }
        }
        Ok(())
    }

    /// Turn a page.
    pub async fn handle(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) {
        let id = ComponentId::parse(&invocation.custom_id);
        let session = self.sessions().get(&invocation.message_id).cloned();
        let answer = match session {
            None => {
                // Evicted, or posted by a previous run: say so rather than doing nothing.
                responder
                    .respond(
                        Reply::text("That list has expired. Run the command again."),
                        true,
                    )
                    .await
            }
            Some(session) if session.owner != invocation.caller.id => {
                responder
                    .respond(
                        Reply::text("That list belongs to whoever ran the command."),
                        true,
                    )
                    .await
            }
            Some(session) => {
                let count = session.pages.len();
                let current = current_page(invocation.message_title.as_deref(), count);
                let target = match id.argument(0) {
                    Some("first") => 0,
                    Some("prev") => current.saturating_sub(1),
                    Some("next") => current + 1,
                    Some("last") => count - 1,
                    _ => current,
                }
                .min(count - 1);
                responder
                    .update(
                        Reply::embed(render(&session.title, &session.pages, target))
                            .with_components(buttons(target, count)),
                    )
                    .await
            }
        };
        if let Err(error) = answer {
            tracing::warn!(%error, "could not turn the page");
        }
    }
}

/// A component handler: whatever owns a custom-id prefix.
pub trait ComponentHandler: Send + Sync {
    fn prefix(&self) -> &str;
    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()>;
}

impl ComponentHandler for Paged {
    fn prefix(&self) -> &str {
        PAGED
    }
    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        Box::pin(Paged::handle(self, invocation, responder))
    }
}
