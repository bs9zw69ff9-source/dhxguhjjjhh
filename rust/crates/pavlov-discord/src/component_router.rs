//! Routing a button press, select choice or modal submit to its handler (C#
//! `DiscordGateway.HandleComponentAsync`), Discord-agnostic so the order is tested.
//!
//! The order, and why:
//! 1. an empty custom id is dropped (nothing to route, nothing that expects an answer);
//! 2. the bar list, for components too: a panel posted in a channel is clickable by anybody
//!    who can see it;
//! 3. a prefix no handler owns is a control from an older build, and is told so privately
//!    rather than left to end in Discord's "interaction failed";
//! 4. access is primed for a role-less caller, because handlers re-check access on every
//!    click;
//! 5. the handler, timed; a panic is caught, counted and apologised for with a correlation
//!    id, so one broken handler never takes the click (or the gateway task) with it.

use std::collections::HashMap;
use std::panic::AssertUnwindSafe;
use std::sync::Arc;

use futures_util::FutureExt;
use pavlov_core::component_id::ComponentId;
use pavlov_host::metrics::{MetricLabels, MetricsRegistry};
use pavlov_host::moderation::CommandBlacklist;
use tracing::Instrument;

use crate::barred::{Asker, refuse_if_barred};
use crate::interaction::{ComponentInvocation, ComponentResponder, Reply};
use crate::paged::ComponentHandler;
use crate::runner::{AccessPrimer, new_correlation_id};

/// What the caller of a control from an older build is told.
pub const STALE_CONTROL: &str =
    "That control belongs to an older version of this message. Run the command again.";

/// How a component interaction ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ComponentOutcome {
    /// No custom id: nothing was routed.
    Ignored,
    Barred,
    /// No handler owns the prefix.
    Unknown,
    Handled,
    /// The handler panicked; the caller was given this id.
    Failed {
        correlation_id: String,
    },
}

/// The router.
pub struct ComponentRouter {
    handlers: HashMap<String, Arc<dyn ComponentHandler>>,
    blacklist: Option<Arc<CommandBlacklist>>,
    primer: Option<Arc<dyn AccessPrimer>>,
    metrics: Arc<MetricsRegistry>,
}

impl std::fmt::Debug for ComponentRouter {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let mut prefixes: Vec<&str> = self.handlers.keys().map(String::as_str).collect();
        prefixes.sort_unstable();
        f.debug_struct("ComponentRouter")
            .field("prefixes", &prefixes)
            .finish_non_exhaustive()
    }
}

impl ComponentRouter {
    /// Build it. Two handlers claiming one prefix is a wiring bug: the FIRST keeps it (the C#
    /// `FirstOrDefault`), and the collision is logged so it is not silent.
    #[must_use]
    pub fn new(
        handlers: Vec<Arc<dyn ComponentHandler>>,
        blacklist: Option<Arc<CommandBlacklist>>,
        primer: Option<Arc<dyn AccessPrimer>>,
        metrics: Arc<MetricsRegistry>,
    ) -> Self {
        let mut by_prefix: HashMap<String, Arc<dyn ComponentHandler>> = HashMap::new();
        for handler in handlers {
            let prefix = handler.prefix().to_owned();
            if by_prefix.contains_key(&prefix) {
                tracing::error!(
                    prefix,
                    "two component handlers claim one prefix; the first keeps it"
                );
                continue;
            }
            by_prefix.insert(prefix, handler);
        }
        Self {
            handlers: by_prefix,
            blacklist,
            primer,
            metrics,
        }
    }

    /// The prefixes routed, sorted.
    #[must_use]
    pub fn prefixes(&self) -> Vec<&str> {
        let mut prefixes: Vec<&str> = self.handlers.keys().map(String::as_str).collect();
        prefixes.sort_unstable();
        prefixes
    }

    /// Route one interaction. Never fails: every failure is answered and logged here.
    pub async fn route(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) -> ComponentOutcome {
        if invocation.custom_id.is_empty() {
            return ComponentOutcome::Ignored;
        }
        let id = ComponentId::parse(&invocation.custom_id);
        let correlation_id = new_correlation_id();
        let span = tracing::info_span!(
            "interaction",
            operation = %format!("component:{}", id.prefix),
            correlation = %correlation_id,
            guild = ?invocation.guild_id,
            user = invocation.caller.id
        );
        self.route_in_scope(invocation, responder, &id.prefix, correlation_id)
            .instrument(span)
            .await
    }

    async fn route_in_scope(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
        prefix: &str,
        correlation_id: String,
    ) -> ComponentOutcome {
        let what = format!("component:{prefix}");
        if let Some(blacklist) = &self.blacklist
            && refuse_if_barred(
                blacklist,
                invocation.caller.id,
                &invocation.username,
                Asker::Component(responder),
                &what,
                &self.metrics,
            )
            .await
        {
            return ComponentOutcome::Barred;
        }

        let Some(handler) = self.handlers.get(prefix) else {
            tracing::warn!(custom_id = %invocation.custom_id, "no handler for component");
            if let Err(error) = responder.respond(Reply::text(STALE_CONTROL), true).await {
                tracing::debug!(%error, "could not answer a stale control");
            }
            return ComponentOutcome::Unknown;
        };

        self.prime(invocation).await;

        let labels = MetricLabels::of("component", prefix);
        let run = AssertUnwindSafe(handler.handle(invocation, responder)).catch_unwind();
        let outcome: Result<(), ()> = self
            .metrics
            .time(
                "component_duration_ms",
                &labels,
                Some("Component interaction duration in milliseconds"),
                async { run.await.map_err(|_| ()) },
            )
            .await;
        if outcome.is_ok() {
            return ComponentOutcome::Handled;
        }

        tracing::error!(custom_id = %invocation.custom_id, "component handler panicked");
        self.metrics.increment(
            "component_errors_total",
            &labels,
            1.0,
            Some("Component interactions that threw"),
        );
        let apology = format!("That didn't work. The error has been logged (`{correlation_id}`).");
        // Whether the handler acknowledged first is unknown here, so try the call that is
        // legal on a fresh interaction, then the one that is legal after it.
        if responder
            .respond(Reply::text(apology.clone()), true)
            .await
            .is_err()
            && let Err(error) = responder.followup(Reply::text(apology), true).await
        {
            tracing::warn!(%error, "could not tell the caller that a component failed");
        }
        ComponentOutcome::Failed { correlation_id }
    }

    /// The same rule as the command runner: prime a caller with no member, or a member with
    /// no roles (a user-installed app in a guild).
    async fn prime(&self, invocation: &ComponentInvocation) {
        let Some(primer) = &self.primer else {
            return;
        };
        let has_roles = invocation
            .caller
            .member
            .as_ref()
            .is_some_and(|m| !m.role_ids.is_empty());
        if !has_roles {
            primer.prime(invocation.caller.id).await;
        }
    }
}
