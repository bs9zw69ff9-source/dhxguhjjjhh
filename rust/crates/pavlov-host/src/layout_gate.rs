//! One server-layout change at a time (C# `Servers/ServerLayoutGate.cs`).
//!
//! `/provisionserver` and `/deleteserver` rewrite `.env` and then restart the bot onto it. A
//! second change started before that restart would plan against the OLD layout the running bot
//! still holds, so once a change has touched `.env` the gate stays shut until the restart.

use std::sync::{Mutex, PoisonError};

use crate::provisioner::ProvisionOutcome;

#[derive(Debug, Default)]
struct State {
    running: Option<String>,
    reload_pending: bool,
}

/// The gate. One per process.
#[derive(Debug, Default)]
pub struct ServerLayoutGate {
    state: Mutex<State>,
}

impl ServerLayoutGate {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Enter for `what`, or why not.
    pub fn try_enter(&self, what: &str) -> Option<String> {
        let mut state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        if state.reload_pending {
            return Some("the server layout already changed and the bot has not restarted onto it yet. Restart the bot and run this again.".into());
        }
        if let Some(running) = &state.running {
            return Some(format!(
                "{running} is still running. Wait for its checklist to finish."
            ));
        }
        state.running = Some(what.to_owned());
        None
    }

    /// Leave after a run. No outcome (the run died) or one that changed `.env` keeps the gate
    /// shut until the restart: either way the layout on disk may not be the one in memory.
    pub fn exit(&self, outcome: Option<&ProvisionOutcome>) {
        let mut state = self.state.lock().unwrap_or_else(PoisonError::into_inner);
        state.running = None;
        if outcome.is_none_or(ProvisionOutcome::changed_env) {
            state.reload_pending = true;
        }
    }

    /// Leave without having started anything.
    pub fn abandon(&self) {
        self.state
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .running = None;
    }
}
