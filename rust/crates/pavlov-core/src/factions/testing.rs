//! A ladder with every moving part, for tests only (`testing` feature).
//!
//! The built-in GWPD (since 2026-11-18) has no sub-classes and no spawn file, so tests of
//! THOSE mechanisms - sub-class picks, spawn-file self-healing, a ladder of eight - run on this
//! one: the GWPD as it was before the move to ServerRoles. It is never the running bot's
//! configuration; `FACTIONS_PATH` is how a real server gets a ladder like it.

use std::sync::LazyLock;

use indexmap::IndexMap;

use super::{FactionDefinition, FactionRegistry, FactionSet};

fn files(pairs: &[(&str, &str)]) -> IndexMap<String, String> {
    pairs
        .iter()
        .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
        .collect()
}

fn with_subclasses() -> FactionDefinition {
    FactionDefinition {
        name: FactionRegistry::POLICE_NAME.to_owned(),
        former_names: vec!["NYPD".to_owned()],
        order: [
            "Cadet",
            "Patrolman",
            "Corporal",
            "Sergeant",
            "Lieutenant",
            "Captain",
            "Deputy Chief",
            "Chief of Police",
        ]
        .map(str::to_owned)
        .to_vec(),
        default: "Cadet".to_owned(),
        spawn_file: "policespawn.txt".to_owned(),
        rank_files: files(&[
            ("Cadet", "policecadet.txt"),
            ("Patrolman", "policepatrolman.txt"),
            ("Corporal", "policecorporal.txt"),
            ("Sergeant", "policesergeant.txt"),
            ("Lieutenant", "policelieutenant.txt"),
            ("Captain", "policecaptain.txt"),
            ("Deputy Chief", "policedeputychief.txt"),
            ("Chief of Police", "policechief.txt"),
        ]),
        subclasses: files(&[
            ("Vice Officer", "policevice.txt"),
            ("Detective", "policedetective.txt"),
            ("Tactical Response Unit", "policetacticalresponse.txt"),
            ("Narcotics Bureau", "policenarcotics.txt"),
        ]),
    }
}

static LADDER: LazyLock<FactionSet> = LazyLock::new(|| FactionSet::of([with_subclasses()]));

/// The full-featured ladder: eight ranks, four sub-classes, a spawn file.
#[must_use]
pub fn ladder() -> &'static FactionSet {
    &LADDER
}

/// One faction of it, by name or former name.
#[must_use]
pub fn faction(name: Option<&str>) -> Option<&'static FactionDefinition> {
    LADDER.get(name)
}
