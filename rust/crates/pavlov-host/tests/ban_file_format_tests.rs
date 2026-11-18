//! Port of `BanFileFormatTests.cs`: reading the file the SERVER enforces. A plain list has no
//! blank lines, so the whole file arrives as one block; read as one record, everybody but the
//! first player quietly stopped being banned.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_host::moderation::ban_file::parse;

fn names(text: &str) -> Vec<String> {
    parse(Some(text)).into_iter().map(|e| e.name).collect()
}

#[test]
fn a_plain_list_imports_every_player_not_just_the_first() {
    let entries = parse(Some("Alice\nBob\nCharlie\n"));
    assert_eq!(
        entries.iter().map(|e| e.name.as_str()).collect::<Vec<_>>(),
        ["Alice", "Bob", "Charlie"]
    );
    assert!(entries.iter().all(|e| e.unban == "Permanent"));
}

#[test]
fn the_old_block_format_still_reads() {
    let entries = parse(Some(
        "Alice\nReason: griefing\nUnban: Permanent\n\nBob\nReason: cheating\nUnban: 3d\n",
    ));
    assert_eq!(
        entries.iter().map(|e| e.name.as_str()).collect::<Vec<_>>(),
        ["Alice", "Bob"]
    );
    assert_eq!(entries[0].reason, "griefing");
    assert_eq!(entries[1].unban, "3d");
}

#[test]
fn a_plain_list_separated_by_blank_lines_is_still_one_player_per_line() {
    assert_eq!(
        names("Alice\nBob\n\n\nCharlie\n"),
        ["Alice", "Bob", "Charlie"]
    );
}

#[test]
fn comments_and_blanks_are_not_players() {
    assert_eq!(
        names("# banned for the tournament\nAlice\n\n// note\nBob\n"),
        ["Alice", "Bob"]
    );
}

#[test]
fn a_stray_metadata_line_is_never_read_as_a_player() {
    // A player called "Reason" would be banned forever by the next export.
    assert_eq!(
        names("Alice\nReason: griefing\n\nUnban: Permanent\n"),
        ["Alice"]
    );
}

#[test]
fn carriage_returns_do_not_become_part_of_the_name() {
    assert_eq!(names("Alice\r\nBob\r\n"), ["Alice", "Bob"]);
}

#[test]
fn an_empty_or_whitespace_file_bans_nobody() {
    assert!(parse(Some("")).is_empty());
    assert!(parse(Some("\n\n  \n")).is_empty());
    assert!(parse(None).is_empty());
}
