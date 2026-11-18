//! Security alerts to the owners (C# `Moderation/SecurityAlerts.cs`): an evasion catch, a VPN
//! verdict, two accounts on one address. The Discord layer delivers them as direct messages.

use std::sync::{Arc, Mutex, PoisonError};

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;

/// What kind of alert.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SecurityAlertKind {
    Evasion,
    Alts,
    Vpn,
}

/// One alert.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SecurityAlert {
    pub kind: SecurityAlertKind,
    pub player: String,
    /// What happened, in a sentence.
    pub headline: String,
    /// What the bot did about it.
    pub action: String,
    pub account_id: Option<String>,
    pub ip: Option<String>,
    pub server: Option<String>,
    pub alts: Vec<String>,
    pub at: Option<DateTime<Utc>>,
}

/// One recipient's delivery.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AlertDelivery {
    pub user: u64,
    pub delivered: bool,
    pub problem: Option<String>,
}

/// Delivers alerts. Must not fail: callers are part-way through a ban.
pub trait SecurityAlertSink: Send + Sync {
    fn post<'a>(&'a self, alert: &'a SecurityAlert) -> BoxFuture<'a, Vec<AlertDelivery>>;
}

/// The alert point every responder posts through; a no-op until a sink is attached.
#[derive(Default)]
pub struct SecurityAlerts {
    sink: Mutex<Option<Arc<dyn SecurityAlertSink>>>,
}

impl std::fmt::Debug for SecurityAlerts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SecurityAlerts")
            .field("enabled", &self.enabled())
            .finish()
    }
}

impl SecurityAlerts {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    pub fn use_sink(&self, sink: Arc<dyn SecurityAlertSink>) {
        *self.sink.lock().unwrap_or_else(PoisonError::into_inner) = Some(sink);
    }

    #[must_use]
    pub fn enabled(&self) -> bool {
        self.sink
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .is_some()
    }

    /// Deliver, or nothing when no sink is attached.
    pub async fn post(&self, alert: &SecurityAlert) -> Vec<AlertDelivery> {
        let sink = self
            .sink
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone();
        match sink {
            Some(sink) => sink.post(alert).await,
            None => Vec::new(),
        }
    }
}
