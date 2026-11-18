//! `/server lookup`'s browser: paging, picking by `ip:port`, only the opener may drive it, and a
//! search re-filters the whole list. (The C# has no test file for the browser.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::commands::server_lookup::{OwnServers, PER_PAGE, ServerBrowser};
use pavlov_discord::component::{ActionRow, Modal};
use pavlov_discord::interaction::{
    ComponentInvocation, ComponentResponder, DiscordError, Reply, Responder,
};
use pavlov_discord::paged::ComponentHandler;
use pavlov_host::http::{HttpClient, HttpError, HttpRequest, HttpResponse};
use pavlov_host::master_list::MasterServerList;

const OWNER: u64 = 4;
const MESSAGE: u64 = 900;

struct Listing;
impl HttpClient for Listing {
    fn send(&self, _: HttpRequest) -> BoxFuture<'_, Result<HttpResponse, HttpError>> {
        let servers: Vec<String> = (0..30)
            .map(|i| {
                format!(
                    r#"{{"version":"1.0","name":"{}","ip":"10.0.0.{i}","port":7777,"slots":{},"maxSlots":24,"mapLabel":"Station","mapId":"UGC{i}","gameMode":"SND","gameModeLabel":"SND","bPasswordProtected":false,"bSecured":true}}"#,
                    if i == 7 { "Precinct Seven".to_owned() } else { format!("Server {i}") },
                    i % 5
                )
            })
            .collect();
        let body = format!(r#"{{"servers":[{}]}}"#, servers.join(","));
        Box::pin(async move { Ok(HttpResponse::new(200, body)) })
    }
}

#[derive(Default)]
struct Opening;
impl Responder for Opening {
    fn defer(&self, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn respond(&self, _: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn edit_original(&self, _: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn edit_original_message(&self, _: Reply) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        async { Ok(Some(MESSAGE)) }.boxed()
    }
    fn followup(&self, _: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn has_responded(&self) -> bool {
        true
    }
}

#[derive(Default)]
struct Press {
    updates: Mutex<Vec<Reply>>,
    said: Mutex<Vec<String>>,
}
impl ComponentResponder for Press {
    fn update(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.updates.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn respond(&self, reply: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.said
            .lock()
            .unwrap()
            .push(reply.content.unwrap_or_default());
        async { Ok(()) }.boxed()
    }
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.updates.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn acknowledge(&self) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn show_modal(&self, _: Modal) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn defer_reply(&self, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn followup(&self, _: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
}

fn press(step: &str, user: u64, values: &[&str]) -> ComponentInvocation {
    ComponentInvocation {
        custom_id: ComponentId::encode("srv", &[step]).unwrap(),
        caller: Caller::user(user),
        message_id: MESSAGE,
        values: values.iter().map(|v| (*v).to_owned()).collect(),
        ..ComponentInvocation::default()
    }
}

async fn opened() -> ServerBrowser {
    let master = Arc::new(MasterServerList::new(
        Arc::new(Listing),
        None,
        Arc::new(SystemClock),
    ));
    let browser = ServerBrowser::new(
        master,
        OwnServers {
            hosts: vec!["10.0.0.7".into()],
            names: Vec::new(),
        },
        Arc::new(SystemClock),
    );
    browser.begin(&Opening, OWNER, None).await.unwrap();
    browser
}

fn options(reply: &Reply) -> Vec<String> {
    match &reply.components[0] {
        ActionRow::Select(menu) => menu.options.iter().map(|o| o.value.clone()).collect(),
        ActionRow::Buttons(_) => panic!("expected the picker first"),
    }
}

#[tokio::test]
async fn pages_hold_at_most_twenty_five_and_values_are_addresses() {
    let browser = opened().await;
    let r = Press::default();
    browser.handle(&press("next", OWNER, &[]), &r).await;
    let page_two = r.updates.lock().unwrap()[0].clone();
    assert_eq!(options(&page_two).len(), 30 - PER_PAGE);
    assert!(options(&page_two).iter().all(|v| v.ends_with(":7777")));
    assert!(
        page_two.embeds[0]
            .title
            .as_deref()
            .unwrap()
            .contains("page 2/2")
    );
}

#[tokio::test]
async fn picking_shows_the_card_and_marks_our_own_server() {
    let browser = opened().await;
    let r = Press::default();
    browser
        .handle(&press("pick", OWNER, &["10.0.0.7:7777"]), &r)
        .await;
    let card = &r.updates.lock().unwrap()[0].embeds[0];
    assert!(
        card.title
            .as_deref()
            .unwrap()
            .ends_with("Precinct Seven · yours"),
        "{:?}",
        card.title
    );
    assert_eq!(
        card.field_named("Connect").unwrap().value,
        "`10.0.0.7:7777`"
    );
}

#[tokio::test]
async fn only_the_opener_may_drive_it() {
    let browser = opened().await;
    let r = Press::default();
    browser.handle(&press("next", 99, &[]), &r).await;
    assert!(r.said.lock().unwrap()[0].contains("belongs to whoever ran the command"));
    assert!(r.updates.lock().unwrap().is_empty());
}

#[tokio::test]
async fn a_search_narrows_and_an_empty_one_widens_again() {
    let browser = opened().await;
    let r = Press::default();
    let mut search = press("search", OWNER, &[]);
    search.fields = vec![("query".into(), "precinct".into())];
    browser.handle(&search, &r).await;
    assert_eq!(options(&r.updates.lock().unwrap()[0]), ["10.0.0.7:7777"]);
    search.fields = vec![("query".into(), String::new())];
    browser.handle(&search, &r).await;
    assert_eq!(options(&r.updates.lock().unwrap()[1]).len(), PER_PAGE);
}
