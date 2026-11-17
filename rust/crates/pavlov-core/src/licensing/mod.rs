//! In-game licences `/license remove` can take away (C# `Core/Licensing/License.cs`).
//!
//! The keys are spelled "Liscense" because that is the ModSave key the game mod reads. A
//! corrected spelling would remove nothing.

use crate::text::case::eq_ignore_case;

/// One licence.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct License {
    /// The ModSave key.
    pub key: &'static str,
    pub label: &'static str,
    /// The command choice value.
    pub choice: &'static str,
}

/// Namespace for the licences, mirroring the C# static class.
#[derive(Debug)]
pub struct Licenses;

impl Licenses {
    pub const DRIVERS: License = License {
        key: "DriversLiscense",
        label: "Driver's license",
        choice: "drivers",
    };
    pub const FIREARM: License = License {
        key: "FirearmLiscense",
        label: "Firearm license",
        choice: "firearm",
    };
    pub const ALL: [License; 2] = [Self::DRIVERS, Self::FIREARM];
    /// The choice that means every licence.
    pub const EVERY: &'static str = "all";

    /// The licences a command choice names: all of them for `all`, the one matching otherwise,
    /// none for an unknown choice.
    #[must_use]
    pub fn named(choice: Option<&str>) -> Vec<License> {
        let choice = choice.map(str::trim);
        if choice.is_some_and(|c| eq_ignore_case(c, Self::EVERY)) {
            return Self::ALL.to_vec();
        }
        Self::ALL
            .into_iter()
            .filter(|l| choice.is_some_and(|c| eq_ignore_case(l.choice, c)))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_names_both_and_a_choice_names_one() {
        assert_eq!(Licenses::named(Some(" ALL ")).len(), 2);
        assert_eq!(Licenses::named(Some("Drivers")), vec![Licenses::DRIVERS]);
        assert!(Licenses::named(Some("pilot")).is_empty());
        assert!(Licenses::named(None).is_empty());
        // The game mod's own spelling, which a "corrected" key would miss.
        assert_eq!(Licenses::FIREARM.key, "FirearmLiscense");
    }
}
