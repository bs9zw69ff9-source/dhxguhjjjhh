//! One player's `modsave/<player>.txt`, in either of the two shapes a mod writes (C#
//! `Core/Sync/ModSaveFile.cs`).
//!
//! TWO SHAPES, AND THE BOT ONLY KNEW ONE. Some mods write a bare number. The police server's
//! mod writes key and value on alternating lines:
//!
//! ```text
//! Money
//! 0
//! DriversLiscense
//! false
//! FirearmLiscense
//! false
//! ```
//!
//! Every reader once parsed the whole file as one integer, so on the keyed shape a balance read
//! as "no ledger", and a write replaced the file with a bare number - taking the licences, and
//! anything else the mod keeps there, with it.
//!
//! AN EDIT CHANGES ONE VALUE AND NOTHING ELSE. Keys the bot does not know, their order, their
//! spelling ("Liscense" is the mod's, and the game looks it up that way), the line endings and
//! the trailing newline all come back out as they went in. The game owns this file; the bot is
//! a guest in it.
//!
//! `None` from [`ModSaveFile::parse`] means "not a shape this understands", and every writer
//! treats that as a refusal. Overwriting a file you cannot read is how the licences were lost.

use std::fmt;

use crate::text::case::eq_ignore_case;

/// Why [`ModSaveFile::with`] refused an edit.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModSaveEditError {
    /// The key is not in the file. A key the mod did not write is one it may not read, or may
    /// read as something else; inventing it is the same mistake as inventing a ledger.
    NoSuchKey,
    /// The value holds a line break, which would shift every key after it.
    MultiLineValue,
}

impl fmt::Display for ModSaveEditError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::NoSuchKey => "the key is not in the save file",
            Self::MultiLineValue => {
                "A value must fit on one line, or it would shift every key after it."
            }
        })
    }
}

impl std::error::Error for ModSaveEditError {}

/// A parsed save file. [`fmt::Display`] reproduces it byte for byte.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModSaveFile {
    bare: Option<i64>,
    pairs: Vec<(String, String)>,
    newline: &'static str,
    trailing_newline: bool,
}

/// .NET `long.TryParse(NumberStyles.Integer)` on already-trimmed text: an optional sign and
/// digits, nothing else.
fn integer(text: &str) -> Option<i64> {
    text.trim().parse::<i64>().ok()
}

impl ModSaveFile {
    /// The key the balance is filed under in the keyed shape.
    pub const MONEY_KEY: &'static str = "Money";

    /// The file, or `None` when it is neither shape.
    ///
    /// The keyed shape must pair up exactly - an odd line count is a file the game is half way
    /// through writing, or one that is not a save at all, and either way it is not edited.
    #[must_use]
    pub fn parse(content: Option<&str>) -> Option<Self> {
        let content = content?;
        if content.trim().is_empty() {
            return None;
        }

        if let Some(bare) = integer(content) {
            return Some(Self {
                bare: Some(bare),
                pairs: Vec::new(),
                newline: "\n",
                trailing_newline: false,
            });
        }

        let newline = if content.contains("\r\n") {
            "\r\n"
        } else {
            "\n"
        };
        let mut lines: Vec<&str> = content
            .split('\n')
            .map(|l| l.trim_end_matches('\r'))
            .collect();
        let trailing = lines.last().is_some_and(|l| l.is_empty());
        if trailing {
            lines.pop();
        }

        if lines.is_empty() || lines.len() % 2 != 0 {
            return None;
        }

        let mut pairs = Vec::with_capacity(lines.len() / 2);
        for pair in lines.chunks_exact(2) {
            let (key, value) = (pair[0], pair[1]);
            if key.trim().is_empty() {
                return None;
            }
            pairs.push((key.to_owned(), value.to_owned()));
        }

        Some(Self {
            bare: None,
            pairs,
            newline,
            trailing_newline: trailing,
        })
    }

    /// The whole file is one number.
    #[must_use]
    pub fn is_bare(&self) -> bool {
        self.bare.is_some()
    }

    /// The balance, or `None` when the file has none or it is not a number.
    #[must_use]
    pub fn money(&self) -> Option<i64> {
        self.bare
            .or_else(|| self.get(Self::MONEY_KEY).and_then(integer))
    }

    /// A value by key, ignoring case and surrounding space; `None` when absent.
    #[must_use]
    pub fn get(&self, key: &str) -> Option<&str> {
        self.index_of(key).map(|i| self.pairs[i].1.as_str())
    }

    /// Whether a value reads as `true`. False when it is anything else or absent.
    #[must_use]
    pub fn is_true(&self, key: &str) -> bool {
        self.get(key)
            .is_some_and(|v| eq_ignore_case(v.trim(), "true"))
    }

    /// The same file with one value replaced. NEVER ADDS A KEY.
    ///
    /// # Errors
    /// [`ModSaveEditError::MultiLineValue`] for a value with a line break (checked first, as the
    /// C# argument check is), [`ModSaveEditError::NoSuchKey`] when the key is not in the file.
    pub fn with(&self, key: &str, value: &str) -> Result<Self, ModSaveEditError> {
        if value.contains(['\n', '\r']) {
            return Err(ModSaveEditError::MultiLineValue);
        }
        let index = self.index_of(key).ok_or(ModSaveEditError::NoSuchKey)?;
        let mut pairs = self.pairs.clone();
        pairs[index].1 = value.to_owned();
        Ok(Self {
            bare: None,
            pairs,
            newline: self.newline,
            trailing_newline: self.trailing_newline,
        })
    }

    /// The same file with a new balance, or `None` when it has nowhere to put one.
    #[must_use]
    pub fn with_money(&self, balance: i64) -> Option<Self> {
        if self.is_bare() {
            return Some(Self {
                bare: Some(balance),
                ..self.clone()
            });
        }
        self.with(Self::MONEY_KEY, &balance.to_string()).ok()
    }

    fn index_of(&self, key: &str) -> Option<usize> {
        let key = key.trim();
        self.pairs
            .iter()
            .position(|(k, _)| eq_ignore_case(k.trim(), key))
    }
}

impl fmt::Display for ModSaveFile {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if let Some(bare) = self.bare {
            return write!(f, "{bare}");
        }
        for (i, (key, value)) in self.pairs.iter().enumerate() {
            if i > 0 {
                f.write_str(self.newline)?;
            }
            write!(f, "{key}{}{value}", self.newline)?;
        }
        if self.trailing_newline {
            f.write_str(self.newline)?;
        }
        Ok(())
    }
}
