//! `/whitelist` end to end against real roster files (the C# exercised these paths only by
//! hand): add, rank placement, setrank, remove, and the wipe confirmation.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use common::{RecordingResponder, Sent};
use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::factions::FactionRegistry;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::whitelist_command::{WhitelistCommand, WhitelistWipe, wipe_controls};
use pavlov_discord::component::ActionRow;
use pavlov_discord::interaction::{
    ComponentInvocation, ComponentResponder, DiscordError, Invocation, InvokedOption, OptionValue,
    Reply, ResolvedUser,
};
use pavlov_discord::slash::{CommandContext, SlashCommand};

const OWNER: u64 = 444_000_000_000_000_001;
const MEMBER: u64 = 555_000_000_000_000_001;

struct Rig {
    dir: tempfile::TempDir,
    command: WhitelistCommand,
}

impl Rig {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = common::commands::store();
        let command = common::commands::whitelist(
            &store,
            common::commands::rosters(Some(dir.path())),
            &[OWNER],
        );
        Self { dir, command }
    }

    async fn run(&self, caller: u64, sub: &str, options: Vec<InvokedOption>) -> RecordingResponder {
        let invocation = Invocation {
            username: "Mayor".into(),
            resolved_users: HashMap::from([(
                MEMBER,
                ResolvedUser {
                    id: MEMBER,
                    username: "member".into(),
                    ..ResolvedUser::default()
                },
            )]),
            ..Invocation::new("whitelist", Caller::user(caller))
                .with(InvokedOption::sub(sub, options))
        };
        let responder = RecordingResponder::default();
        self.command
            .handle(CommandContext {
                invocation: &invocation,
                responder: &responder,
            })
            .await
            .unwrap();
        responder
    }

    fn lines(&self, file: &str) -> Vec<String> {
        std::fs::read_to_string(self.dir.path().join(file))
            .unwrap_or_default()
            .lines()
            .filter(|l| !l.trim().is_empty())
            .map(str::to_owned)
            .collect()
    }
}

fn s(name: &str, value: &str) -> InvokedOption {
    InvokedOption::value(name, OptionKind::String, OptionValue::String(value.into()))
}

fn member() -> InvokedOption {
    InvokedOption::value("member", OptionKind::User, OptionValue::User(MEMBER))
}

fn gwpd() -> &'static pavlov_core::factions::FactionDefinition {
    FactionRegistry::police().get(Some("GWPD")).unwrap()
}

#[tokio::test]
async fn adding_writes_the_rank_and_spawn_files_and_links_the_account() {
    let r = Rig::new();
    let reply = r
        .run(
            OWNER,
            "add",
            vec![
                s("faction", "GWPD"),
                member(),
                s("ingame_name", "Officer12"),
            ],
        )
        .await;

    assert!(
        reply.last_text().contains("Whitelist updated"),
        "{}",
        reply.last_text()
    );
    assert!(
        r.lines(&gwpd().spawn_file)
            .contains(&"Officer12".to_owned())
    );
    assert!(
        r.lines(&gwpd().rank_files[gwpd().lowest()])
            .contains(&"Officer12".to_owned())
    );
    assert_eq!(r.command.members.of(MEMBER).unwrap().name, "Officer12");
    assert!(
        r.command
            .audit
            .all()
            .iter()
            .any(|a| a.action == "whitelist-add")
    );
}

#[tokio::test]
async fn adding_at_a_rank_places_them_there() {
    let r = Rig::new();
    let high = gwpd().order.last().unwrap().clone();
    let reply = r
        .run(
            OWNER,
            "add",
            vec![
                s("faction", "GWPD"),
                member(),
                s("ingame_name", "Chief"),
                s("rank", &high),
            ],
        )
        .await;

    assert!(reply.last_text().contains(&high), "{}", reply.last_text());
    assert!(
        r.lines(&gwpd().rank_files[&high])
            .iter()
            .any(|l| eq_ignore_case(l, "Chief"))
    );
}

#[tokio::test]
async fn a_bad_rank_is_refused_before_anything_is_written() {
    let r = Rig::new();
    let reply = r
        .run(
            OWNER,
            "add",
            vec![
                s("faction", "GWPD"),
                member(),
                s("ingame_name", "Chief"),
                s("rank", "Emperor"),
            ],
        )
        .await;

    assert!(reply.last_text().contains("No such rank"));
    assert!(r.lines(&gwpd().spawn_file).is_empty());
    assert!(r.command.members.of(MEMBER).is_none());
}

