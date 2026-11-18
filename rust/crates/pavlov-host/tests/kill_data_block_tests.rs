//! Port of `KillDataBlockTests.cs`: the kill block a live Pavlov server actually writes, one
//! field per line. The assembler once reported on the "Killed" line, so every kill fired early
//! with no weapon and the orphaned "KilledBy" was merged into the NEXT kill: every death after
//! the first was attributed to the previous death's gun.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::{Arc, Mutex};

use common::FakeHttp;
use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::logs::KillEvent;
use pavlov_core::time::SystemClock;
use pavlov_host::feeds::FeedWebhooks;
use pavlov_host::kill_stats::KillStats;
use pavlov_host::logs::{FeedBridge, FeedBridgeParts, IpTrackingService, LogLine};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::server_labels::ServerLabels;

const FILE: &str = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";

fn memory_store() -> Arc<SerializedStore> {
    Arc::new(SerializedStore::new(Arc::new(MemoryBackend::new())))
}

struct Fixture {
    tracking: Arc<IpTrackingService>,
    raised: Arc<Mutex<Vec<KillEvent>>>,
}

impl Fixture {
    fn new() -> Self {
        let tracking = Arc::new(IpTrackingService::new(
            memory_store(),
            Arc::new(MetricsRegistry::default()),
            Arc::new(SystemClock),
        ));
        let raised = Arc::new(Mutex::new(Vec::new()));
        let sink = Arc::clone(&raised);
        tracking.kill.subscribe(move |k| {
            sink.lock().unwrap().push(k);
            Box::pin(async {})
        });
        Self { tracking, raised }
    }

    async fn line(&self, text: &str) {
        self.tracking.ingest(&LogLine::new(FILE, text)).await;
    }

    /// One KillData block, exactly as the game writes it.
    async fn block(&self, killer: &str, killed: &str, weapon: &str, headshot: bool) {
        for line in [
            "   \"KillData\":".to_owned(),
            "   {".to_owned(),
            format!("           \"Killer\": \"{killer}\","),
            format!("           \"Killed\": \"{killed}\","),
            format!("           \"KilledBy\": \"{weapon}\","),
            format!("           \"Headshot\": {headshot}"),
            "   }".to_owned(),
            "}".to_owned(),
        ] {
            self.line(&line).await;
        }
    }

    fn raised(&self) -> Vec<KillEvent> {
        self.raised.lock().unwrap().clone()
    }
}

#[tokio::test]
async fn one_block_is_one_kill_with_its_own_weapon() {
    let f = Fixture::new();
    f.block("afrobrofosho", "Swagthingchunk32", "obrez", false)
        .await;

    let raised = f.raised();
    assert_eq!(raised.len(), 1);
    assert_eq!(raised[0].killer.as_deref(), Some("afrobrofosho"));
    assert_eq!(raised[0].killed.as_deref(), Some("Swagthingchunk32"));
    assert_eq!(raised[0].killed_by.as_deref(), Some("obrez"));
}

#[tokio::test]
async fn the_second_kill_is_not_attributed_to_the_firsts_weapon() {
    let f = Fixture::new();
    f.block("kingofthepirats", "afrobrofosho", "nv_laserrifle", false)
        .await;
    f.block("robbie1220", "lifelessB", "service_rifle", false)
        .await;
    f.block("sweet_tea_is_good", "hssny313", "m60", false).await;

    let raised = f.raised();
    assert_eq!(raised.len(), 3);
    let weapons: Vec<_> = raised.iter().map(|k| k.killed_by.as_deref()).collect();
    assert_eq!(
        weapons,
        [Some("nv_laserrifle"), Some("service_rifle"), Some("m60")]
    );
    let killed: Vec<_> = raised.iter().map(|k| k.killed.as_deref()).collect();
    assert_eq!(
        killed,
        [Some("afrobrofosho"), Some("lifelessB"), Some("hssny313")]
    );
}

#[tokio::test]
async fn a_suicide_block_is_still_one_record() {
    // Most of a real log: the same name on both sides, KilledBy "None".
    let f = Fixture::new();
    f.block("mrcat76o", "mrcat76o", "None", false).await;

    let raised = f.raised();
    assert_eq!(raised.len(), 1);
    assert_eq!(raised[0].killer, raised[0].killed);
}

#[tokio::test]
async fn a_block_with_no_weapon_is_not_swallowed_by_the_next_one() {
    // Held until the next block's Killer line proves the old one is finished.
    let f = Fixture::new();
    f.line(r#"        "Killer": "Alice","#).await;
    f.line(r#"        "Killed": "Bob","#).await;
    assert!(f.raised().is_empty());

    f.block("Carol", "Dave", "de", false).await;

    let raised = f.raised();
    assert_eq!(raised.len(), 2);
    assert_eq!(raised[0].killed.as_deref(), Some("Bob"));
    assert_eq!(raised[0].killed_by, None);
    assert_eq!(raised[1].killed.as_deref(), Some("Dave"));
    assert_eq!(raised[1].killed_by.as_deref(), Some("de"));
}

#[tokio::test]
async fn a_whole_round_is_counted_correctly_end_to_end() {
    // Through the bridge and into the counter /stats reads.
    let f = Fixture::new();
    let stats = Arc::new(KillStats::new(memory_store()));
    let servers = Arc::new(ServerLabels::default());
    servers.assign(vec![FILE.to_owned()]);
    let feeds = Arc::new(FeedWebhooks::new(
        Arc::new(FakeHttp::canned(204, "")) as _,
        Arc::new(MetricsRegistry::default()),
        Arc::new(SystemClock),
    ));
    let _bridge = FeedBridge::attach(
        Arc::clone(&f.tracking),
        feeds,
        servers,
        FeedBridgeParts {
            kill_stats: Some(Arc::clone(&stats)),
            ..FeedBridgeParts::default()
        },
    );

    f.block("Smokythabear45", "mrcat76o", "de", true).await;
    f.block("Smokythabear45", "robbie1220", "de", false).await;
    f.block("mrcat76o", "Smokythabear45", "m14", false).await;
    f.block("lifelessB", "lifelessB", "None", false).await; // a suicide

    assert_eq!(stats.of("Smokythabear45").kills, 2);
    assert_eq!(stats.of("Smokythabear45").deaths, 1);
    assert_eq!(stats.of("mrcat76o").kills, 1);
    assert_eq!(stats.of("mrcat76o").deaths, 1);

    let suicide = stats.of("lifelessB");
    assert_eq!(suicide.kills, 0);
    assert_eq!(suicide.deaths, 1);
    assert_eq!(suicide.suicides, 1);

    assert!((stats.of("Smokythabear45").ratio() - 2.0).abs() < f64::EPSILON);
}
