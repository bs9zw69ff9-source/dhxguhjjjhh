//! `/givemenu`, `/stripmenu` and `/unlinkname` over the ServerRoles tier files (owner change,
//! 2026-11-18, replacing RCON+): a tier is a file write, a player holds one tier, owners alone
//! touch owner and manager, and the permanent name binding still holds. (The C# has no test
//! file for these; `MenuLinkTests` covers the binding decision.)

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::path::Path;
use std::sync::Arc;

use pavlov_core::data::SerializedStore;
use pavlov_core::roles::RoleMap;
use pavlov_core::time::SystemClock;
use pavlov_discord::access::Access;
use pavlov_discord::caller::Caller;
use pavlov_discord::command::OptionKind;
use pavlov_discord::commands::menu::{
    GiveMenuCommand, MenuAccess, StripMenuCommand, UnlinkNameCommand,
};
use pavlov_discord::interaction::{Invocation, InvokedOption, OptionValue};
use pavlov_discord::slash::{CommandContext, SlashCommand};
use pavlov_storage::datasets::ROLES;

const OWNER: u64 = 1;
const ADMIN: u64 = 2;
const MOD: u64 = 3;
const ADMIN_ROLE: u64 = 200;
const MOD_ROLE: u64 = 100;
const MEMBER: u64 = 77;

struct Rig {
    dir: tempfile::TempDir,
    store: Arc<SerializedStore>,
    access: Arc<Access>,
    menus: Arc<MenuAccess>,
}

impl Rig {
    async fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let store = common::commands::store();
        store
            .write(
                ROLES,
                &RoleMap {
                    mod_role: Some(MOD_ROLE),
                    admin_role: Some(ADMIN_ROLE),
                    ..RoleMap::default()
                },
            )
            .await
            .unwrap();
        let access = common::commands::access(&store, &[OWNER]);
        let menus = Arc::new(MenuAccess {
            rosters: common::commands::rosters(Some(dir.path())),
            store: Arc::clone(&store),
            clock: Arc::new(SystemClock),
        });
        Self {
            dir,
            store,
            access,
            menus,
        }
    }

    fn give(&self) -> GiveMenuCommand {
        GiveMenuCommand {
            menus: Arc::clone(&self.menus),
            access: Arc::clone(&self.access),
        }
    }

    fn strip(&self) -> StripMenuCommand {
        StripMenuCommand {
            menus: Arc::clone(&self.menus),
            access: Arc::clone(&self.access),
        }
    }

    fn lines(&self, file: &str) -> Vec<String> {
        lines(&self.dir.path().join(file))
    }
}

fn lines(path: &Path) -> Vec<String> {
    std::fs::read_to_string(path)
        .map(|t| {
            t.lines()
                .map(str::to_owned)
                .filter(|l| !l.is_empty())
                .collect()
        })
        .unwrap_or_default()
}

fn caller(id: u64) -> Caller {
    match id {
        ADMIN => Caller::member(id, 0, &[ADMIN_ROLE]),
        MOD => Caller::member(id, 0, &[MOD_ROLE]),
        _ => Caller::user(id),
    }
}

fn give(by: u64, name: &str, tier: &str) -> Invocation {
    Invocation::new("givemenu", caller(by))
        .with(InvokedOption::value(
            "member",
            OptionKind::User,
            OptionValue::User(MEMBER),
        ))
        .with(InvokedOption::value(
            "playerid",
            OptionKind::String,
            OptionValue::String(name.into()),
        ))
        .with(InvokedOption::value(
            "tier",
            OptionKind::String,
            OptionValue::String(tier.into()),
        ))
}

fn strip(by: u64) -> Invocation {
    Invocation::new("stripmenu", caller(by)).with(InvokedOption::value(
        "member",
        OptionKind::User,
        OptionValue::User(MEMBER),
    ))
}

async fn run(command: &dyn SlashCommand, invocation: &Invocation) -> String {
    let responder = common::RecordingResponder::default();
    command
        .handle(CommandContext {
            invocation,
            responder: &responder,
        })
        .await
        .unwrap();
    responder.last_text()
}

#[tokio::test]
async fn a_tier_is_written_to_its_file_and_the_grant_and_binding_recorded() {
    let r = Rig::new().await;

    let reply = run(&r.give(), &give(OWNER, "Officer", "staff")).await;

    assert!(reply.contains("Staff menu granted"), "{reply}");
    assert_eq!(r.lines("staff.txt"), ["Officer"]);
    assert!(
        r.store
            .read_raw("menu_grants")
            .unwrap()
            .unwrap()
            .contains("Officer")
    );
    assert!(
        r.store
            .read_raw("menu_links")
            .unwrap()
            .unwrap()
            .contains("Officer")
    );
}

#[tokio::test]
async fn giving_another_tier_moves_the_player_and_keeps_everyone_else() {
    let r = Rig::new().await;
    std::fs::write(r.dir.path().join("jrstaff.txt"), "Officer\nOther\n").unwrap();

    let reply = run(&r.give(), &give(OWNER, "Officer", "srstaff")).await;

    assert!(reply.contains("moved from Jr. Staff"), "{reply}");
    assert_eq!(r.lines("srstaff.txt"), ["Officer"]);
    assert_eq!(r.lines("jrstaff.txt"), ["Other"]);
}

