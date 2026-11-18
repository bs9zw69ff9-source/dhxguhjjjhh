//! The faction commands wired to in-memory stores, as the C# tests built them.

use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::time::SystemClock;
use pavlov_discord::access::Access;
use pavlov_discord::boards::Boards;
use pavlov_discord::commands::rank_change::{RankChangeCommand, SubclassesCommand};
use pavlov_discord::commands::whitelist_command::WhitelistCommand;
use pavlov_discord::paged::Paged;
use pavlov_host::factions::{FactionMembers, RosterService};
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::registry::RconRegistry;

pub fn store() -> Arc<SerializedStore> {
    Arc::new(SerializedStore::new(Arc::new(MemoryBackend::default())))
}

/// A roster service over `directory`, or with none (the C# `new RosterService(null, ...)`).
pub fn rosters(directory: Option<&Path>) -> Arc<RosterService> {
    Arc::new(
        RosterService::new(
            directory.map(Path::to_path_buf),
            std::env::temp_dir().join("pavlov-roster-bak"),
        )
        .with_factions(pavlov_core::factions::testing::ladder().clone()),
    )
}

pub fn access(store: &Arc<SerializedStore>, owners: &[u64]) -> Arc<Access> {
    Arc::new(Access::new(Arc::clone(store), owners.iter().copied(), []).unwrap())
}

pub fn whitelist(
    store: &Arc<SerializedStore>,
    rosters: Arc<RosterService>,
    owners: &[u64],
) -> WhitelistCommand {
    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    WhitelistCommand {
        rosters,
        members: Arc::new(FactionMembers::new(Arc::clone(store))),
        access: access(store, owners),
        boards: Arc::new(Boards::new(
            Arc::clone(store),
            rcon,
            None,
            None,
            Arc::new(SystemClock),
        )),
        audit: Arc::new(AuditLog::new(Arc::clone(store), Arc::new(SystemClock))),
        paged: None,
    }
}

pub fn rank(up: bool) -> RankChangeCommand {
    let store = store();
    let members = Arc::new(FactionMembers::new(Arc::clone(&store)));
    let audit = Arc::new(AuditLog::new(Arc::clone(&store), Arc::new(SystemClock)));
    if up {
        RankChangeCommand::promotion(rosters(None), members, access(&store, &[]), audit)
    } else {
        RankChangeCommand::demotion(rosters(None), members, access(&store, &[]), audit)
    }
}

pub fn subclasses() -> SubclassesCommand {
    SubclassesCommand {
        rosters: rosters(None),
        paged: Arc::new(Paged::new()),
    }
}
