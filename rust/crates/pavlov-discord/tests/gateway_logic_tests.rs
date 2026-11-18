//! The gateway's Discord-agnostic half: component routing, the autocomplete answer, command
//! registration scopes and the staff-guild member cache. In the C# these lived inside
//! `DiscordGateway` and `HomeGuildMembers`, which needed a live socket client, so only
//! `ClearGlobals` had a seam; the rules are pinned here instead.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_discord::autocomplete::{AutocompleteRequest, MAX_CHOICES, answer};
use pavlov_discord::caller::{Caller, MemberView};
use pavlov_discord::command::{Choice, CommandSpec};
use pavlov_discord::component::Modal;
use pavlov_discord::component_router::{ComponentOutcome, ComponentRouter, STALE_CONTROL};
use pavlov_discord::home_guild::{HomeGuildMembers, MemberSource};
use pavlov_discord::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply};
use pavlov_discord::paged::ComponentHandler;
use pavlov_discord::registration::{
    CommandRegistry, GuildRef, RegistrationSettings, Scope, as_user_app, clear_globals,
    clear_other_scope, faction_set, main_set, register_faction, register_in_joined_guild,
    register_main, validated,
};
use pavlov_discord::runner::AccessPrimer;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::CommandBlacklist;

const BARRED: u64 = 666;

// ---- components ----

#[derive(Default)]
struct Press {
    said: Mutex<Vec<(Reply, bool)>>,
    followups: Mutex<Vec<Reply>>,
    refuse_respond: bool,
}

impl ComponentResponder for Press {
    fn update(&self, _: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn respond(&self, reply: Reply, ephemeral: bool) -> BoxFuture<'_, Result<(), DiscordError>> {
        let refuse = self.refuse_respond;
        if !refuse {
            self.said.lock().unwrap().push((reply, ephemeral));
        }
        Box::pin(async move {
            if refuse {
                Err(DiscordError("already acknowledged".into()))
            } else {
                Ok(())
            }
        })
    }
    fn edit_original(&self, _: Reply) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn acknowledge(&self) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async { Ok(()) })
    }
    fn show_modal(&self, _: Modal) -> BoxFuture<'_, Result<(), DiscordError>> {
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

struct Handler {
    prefix: &'static str,
    calls: AtomicUsize,
    explode: bool,
}

impl Handler {
    fn new(prefix: &'static str) -> Arc<Self> {
        Arc::new(Self {
            prefix,
            calls: AtomicUsize::new(0),
            explode: false,
        })
    }
}

impl ComponentHandler for Handler {
    fn prefix(&self) -> &str {
        self.prefix
    }
    fn handle<'a>(
        &'a self,
        _: &'a ComponentInvocation,
        _: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        self.calls.fetch_add(1, Ordering::SeqCst);
        let explode = self.explode;
        Box::pin(async move {
            assert!(!explode, "handler exploded");
        })
    }
}

#[derive(Default)]
struct CountingPrimer {
    primed: Mutex<Vec<u64>>,
}

impl AccessPrimer for CountingPrimer {
    fn prime(&self, user_id: u64) -> BoxFuture<'_, ()> {
        self.primed.lock().unwrap().push(user_id);
        Box::pin(async {})
    }
}

fn blacklist() -> Arc<CommandBlacklist> {
    Arc::new(CommandBlacklist::new(
        common::commands::store(),
        Arc::new(|_| false),
        &[BARRED],
    ))
}

fn click(prefix: &str, caller: Caller) -> ComponentInvocation {
    ComponentInvocation {
        custom_id: ComponentId::encode(prefix, &["1"]).unwrap(),
        caller,
        username: "someone".into(),
        ..ComponentInvocation::default()
    }
}

fn router(
    handlers: Vec<Arc<dyn ComponentHandler>>,
    primer: Option<Arc<dyn AccessPrimer>>,
) -> ComponentRouter {
    ComponentRouter::new(
        handlers,
        Some(blacklist()),
        primer,
        Arc::new(MetricsRegistry::default()),
    )
}

#[tokio::test]
async fn a_click_reaches_the_handler_that_owns_its_prefix() {
    let paged = Handler::new("pg");
    let other = Handler::new("cfg");
    let router = router(vec![paged.clone(), other.clone()], None);
    let press = Press::default();

    let outcome = router.route(&click("pg", Caller::user(5)), &press).await;

    assert_eq!(outcome, ComponentOutcome::Handled);
    assert_eq!(paged.calls.load(Ordering::SeqCst), 1);
    assert_eq!(other.calls.load(Ordering::SeqCst), 0);
    assert!(press.said.lock().unwrap().is_empty());
}

