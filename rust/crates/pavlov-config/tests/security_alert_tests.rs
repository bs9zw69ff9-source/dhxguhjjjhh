//! Port of `SecurityAlertTests.cs`, the recipients half: who a security alert goes to. The
//! alerts themselves (evasion, alts, VPN bans, delivery) arrive with the services in M6.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use common::features;

#[test]
fn the_explicit_list_wins() {
    let options = features(&[
        ("SECURITY_DM_IDS", "307052224087851009"),
        ("OWNER_IDS", "1,2"),
    ]);

    assert_eq!(
        options.security_alert_recipients(),
        [307_052_224_087_851_009]
    );
}

#[test]
fn with_no_explicit_list_the_owners_get_them() {
    /* The alternative default is "nobody", which is indistinguishable from the feature being
    broken - and this is a feature whose entire job is to speak up. */
    let options = features(&[("OWNER_IDS", "1,2"), ("SUPER_OWNER_IDS", "3")]);

    assert_eq!(options.security_alert_recipients(), [3, 1, 2]);
}

#[test]
fn nobody_is_messaged_twice() {
    // An id in both OWNER_IDS and SUPER_OWNER_IDS is an ordinary way to write "this person
    // owns the bot", not a request for two identical DMs.
    assert_eq!(
        features(&[("OWNER_IDS", "7"), ("SUPER_OWNER_IDS", "7")]).security_alert_recipients(),
        [7]
    );
}

#[test]
fn with_nothing_configured_the_feature_is_off() {
    assert!(features(&[]).security_alert_recipients().is_empty());
}
