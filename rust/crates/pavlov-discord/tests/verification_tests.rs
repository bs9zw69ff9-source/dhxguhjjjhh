//! Verification (C# `VerificationService`; the C# had no test file for its flow): the form,
//! the staff request with Accept and Deny, the conflict refused to the member, the re-check at
//! decision time, the role note, and a request decided twice.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::embed::Embed;
use pavlov_core::time::SystemClock;
use pavlov_discord::caller::Caller;
use pavlov_discord::component::{ActionRow, Modal};
use pavlov_discord::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply};
use pavlov_discord::paged::ComponentHandler;
use pavlov_discord::post::{ChannelPoster, PostEdit};
use pavlov_discord::verification::{
    PREFIX, RoleGrantError, RoleGranter, StoredVerification, VerificationChannels,
    VerificationService, panel, panel_button,
};
use pavlov_host::feeds::FeedWebhooks;
use pavlov_host::http::{HttpClient, HttpError, HttpRequest, HttpResponse};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_storage::datasets::{VERIFICATIONS, VERIFY_STATE};

const STAFF: u64 = 50;
const ROLE: u64 = 60;
const GUILD: u64 = 70;

struct NoNetwork;
impl HttpClient for NoNetwork {
    fn send(&self, _: HttpRequest) -> BoxFuture<'_, Result<HttpResponse, HttpError>> {
        async { Err(HttpError::Transport("no network in tests".into())) }.boxed()
    }
}

#[derive(Default)]
struct StaffChannel {
    posts: Mutex<Vec<(u64, Embed, Vec<ActionRow>)>>,
}

impl ChannelPoster for StaffChannel {
    fn edit(&self, _: u64, _: u64, _: Embed, _: Vec<ActionRow>) -> BoxFuture<'_, PostEdit> {
        Box::pin(async { PostEdit::Edited })
    }
    fn send(
        &self,
        channel_id: u64,
        embed: Embed,
        components: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        self.posts
            .lock()
            .unwrap()
            .push((channel_id, embed, components));
        Box::pin(async { Ok(Some(999)) })
    }
}

#[derive(Default)]
struct Roles {
    granted: Mutex<Vec<(u64, u64, u64)>>,
}

impl RoleGranter for Roles {
    fn grant(
        &self,
        guild: u64,
        user: u64,
        role: u64,
    ) -> BoxFuture<'_, Result<String, RoleGrantError>> {
        self.granted.lock().unwrap().push((guild, user, role));
        Box::pin(async { Ok("Verified".to_owned()) })
    }
}

#[derive(Default)]
struct Press {
    modals: Mutex<Vec<Modal>>,
    followups: Mutex<Vec<Reply>>,
    edits: Mutex<Vec<Reply>>,
}