#[tokio::test]
async fn a_barred_user_is_refused_privately_and_the_handler_never_runs() {
    let handler = Handler::new("pg");
    let router = router(vec![handler.clone()], None);
    let press = Press::default();

    let outcome = router
        .route(&click("pg", Caller::user(BARRED)), &press)
        .await;

    assert_eq!(outcome, ComponentOutcome::Barred);
    assert_eq!(handler.calls.load(Ordering::SeqCst), 0);
    let said = press.said.lock().unwrap();
    assert!(said[0].1, "the refusal is ephemeral");
    assert!(
        said[0].0.embeds[0]
            .title
            .as_deref()
            .unwrap()
            .ends_with("Blacklisted")
    );
}

#[tokio::test]
async fn a_control_from_an_older_build_is_told_so() {
    let router = router(vec![Handler::new("pg")], None);
    let press = Press::default();

    let outcome = router.route(&click("gone", Caller::user(5)), &press).await;

    assert_eq!(outcome, ComponentOutcome::Unknown);
    let said = press.said.lock().unwrap();
    assert_eq!(said[0].0.content.as_deref(), Some(STALE_CONTROL));
    assert!(said[0].1);
}

#[tokio::test]
async fn an_empty_custom_id_is_ignored() {
    let router = router(vec![Handler::new("pg")], None);
    let press = Press::default();
    let mut invocation = click("pg", Caller::user(5));
    invocation.custom_id.clear();

    assert_eq!(
        router.route(&invocation, &press).await,
        ComponentOutcome::Ignored
    );
    assert!(press.said.lock().unwrap().is_empty());
}

#[tokio::test]
async fn only_a_role_less_caller_is_primed() {
    let primer = Arc::new(CountingPrimer::default());
    let router = router(
        vec![Handler::new("pg")],
        Some(primer.clone() as Arc<dyn AccessPrimer>),
    );
    let press = Press::default();

    router.route(&click("pg", Caller::user(1)), &press).await;
    router
        .route(&click("pg", Caller::member(2, 9, &[])), &press)
        .await;
    router
        .route(&click("pg", Caller::member(3, 9, &[77])), &press)
        .await;

    assert_eq!(*primer.primed.lock().unwrap(), [1, 2]);
}

#[tokio::test]
async fn a_panicking_handler_is_apologised_for_with_a_correlation_id() {
    let handler = Arc::new(Handler {
        prefix: "pg",
        calls: AtomicUsize::new(0),
        explode: true,
    });
    let router = router(vec![handler], None);
    let press = Press::default();

    let ComponentOutcome::Failed { correlation_id } =
        router.route(&click("pg", Caller::user(5)), &press).await
    else {
        panic!("expected a failure")
    };

    let said = press.said.lock().unwrap();
    assert!(
        said[0]
            .0
            .content
            .as_deref()
            .unwrap()
            .contains(&format!("`{correlation_id}`"))
    );
}

#[tokio::test]
async fn the_apology_falls_back_to_a_followup_once_the_click_was_acknowledged() {
    let handler = Arc::new(Handler {
        prefix: "pg",
        calls: AtomicUsize::new(0),
        explode: true,
    });
    let router = router(vec![handler], None);
    let press = Press {
        refuse_respond: true,
        ..Press::default()
    };

    router.route(&click("pg", Caller::user(5)), &press).await;

    assert_eq!(press.followups.lock().unwrap().len(), 1);
}

#[test]
fn a_second_handler_for_one_prefix_does_not_replace_the_first() {
    let first = Handler::new("pg");
    let router = router(vec![first, Handler::new("pg"), Handler::new("cfg")], None);
    assert_eq!(router.prefixes(), ["cfg", "pg"]);
}

// ---- autocomplete ----

fn request(user: u64, focused: &str, faction: Option<&str>) -> AutocompleteRequest {
    AutocompleteRequest {
        user_id: user,
        username: "mod".into(),
        focused: focused.into(),
        typed: "al".into(),
        faction: faction.map(str::to_owned),
    }
}

#[tokio::test]
async fn autocomplete_passes_the_focused_field_and_the_sibling_faction() {
    let suggester = common::RecordingSuggester::default();
    let seen = Mutex::new(None);

    answer(
        &request(5, "rank", Some("lspd")),
        Some(&blacklist()),
        &MetricsRegistry::default(),
        &suggester,
        |focused, typed, faction| {
            *seen.lock().unwrap() = Some((
                focused.to_owned(),
                typed.to_owned(),
                faction.map(str::to_owned),
            ));
            vec![Choice::string("Sergeant", "Sergeant")]
        },
    )
    .await;

    assert_eq!(
        seen.lock().unwrap().clone().unwrap(),
        ("rank".to_owned(), "al".to_owned(), Some("lspd".to_owned()))
    );
    assert_eq!(suggester.offered.lock().unwrap()[0].len(), 1);
}

