//! `/subclass`'s picker (C# `SubclassCommand.SubclassChoices` and `ParseValue`): every
//! sub-class as a fixed choice, labelled with its faction, so two factions sharing a name
//! stay two distinct choices.

use pavlov_core::factions::FactionSet;
use pavlov_core::text::case::cmp_ignore_case;

/// Discord's choice limit per option.
pub const MAX_CHOICES: usize = 25;
/// Discord's limit on a choice's label and value.
pub const MAX_CHOICE_LENGTH: usize = 100;
/// Between the faction and the sub-class in a choice's value.
pub const VALUE_SEPARATOR: char = ':';

/// One picker entry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SubclassChoice {
    pub faction: String,
    pub name: String,
}

fn cut(text: String) -> String {
    if text.chars().count() <= MAX_CHOICE_LENGTH {
        text
    } else {
        text.chars().take(MAX_CHOICE_LENGTH).collect()
    }
}

impl SubclassChoice {
    /// `NCR - Patrol Ranger`.
    #[must_use]
    pub fn label(&self) -> String {
        cut(format!("{} - {}", self.faction, self.name))
    }

    /// `NCR:Patrol Ranger`.
    #[must_use]
    pub fn value(&self) -> String {
        cut(format!("{}{VALUE_SEPARATOR}{}", self.faction, self.name))
    }
}

/// Every sub-class, grouped by faction in faction order, sorted by name within one. A choice
/// whose label or value would repeat one already taken (only possible after truncation) is
/// left out, since Discord rejects the whole command over a duplicate.
#[must_use]
pub fn subclass_choices(factions: &FactionSet) -> Vec<SubclassChoice> {
    let mut labels = std::collections::HashSet::new();
    let mut values = std::collections::HashSet::new();
    let mut choices = Vec::new();
    for faction in factions.all() {
        let mut names: Vec<&String> = faction.subclasses.keys().collect();
        names.sort_by(|a, b| cmp_ignore_case(a, b));
        for name in names {
            let choice = SubclassChoice {
                faction: faction.name.clone(),
                name: name.clone(),
            };
            if labels.contains(&choice.label()) || values.contains(&choice.value()) {
                continue;
            }
            labels.insert(choice.label());
            values.insert(choice.value());
            choices.push(choice);
        }
    }
    choices
}

/// A choice's value back into its faction and name. A bare name, from a registration older
/// than the faction prefix, parses with no faction.
#[must_use]
pub fn parse_value(value: &str) -> (Option<String>, String) {
    match value.split_once(VALUE_SEPARATOR) {
        Some((faction, name)) => (Some(faction.trim().to_owned()), name.trim().to_owned()),
        None => (None, value.trim().to_owned()),
    }
}
