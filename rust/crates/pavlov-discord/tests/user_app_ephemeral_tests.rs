//! Port of `UserAppEphemeralTests.cs`: deferring ephemerally where Discord requires it. A
//! user-installed app may only speak ephemerally in a server it has not been added to, and a
//! public deferral there is Discord's own "the application did not respond".

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_discord::interaction::IntegrationOwners;
use pavlov_discord::runner::requires_ephemeral;

const USER: IntegrationOwners = IntegrationOwners {
    guild_install: None,
    user_install: Some(1234),
};
const GUILD: IntegrationOwners = IntegrationOwners {
    guild_install: Some(1234),
    user_install: None,
};
const BOTH: IntegrationOwners = IntegrationOwners {
    guild_install: Some(1234),
    user_install: Some(1234),
};
const NONE: IntegrationOwners = IntegrationOwners {
    guild_install: None,
    user_install: None,
};

/// Fails the test if the fallback is consulted when the owners were authoritative.
fn unreachable() -> bool {
    panic!("the owners were authoritative; the guild cache should not have been asked")
}

#[test]
fn a_user_installed_app_in_somebody_elses_server_is_forced_ephemeral() {
    assert!(requires_ephemeral(Some(999), Some(&USER), unreachable));
}

#[test]
fn a_guild_the_bot_is_installed_in_is_left_alone() {
    assert!(!requires_ephemeral(Some(999), Some(&GUILD), unreachable));
}

#[test]
fn both_installations_at_once_is_still_a_guild_install() {
    assert!(!requires_ephemeral(Some(999), Some(&BOTH), unreachable));
}

#[test]
fn a_direct_message_is_never_forced() {
    assert!(!requires_ephemeral(None, None, unreachable));
    assert!(!requires_ephemeral(None, Some(&USER), unreachable));
}

#[test]
fn with_no_owners_the_guild_cache_decides() {
    assert!(requires_ephemeral(Some(999), None, || false));
    assert!(!requires_ephemeral(Some(999), None, || true));
}

#[test]
fn an_empty_owner_list_falls_back_rather_than_assuming() {
    assert!(!requires_ephemeral(Some(999), Some(&NONE), || true));
    assert!(requires_ephemeral(Some(999), Some(&NONE), || false));
}
