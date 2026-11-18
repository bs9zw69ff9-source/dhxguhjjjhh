//! Staff actions posted into Discord channels by id (C# `ChannelStaffLog.cs`): the channel
//! counterpart to `STAFF_WEBHOOK_URL`. Both can be set; a webhook works without the gateway,
//! a channel posts as the bot and moves without reissuing a URL.
//!
//! ROUTING FALLS THROUGH rather than dropping a line: setting only `MOD_LOG_CHANNEL` means
//! "everything in one place". Police actions were MISSING ENTIRELY in the C# port at first
//! (a port gap from the Node bot's `POLICE_LOG_CHANNEL || ARREST_CHANNEL || STAFF_LOG_CHANNEL`).

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_core::time::EasternTime;
use pavlov_host::moderation::{ModAction, StaffLogSink};

use crate::post::ChannelPoster;

/// Where each kind of action is published. Named fields, not four adjacent `Option<u64>`s: a
/// mis-ordering would compile and quietly put arrests in the ban channel.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct StaffLogChannels {
    /// General actions, and the fallback for everything else.
    pub moderation: Option<u64>,
    /// Bans, temp bans, unbans, auto bans.
    pub ban: Option<u64>,
    /// Arrests, warrants, releases, rank suspensions.
    pub police: Option<u64>,
    /// Overrides `police` for arrest bookings only.
    pub arrest: Option<u64>,
}

impl StaffLogChannels {
    #[must_use]
    pub fn new(moderation: Option<u64>, ban: Option<u64>) -> Self {
        Self {
            moderation,
            ban,
            ..Self::default()
        }
    }

    #[must_use]
    pub fn any(&self) -> bool {
        self.moderation.is_some()
            || self.ban.is_some()
            || self.police.is_some()
            || self.arrest.is_some()
    }
}

/// By SUBSTRING, so a ban variant added later routes without anybody extending a list. The cost
/// of being wrong is a line in the wrong channel, which is why a substring is acceptable here.
#[must_use]
pub fn is_ban_action(action: &str) -> bool {
    action.to_ascii_lowercase().contains("ban")
}

/// An EXPLICIT set: "arrest", "release" and "warrant" are not distinctive the way "ban" is.
const POLICE_ACTIONS: [&str; 13] = [
    "arrest",
    "release",
    "sentence",
    "warrant",
    "warrant-clear",
    "bail",
    "suspendrank",
    "unsuspendrank",
    "promotion",
    "demotion",
    "subclass",
    "license-remove",
    "license-give",
];

#[must_use]
pub fn is_police_action(action: &str) -> bool {
    let action = action.trim();
    POLICE_ACTIONS.iter().any(|p| eq_ignore_case(p, action))
}

/// The channel an action goes to, or `None` when none is configured. Police order matches the
/// Node bot: `ARREST_CHANNEL` wins for bookings, else `POLICE_LOG_CHANNEL`, and the arrest
/// channel serves every police action when it is the only one set.
#[must_use]
pub fn channel_for(action: &str, channels: &StaffLogChannels) -> Option<u64> {
    if is_ban_action(action) {
        return channels.ban.or(channels.moderation);
    }
    if is_police_action(action) {
        let police = if eq_ignore_case(action.trim(), "arrest") {
            channels.arrest.or(channels.police)
        } else {
            channels.police.or(channels.arrest)
        };
        return police.or(channels.moderation).or(channels.ban);
    }
    channels.moderation.or(channels.ban)
}

/// The embed one action becomes. Every field is attacker-influenced somewhere, so names go in
/// code spans (a player called "everyone" cannot read as a mention) and the reason is
/// flattened to one line (a newline cannot forge a field).
#[must_use]
pub fn build(action: &ModAction) -> Embed {
    let kind = Sanitize::message(Some(&action.action)).to_uppercase();
    let embed = if is_ban_action(&action.action) {
        if action.action.to_ascii_lowercase().starts_with("un") {
            theme::success(&kind, None)
        } else {
            theme::punishment(&format!("{} {kind}", theme::DENY), None)
        }
    } else {
        theme::notice(&format!("{} {kind}", theme::DOT), None)
    };
    let mut embed = embed
        .field(
            "Target",
            format!("`{}`", Sanitize::code(Some(&action.player))),
            true,
        )
        .field(
            "Staff",
            format!("`{}`", Sanitize::code(Some(&action.moderator))),
            true,
        );
    if let Some(reason) = action.reason.as_deref().filter(|r| !r.is_empty()) {
        embed = embed.field("Reason", Sanitize::message(Some(reason)), false);
    }
    embed.brand(Some(&format!(
        "{} Eastern",
        EasternTime::stamp(Some(action.at))
    )))
}

/// The sink.
pub struct ChannelStaffLog {
    target: Arc<dyn ChannelPoster>,
    channels: StaffLogChannels,
}

impl std::fmt::Debug for ChannelStaffLog {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ChannelStaffLog")
            .field("channels", &self.channels)
            .finish_non_exhaustive()
    }
}

impl ChannelStaffLog {
    #[must_use]
    pub fn new(target: Arc<dyn ChannelPoster>, channels: StaffLogChannels) -> Self {
        Self { target, channels }
    }

    /// At least one channel is configured.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.channels.any()
    }

    /// Post one action. NEVER FAILS: the audit record is already written, and a delivery
    /// problem must not read as a lost ban.
    pub async fn post_action(&self, action: &ModAction) {
        let Some(channel) = channel_for(&action.action, &self.channels) else {
            return;
        };
        if let Err(error) = self.target.send(channel, build(action), Vec::new()).await {
            tracing::warn!(action = %action.action, channel, %error, "could not post to the staff log channel");
        }
    }
}

impl StaffLogSink for ChannelStaffLog {
    fn post(&self, action: ModAction) -> BoxFuture<'_, ()> {
        Box::pin(async move { self.post_action(&action).await })
    }
}
