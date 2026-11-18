//! Port of `WhitelistEphemeralTests.cs`: roster replies are visible only to whoever ran them.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use futures_util::future::BoxFuture;
use pavlov_discord::command::CommandSpec;
use pavlov_discord::slash::{CommandContext, CommandError, SlashCommand};

#[test]
fn whitelist_replies_are_visible_only_to_whoever_ran_the_command() {
    let store = common::commands::store();
    assert!(common::commands::whitelist(&store, common::commands::rosters(None), &[]).ephemeral());
}

#[test]
fn promotion_and_demotion_are_quiet_too() {
    assert!(common::commands::rank(true).ephemeral());
    assert!(common::commands::rank(false).ephemeral());
}

struct Plain;

impl SlashCommand for Plain {
    fn name(&self) -> &str {
        "plain"
    }
    fn build(&self) -> CommandSpec {
        CommandSpec::new("plain", "plain")
    }
    fn handle<'a>(&'a self, _: CommandContext<'a>) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async { Ok(()) })
    }
}

#[test]
fn the_interface_default_is_still_public() {
    assert!(!Plain.ephemeral());
}
