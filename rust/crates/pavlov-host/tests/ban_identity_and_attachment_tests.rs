//! Port of `BanIdentityAndAttachmentTests.cs`: in-game bans filed under a readable name.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::sync::Arc;

use pavlov_host::moderation::ResolveName;
use pavlov_host::moderation::ban_file::resolve_name;

const EOS: &str = "0002a1b3c4d5e6f708192a3b4c5d6e7f";

fn resolver(f: impl Fn(&str) -> Option<String> + Send + Sync + 'static) -> ResolveName {
    Arc::new(f)
}

#[test]
fn an_in_game_ban_is_filed_under_the_username_when_the_id_is_known() {
    let r = resolver(|id| (id == EOS).then(|| "Holosight1".to_owned()));
    assert_eq!(resolve_name(EOS, Some(&r)), "Holosight1");
}

#[test]
fn an_unknown_id_keeps_the_id_rather_than_being_dropped() {
    assert_eq!(resolve_name(EOS, Some(&resolver(|_| None))), EOS);
    assert_eq!(
        resolve_name(EOS, Some(&resolver(|_| Some("   ".into())))),
        EOS
    );
}

#[test]
fn an_already_named_entry_is_left_alone() {
    assert_eq!(resolve_name("Alice", Some(&resolver(|_| None))), "Alice");
}

#[test]
fn with_no_resolver_configured_nothing_changes() {
    assert_eq!(resolve_name(EOS, None), EOS);
}
