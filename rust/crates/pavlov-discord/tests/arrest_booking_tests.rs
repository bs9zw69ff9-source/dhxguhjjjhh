//! The arrest booking flow end to end: player, section, charges, confirm; the record lands in
//! the Node shape and satisfies the warrant; only the officer who opened it may touch it; a
//! double confirm books once; the modals set a name and a time. (The C# has no test file for
//! the flow; `SentenceCapTests` covers the arithmetic.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::data::SerializedStore;
use pavlov_core::penal::PenalCode;
use pavlov_core::police::{Arrest, Warrant};
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::commands::arrest::{ArrestBooking, ArrestRecorder};
use pavlov_discord::component::{ActionRow, Modal};
use pavlov_discord::interaction::{
    ComponentInvocation, ComponentResponder, DiscordError, Reply, Responder,
};
use pavlov_discord::paged::ComponentHandler;
use pavlov_host::moderation::AuditLog;

const OFFICER: u64 = 10;
const MESSAGE: u64 = 555;

/// A responder that knows the message id it created.
#[derive(Default)]
struct Opening {
    replies: Mutex<Vec<Reply>>,
}

impl Responder for Opening {
    fn defer(&self, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        async { Ok(()) }.boxed()
    }
    fn respond(&self, reply: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.replies.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.replies.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn edit_original_message(
        &self,
        reply: Reply,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        self.replies.lock().unwrap().push(reply);
        async { Ok(Some(MESSAGE)) }.boxed()
    }
    fn followup(&self, reply: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.replies.lock().unwrap().push(reply);
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
    modals: Mutex<Vec<Modal>>,
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
    fn show_modal(&self, modal: Modal) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.modals.lock().unwrap().push(modal);
        async { Ok(()) }.boxed()
    }
}

fn press(step: &str, user: u64, values: &[&str]) -> ComponentInvocation {
    ComponentInvocation {
        custom_id: ComponentId::encode("arr", &[step]).unwrap(),
        caller: Caller::user(user),
        username: "Officer".into(),
        message_id: MESSAGE,
        values: values.iter().map(|v| (*v).to_owned()).collect(),
        ..ComponentInvocation::default()
    }
}

async fn opened(online: &[&str]) -> (Arc<SerializedStore>, ArrestBooking) {
    let store = common::commands::store();
    let clock = Arc::new(SystemClock);
    let recorder = Arc::new(ArrestRecorder {
        store: Arc::clone(&store),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), clock.clone())),
        clock,
    });
    let booking = ArrestBooking::new(recorder);
    booking
        .begin(
            &Opening::default(),
            OFFICER,
            online.iter().map(|n| (*n).to_owned()).collect(),
            1.0,
            0,
        )
        .await
        .unwrap();
    (store, booking)
}

fn first_charge() -> (i32, &'static str) {
    let (section, charges) = PenalCode::sections().into_iter().next().unwrap();
    (section, charges[0].code)
}

#[tokio::test]
async fn a_full_booking_records_the_arrest_and_clears_the_warrant() {
    let (store, booking) = opened(&["Robber"]).await;
    let mut warrants = IgnoreCaseMap::new();
    warrants.insert(
        "Robber",
        vec![Warrant {
            player: "Robber".into(),
            reason: "x".into(),
            issued_by: "y".into(),
            at: chrono::Utc::now(),
        }],
    );
    store.write("warrants", &warrants).await.unwrap();

    let (section, code) = first_charge();
    let r = Press::default();
    booking
        .handle(&press("player", OFFICER, &["Robber"]), &r)
        .await;
    booking
        .handle(&press("section", OFFICER, &[&section.to_string()]), &r)
        .await;
    let with_charges = r.updates.lock().unwrap().last().cloned().unwrap();
    assert!(
        with_charges
            .components
            .iter()
            .any(|row| matches!(row, ActionRow::Select(s) if s.custom_id.contains("charge")))
    );
    booking
        .handle(&press("charge", OFFICER, &[code, code]), &r)
        .await;
    booking.handle(&press("confirm", OFFICER, &[]), &r).await;

    let arrests: IgnoreCaseMap<Vec<Arrest>> = store.read("arrests", IgnoreCaseMap::new()).unwrap();
    let booked = arrests.get("Robber").unwrap();
    assert_eq!(booked.len(), 1);
    assert_eq!(
        booked[0].codes,
        [code],
        "a charge picked twice is booked once"
    );
    let left: IgnoreCaseMap<Vec<Warrant>> = store.read("warrants", IgnoreCaseMap::new()).unwrap();
    assert!(
        left.get("Robber").is_none(),
        "the arrest satisfies the warrant"
    );
    assert!(!booking.is_open(MESSAGE));
    let raw = store.read_raw("arrests").unwrap().unwrap();
    assert!(
        raw.contains("\"playerId\""),
        "stored in the Node shape: {raw}"
    );
}

#[tokio::test]
async fn only_the_officer_who_opened_it_may_touch_it() {
    let (_store, booking) = opened(&["Robber"]).await;
    let r = Press::default();
    booking.handle(&press("player", 99, &["Robber"]), &r).await;
    assert!(r.said.lock().unwrap()[0].contains("belongs to whoever ran the command"));
    assert!(r.updates.lock().unwrap().is_empty());
}

#[tokio::test]
async fn confirm_is_disabled_until_there_is_a_player_and_a_charge() {
    let (store, booking) = opened(&[]).await;
    let r = Press::default();
    booking.handle(&press("confirm", OFFICER, &[]), &r).await;
    let arrests: IgnoreCaseMap<Vec<Arrest>> = store.read("arrests", IgnoreCaseMap::new()).unwrap();
    assert!(arrests.is_empty());
    assert!(
        booking.is_open(MESSAGE),
        "a premature confirm leaves the booking open"
    );
}

#[tokio::test]
async fn the_name_and_time_modals_set_the_booking() {
    let (store, booking) = opened(&[]).await;
    let r = Press::default();
    booking.handle(&press("name", OFFICER, &[]), &r).await;
    booking.handle(&press("time", OFFICER, &[]), &r).await;
    assert_eq!(
        r.modals.lock().unwrap().len(),
        2,
        "both open a modal, neither defers"
    );

    let mut name = press("setname", OFFICER, &[]);
    name.fields = vec![("player".into(), "GonePlayer".into())];
    booking.handle(&name, &r).await;
    let mut time = press("settime", OFFICER, &[]);
    time.fields = vec![("minutes".into(), "abc".into())];
    booking.handle(&time, &r).await;
    assert!(
        r.said
            .lock()
            .unwrap()
            .last()
            .unwrap()
            .contains("whole number of minutes")
    );
    time.fields = vec![("minutes".into(), "7".into())];
    booking.handle(&time, &r).await;

    let (_, code) = first_charge();
    booking.handle(&press("charge", OFFICER, &[code]), &r).await;
    booking.handle(&press("confirm", OFFICER, &[]), &r).await;
    let arrests: IgnoreCaseMap<Vec<Arrest>> = store.read("arrests", IgnoreCaseMap::new()).unwrap();
    let booked = &arrests.get("GonePlayer").unwrap()[0];
    assert_eq!(
        booked.jail_minutes, 7,
        "the officer's time replaces the computed one"
    );
}

#[tokio::test]
async fn an_unknown_message_is_an_expired_booking() {
    let (_store, booking) = opened(&[]).await;
    let r = Press::default();
    let mut stale = press("player", OFFICER, &["x"]);
    stale.message_id = 1;
    booking.handle(&stale, &r).await;
    assert!(r.said.lock().unwrap()[0].contains("expired"));
}
