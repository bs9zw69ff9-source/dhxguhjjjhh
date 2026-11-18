//! Port of `AuditLogTests.cs`, the board half: the staff board reads what the audit log writes.
//! They were built separately and nothing connected them, so the board read a dataset no code
//! path wrote. The log itself is tested in `pavlov-host`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;
use std::time::Duration;

use pavlov_core::data::{MemoryBackend, SerializedStore};
use pavlov_core::time::SystemClock;
use pavlov_discord::boards::Boards;
use pavlov_host::metrics::MetricsRegistry;
use pavlov_host::moderation::AuditLog;
use pavlov_host::registry::RconRegistry;

#[tokio::test]
async fn the_staff_board_reads_what_the_audit_log_writes() {
    let store = Arc::new(SerializedStore::new(Arc::new(MemoryBackend::default())));
    let audit = AuditLog::new(Arc::clone(&store), Arc::new(SystemClock));
    audit
        .record("tempban", "Dana", "Alice", None)
        .await
        .unwrap();
    audit.record("kick", "Dana", "Bob", None).await.unwrap();

    let rcon = Arc::new(RconRegistry::with_system_clock(
        &[],
        Duration::from_secs(60),
        Arc::new(MetricsRegistry::default()),
    ));
    let board = Boards::new(store, rcon, None, None, Arc::new(SystemClock))
        .build_staff_board()
        .unwrap();

    assert!(board.description.as_deref().unwrap().contains("Dana"));
}