#[tokio::test]
async fn somebody_who_manages_nothing_cannot_add() {
    let r = Rig::new();
    let reply = r
        .run(
            777,
            "add",
            vec![s("faction", "GWPD"), member(), s("ingame_name", "Sneaky")],
        )
        .await;
    assert!(
        reply.last_text().contains("Not your roster"),
        "{}",
        reply.last_text()
    );
    assert!(r.lines(&gwpd().spawn_file).is_empty());
}

#[tokio::test]
async fn setrank_moves_a_linked_member() {
    let r = Rig::new();
    r.run(
        OWNER,
        "add",
        vec![
            s("faction", "GWPD"),
            member(),
            s("ingame_name", "Officer12"),
        ],
    )
    .await;
    let second = gwpd().order[1].clone();

    let reply = r
        .run(
            OWNER,
            "setrank",
            vec![s("faction", "GWPD"), member(), s("rank", &second)],
        )
        .await;

    assert!(
        reply.last_text().contains("Rank set"),
        "{}",
        reply.last_text()
    );
    assert!(
        r.lines(&gwpd().rank_files[&second])
            .contains(&"Officer12".to_owned())
    );
}

#[tokio::test]
async fn removing_takes_them_off_every_file_and_forgets_the_link() {
    let r = Rig::new();
    r.run(
        OWNER,
        "add",
        vec![
            s("faction", "GWPD"),
            member(),
            s("ingame_name", "Officer12"),
        ],
    )
    .await;

    let reply = r.run(OWNER, "remove", vec![member()]).await;

    assert!(
        reply.last_text().contains("Whitelist updated"),
        "{}",
        reply.last_text()
    );
    assert!(r.lines(&gwpd().spawn_file).is_empty());
    assert!(r.command.members.of(MEMBER).is_none());
}

#[tokio::test]
async fn the_list_shows_the_roster() {
    let r = Rig::new();
    r.run(
        OWNER,
        "add",
        vec![
            s("faction", "GWPD"),
            member(),
            s("ingame_name", "Officer12"),
        ],
    )
    .await;
    let reply = r.run(OWNER, "list", vec![s("faction", "GWPD")]).await;
    assert!(
        reply.last_text().contains("`Officer12`"),
        "{}",
        reply.last_text()
    );
}

#[derive(Default)]
struct Component {
    sent: Mutex<Vec<Reply>>,
    answered: Mutex<Vec<String>>,
}

impl ComponentResponder for Component {
    fn update(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.sent.lock().unwrap().push(reply);
        async { Ok(()) }.boxed()
    }
    fn respond(&self, reply: Reply, _: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.answered
            .lock()
            .unwrap()
            .push(reply.content.unwrap_or_default());
        async { Ok(()) }.boxed()
    }
    fn edit_original(&self, reply: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        self.sent.lock().unwrap().push(reply);
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

fn custom_id(rows: &[ActionRow], index: usize) -> String {
    match &rows[0] {
        ActionRow::Buttons(buttons) => buttons[index].target.clone(),
        ActionRow::Select(_) => panic!("expected buttons"),
    }
}

#[tokio::test]
async fn the_wipe_asks_first_and_only_its_owner_may_confirm() {
    let r = Rig::new();
    r.run(
        OWNER,
        "add",
        vec![
            s("faction", "GWPD"),
            member(),
            s("ingame_name", "Officer12"),
        ],
    )
    .await;

    let prompt = r.run(OWNER, "wipe", vec![s("faction", "GWPD")]).await;
    let Some(Sent::Edit { reply }) = prompt.sent().last().cloned() else {
        panic!("expected the prompt");
    };
    assert!(!reply.components.is_empty());
    assert_eq!(reply.components, wipe_controls("GWPD", OWNER));

    let wipe = WhitelistWipe {
        rosters: Arc::clone(&r.command.rosters),
        members: Arc::clone(&r.command.members),
        access: Arc::clone(&r.command.access),
        audit: Arc::clone(&r.command.audit),
    };
    let press = |user: u64| ComponentInvocation {
        custom_id: custom_id(&reply.components, 0),
        caller: Caller::user(user),
        username: "Mayor".into(),
        ..ComponentInvocation::default()
    };

    // A bystander's press does nothing.
    let bystander = Component::default();
    wipe.press_for_test(&press(777), &bystander).await;
    assert!(bystander.answered.lock().unwrap()[0].contains("belongs to whoever ran"));
    assert!(!r.lines(&gwpd().spawn_file).is_empty());

    let owner = Component::default();
    wipe.press_for_test(&press(OWNER), &owner).await;
    assert!(r.lines(&gwpd().spawn_file).is_empty());
    assert!(r.command.members.of(MEMBER).is_none());
    let last = owner.sent.lock().unwrap().last().cloned().unwrap();
    assert!(last.embeds[0].title.as_deref().unwrap().contains("wiped"));
}