impl ComponentResponder for Press {
    fn update(&self, _: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn respond(&self, _: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.edits.lock().unwrap().push(reply);
        Box::pin(async { Ok(()) })
    }
    fn acknowledge(&self) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn show_modal(&self, modal: Modal) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.modals.lock().unwrap().push(modal);
        Box::pin(async { Ok(()) })
    }
    fn defer_reply(&self, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn followup(&self, reply: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.followups.lock().unwrap().push(reply);
        Box::pin(async { Ok(()) })
    }
}

struct Fixture {
    service: VerificationService,
    staff: Arc<StaffChannel>,
    roles: Arc<Roles>,
}

fn fixture() -> Fixture {
    let store = common::commands::store();
    let clock = Arc::new(SystemClock);
    let metrics = Arc::new(MetricsRegistry::default());
    let staff = Arc::new(StaffChannel::default());
    let roles = Arc::new(Roles::default());
    let service = VerificationService {
        store: Arc::clone(&store),
        tracking: Arc::new(IpTrackingService::new(
            Arc::clone(&store),
            Arc::clone(&metrics),
            clock.clone(),
        )),
        feeds: Arc::new(FeedWebhooks::new(
            Arc::new(NoNetwork),
            metrics,
            clock.clone(),
        )),
        audit: Arc::new(AuditLog::new(Arc::clone(&store), clock.clone())),
        poster: staff.clone(),
        roles: roles.clone(),
        channels: VerificationChannels {
            panel: Some(40),
            staff: Some(STAFF),
            role: Some(ROLE),
        },
        clock,
    };
    Fixture {
        service,
        staff,
        roles,
    }
}

fn click(parts: &[&str], user: u64, fields: &[(&str, &str)]) -> ComponentInvocation {
    ComponentInvocation {
        custom_id: ComponentId::encode(PREFIX, parts).unwrap(),
        caller: Caller::user(user),
        username: format!("user{user}"),
        guild_id: Some(GUILD),
        fields: fields
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect(),
        ..ComponentInvocation::default()
    }
}

fn title(reply: &Reply) -> String {
    reply.embeds[0].title.clone().unwrap_or_default()
}

/// Submit a request and return the token on its Accept button.
async fn request(f: &Fixture, user: u64, name: &str) -> String {
    let press = Press::default();
    f.service
        .handle(&click(&["submit"], user, &[("name", name)]), &press)
        .await;
    let posts = f.staff.posts.lock().unwrap();
    let (_, _, rows) = posts.last().unwrap();
    let ActionRow::Buttons(buttons) = &rows[0] else {
        panic!("the request has buttons")
    };
    ComponentId::parse(&buttons[0].target)
        .argument(1)
        .unwrap()
        .to_owned()
}

#[test]
fn the_panel_has_one_verify_button() {
    assert!(panel().title.unwrap().contains("Verify"));
    let ActionRow::Buttons(buttons) = &panel_button()[0] else {
        panic!()
    };
    assert_eq!(buttons.len(), 1);
    assert_eq!(
        ComponentId::parse(&buttons[0].target).argument(0),
        Some("start")
    );
}

#[tokio::test]
async fn verify_opens_the_name_form() {
    let f = fixture();
    let press = Press::default();
    f.service.handle(&click(&["start"], 5, &[]), &press).await;
    let modals = press.modals.lock().unwrap();
    assert_eq!(modals[0].inputs[0].custom_id, "name");
    assert_eq!(modals[0].inputs[0].max_length, Some(64));
}

#[tokio::test]
async fn a_request_goes_to_staff_and_accepting_records_it_and_grants_the_role() {
    let f = fixture();
    let token = request(&f, 5, "Alice").await;
    {
        let posts = f.staff.posts.lock().unwrap();
        assert_eq!(posts[0].0, STAFF);
        assert_eq!(
            posts[0].1.field_named("Claims to be").unwrap().value,
            "`Alice`"
        );
        assert!(
            posts[0]
                .1
                .field_named("Confirmed addresses")
                .unwrap()
                .value
                .contains("none on record")
        );
    }

    let press = Press::default();
    f.service
        .handle(&click(&["ok", &token], 9, &[]), &press)
        .await;

    let verified: indexmap::IndexMap<String, StoredVerification> = f
        .service
        .store
        .read(VERIFICATIONS, indexmap::IndexMap::new())
        .unwrap();
    assert_eq!(verified["5"].name, "Alice");
    assert_eq!(*f.roles.granted.lock().unwrap(), [(GUILD, 5, ROLE)]);
    let closed = &press.edits.lock().unwrap()[0];
    assert!(closed.components.is_empty(), "spent buttons are removed");
    assert_eq!(
        closed.embeds[0].field_named("Role").unwrap().value,
        "Verified"
    );
    let pending: serde_json::Value = f
        .service
        .store
        .read(VERIFY_STATE, serde_json::json!({}))
        .unwrap();
    assert_eq!(pending, serde_json::json!({}));
}

#[tokio::test]
async fn a_second_decision_on_one_request_says_it_was_already_handled() {
    let f = fixture();
    let token = request(&f, 5, "Alice").await;
    f.service
        .handle(&click(&["no", &token], 9, &[]), &Press::default())
        .await;

    let press = Press::default();
    f.service
        .handle(&click(&["ok", &token], 10, &[]), &press)
        .await;

    assert!(title(&press.edits.lock().unwrap()[0]).ends_with("Already handled"));
    assert!(f.roles.granted.lock().unwrap().is_empty());
}

#[tokio::test]
async fn a_name_verified_to_somebody_else_is_refused_to_the_member() {
    let f = fixture();
    let token = request(&f, 5, "Alice").await;
    f.service
        .handle(&click(&["ok", &token], 9, &[]), &Press::default())
        .await;

    let press = Press::default();
    f.service
        .handle(&click(&["submit"], 6, &[("name", "alice")]), &press)
        .await;

    let said = &press.followups.lock().unwrap()[0];
    assert!(title(said).ends_with("Cannot verify"));
    assert_eq!(
        f.staff.posts.lock().unwrap().len(),
        1,
        "staff were not asked"
    );
}

#[tokio::test]
async fn the_decision_is_re_checked_when_staff_accept() {
    let f = fixture();
    let first = request(&f, 5, "Alice").await;
    let second = request(&f, 6, "Alice").await;
    f.service
        .handle(&click(&["ok", &first], 9, &[]), &Press::default())
        .await;

    let press = Press::default();
    f.service
        .handle(&click(&["ok", &second], 9, &[]), &press)
        .await;

    assert!(title(&press.edits.lock().unwrap()[0]).ends_with("No longer possible"));
}

#[tokio::test]
async fn an_empty_name_is_refused_before_anything_is_stored() {
    let f = fixture();
    let press = Press::default();
    f.service
        .handle(&click(&["submit"], 5, &[("name", "   ")]), &press)
        .await;
    assert!(title(&press.followups.lock().unwrap()[0]).ends_with("No name given"));
    assert!(f.staff.posts.lock().unwrap().is_empty());
}