#[tokio::test]
async fn an_admin_gives_srstaff_and_below_but_never_manager_or_owner() {
    let r = Rig::new().await;
    for tier in ["owner", "manager"] {
        let reply = run(&r.give(), &give(ADMIN, "Officer", tier)).await;
        assert!(reply.contains("Owners only"), "{tier}: {reply}");
    }
    assert!(r.lines("owner.txt").is_empty());
    assert!(r.lines("manager.txt").is_empty());

    let reply = run(&r.give(), &give(ADMIN, "Officer", "srstaff")).await;
    assert!(reply.contains("granted"), "{reply}");
    assert_eq!(r.lines("srstaff.txt"), ["Officer"]);
}

#[tokio::test]
async fn an_owner_gives_owner_and_a_mod_gives_nothing() {
    let r = Rig::new().await;
    assert!(
        run(&r.give(), &give(OWNER, "Boss", "owner"))
            .await
            .contains("granted")
    );
    assert_eq!(r.lines("owner.txt"), ["Boss"]);

    let reply = run(&r.give(), &give(MOD, "Other", "jrstaff")).await;
    assert!(reply.contains("Not allowed"), "{reply}");
    assert!(r.lines("jrstaff.txt").is_empty());
}

#[tokio::test]
async fn an_admin_cannot_move_somebody_out_of_the_top_tiers() {
    let r = Rig::new().await;
    std::fs::write(r.dir.path().join("manager.txt"), "Officer\n").unwrap();

    let reply = run(&r.give(), &give(ADMIN, "Officer", "staff")).await;

    assert!(reply.contains("Owners only"), "{reply}");
    assert_eq!(r.lines("manager.txt"), ["Officer"]);
    assert!(r.lines("staff.txt").is_empty());
}

#[tokio::test]
async fn a_name_bound_to_somebody_else_is_refused() {
    let r = Rig::new().await;
    r.menus.bind("999", "Officer").await.unwrap();

    let reply = run(&r.give(), &give(OWNER, "Officer", "staff")).await;

    assert!(reply.contains("Name already bound"), "{reply}");
    assert!(r.lines("staff.txt").is_empty());
}

#[tokio::test]
async fn nothing_is_recorded_when_the_files_cannot_be_written() {
    let r = Rig::new().await;
    let gone = r.dir.path().join("missing");
    let menus = Arc::new(MenuAccess {
        rosters: common::commands::rosters(Some(&gone)),
        store: Arc::clone(&r.store),
        clock: Arc::new(SystemClock),
    });
    let command = GiveMenuCommand {
        menus,
        access: Arc::clone(&r.access),
    };

    let reply = run(&command, &give(OWNER, "Officer", "staff")).await;

    assert!(reply.contains("Not available"), "{reply}");
    assert!(reply.contains("ServerRoles"), "{reply}");
    assert!(r.store.read_raw("menu_grants").unwrap().is_none());
    assert!(r.store.read_raw("menu_links").unwrap().is_none());
}

#[tokio::test]
async fn stripping_takes_the_name_off_every_tier_and_drops_the_grant() {
    let r = Rig::new().await;
    run(&r.give(), &give(OWNER, "Officer", "staff")).await;
    std::fs::write(r.dir.path().join("jrstaff.txt"), "Officer\nOther\n").unwrap();

    let reply = run(&r.strip(), &strip(ADMIN)).await;

    assert!(reply.contains("Menu access removed"), "{reply}");
    assert!(r.lines("staff.txt").is_empty());
    assert_eq!(r.lines("jrstaff.txt"), ["Other"]);
    assert!(
        !r.store
            .read_raw("menu_grants")
            .unwrap()
            .unwrap()
            .contains("Officer")
    );
    assert!(
        r.store
            .read_raw("menu_links")
            .unwrap()
            .unwrap()
            .contains("Officer"),
        "the binding stays"
    );
}

#[tokio::test]
async fn an_admin_cannot_strip_an_owner_tier() {
    let r = Rig::new().await;
    run(&r.give(), &give(OWNER, "Boss", "owner")).await;

    let reply = run(&r.strip(), &strip(ADMIN)).await;

    assert!(reply.contains("Owners only"), "{reply}");
    assert_eq!(r.lines("owner.txt"), ["Boss"]);
    assert!(run(&r.strip(), &strip(OWNER)).await.contains("removed"));
    assert!(r.lines("owner.txt").is_empty());
}

#[tokio::test]
async fn the_tier_option_offers_the_five_files() {
    let r = Rig::new().await;
    let built = r.give().build();
    let tier = built.options.iter().find(|o| o.name == "tier").unwrap();
    let values: Vec<String> = tier.choices.iter().map(|c| c.value.to_string()).collect();
    assert_eq!(values, ["owner", "manager", "srstaff", "staff", "jrstaff"]);
    assert!(tier.required);
}

#[tokio::test]
async fn unlinking_breaks_the_binding() {
    let r = Rig::new().await;
    r.menus.bind("77", "Officer").await.unwrap();
    let command = UnlinkNameCommand {
        store: Arc::clone(&r.store),
        access: Arc::clone(&r.access),
    };
    let invocation = Invocation::new("unlinkname", Caller::user(OWNER)).with(InvokedOption::value(
        "member",
        OptionKind::User,
        OptionValue::User(MEMBER),
    ));
    assert!(run(&command, &invocation).await.contains("Binding removed"));
    assert!(
        !r.store
            .read_raw("menu_links")
            .unwrap()
            .unwrap()
            .contains("Officer")
    );
}
