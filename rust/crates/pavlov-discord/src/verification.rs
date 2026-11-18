//! Verification (C# `Verification/VerificationService.cs`): a member presses **Verify**, gives
//! their exact in-game name, and staff accept or deny the request in their channel.
//!
//! - CONFIRMED ADDRESSES ONLY. A guessed address is a correlation with a nearby log line, and
//!   verifying somebody as its owner on that basis locks out whoever really connected.
//! - A conflict is refused to the MEMBER, with "ask a staff member", not sent to staff: the
//!   alt rule has false positives by design and the fix is a manual verification.
//! - RE-CHECKED AT DECISION TIME: somebody else may have verified that name in between.
//! - THE DECISION IS RECORDED even when the role cannot be granted: staff add it by hand.
//! - The Discord-to-address link goes to the CONNECT feed (the private, address-bearing one),
//!   never the public join log.
//! - Spent buttons are removed, and a request decided twice says so instead of a dead button.

use std::io::Read;
use std::sync::Arc;
use std::time::Duration;

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use indexmap::IndexMap;
use pavlov_core::component_id::ComponentId;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::skin::{police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::time::Clock;
use pavlov_core::verification::{VerificationRecord, VerificationRules};
use pavlov_host::feeds::{CONNECT, FeedWebhooks};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::moderation::AuditLog;
use pavlov_storage::datasets::{VERIFICATIONS, VERIFY_STATE};
use serde::{Deserialize, Serialize};

use crate::component::{ActionRow, Button, ButtonStyle, Modal, TextInput};
use crate::interaction::{ComponentInvocation, ComponentResponder, Reply};
use crate::paged::ComponentHandler;
use crate::post::ChannelPoster;

/// The component prefix.
pub const PREFIX: &str = pavlov_core::verification::COMPONENT_PREFIX;
/// How long an undecided request is kept.
pub const PENDING_LIFETIME: Duration = Duration::from_secs(7 * 24 * 3600);

/// A request waiting for staff.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PendingVerification {
    pub discord_id: String,
    pub name: String,
    #[serde(default)]
    pub ips: Vec<String>,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub at: DateTime<Utc>,
}

/// A verification as stored (`verifications`: Discord id to record).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoredVerification {
    pub name: String,
    #[serde(default)]
    pub ips: Vec<String>,
}

/// Why a role could not be granted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RoleGrantError {
    MemberLeft,
    RoleMissing,
    Failed(String),
}

/// Gives a member a role (the gateway implements it). `Ok` carries the role's name.
pub trait RoleGranter: Send + Sync {
    fn grant(
        &self,
        guild_id: u64,
        user_id: u64,
        role_id: u64,
    ) -> BoxFuture<'_, Result<String, RoleGrantError>>;
}

/// The channels and role verification uses.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct VerificationChannels {
    pub panel: Option<u64>,
    pub staff: Option<u64>,
    pub role: Option<u64>,
}

/// The service.
pub struct VerificationService {
    pub store: Arc<SerializedStore>,
    pub tracking: Arc<IpTrackingService>,
    pub feeds: Arc<FeedWebhooks>,
    pub audit: Arc<AuditLog>,
    pub poster: Arc<dyn ChannelPoster>,
    pub roles: Arc<dyn RoleGranter>,
    pub channels: VerificationChannels,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for VerificationService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("VerificationService")
            .field("channels", &self.channels)
            .finish_non_exhaustive()
    }
}

fn token() -> Option<String> {
    let mut bytes = [0u8; 8];
    std::fs::File::open("/dev/urandom")
        .and_then(|mut f| f.read_exact(&mut bytes))
        .ok()?;
    Some(bytes.iter().map(|b| format!("{b:02x}")).collect())
}

fn id(parts: &[&str]) -> String {
    // The parts are fixed words and a 16-hex token: always far inside Discord's 100.
    ComponentId::encode(PREFIX, parts).unwrap_or_else(|_| PREFIX.to_owned())
}

/// The panel's button.
#[must_use]
pub fn panel_button() -> Vec<ActionRow> {
    vec![ActionRow::Buttons(vec![Button::new(
        ButtonStyle::Success,
        "Verify",
        id(&["start"]),
    )])]
}

/// The panel posted in the verify channel.
#[must_use]
pub fn panel() -> Embed {
    let words = &police().words;
    theme::success(
        &format!("{} {}", words.mark, words.verify_title),
        Some(&format!(
            "Press **Verify** and enter your **exact** Pavlov in-game name.\n\n{}\n\n{}",
            words.verify_review, words.verify_alts
        )),
    )
    .brand(None)
}

fn verify_modal() -> Modal {
    Modal {
        custom_id: id(&["submit"]),
        title: "Verify".to_owned(),
        inputs: vec![TextInput {
            custom_id: "name".to_owned(),
            label: "Your exact Pavlov in-game name".to_owned(),
            paragraph: false,
            required: true,
            placeholder: None,
            value: None,
            min_length: None,
            max_length: Some(64),
        }],
    }
}

fn closed(embed: Embed) -> Reply {
    // No components: spent buttons must not be clickable.
    Reply::embed(embed.brand(None))
}

