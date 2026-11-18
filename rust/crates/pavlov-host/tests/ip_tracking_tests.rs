//! Port of `IpTrackingTests.cs` (`IpTrackingServiceTests`): log lines in, account knowledge
//! and flags out, over the real file store.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use common::file_store;
use futures_util::future::BoxFuture;
use pavlov_core::evasion::FlagMatch;
use pavlov_core::logs::{KillEvent, RconAction};
use pavlov_core::time::SystemClock;
use pavlov_host::logs::{FlaggedJoin, IpTrackingService, LogLine, PlayerJoined, RconLogged};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::MasterNames;

const FILE: &str = "server1.log";
const ACCEPT: &str =
    "[2026.07.15-12.00.00:000]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.5:7777";
const LOGIN: &str =
    "[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Alice userId: EOS:0002abc";
const CLOSE: &str = "[2026.07.15-12.30.00:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777";

struct Fixture {
    _dir: tempfile::TempDir,
    service: IpTrackingService,
}

impl Fixture {
    fn new() -> Self {
        Self::with(|s| s)
    }

    fn with(build: impl FnOnce(IpTrackingService) -> IpTrackingService) -> Self {
        let dir = tempfile::tempdir().unwrap();
        let service = IpTrackingService::new(
            file_store(dir.path()),
            Arc::new(MetricsRegistry::default()),
            Arc::new(SystemClock),
        );
        Self {
            service: build(service),
            _dir: dir,
        }
    }

    async fn feed(&self, text: &str) {
        self.service.ingest(&LogLine::new(FILE, text)).await;
    }
}

/// Collects every event of one kind.
fn collect<T: Clone + Send + 'static>(
    handlers: &pavlov_host::handlers::Handlers<T>,
) -> Arc<Mutex<Vec<T>>> {
    let seen = Arc::new(Mutex::new(Vec::new()));
    let sink = Arc::clone(&seen);
    handlers.subscribe(move |value| {
        sink.lock().unwrap().push(value);
        Box::pin(async {})
    });
    seen
}

#[tokio::test]
async fn a_join_is_recorded_with_its_guessed_address() {
    let f = Fixture::new();
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;

    let account = f.service.account("0002abc").unwrap();
    assert_eq!(account.name(), Some("Alice"));
    assert_eq!(account.guessed_ips, ["203.0.113.5"]);
    assert!(account.confirmed_ips.is_empty()); // a correlated address is NOT confirmed
}

#[tokio::test]
async fn a_disconnect_confirms_the_address_and_promotes_it_out_of_the_guess_list() {
    let f = Fixture::new();
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;
    f.feed(CLOSE).await;

    let account = f.service.account("0002abc").unwrap();
    assert_eq!(account.confirmed_ips, ["203.0.113.5"]);
    assert!(account.guessed_ips.is_empty());
}

#[tokio::test]
async fn an_ambiguous_join_records_no_address_at_all() {
    let f = Fixture::new();
    f.feed(ACCEPT).await;
    f.feed("[2026.07.15-12.00.00:500]LogNet: NotifyAcceptingConnection accepted from: 203.0.113.6:7777").await;
    f.feed(LOGIN).await;

    assert!(f.service.account("0002abc").unwrap().guessed_ips.is_empty());
}

#[tokio::test]
async fn names_are_kept_most_recent_first() {
    let f = Fixture::new();
    f.feed(LOGIN).await;
    f.feed("[2026.07.15-13.00.00:000]LogNet: Login request: ?Name=AliceV2 userId: EOS:0002abc")
        .await;

    assert_eq!(
        f.service.account("0002abc").unwrap().names,
        ["AliceV2", "Alice"]
    );
}

#[tokio::test]
async fn an_account_is_findable_by_any_name_it_has_used() {
    let f = Fixture::new();
    f.feed(LOGIN).await;
    f.feed("[2026.07.15-13.00.00:000]LogNet: Login request: ?Name=AliceV2 userId: EOS:0002abc")
        .await;

    assert_eq!(f.service.account_by_name("Alice").unwrap().id, "0002abc");
    assert_eq!(f.service.account_by_name("alicev2").unwrap().id, "0002abc");
}

#[tokio::test]
async fn an_untracked_name_leaves_no_trail() {
    let f = Fixture::new();
    f.service.untrack("Owner");
    f.feed("[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Owner userId: EOS:0009owner")
        .await;

    assert_eq!(f.service.account("0009owner"), None);
}