#[tokio::test]
async fn a_barred_user_is_offered_nothing_and_the_names_are_never_read() {
    let suggester = common::RecordingSuggester::default();
    let mut asked = false;

    answer(
        &request(BARRED, "player", None),
        Some(&blacklist()),
        &MetricsRegistry::default(),
        &suggester,
        |_, _, _| {
            asked = true;
            vec![Choice::string("Alice", "Alice")]
        },
    )
    .await;

    assert!(!asked);
    assert_eq!(*suggester.offered.lock().unwrap(), [Vec::<Choice>::new()]);
}

#[tokio::test]
async fn autocomplete_never_offers_more_than_discord_accepts() {
    let suggester = common::RecordingSuggester::default();
    answer(
        &request(5, "player", None),
        None,
        &MetricsRegistry::default(),
        &suggester,
        |_, _, _| {
            (0..40)
                .map(|i| Choice::string(format!("p{i}"), format!("p{i}")))
                .collect()
        },
    )
    .await;
    assert_eq!(suggester.offered.lock().unwrap()[0].len(), MAX_CHOICES);
}

// ---- registration ----

#[derive(Default)]
struct FakeRegistry {
    guilds: Vec<GuildRef>,
    refusing: Vec<u64>,
    global: Mutex<Vec<String>>,
    by_guild: Mutex<Vec<(u64, Vec<String>)>>,
    global_overwrites: AtomicUsize,
    stale_global: usize,
    stale_guild: usize,
}

fn names(specs: &[CommandSpec]) -> Vec<String> {
    specs.iter().map(|s| s.name.clone()).collect()
}

impl CommandRegistry for FakeRegistry {
    fn guilds(&self) -> Vec<GuildRef> {
        self.guilds.clone()
    }
    fn overwrite_guild<'a>(
        &'a self,
        guild_id: u64,
        specs: &'a [CommandSpec],
    ) -> BoxFuture<'a, Result<(), DiscordError>> {
        Box::pin(async move {
            if self.refusing.contains(&guild_id) {
                return Err(DiscordError("missing access".into()));
            }
            self.by_guild.lock().unwrap().push((guild_id, names(specs)));
            Ok(())
        })
    }
    fn overwrite_global<'a>(
        &'a self,
        specs: &'a [CommandSpec],
    ) -> BoxFuture<'a, Result<(), DiscordError>> {
        Box::pin(async move {
            self.global_overwrites.fetch_add(1, Ordering::SeqCst);
            *self.global.lock().unwrap() = names(specs);
            Ok(())
        })
    }
    fn global_count(&self) -> BoxFuture<'_, Result<usize, DiscordError>> {
        Box::pin(async { Ok(self.stale_global) })
    }
    fn guild_count(&self, _: u64) -> BoxFuture<'_, Result<usize, DiscordError>> {
        Box::pin(async { Ok(self.stale_guild) })
    }
}

fn guild(id: u64) -> GuildRef {
    GuildRef {
        id,
        name: format!("guild{id}"),
    }
}

fn specs() -> Vec<CommandSpec> {
    vec![
        CommandSpec::new("ban", "Ban a player"),
        CommandSpec::new("whitelist", "Faction whitelist"),
    ]
}

#[test]
fn the_global_set_is_cleared_only_when_every_guild_took_the_update() {
    assert!(clear_globals(3, 0));
    assert!(!clear_globals(2, 1));
    assert!(!clear_globals(0, 0));
}

#[test]
fn the_faction_commands_move_to_the_whitelist_bot_only_when_it_is_on() {
    assert_eq!(names(&main_set(specs(), false)), ["ban", "whitelist"]);
    assert_eq!(names(&main_set(specs(), true)), ["ban"]);
    assert_eq!(names(&faction_set(specs())), ["whitelist"]);
}

#[test]
fn a_malformed_command_is_dropped_and_the_rest_kept() {
    let mut bad = CommandSpec::new("Bad Name", "spaces are not allowed");
    bad.description.clear();
    let kept = validated(vec![bad, CommandSpec::new("ok", "fine")], "main");
    assert_eq!(names(&kept), ["ok"]);
}

#[test]
fn a_user_app_is_offered_in_every_install_and_context() {
    let wide = as_user_app(specs());
    let installs = wide[0].installs.unwrap();
    let contexts = wide[0].contexts.unwrap();
    assert!(installs.guild && installs.user);
    assert!(contexts.guild && contexts.bot_dm && contexts.private_channel);
}

