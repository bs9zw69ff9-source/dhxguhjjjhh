//! Joins and flagged joins onto the timeline (C# `Events/PlayerEventBridge.cs`).
//!
//! A flagged join is recorded as THE DETECTION, not the response: whatever the evasion
//! responder then does reaches the timeline separately through the audit log, so "we spotted
//! this" and "we did this about it" stay distinguishable.

use std::path::Path;
use std::sync::Arc;

use pavlov_core::events::{EventCategory, ServerEvent};

use crate::logs::ip_tracking::IpTrackingService;
use crate::timeline::Timeline;

/// The install a log belongs to: `<install>/Pavlov/Saved/Logs/Pavlov.log` gives `<install>`'s
/// name. `None` for a path too short to have one.
#[must_use]
pub fn server_of(log_file: &str) -> Option<String> {
    if log_file.trim().is_empty() {
        return None;
    }
    let install = Path::new(log_file).ancestors().nth(4)?;
    install
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .filter(|n| !n.is_empty())
}

/// Subscribe the timeline to the tracker's joins and flagged joins.
pub fn attach(tracking: &IpTrackingService, timeline: Arc<dyn Timeline>) {
    let joins = Arc::clone(&timeline);
    tracking.joined.subscribe(move |join| {
        let mut event = ServerEvent::new(join.at, EventCategory::Player, "player.join");
        event.player = join.name.clone();
        event.server = server_of(&join.file);
        event.detail = (!join.confident).then(|| "address not confirmed at join time".to_owned());
        joins.record(event);
        Box::pin(async {})
    });
    tracking.flagged.subscribe(move |flagged| {
        let mut event =
            ServerEvent::new(flagged.at, EventCategory::Security, "security.flagged-join");
        event.player = flagged.name.clone();
        event.actor = Some("auto".to_owned());
        event.detail = Some(format!(
            "matched a standing flag ({:?})",
            flagged.verdict.matched
        ));
        timeline.record(event);
        Box::pin(async {})
    });
}
