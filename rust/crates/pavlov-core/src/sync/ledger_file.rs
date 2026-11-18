//! Recognising a player save by its contents, and the money guard that reading protects (C#
//! `Core/Sync/LedgerFile.cs`, now over the JSON saves: see [`PlayerSave`]).
//!
//! Recognising one by CONTENT rather than by its folder is deliberate: the folder holds other
//! files, but a save always has a whole-number `money`.
//!
//! THE MONEY GUARD is [`would_wipe_balance`]. A player hopping to a server that just made them
//! a fresh zero-money save must not have that zero propagate over the money they earned
//! elsewhere.
//!
//! There is no online guard any more (owner, 2026-11-18): the mod writes a player's in-game
//! state to the file live, so the newest file IS the current one, online or not.

use super::PlayerSave;

/// True when the content is a save holding a non-negative balance.
#[must_use]
pub fn looks_like_ledger(content: Option<&str>) -> bool {
    balance(content).is_some()
}

/// The balance, or `None` when the content holds no non-negative one.
#[must_use]
pub fn balance(content: Option<&str>) -> Option<i64> {
    PlayerSave::parse(content)
        .and_then(|f| f.money())
        .filter(|v| *v >= 0)
}

/// Whether copying `source` over `destination` would replace a positive balance with nothing.
///
/// True only in the exact losing shape: the source is empty or holds a balance of zero, and the
/// destination holds a positive one. Anything else is a normal copy, because this guard exists
/// to stop money loss and not to second-guess every write.
#[must_use]
pub fn would_wipe_balance(source: Option<&str>, destination: Option<&str>) -> bool {
    let source_blank = source.is_none_or(|s| s.trim().is_empty());
    if !source_blank && balance(source) != Some(0) {
        return false; // the source carries something real
    }
    balance(destination).is_some_and(|b| b > 0) // and the destination is money we would lose
}
