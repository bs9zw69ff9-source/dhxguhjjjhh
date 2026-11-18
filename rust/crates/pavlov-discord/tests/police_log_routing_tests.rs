//! Port of `PoliceLogRoutingTests.cs`. REPORTED FROM PRODUCTION: "arrests etc do not go to
//! police logs". `POLICE_LOG_CHANNEL` and `ARREST_CHANNEL` were read nowhere in the C# port.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use futures_util::FutureExt;
use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_discord::component::ActionRow;
use pavlov_discord::interaction::DiscordError;
use pavlov_discord::post::{ChannelPoster, PostEdit};
use pavlov_discord::staff_log::{ChannelStaffLog, StaffLogChannels, channel_for};

const MOD: u64 = 1;
const BAN: u64 = 2;
const POLICE: u64 = 3;
const ARREST: u64 = 4;

fn all() -> StaffLogChannels {
    StaffLogChannels {
        moderation: Some(MOD),
        ban: Some(BAN),
        police: Some(POLICE),
        arrest: Some(ARREST),
    }
}

#[test]
fn police_actions_go_to_the_police_channel() {
    let channels = StaffLogChannels {
        police: Some(POLICE),
        ..StaffLogChannels::new(Some(MOD), Some(BAN))
    };
    for kind in ["arrest", "release", "warrant", "suspendrank", "promotion"] {
        assert_eq!(channel_for(kind, &channels), Some(POLICE), "{kind}");
    }
}

#[test]
fn arrest_channel_overrides_the_police_channel_for_bookings_only() {
    assert_eq!(channel_for("arrest", &all()), Some(ARREST));
    assert_eq!(channel_for("warrant", &all()), Some(POLICE));
    assert_eq!(channel_for("release", &all()), Some(POLICE));
}

#[test]
fn arrest_channel_alone_still_serves_every_police_action() {
    let channels = StaffLogChannels {
        arrest: Some(ARREST),
        ..StaffLogChannels::new(Some(MOD), Some(BAN))
    };
    assert_eq!(channel_for("arrest", &channels), Some(ARREST));
    assert_eq!(channel_for("warrant", &channels), Some(ARREST));
}

#[test]
fn without_a_police_channel_they_still_go_somewhere() {
    assert_eq!(
        channel_for("arrest", &StaffLogChannels::new(Some(MOD), Some(BAN))),
        Some(MOD)
    );
    assert_eq!(
        channel_for("arrest", &StaffLogChannels::new(None, Some(BAN))),
        Some(BAN)
    );
}

#[test]
fn bans_and_general_actions_are_unaffected() {
    assert_eq!(channel_for("permban", &all()), Some(BAN));
    assert_eq!(channel_for("unban", &all()), Some(BAN));
    assert_eq!(channel_for("vpnban", &all()), Some(BAN));
    assert_eq!(channel_for("serverswitch", &all()), Some(MOD));
    assert_eq!(channel_for("kick", &all()), Some(MOD));
    assert_eq!(channel_for("manual", &all()), Some(MOD));
}

struct Nowhere;

impl ChannelPoster for Nowhere {
    fn edit(&self, _: u64, _: u64, _: Embed, _: Vec<ActionRow>) -> BoxFuture<'_, PostEdit> {
        async { PostEdit::Missing }.boxed()
    }
    fn send(
        &self,
        _: u64,
        _: Embed,
        _: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        async { Ok(Some(1)) }.boxed()
    }
}

#[test]
fn a_police_channel_on_its_own_is_enough_to_enable_the_sink() {
    let channels = StaffLogChannels {
        police: Some(POLICE),
        ..StaffLogChannels::default()
    };
    assert!(channels.any());
    assert_eq!(channel_for("arrest", &channels), Some(POLICE));
    assert!(ChannelStaffLog::new(Arc::new(Nowhere), channels).enabled());
}
