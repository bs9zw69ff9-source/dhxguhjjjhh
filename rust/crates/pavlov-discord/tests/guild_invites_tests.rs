//! Port of `GuildInvitesTests.cs`: `/guildinvites` DMs the super owner an invite to every
//! guild the bot is in. The rendering is pinned here; the command itself is in M8.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::time::Duration;

use pavlov_core::security::OwnerGuard;
use pavlov_discord::guild_directory::{GuildInvite, INVITE_LIFETIME, INVITE_USES, invite_pages};

fn with(name: &str, url: Option<&str>, problem: Option<&str>, members: i64) -> GuildInvite {
    GuildInvite {
        guild_id: 1_234_567_890,
        guild_name: name.into(),
        member_count: members,
        url: url.map(str::to_owned),
        problem: problem.map(str::to_owned),
    }
}

const DAY: Duration = Duration::from_secs(24 * 3600);

#[test]
fn the_recipient_is_the_compiled_in_super_owner() {
    // Not an option somebody can type: that would mail working invites to anyone at all.
    assert_eq!(OwnerGuard::SUPER_OWNER_ID, 1_014_251_293_159_731_310);
}

#[test]
fn invites_are_short_lived_and_single_use() {
    assert_eq!(INVITE_LIFETIME, DAY);
    assert_eq!(INVITE_USES, 1);
}

#[test]
fn each_guild_is_one_line_with_its_invite() {
    let pages = invite_pages(
        &[
            with("New Vegas RP", Some("https://discord.gg/abc123"), None, 42),
            with("Mojave", Some("https://discord.gg/def456"), None, 42),
        ],
        DAY,
    );
    let text = pages.join("\n");
    for part in [
        "New Vegas RP",
        "https://discord.gg/abc123",
        "Mojave",
        "https://discord.gg/def456",
    ] {
        assert!(text.contains(part));
    }
    assert!(pages[0].contains("Single-use, expire in 24h."));
}

#[test]
fn a_guild_with_no_invite_is_still_listed_with_the_reason() {
    let pages = invite_pages(
        &[with(
            "Locked Down",
            None,
            Some("no channel here lets the bot create an invite (needs Create Invite)"),
            42,
        )],
        DAY,
    );
    let text = pages.join("\n");
    assert!(text.contains("Locked Down"));
    assert!(text.contains("needs Create Invite"));
}

#[test]
fn member_counts_are_grouped_so_a_large_guild_is_readable() {
    let pages = invite_pages(
        &[with("Big", Some("https://discord.gg/x"), None, 12_345)],
        DAY,
    );
    assert!(pages.join("\n").contains("12,345"));
}

#[test]
fn a_guild_name_cannot_break_out_of_the_markdown() {
    let pages = invite_pages(
        &[with("Evil ` Name", Some("https://discord.gg/x"), None, 42)],
        DAY,
    );
    assert!(!pages.join("\n").contains('`'));
}

#[test]
fn a_long_list_paginates_rather_than_losing_the_tail() {
    let many: Vec<GuildInvite> = (1..=400)
        .map(|i| {
            with(
                &format!("Guild number {i} with a reasonably long name"),
                Some(&format!("https://discord.gg/invite{i}")),
                None,
                42,
            )
        })
        .collect();
    let pages = invite_pages(&many, DAY);

    assert!(pages.len() > 1);
    assert!(pages.iter().all(|p| p.chars().count() <= 4096));
    assert!(pages.join("\n").contains("Guild number 400"));
}
