//! The `playtime` dataset's row: minutes played, accumulated by the playtime ticker. Stored
//! camelCase with the time in epoch milliseconds, as the Node bot reads it.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaytimeEntry {
    #[serde(default)]
    pub player: String,
    #[serde(default)]
    pub minutes: i64,
    #[serde(with = "pavlov_core::json::epoch_ms_opt", default)]
    pub last_seen: Option<DateTime<Utc>>,
}