#[tokio::test]
async fn user_app_registers_globally_even_with_a_guild_id() {
    let registry = FakeRegistry {
        guilds: vec![guild(1)],
        ..FakeRegistry::default()
    };
    let scope = register_main(
        &registry,
        &specs(),
        RegistrationSettings {
            user_app: true,
            guild_id: Some(1),
            faction_bot_enabled: false,
        },
    )
    .await
    .unwrap();
    assert_eq!(scope, Scope::Global);
    assert!(registry.by_guild.lock().unwrap().is_empty());
    assert_eq!(registry.global.lock().unwrap().len(), 2);
}

#[tokio::test]
async fn a_configured_guild_registers_there_and_an_absent_one_falls_back_to_global() {
    let registry = FakeRegistry {
        guilds: vec![guild(1), guild(2)],
        ..FakeRegistry::default()
    };
    let settings = |id| RegistrationSettings {
        guild_id: Some(id),
        ..RegistrationSettings::default()
    };

    let scope = register_main(&registry, &specs(), settings(2))
        .await
        .unwrap();
    assert_eq!(scope, Scope::Guild);
    assert_eq!(registry.by_guild.lock().unwrap()[0].0, 2);

    let scope = register_main(&registry, &specs(), settings(9))
        .await
        .unwrap();
    assert_eq!(scope, Scope::Global);
}

