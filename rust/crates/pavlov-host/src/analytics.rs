//! Aggregate questions answered against the event table (C# `Events/AnalyticsService.cs`).
//!
//! - EVERY NUMBER IS COMPUTED IN SQL: fetching thirty days of rows to count them in memory is
//!   the full scan the table exists to avoid.
//! - No sessions, durations, retention or average concurrency: there is no leave event and no
//!   sampled player count, and an invented number is worse than an absent one.
//! - Automated actors ("auto", "system") are left out of the staff numbers.

use std::sync::Arc;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::events::mapping::is_automated;
use pavlov_core::events::{EventCategory, EventQuery};
use pavlov_storage::{EventGrouping, EventStore};

/// Most rows any grouped query returns: well past what an embed can show.
const GROUP_LIMIT: i32 = 50;

/// The picture for one window.
#[derive(Debug, Clone, PartialEq)]
pub struct ServerAnalytics {
    pub window: TimeDelta,
    pub unique_players: u64,
    /// Join events, not sessions.
    pub joins: u64,
    /// The UTC hour with the most joins, and how many.
    pub busiest_hour: Option<(String, u64)>,
    /// Joins per server, busiest first.
    pub servers: Vec<(String, u64)>,
    /// Joins per UTC day, oldest first.
    pub per_day: Vec<(String, u64)>,
    pub staff_actions: u64,
    pub security_detections: u64,
    pub bans: u64,
}

impl ServerAnalytics {
    #[must_use]
    pub fn empty(window: TimeDelta) -> Self {
        Self {
            window,
            unique_players: 0,
            joins: 0,
            busiest_hour: None,
            servers: Vec::new(),
            per_day: Vec::new(),
            staff_actions: 0,
            security_detections: 0,
            bans: 0,
        }
    }

    /// Mean joins per day across the window; zero for a window under a day.
    #[must_use]
    pub fn joins_per_day(&self) -> f64 {
        #[allow(clippy::cast_precision_loss)]
        let days = self.window.num_seconds() as f64 / 86_400.0;
        #[allow(clippy::cast_precision_loss)]
        if days >= 1.0 {
            self.joins as f64 / days
        } else {
            0.0
        }
    }
}

/// What one staff member did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StaffActivity {
    pub moderator: String,
    pub total: u64,
    /// Most frequent first.
    pub actions: Vec<(String, u64)>,
    pub busiest_hour: Option<(String, u64)>,
}

/// The service.
pub struct AnalyticsService {
    events: Arc<dyn EventStore>,
}

impl std::fmt::Debug for AnalyticsService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AnalyticsService").finish_non_exhaustive()
    }
}

fn busiest(rows: &[(String, u64)]) -> Option<(String, u64)> {
    // The FIRST of equal maxima, as LINQ's MaxBy picks it.
    rows.iter()
        .fold(None, |best: Option<&(String, u64)>, row| match best {
            Some(b) if b.1 >= row.1 => Some(b),
            _ => Some(row),
        })
        .cloned()
}

impl AnalyticsService {
    #[must_use]
    pub fn new(events: Arc<dyn EventStore>) -> Self {
        Self { events }
    }

    fn query(since: DateTime<Utc>) -> EventQuery {
        EventQuery {
            since: Some(since),
            ..EventQuery::new(GROUP_LIMIT)
        }
    }

    /// The picture for `window`, optionally narrowed to one server.
    ///
    /// # Errors
    /// When the event table cannot be read.
    pub fn overview(
        &self,
        window: TimeDelta,
        server: Option<&str>,
        now: DateTime<Utc>,
    ) -> std::io::Result<ServerAnalytics> {
        let since = now - window;
        let scoped = |category| EventQuery {
            category: Some(category),
            server: server.map(str::to_owned),
            ..Self::query(since)
        };
        let joins = scoped(EventCategory::Player);
        let per_hour = self.events.count_by(EventGrouping::Hour, &joins)?;
        let mut per_day = self.events.count_by(EventGrouping::Day, &joins)?;
        // Chronological: this one is read as a trend line.
        per_day.sort_by(|a, b| a.0.cmp(&b.0));
        let staff = self
            .events
            .count_by(EventGrouping::Kind, &scoped(EventCategory::Staff))?;
        let security = self
            .events
            .count_by(EventGrouping::Kind, &scoped(EventCategory::Security))?;
        Ok(ServerAnalytics {
            window,
            unique_players: self.events.count_distinct(EventGrouping::Player, &joins)?,
            joins: per_hour.iter().map(|h| h.1).sum(),
            busiest_hour: busiest(&per_hour),
            servers: self.events.count_by(EventGrouping::Server, &joins)?,
            per_day,
            staff_actions: staff.iter().map(|k| k.1).sum(),
            security_detections: security.iter().map(|k| k.1).sum(),
            bans: staff
                .iter()
                .filter(|k| k.0.to_ascii_lowercase().contains("ban"))
                .map(|k| k.1)
                .sum(),
        })
    }

    /// Who has been moderating, busiest first, automated actors left out.
    ///
    /// # Errors
    /// When the event table cannot be read.
    pub fn staff_leaderboard(
        &self,
        window: TimeDelta,
        now: DateTime<Utc>,
    ) -> std::io::Result<Vec<(String, u64)>> {
        Ok(self
            .events
            .count_by(EventGrouping::Actor, &Self::query(now - window))?
            .into_iter()
            .filter(|row| !is_automated(Some(&row.0)))
            .collect())
    }

    /// What one staff member did in the window.
    ///
    /// # Errors
    /// When the event table cannot be read.
    pub fn activity_of(
        &self,
        moderator: &str,
        window: TimeDelta,
        now: DateTime<Utc>,
    ) -> std::io::Result<StaffActivity> {
        let query = EventQuery {
            actor: Some(moderator.to_owned()),
            ..Self::query(now - window)
        };
        let actions = self.events.count_by(EventGrouping::Kind, &query)?;
        let per_hour = self.events.count_by(EventGrouping::Hour, &query)?;
        Ok(StaffActivity {
            moderator: moderator.to_owned(),
            total: actions.iter().map(|a| a.1).sum(),
            actions,
            busiest_hour: busiest(&per_hour),
        })
    }
}