struct OnlyMaster(&'static str);

impl MasterNames for OnlyMaster {
    fn is_master(&self, who: &str) -> bool {
        who.eq_ignore_ascii_case(self.0)
    }
    fn is_exempt(&self, who: &str) -> bool {
        self.is_master(who)
    }
    fn is_protected(&self, _: &str) -> bool {
        false
    }
    fn exempt<'a>(&'a self, _: &'a str, _: Option<Duration>) -> BoxFuture<'a, ()> {
        Box::pin(async {})
    }
}

#[tokio::test]
async fn a_master_still_appears_in_the_join_log() {
    // Untracked means UNRECORDED, not invisible - and still never an address.
    let f = Fixture::with(|s| s.with_masters(Arc::new(OnlyMaster("Owner"))));
    f.service.untrack("Owner");
    let seen = collect::<PlayerJoined>(&f.service.joined);

    f.feed(ACCEPT).await;
    f.feed("[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Owner userId: EOS:0009owner")
        .await;

    let seen = seen.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].name.as_deref(), Some("Owner"));
    assert_eq!(seen[0].ip, None); // no address, ever
    assert_eq!(f.service.account("0009owner"), None); // and still nothing recorded
}

#[tokio::test]
async fn an_ignored_name_that_is_not_a_master_stays_silent() {
    let f = Fixture::new();
    f.service.untrack("Watcher");
    let seen = collect::<PlayerJoined>(&f.service.joined);

    f.feed("[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Watcher userId: EOS:0009watch")
        .await;

    assert!(seen.lock().unwrap().is_empty());
}

#[tokio::test]
async fn a_placeholder_id_is_not_tracked() {
    let f = Fixture::new();
    f.feed("[2026.07.15-12.00.01:000]LogNet: Login request: ?Name=Alice userId: INVALID")
        .await;
    assert_eq!(f.service.account("INVALID"), None);
}

#[tokio::test]
async fn banning_an_online_player_defers_the_flag_until_their_disconnect_confirms_it() {
    let f = Fixture::new();
    f.feed(ACCEPT).await;
    f.feed(LOGIN).await;

    let outcome = f.service.request_flag("0002abc", true).await;
    assert!(outcome.pending);
    assert!(outcome.ips.is_empty());
    assert!(f.service.load_flags().ips.is_empty());

    // The kick produces the disconnect, which resolves it.
    f.feed(CLOSE).await;
    assert!(f.service.load_flags().ips.contains("203.0.113.5"));
}

#[tokio::test]
async fn a_temp_ban_flags_the_address_but_not_the_account_id() {
    let f = Fixture::new();
    f.feed(CLOSE).await;
    f.service.request_flag("0002abc", false).await;

    let flags = f.service.load_flags();
    assert!(flags.ips.contains("203.0.113.5"));
    assert!(!flags.ids.contains("0002abc"));
}

#[tokio::test]
async fn a_permanent_ban_flags_the_account_id_too() {
    let f = Fixture::new();
    f.feed(CLOSE).await;
    f.service.request_flag("0002abc", true).await;
    assert!(f.service.load_flags().ids.contains("0002abc"));
}

