//! The built-in faction set: the GWPD (C# `FactionRegistry`, police part).

use std::sync::LazyLock;

use indexmap::IndexMap;

use super::{FactionDefinition, FactionSet};

fn files(pairs: &[(&str, &str)]) -> IndexMap<String, String> {
    pairs
        .iter()
        .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
        .collect()
}

/// GWPD, AND IT WAS NYPD. The name stays; the LADDER moved (owner, 2026-11-18) to the sheriff's
/// ranks in `ModSave/ServerRoles`, one in-game name per line, read live by the game:
/// `deputyfirstclass.txt` up to `sheriff.txt`.
///
/// - NO SUB-CLASSES and NO SPAWN FILE: being in a rank file is all the game needs. An empty
///   `spawn_file` means none, and nothing writes one; a `FACTIONS_PATH` ladder can still name one.
/// - The old name stays as a former name so records filed under it still resolve.
fn gwpd() -> FactionDefinition {
    FactionDefinition {
        name: FactionRegistry::POLICE_NAME.to_owned(),
        former_names: vec!["NYPD".to_owned()],
        order: [
            "Deputy First Class",
            "Corporal",
            "Sergeant",
            "Lieutenant",
            "Captain",
            "Undersheriff",
            "Sheriff",
        ]
        .map(str::to_owned)
        .to_vec(),
        default: "Deputy First Class".to_owned(),
        spawn_file: String::new(),
        rank_files: files(&[
            ("Deputy First Class", "deputyfirstclass.txt"),
            ("Corporal", "corporal.txt"),
            ("Sergeant", "sergeant.txt"),
            ("Lieutenant", "lieutenant.txt"),
            ("Captain", "captain.txt"),
            ("Undersheriff", "undersheriff.txt"),
            ("Sheriff", "sheriff.txt"),
        ]),
        subclasses: IndexMap::new(),
    }
}

static POLICE: LazyLock<FactionSet> = LazyLock::new(|| FactionSet::of([gwpd()]));

/// Namespace for the built-in set, mirroring the C# static class.
#[derive(Debug)]
pub struct FactionRegistry;

impl FactionRegistry {
    /// The police faction's name.
    pub const POLICE_NAME: &'static str = "GWPD";

    /// Preset names `FACTION_SET` accepts, for the error message when one does not match.
    pub const PRESET_NAMES: [&'static str; 1] = ["police"];

    /// The police set: the GWPD and nothing else.
    #[must_use]
    pub fn police() -> &'static FactionSet {
        &POLICE
    }

    /// What a bot runs when nothing is configured: the police set, the only built-in one.
    #[must_use]
    pub fn default_set() -> &'static FactionSet {
        &POLICE
    }

    /// A built-in set by name, or `None` when the name is not one, so a typo is refused at
    /// startup rather than quietly running the wrong factions. "fallout" is `None` too; see
    /// [`FactionRegistry::is_removed_preset`].
    #[must_use]
    pub fn preset(name: Option<&str>) -> Option<&'static FactionSet> {
        match name?.trim().to_lowercase().as_str() {
            "police" | "gwpd" | "nypd" | "default" | "builtin" | "built-in" => Some(&POLICE),
            _ => None,
        }
    }

    /// The preset name that used to select the fallout ladders, refused by name.
    pub const REMOVED_FALLOUT_NAME: &'static str = "fallout";

    /// True when `name` asks for the fallout ladders, which were removed (2026-11-17).
    ///
    /// Its own check so the startup error can say "removed" instead of "unknown": a `.env`
    /// that said fallout last week is not a typo.
    #[must_use]
    pub fn is_removed_preset(name: Option<&str>) -> bool {
        name.is_some_and(|n| n.trim().eq_ignore_ascii_case(Self::REMOVED_FALLOUT_NAME))
    }

    /// One built-in faction by current or former name.
    #[must_use]
    pub fn get(faction: Option<&str>) -> Option<&'static FactionDefinition> {
        POLICE.get(faction)
    }
}
