//! What commands exist, for `/help` (C# `CommandCatalog.cs`). It breaks a cycle: `/help` lists
//! every command, so it cannot hold the command list itself. The composition root populates it
//! once, after every command is built.

use std::sync::{PoisonError, RwLock};

/// One command, as the picker and `/help` describe it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommandSummary {
    pub name: String,
    pub description: String,
}

#[derive(Debug, Default)]
pub struct CommandCatalog {
    commands: RwLock<Vec<CommandSummary>>,
}

impl CommandCatalog {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Called once, with every registered command; kept sorted by name (ordinal).
    pub fn populate(&self, commands: impl IntoIterator<Item = CommandSummary>) {
        let mut all: Vec<CommandSummary> = commands.into_iter().collect();
        all.sort_by(|a, b| a.name.cmp(&b.name));
        *self
            .commands
            .write()
            .unwrap_or_else(PoisonError::into_inner) = all;
    }

    #[must_use]
    pub fn commands(&self) -> Vec<CommandSummary> {
        self.commands
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }
}
