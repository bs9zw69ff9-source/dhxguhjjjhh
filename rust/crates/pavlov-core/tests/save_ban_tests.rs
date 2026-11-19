//! The `ban` block in a player's save (owner, 2026-11-19): what the bot writes, what it keeps,
//! and when it leaves the file alone.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::sync::{PlayerSave, SaveBan};
use serde_json::Value;

fn save(text: &str) -> PlayerSave {
    PlayerSave::parse(Some(text)).unwrap()
}

fn json(save: &PlayerSave) -> Value {
    serde_json::from_str(&save.render()).unwrap()
}

fn ban(reason: &str, date: &str) -> SaveBan {
    SaveBan {
        reason: reason.into(),
        unban_date: date.into(),
    }
}

#[test]
fn a_ban_is_written_with_the_three_fields_the_mod_reads() {
    let before = save(r#"{"money": 5, "ban": {"banned": false, "reason": "", "unban_date": ""}}"#);

    let after = before.with_ban(Some(&ban("cheating", "11/20/26"))).unwrap();

    assert_eq!(
        json(&after)["ban"],
        serde_json::json!({"banned": true, "reason": "cheating", "unban_date": "11/20/26"})
    );
    assert_eq!(after.ban(), Some(ban("cheating", "11/20/26")));
}

#[test]
fn a_save_without_a_ban_block_gets_one() {
    let after = save(r#"{"money": 5}"#)
        .with_ban(Some(&ban("alt", "")))
        .unwrap();

    assert_eq!(after.ban(), Some(ban("alt", "")));
}

#[test]
fn every_other_field_and_any_extra_key_in_the_block_is_kept() {
    let before = save(
        r#"{"name": "A", "money": 5, "look": {"shirt": 24},
            "ban": {"banned": false, "reason": "", "unban_date": "", "appeal": "x"}}"#,
    );

    let after = json(&before.with_ban(Some(&ban("r", "01/02/27"))).unwrap());

    assert_eq!(after["look"]["shirt"], 24);
    assert_eq!(after["name"], "A");
    assert_eq!(after["ban"]["appeal"], "x");
}

#[test]
fn an_unchanged_ban_is_not_rewritten() {
    let banned =
        save(r#"{"money": 5, "ban": {"banned": true, "reason": "r", "unban_date": "01/02/27"}}"#);

    assert!(banned.with_ban(Some(&ban("r", "01/02/27"))).is_none());
    // A different date or reason is a change.
    assert!(banned.with_ban(Some(&ban("r", "01/03/27"))).is_some());
}

#[test]
fn lifting_writes_the_cleared_shape_and_only_once() {
    let banned =
        save(r#"{"money": 5, "ban": {"banned": true, "reason": "r", "unban_date": "01/02/27"}}"#);

    let lifted = banned.with_ban(None).unwrap();

    assert_eq!(
        json(&lifted)["ban"],
        serde_json::json!({"banned": false, "reason": "", "unban_date": ""})
    );
    assert_eq!(lifted.ban(), None);
    assert!(lifted.with_ban(None).is_none(), "already clear");
}

#[test]
fn banned_must_be_true_to_count() {
    for block in [
        r#"{"banned": false, "reason": "r", "unban_date": ""}"#,
        r#"{"banned": "true", "reason": "r"}"#,
        r#""banned""#,
    ] {
        let text = format!(r#"{{"money": 1, "ban": {block}}}"#);
        assert_eq!(save(&text).ban(), None, "{block}");
    }
}
