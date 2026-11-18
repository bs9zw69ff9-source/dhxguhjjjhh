//! Port of `VpnAutoBanSwitchTests.cs`, the setting half. What the switch withholds (the ban,
//! never the screening) arrives with the VPN responder in M6.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use common::features;

#[test]
fn the_default_is_on_and_an_explicit_zero_turns_it_off() {
    /* Unset must not change behaviour for a deployment that has this working, and the
    documented values have to be the ones that actually parse. */
    let read = |value: Option<&str>| match value {
        None => features(&[]).vpn_auto_ban,
        Some(v) => features(&[("VPN_AUTOBAN", v)]).vpn_auto_ban,
    };

    assert!(read(None));
    assert!(read(Some("1")));
    assert!(read(Some("true")));
    assert!(!read(Some("0")));
}
