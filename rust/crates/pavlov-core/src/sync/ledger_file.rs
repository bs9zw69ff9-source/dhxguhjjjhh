//! Recognising a caps ledger by its contents, and the two guards that reading protects (C#
//! `Core/Sync/LedgerFile.cs`).
//!
//! A player's balance file is either a single non-negative integer, or the keyed shape with a
//! `Money` line (see [`ModSaveFile`]). Recognising one by CONTENT rather than by its folder is
//! deliberate: the sub-directory a mod files balances under is the mod's choice, but the shape
//! of the file is the same on every server.
//!
//! - THE ONLINE GUARD keys off [`looks_like_ledger`]. A connected player's balance is still
//!   moving in server memory, so the newest modified time no longer says which copy is current.
//!   The blanket sweep skips a ledger whose player is online; the join-time sync handles those.
//! - THE MONEY GUARD is [`would_wipe_balance`]. A player hopping to a server that just made them
//!   a fresh zero-cap file must not have that zero propagate over the caps they earned elsewhere.

use super::ModSaveFile;

/// True when the content holds a non-negative balance, in either shape.
#[must_use]
pub fn looks_like_ledger(content: Option<&str>) -> bool {
    balance(content).is_some()
}

/// The balance, or `None` when the content holds no non-negative one.
#[must_use]
pub fn balance(content: Option<&str>) -> Option<i64> {
    ModSaveFile::parse(content)
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
    balance(destination).is_some_and(|b| b > 0) // and the destination is caps we would lose
}
