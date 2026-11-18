//! Paged replies (C# `Paged.cs`, which had no tests): one page is plain, more get buttons, only
//! the owner turns them, and an expired list says so.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Mutex;

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_discord::caller::Caller;
use pavlov_discord::interaction::{
    ComponentInvocation, ComponentResponder, DiscordError, Reply, Responder,
};
use pavlov_discord::paged::{Paged, current_page};
use pavlov_discord::prefixes::PAGED;

/// A responder whose edit reports message id 77.
#[derive(Default)]
struct Original {
    edits: Mutex<Vec<Reply>>,
}

impl Responder for Original {
    fn defer(&self, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn respond(&self, _: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.edits.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn edit_original_message(
        &self,
        reply: Reply,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        self.edits.lock().unwrap().push(reply);
        async { Ok(Some(77)) }.boxed()
    }
    fn followup(&self, _: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn has_responded(&self) -> bool {
        true
    }
}

#[derive(Default)]
struct Component {
    updates: Mutex<Vec<Reply>>,
    answers: Mutex<Vec<(Reply, bool)>>,
}

impl ComponentResponder for Component {
    fn update(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.updates.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn respond(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.answers.lock().unwrap().push((reply, ephemeral));
        async { Ok(()) }.boxed()
    }
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.updates.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn acknowledge(&self) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn show_modal(
        &self,
        _: pavlov_discord::component::Modal,
    ) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
}

fn press(step: &str, user: u64, title: &str) -> ComponentInvocation {
    ComponentInvocation {
        custom_id: ComponentId::encode(PAGED, &[step]).unwrap(),
        caller: Caller::user(user),
        message_id: 77,
        message_title: Some(title.to_owned()),
        ..ComponentInvocation::default()
    }
}

fn pages(n: usize) -> Vec<String> {
    (1..=n).map(|i| format!("page body {i}")).collect()
}

#[tokio::test]
async fn one_page_is_sent_plain() {
    let paged = Paged::new();
    let original = Original::default();
    paged.send(&original, 1, "Roster", pages(1)).await.unwrap();
    let edits = original.edits.lock().unwrap();
    assert!(edits[0].components.is_empty());
    assert_eq!(
        edits[0].embeds[0].description.as_deref(),
        Some("page body 1")
    );
}

#[tokio::test]
async fn several_pages_get_buttons_and_turn() {
    let paged = Paged::new();
    let original = Original::default();
    paged.send(&original, 1, "Roster", pages(3)).await.unwrap();
    assert!(!original.edits.lock().unwrap()[0].components.is_empty());

    let component = Component::default();
    paged
        .handle(&press("next", 1, "Roster — page 1/3"), &component)
        .await;

    let updates = component.updates.lock().unwrap();
    assert_eq!(
        updates[0].embeds[0]
            .title
            .as_deref()
            .map(|t| t.contains("page 2/3")),
        Some(true)
    );
    assert_eq!(
        updates[0].embeds[0].description.as_deref(),
        Some("page body 2")
    );
}

#[tokio::test]
async fn only_the_owner_turns_the_pages() {
    let paged = Paged::new();
    paged
        .send(&Original::default(), 1, "Roster", pages(3))
        .await
        .unwrap();

    let component = Component::default();
    paged
        .handle(&press("next", 2, "Roster — page 1/3"), &component)
        .await;

    assert!(component.updates.lock().unwrap().is_empty());
    let answers = component.answers.lock().unwrap();
    assert!(answers[0].1);
    assert!(
        answers[0]
            .0
            .content
            .as_deref()
            .unwrap()
            .contains("belongs to whoever ran")
    );
}

#[tokio::test]
async fn an_expired_list_says_so() {
    let component = Component::default();
    Paged::new()
        .handle(&press("next", 1, "Roster — page 1/3"), &component)
        .await;
    assert!(
        component.answers.lock().unwrap()[0]
            .0
            .content
            .as_deref()
            .unwrap()
            .contains("expired")
    );
}

#[test]
fn the_current_page_is_read_from_the_title_and_clamped() {
    assert_eq!(current_page(Some("Roster — page 2/5"), 5), 1);
    assert_eq!(current_page(Some("Roster — page 9/5"), 5), 4);
    assert_eq!(current_page(Some("Roster"), 5), 0);
    assert_eq!(current_page(None, 5), 0);
}
