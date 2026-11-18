//! Warning players before an action drops them (C# `Servers/PlayerNotice.cs`), and the map
//! rotation that uses the same verified send (C# `Servers/MapRotation.cs`).

use std::sync::Arc;
use std::time::Duration;

use pavlov_core::text::Sanitize;
use pavlov_rcon::RconError;

use crate::registry::RconRegistry;
use crate::service_control::rcon_name_for;

/// How long players get between the warning and the action.
pub const GRACE: Duration = Duration::from_secs(5);
/// How long the warning itself may take.
const BUDGET: Duration = Duration::from_secs(3);
/// Every broadcast is addressed to everyone; a message must not carry its own target.
const EVERYONE: &str = "All";

/// Whether the warning reached the server, and what to tell the admin.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NoticeResult {
    pub delivered: bool,
    pub detail: String,
}

/// Broadcasts to one server's players.
#[derive(Debug, Clone)]
pub struct PlayerNotice {
    rcon: Arc<RconRegistry>,
}

impl PlayerNotice {
    #[must_use]
    pub fn new(rcon: Arc<RconRegistry>) -> Self {
        Self { rcon }
    }

    /// Warn server N's players. VERIFIED: "delivered" is shown to the admin, and a refusal
    /// (`"Successful": false`) is not a delivery.
    pub async fn warn(&self, server_number: i32, message: &str) -> NoticeResult {
        let name = rcon_name_for(server_number);
        // A server with no RCON is not a server that failed to answer.
        if self.rcon.client(&name).is_none() {
            tracing::warn!(%name, "no RCON configured - players were not warned");
            return NoticeResult {
                delivered: false,
                detail: format!(
                    "no RCON is configured for server {server_number} (`RCON_HOST_{server_number}` is not set), so nobody could be told"
                ),
            };
        }
        // Sanitised even for a constant: every string that reaches RCON goes through one door.
        let command = format!("Notify {EVERYONE} {}", Sanitize::message(Some(message)));
        match tokio::time::timeout(BUDGET, self.rcon.send_verified(&name, &command)).await {
            Ok(Ok(_)) => {
                tracing::info!(%name, message, "warned players");
                NoticeResult {
                    delivered: true,
                    detail: message.to_owned(),
                }
            }
            Err(_) => NoticeResult {
                delivered: false,
                detail: format!("the server did not answer within {}s", BUDGET.as_secs()),
            },
            // THE REAL REASON: a wrong password, a closed port and a refusal are three fixes.
            Ok(Err(error)) => NoticeResult {
                delivered: false,
                detail: Sanitize::message(Some(&error.to_string())),
            },
        }
    }

    /// The grace period, waited.
    pub async fn wait() {
        tokio::time::sleep(GRACE).await;
    }
}

/// How a rotation went.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RotationOutcome {
    Rotated,
    /// Sent, and the reply never came: NOT a failure to report as one, because a second run
    /// skips a map.
    Unconfirmed,
    Failed,
}

/// The outcome and what to say.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RotationResult {
    pub number: i32,
    pub outcome: RotationOutcome,
    pub detail: String,
}

impl RotationResult {
    #[must_use]
    pub fn ok(&self) -> bool {
        self.outcome == RotationOutcome::Rotated
    }
}

/// The exact command sent.
pub const ROTATE_COMMAND: &str = "RotateMap";

/// Rotate server N's map, verified.
pub async fn rotate(rcon: &RconRegistry, server_number: i32) -> RotationResult {
    let name = rcon_name_for(server_number);
    let result = |outcome, detail: String| RotationResult {
        number: server_number,
        outcome,
        detail,
    };
    if rcon.client(&name).is_none() {
        return result(
            RotationOutcome::Failed,
            format!(
                "no RCON is configured for server {server_number} (`RCON_HOST_{server_number}` is not set)"
            ),
        );
    }
    match rcon.send_verified(&name, ROTATE_COMMAND).await {
        Ok(_) => {
            tracing::info!(%name, "rotated the map");
            result(RotationOutcome::Rotated, "map rotated".into())
        }
        Err(RconError::Unconfirmed { .. }) => result(
            RotationOutcome::Unconfirmed,
            "sent, but the server did not confirm it - check in-game before running this again"
                .into(),
        ),
        Err(error) => {
            tracing::warn!(%name, %error, "RotateMap failed");
            result(
                RotationOutcome::Failed,
                Sanitize::message(Some(&error.to_string())),
            )
        }
    }
}
