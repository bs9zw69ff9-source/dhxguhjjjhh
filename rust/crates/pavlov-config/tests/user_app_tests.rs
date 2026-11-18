//! Port of `UserAppTests.cs`: the `USER_APP` switch. The registration it changes is the
//! gateway adapter's and is verified by the startup log on a live bot, not here.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::path::Path;

use pavlov_config::BotOptions;

fn bind(settings: &[(&str, &str)]) -> BotOptions {
    let mut pairs = settings.to_vec();
    pairs.push(("DISCORD_TOKEN", "main-token"));
    BotOptions::bind(&common::config(&pairs), Path::new("/srv/bot"))
}

#[test]
fn it_is_off_by_default() {
    // Turning it on moves every command to global registration (up to an hour to show).
    assert!(!bind(&[]).user_app);
}

#[test]
fn the_usual_ways_of_saying_yes_all_work() {
    for value in ["1", "true", "TRUE", "yes", "on", " 1 "] {
        assert!(bind(&[("USER_APP", value)]).user_app, "{value:?}");
    }
}

#[test]
fn anything_else_is_off() {
    for value in ["0", "false", "no", "off", ""] {
        assert!(!bind(&[("USER_APP", value)]).user_app, "{value:?}");
    }
}

#[test]
fn guild_id_is_still_read_when_user_app_is_on() {
    // USER_APP wins at registration; GUILD_ID is kept so turning it off needs nothing undone.
    let options = bind(&[("USER_APP", "1"), ("GUILD_ID", "123456789012345678")]);
    assert!(options.user_app);
    assert_eq!(options.guild_id, Some(123_456_789_012_345_678));
}
