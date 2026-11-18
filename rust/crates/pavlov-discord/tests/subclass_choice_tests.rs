//! Port of `SubclassChoiceTests.cs`: `/subclass`'s picker.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use indexmap::IndexMap;
use pavlov_core::factions::{FactionDefinition, FactionRegistry, FactionSet};
use pavlov_discord::commands::subclass::{MAX_CHOICES, parse_value, subclass_choices};

fn faction(name: &str, subclasses: &[&str]) -> FactionDefinition {
    let lower = name.to_lowercase();
    FactionDefinition {
        name: name.into(),
        spawn_file: format!("{lower}spawn.txt"),
        rank_files: IndexMap::from([("Member".to_owned(), format!("{lower}spawn.txt"))]),
        order: vec!["Member".into()],
        default: "Member".into(),
        subclasses: subclasses
            .iter()
            .map(|s| ((*s).to_owned(), format!("{lower}{s}.txt")))
            .collect(),
        former_names: vec![],
    }
}

fn names(set: &FactionSet) -> Vec<String> {
    subclass_choices(set).into_iter().map(|c| c.name).collect()
}

#[test]
fn subclasses_come_out_grouped_by_faction_in_faction_order() {
    let set = FactionSet::of([
        faction("NCR", &["Veteran Ranger", "Patrol Ranger"]),
        faction("Legion", &["Frumentarius"]),
    ]);
    let choices = subclass_choices(&set);
    assert_eq!(
        names(&set),
        ["Patrol Ranger", "Veteran Ranger", "Frumentarius"]
    );
    assert_eq!(choices[0].label(), "NCR - Patrol Ranger");
    assert_eq!(choices[2].label(), "Legion - Frumentarius");
}

#[test]
fn within_a_faction_they_are_sorted_by_name() {
    let set = FactionSet::of([faction(
        "NCR",
        &["Veteran Ranger", "Heavy Trooper", "Patrol Ranger"],
    )]);
    assert_eq!(
        names(&set),
        ["Heavy Trooper", "Patrol Ranger", "Veteran Ranger"]
    );
}

#[test]
fn a_name_two_factions_share_gets_a_choice_per_faction_with_unique_labels_and_values() {
    let set = FactionSet::of([faction("NCR", &["Scout"]), faction("Legion", &["Scout"])]);
    let choices = subclass_choices(&set);
    assert_eq!(
        choices.iter().map(|c| c.label()).collect::<Vec<_>>(),
        ["NCR - Scout", "Legion - Scout"]
    );
    assert_eq!(
        choices.iter().map(|c| c.value()).collect::<Vec<_>>(),
        ["NCR:Scout", "Legion:Scout"]
    );
}

#[test]
fn a_value_parses_back_to_its_faction_and_name() {
    assert_eq!(
        parse_value("Enclave:Recon"),
        (Some("Enclave".into()), "Recon".into())
    );
    assert_eq!(
        parse_value("NCR:Veteran Ranger"),
        (Some("NCR".into()), "Veteran Ranger".into())
    );
}

#[test]
fn a_bare_name_from_an_old_registration_still_parses() {
    assert_eq!(parse_value("Hellfire"), (None, "Hellfire".into()));
}

#[test]
fn every_choice_fits_discords_limits() {
    let choices = subclass_choices(FactionRegistry::default_set());
    assert!((1..=MAX_CHOICES).contains(&choices.len()));
    for choice in &choices {
        assert!((1..=100).contains(&choice.label().chars().count()));
        assert!((1..=100).contains(&choice.value().chars().count()));
    }
    let labels: std::collections::HashSet<String> = choices.iter().map(|c| c.label()).collect();
    let values: std::collections::HashSet<String> = choices.iter().map(|c| c.value()).collect();
    assert_eq!(labels.len(), choices.len());
    assert_eq!(values.len(), choices.len());
}

#[test]
fn a_faction_with_no_subclasses_contributes_nothing() {
    let set = FactionSet::of([faction("NCR", &["Veteran Ranger"]), faction("Enclave", &[])]);
    assert_eq!(names(&set), ["Veteran Ranger"]);
}

#[test]
fn no_subclasses_anywhere_is_an_empty_list_rather_than_a_throw() {
    assert!(subclass_choices(&FactionSet::of([faction("NCR", &[])])).is_empty());
}