#[tokio::test]
async fn without_a_guild_id_every_guild_gets_the_set() {
    let registry = FakeRegistry {
        guilds: vec![guild(1), guild(2)],
        ..FakeRegistry::default()
    };
    let scope = register_main(&registry, &specs(), RegistrationSettings::default())
        .await
        .unwrap();
    assert_eq!(scope, Scope::Guild);
    assert_eq!(registry.by_guild.lock().unwrap().len(), 2);
    assert_eq!(registry.global_overwrites.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn one_guild_refusing_sends_the_whole_set_global() {
    let registry = FakeRegistry {
        guilds: vec![guild(1), guild(2)],
        refusing: vec![2],
        ..FakeRegistry::default()
    };
    let scope = register_main(&registry, &specs(), RegistrationSettings::default())
        .await
        .unwrap();
    assert_eq!(scope, Scope::Global);
    assert_eq!(registry.global.lock().unwrap().len(), 2);
}

#[tokio::test]
async fn the_unused_scope_is_emptied_both_ways_round() {
    let after_guild = FakeRegistry {
        guilds: vec![guild(1)],
        stale_global: 4,
        ..FakeRegistry::default()
    };
    assert_eq!(clear_other_scope(&after_guild, Scope::Guild).await, 4);
    assert!(after_guild.global.lock().unwrap().is_empty());
    assert_eq!(after_guild.global_overwrites.load(Ordering::SeqCst), 1);

    let after_global = FakeRegistry {
        guilds: vec![guild(1), guild(2)],
        stale_guild: 3,
        ..FakeRegistry::default()
    };
    assert_eq!(clear_other_scope(&after_global, Scope::Global).await, 6);
    assert!(
        after_global
            .by_guild
            .lock()
            .unwrap()
            .iter()
            .all(|(_, set)| set.is_empty())
    );

    let nothing_stale = FakeRegistry::default();
    assert_eq!(clear_other_scope(&nothing_stale, Scope::Guild).await, 0);
    assert_eq!(nothing_stale.global_overwrites.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn a_joined_guild_gets_the_set_only_when_registration_is_guild_scoped() {
    let registry = FakeRegistry::default();
    assert!(register_in_joined_guild(&registry, &guild(5), &specs(), Scope::Guild, false).await);
    assert!(!register_in_joined_guild(&registry, &guild(5), &specs(), Scope::Global, false).await);
    assert!(!register_in_joined_guild(&registry, &guild(5), &specs(), Scope::Guild, true).await);
    assert_eq!(registry.by_guild.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn the_whitelist_bot_keeps_its_global_set_while_any_guild_refused() {
    let all_took_it = FakeRegistry {
        guilds: vec![guild(1), guild(2)],
        ..FakeRegistry::default()
    };
    let done = register_faction(&all_took_it, &faction_set(specs())).await;
    assert_eq!((done.registered, done.failed, done.global), (2, 0, false));
    assert_eq!(all_took_it.global_overwrites.load(Ordering::SeqCst), 1);
    assert!(all_took_it.global.lock().unwrap().is_empty());

    let one_refused = FakeRegistry {
        guilds: vec![guild(1), guild(2)],
        refusing: vec![1],
        ..FakeRegistry::default()
    };
    register_faction(&one_refused, &faction_set(specs())).await;
    assert_eq!(one_refused.global_overwrites.load(Ordering::SeqCst), 0);

    let nowhere = FakeRegistry::default();
    let done = register_faction(&nowhere, &faction_set(specs())).await;
    assert!(done.global);
    assert_eq!(*nowhere.global.lock().unwrap(), ["whitelist"]);
}

// ---- the staff guild ----

struct Members {
    guilds: Vec<u64>,
    fetches: AtomicUsize,
    fail: bool,
    member: Option<MemberView>,
}

impl MemberSource for Members {
    fn guild_ids(&self) -> Vec<u64> {
        self.guilds.clone()
    }
    fn fetch_member(
        &self,
        guild_id: u64,
        _: u64,
    ) -> BoxFuture<'_, Result<Option<MemberView>, DiscordError>> {
        self.fetches.fetch_add(1, Ordering::SeqCst);
        let fail = self.fail;
        let member = self.member.clone().map(|mut m| {
            m.guild_id = guild_id;
            m
        });
        Box::pin(async move {
            if fail {
                Err(DiscordError("gateway unavailable".into()))
            } else {
                Ok(member)
            }
        })
    }
}

fn members(guilds: &[u64], member: Option<MemberView>, fail: bool) -> Arc<Members> {
    Arc::new(Members {
        guilds: guilds.to_vec(),
        fetches: AtomicUsize::new(0),
        fail,
        member,
    })
}

fn staff() -> MemberView {
    MemberView {
        guild_id: 0,
        role_ids: vec![42],
        administrator: false,
    }
}

#[test]
fn the_staff_guild_is_home_then_guild_id_then_the_only_guild() {
    let source = members(&[1, 2], None, false);
    assert_eq!(
        HomeGuildMembers::new(source.clone(), Some(2), Some(1)).home(),
        Some(2)
    );
    assert_eq!(
        HomeGuildMembers::new(source.clone(), Some(9), Some(1)).home(),
        None,
        "a HOME_GUILD_ID the bot is not in does not fall back"
    );
    assert_eq!(
        HomeGuildMembers::new(source.clone(), None, Some(1)).home(),
        Some(1)
    );
    assert_eq!(
        HomeGuildMembers::new(source, None, None).home(),
        None,
        "several guilds and nothing named is ambiguous"
    );
    assert_eq!(
        HomeGuildMembers::new(members(&[7], None, false), None, None).home(),
        Some(7)
    );
}

#[tokio::test]
async fn a_lookup_is_cached_for_the_window_including_a_miss() {
    let source = members(&[1], Some(staff()), false);
    let home = HomeGuildMembers::new(source.clone(), None, None);

    home.prime_user(5).await;
    home.prime_user(5).await;

    assert_eq!(source.fetches.load(Ordering::SeqCst), 1);
    assert_eq!(home.member(5).unwrap().role_ids, [42]);
    assert_eq!(home.member(5).unwrap().guild_id, 1);

    let nobody = members(&[1], None, false);
    let home = HomeGuildMembers::new(nobody.clone(), None, None);
    home.prime_user(5).await;
    home.prime_user(5).await;
    assert_eq!(
        nobody.fetches.load(Ordering::SeqCst),
        1,
        "a miss is cached too"
    );
    assert!(home.member(5).is_none());
}

#[tokio::test]
async fn an_error_is_not_cached_and_an_expired_entry_is_looked_up_again() {
    let failing = members(&[1], Some(staff()), true);
    let home = HomeGuildMembers::new(failing.clone(), None, None);
    home.prime_user(5).await;
    home.prime_user(5).await;
    assert_eq!(failing.fetches.load(Ordering::SeqCst), 2);

    let source = members(&[1], Some(staff()), false);
    let home = HomeGuildMembers::new(source.clone(), None, None).with_window(Duration::ZERO);
    home.prime_user(5).await;
    home.prime_user(5).await;
    assert_eq!(source.fetches.load(Ordering::SeqCst), 2);
    assert!(home.member(5).is_none(), "nothing is fresh with no window");
}

#[test]
fn the_choice_of_staff_guild_is_described_for_the_startup_log() {
    let source = members(&[], None, false);
    assert_eq!(
        HomeGuildMembers::new(source.clone(), Some(3), None).describe(),
        "HOME_GUILD_ID 3"
    );
    assert!(
        HomeGuildMembers::new(source, None, Some(4))
            .describe()
            .starts_with("GUILD_ID 4")
    );
}