impl VerificationService {
    /// Both channels are needed: a panel with nowhere to send requests is a dead button.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.channels.panel.is_some() && self.channels.staff.is_some()
    }

    fn verified(&self) -> IndexMap<String, VerificationRecord> {
        self.store
            .read::<IndexMap<String, StoredVerification>>(VERIFICATIONS, IndexMap::new())
            .unwrap_or_else(|error| {
                tracing::error!(%error, "cannot read the verifications dataset");
                IndexMap::new()
            })
            .into_iter()
            .map(|(id, v)| {
                (
                    id,
                    VerificationRecord {
                        name: v.name,
                        ips: v.ips,
                    },
                )
            })
            .collect()
    }

    fn pending(&self) -> IndexMap<String, PendingVerification> {
        self.store
            .read(VERIFY_STATE, IndexMap::new())
            .unwrap_or_default()
    }

    async fn followup(responder: &dyn ComponentResponder, embed: Embed) {
        if let Err(error) = responder
            .followup(Reply::embed(embed.brand(None)), true)
            .await
        {
            tracing::warn!(%error, "could not answer a verification request");
        }
    }

    async fn submitted(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) {
        if responder.defer_reply(true).await.is_err() {
            return;
        }
        let raw = invocation
            .field("name")
            .or_else(|| invocation.field("verify_name"))
            .unwrap_or("");
        let name = Sanitize::message(Some(raw)).trim().to_owned();
        if name.is_empty() {
            Self::followup(
                responder,
                theme::failure("No name given", Some("Enter your exact in-game name.")),
            )
            .await;
            return;
        }
        let discord_id = invocation.caller.id.to_string();
        let ips = self
            .tracking
            .account_by_name(&name)
            .map(|a| a.confirmed_ips)
            .unwrap_or_default();
        let verdict = VerificationRules::check(&self.verified(), &discord_id, Some(&name), &ips);
        if !verdict.is_allowed() {
            let reason = verdict
                .reason
                .unwrap_or_else(|| "That request conflicts with an existing verification.".into());
            Self::followup(
                responder,
                theme::failure("Cannot verify", Some(&reason)).field(
                    "What now",
                    "Ask a staff member to verify you manually.",
                    false,
                ),
            )
            .await;
            return;
        }
        if ips.is_empty() {
            tracing::info!(user = %invocation.username, name,
                "verification request has no confirmed address yet");
        }
        let Some(token) = token() else {
            Self::followup(
                responder,
                theme::failure(
                    "Could not send your request",
                    Some("Try again in a moment."),
                ),
            )
            .await;
            return;
        };
        let now = self.clock.now();
        let cutoff = now - chrono::Duration::from_std(PENDING_LIFETIME).unwrap_or_default();
        let request = PendingVerification {
            discord_id: discord_id.clone(),
            name: name.clone(),
            ips: ips.clone(),
            at: now,
        };
        let stored = self
            .store
            .update(
                VERIFY_STATE,
                IndexMap::<String, PendingVerification>::new,
                |mut pending| {
                    pending.retain(|_, p| p.at >= cutoff);
                    pending.insert(token.clone(), request);
                    Some(pending)
                },
            )
            .await;
        if !stored.is_ok_and(|r| r.ok) {
            Self::followup(
                responder,
                theme::failure(
                    "Could not send your request",
                    Some("Try again in a moment."),
                ),
            )
            .await;
            return;
        }
        if !self.post_request(invocation, &token, &name, &ips).await {
            Self::followup(
                responder,
                theme::failure(
                    "Could not send your request",
                    Some("The staff channel is unreachable. Tell a staff member directly."),
                ),
            )
            .await;
            return;
        }
        Self::followup(
            responder,
            theme::success(
                "Request sent",
                Some(&format!(
                    "Staff will review your request to verify as `{}`. You will get the role once \
                     it is approved.",
                    Sanitize::code(Some(&name))
                )),
            ),
        )
        .await;
    }

    async fn post_request(
        &self,
        invocation: &ComponentInvocation,
        token: &str,
        name: &str,
        ips: &[String],
    ) -> bool {
        let Some(staff) = self.channels.staff else {
            return false;
        };
        if invocation.guild_id.is_none() {
            tracing::warn!("a verification request did not arrive from a guild channel");
            return false;
        }
        let addresses = if ips.is_empty() {
            "**none on record** — this name has never been seen on the server".to_owned()
        } else {
            format!("{} on record", ips.len())
        };
        let card = theme::notice("Verification request", None)
            .field(
                "Discord",
                format!("<@{0}> (`{0}`)", invocation.caller.id),
                true,
            )
            .field(
                "Claims to be",
                format!("`{}`", Sanitize::code(Some(name))),
                true,
            )
            .field("Confirmed addresses", addresses, true)
            .brand(Some(&format!(
                "Requested {} UTC",
                self.clock.now().format("%Y-%m-%d %H:%M")
            )));
        let buttons = vec![ActionRow::Buttons(vec![
            Button::new(ButtonStyle::Success, "Accept", id(&["ok", token])),
            Button::new(ButtonStyle::Danger, "Deny", id(&["no", token])),
        ])];
        match self.poster.send(staff, card, buttons).await {
            Ok(Some(_)) => true,
            Ok(None) => false,
            Err(error) => {
                tracing::warn!(channel = staff, %error, "could not post a verification request");
                false
            }
        }
    }

    async fn decided(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
        approved: bool,
        token: Option<&str>,
    ) {
        if responder.acknowledge().await.is_err() {
            return;
        }
        let close = |embed: Embed| async move {
            if let Err(error) = responder.edit_original(closed(embed)).await {
                tracing::warn!(%error, "could not close a verification request");
            }
        };
        let request = token.and_then(|t| self.pending().get(t).cloned());
        let (Some(token), Some(request)) = (token, request) else {
            close(theme::warning(
                "Already handled",
                Some("This request has already been decided or has expired."),
            ))
            .await;
            return;
        };
        let removed = self
            .store
            .update(
                VERIFY_STATE,
                IndexMap::<String, PendingVerification>::new,
                |mut pending| {
                    pending.shift_remove(token);
                    Some(pending)
                },
            )
            .await;
        if let Err(error) = removed {
            tracing::error!(%error, "could not clear a decided verification request");
        }
        let by = invocation.username.as_str();
        let shown = Sanitize::code(Some(&request.name));

        if !approved {
            if let Err(error) = self
                .audit
                .record("verify-deny", by, &request.name, Some("denied"))
                .await
            {
                tracing::warn!(%error, "could not audit a verification denial");
            }
            close(
                theme::failure("Denied", Some(&format!("`{shown}` was not verified.")))
                    .field("By", by, true),
            )
            .await;
            return;
        }

        let verdict = VerificationRules::check(
            &self.verified(),
            &request.discord_id,
            Some(&request.name),
            &request.ips,
        );
        if !verdict.is_allowed() {
            let reason = verdict
                .reason
                .unwrap_or_else(|| "That name or address is now verified to somebody else.".into());
            close(theme::failure("No longer possible", Some(&reason))).await;
            return;
        }
        let record = StoredVerification {
            name: request.name.clone(),
            ips: request.ips.clone(),
        };
        let saved = self
            .store
            .update(
                VERIFICATIONS,
                IndexMap::<String, StoredVerification>::new,
                |mut verified| {
                    verified.insert(request.discord_id.clone(), record);
                    Some(verified)
                },
            )
            .await;
        if !saved.as_ref().is_ok_and(|r| r.ok) {
            tracing::error!("could not record an approved verification");
            close(theme::failure(
                "Could not record it",
                Some("The verification could not be saved. Try again."),
            ))
            .await;
            return;
        }
        let role = self.grant_role(invocation, &request.discord_id).await;
        if let Err(error) = self
            .audit
            .record("verify-accept", by, &request.name, Some("approved"))
            .await
        {
            tracing::warn!(%error, "could not audit a verification");
        }
        if !request.ips.is_empty() {
            self.feeds.post(
                CONNECT,
                &format!(
                    "[VERIFIED] {}  |  discord={}  |  ips={}  |  by {}",
                    Sanitize::message(Some(&request.name)),
                    request.discord_id,
                    request.ips.join(","),
                    Sanitize::message(Some(by))
                ),
            );
        }
        close(
            theme::success(
                police().words.verified,
                Some(&format!("`{shown}` is now verified.")),
            )
            .field("By", by, true)
            .field("Role", role, true),
        )
        .await;
    }

    async fn grant_role(&self, invocation: &ComponentInvocation, discord_id: &str) -> String {
        let Some(role) = self.channels.role else {
            return "none configured".to_owned();
        };
        let Some(guild) = invocation.guild_id else {
            return "not in a guild".to_owned();
        };
        let Ok(user) = discord_id.parse::<u64>() else {
            return "bad user id".to_owned();
        };
        match self.roles.grant(guild, user, role).await {
            Ok(name) => name,
            Err(RoleGrantError::MemberLeft) => "member has left".to_owned(),
            Err(RoleGrantError::RoleMissing) => format!("role {role} not found"),
            Err(RoleGrantError::Failed(error)) => {
                tracing::warn!(user, %error, "verified, but could not grant the role");
                "FAILED — grant it manually".to_owned()
            }
        }
    }
}

impl ComponentHandler for VerificationService {
    fn prefix(&self) -> &str {
        PREFIX
    }

    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            let id = ComponentId::parse(&invocation.custom_id);
            let is_modal = !invocation.fields.is_empty();
            match id.argument(0) {
                Some("start") if !is_modal => {
                    if let Err(error) = responder.show_modal(verify_modal()).await {
                        tracing::warn!(%error, "could not open the verify form");
                    }
                }
                Some("submit") if is_modal => self.submitted(invocation, responder).await,
                Some(decision @ ("ok" | "no")) if !is_modal => {
                    self.decided(invocation, responder, decision == "ok", id.argument(1))
                        .await;
                }
                _ => {
                    if let Err(error) = responder
                        .respond(Reply::text("That control is no longer supported."), true)
                        .await
                    {
                        tracing::debug!(%error, "could not answer an unsupported verify control");
                    }
                }
            }
        })
    }
}