#[tokio::test]
async fn a_flagged_join_raises_the_event_once() {
    let f = Fixture::new();
    let hits = collect::<FlaggedJoin>(&f.service.flagged);

    f.feed(CLOSE).await;
    f.service.request_flag("0002abc", true).await;

    f.feed("[2026.07.16-12.00.01:000]LogNet: Login request: ?Name=Alice userId: EOS:0002abc")
        .await;
    assert_eq!(hits.lock().unwrap().len(), 1);

    // Debounced: a retrying player must not fire a fresh ban every few seconds.
    f.feed("[2026.07.16-12.00.05:000]LogNet: Login request: ?Name=Alice userId: EOS:0002abc")
        .await;
    assert_eq!(hits.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn clearing_flags_undoes_what_the_ban_created() {
    let f = Fixture::new();
    f.feed(CLOSE).await;
    f.service.request_flag("0002abc", true).await;

    f.service.clear_flags("0002abc").await;

    let flags = f.service.load_flags();
    assert!(flags.ips.is_empty());
    assert!(flags.ids.is_empty());
}

#[tokio::test]
async fn clearing_flags_leaves_a_manual_block_alone() {
    let f = Fixture::new();
    f.feed(CLOSE).await;
    f.service.flag_address_manually("203.0.113.5").await;
    f.service.request_flag("0002abc", true).await;

    f.service.clear_flags("0002abc").await;

    assert!(f.service.load_flags().manual_ips.contains("203.0.113.5"));
}

#[tokio::test]
async fn a_manual_block_catches_the_address_on_the_certain_pairing() {
    // The join-time address is almost never a confident guess; the pairing is certain.
    let f = Fixture::new();
    let flagged = collect::<FlaggedJoin>(&f.service.flagged);

    f.service.flag_address_manually("203.0.113.5").await;
    f.feed(CLOSE).await;

    let flagged = flagged.lock().unwrap();
    assert_eq!(flagged.len(), 1);
    assert_eq!(flagged[0].account_id, "0002abc");
    assert_eq!(flagged[0].ip.as_deref(), Some("203.0.113.5"));
    assert_eq!(flagged[0].verdict.matched, FlagMatch::Ip);
    assert!(flagged[0].verdict.manual);
}

#[tokio::test]
async fn a_ban_derived_address_flag_is_not_acted_on_from_a_certain_pairing() {
    // Acting on it at every disconnect would auto-ban whoever else shares the address.
    let f = Fixture::new();
    let hits = collect::<FlaggedJoin>(&f.service.flagged);

    f.feed(CLOSE).await;
    f.service.request_flag("0002abc", false).await;
    f.feed("[2026.07.15-13.00.00:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777").await;

    assert!(hits.lock().unwrap().is_empty());
}

#[tokio::test]
async fn a_manual_block_is_debounced_across_repeated_disconnects() {
    let f = Fixture::new();
    let hits = collect::<FlaggedJoin>(&f.service.flagged);

    f.service.flag_address_manually("203.0.113.5").await;
    f.feed(CLOSE).await;
    f.feed("[2026.07.15-12.31.00:000]LogNet: UChannel::Close: UniqueId: EOS:0002abc RemoteAddr: 203.0.113.5:7777").await;

    assert_eq!(hits.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn an_rcon_plus_command_is_emitted_with_its_result_line_flag() {
    let f = Fixture::new();
    let events = collect::<RconLogged>(&f.service.rcon);

    f.feed("[2026.09.16-21.39.59:249][906]LogTemp: Warning: Rcon Plus Command Executed: GiveMenu Holosight1 Bob").await;
    assert!(events.lock().unwrap().is_empty()); // buffered, waiting for the result line

    f.feed("Successful: true        Instigated By: Holosight1")
        .await;
    let events = events.lock().unwrap();
    assert_eq!(events.len(), 1);
    let action: &RconAction = &events[0].action;
    assert!(action.plus);
    assert_eq!(action.verb, "GiveMenu");
    assert_eq!(action.successful, Some(true));
}

#[tokio::test]
async fn a_failed_rcon_plus_command_carries_successful_false() {
    let f = Fixture::new();
    let events = collect::<RconLogged>(&f.service.rcon);

    f.feed(
        "[t][906]LogTemp: Error: Rcon Plus Command Executed: RemoveAccessManager DontFuccnKnowMe",
    )
    .await;
    f.feed("Successful: false        Instigated By: Console")
        .await;

    assert_eq!(events.lock().unwrap()[0].action.successful, Some(false));
}

#[tokio::test]
async fn a_base_rcon_command_emits_at_once_with_no_result() {
    let f = Fixture::new();
    let events = collect::<RconLogged>(&f.service.rcon);

    f.feed("[2026.09.16-04.08.14:875][471]LogTemp: Rcon: BanPlayer hhhhhhhhhh")
        .await;

    let events = events.lock().unwrap();
    assert_eq!(events.len(), 1);
    assert!(!events[0].action.plus);
    assert_eq!(events[0].action.verb, "Ban"); // engine label mapped back to the sent verb
    assert_eq!(events[0].action.successful, None);
}

#[tokio::test]
async fn a_kill_record_split_across_lines_is_assembled() {
    let f = Fixture::new();
    let kills = collect::<KillEvent>(&f.service.kill);

    f.feed(r#"LogTemp: KillData {"Killer": "Alice","#).await;
    assert!(kills.lock().unwrap().is_empty()); // incomplete: nothing reported yet

    f.feed(r#"  "Killed": "Bob", "KilledBy": "AK47"}"#).await;
    let kills = kills.lock().unwrap();
    assert_eq!(kills.len(), 1);
    assert_eq!(kills[0].killer.as_deref(), Some("Alice"));
    assert_eq!(kills[0].killed.as_deref(), Some("Bob"));
    assert_eq!(kills[0].killed_by.as_deref(), Some("AK47"));
}

#[tokio::test]
async fn each_kill_keeps_its_own_weapon_across_split_records() {
    // Rust-only: the bug where every kill after the first carried the previous one's weapon.
    let f = Fixture::new();
    let kills = collect::<KillEvent>(&f.service.kill);

    for (killer, killed, weapon) in [("A", "B", "obrez"), ("C", "D", "ak47")] {
        f.feed(&format!(r#""Killer": "{killer}","#)).await;
        f.feed(&format!(r#""Killed": "{killed}","#)).await;
        f.feed(&format!(r#""KilledBy": "{weapon}","#)).await;
    }

    let kills = kills.lock().unwrap();
    assert_eq!(kills.len(), 2);
    assert_eq!(kills[0].killed_by.as_deref(), Some("obrez"));
    assert_eq!(kills[1].killer.as_deref(), Some("C"));
    assert_eq!(kills[1].killed_by.as_deref(), Some("ak47"));
}

#[tokio::test]
async fn stats_log_kills_switch_the_pavlov_log_kill_lines_off() {
    let f = Fixture::new();
    let kills = collect::<KillEvent>(&f.service.kill);
    f.service.use_stats_log_kills();

    f.feed(r#"LogTemp: KillData {"Killer": "Alice", "Killed": "Bob", "KilledBy": "AK47"}"#)
        .await;

    assert!(kills.lock().unwrap().is_empty());
}
