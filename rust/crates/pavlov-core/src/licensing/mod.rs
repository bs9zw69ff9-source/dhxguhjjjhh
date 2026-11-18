//! In-game licences `/license` gives and takes away (C# `Core/Licensing/License.cs`).
//!
//! The keys are the fields of the JSON save the game mod writes (`drivers_license`,
//! `firearm_license`). The old text saves spelled them "Liscense"; that format is gone.

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
        key: "drivers_license",
        label: "Driver's license",
        choice: "drivers",
    };
    pub const FIREARM: License = License {
        key: "firearm_license",
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
        // The JSON save's field names, exactly.
        assert_eq!(Licenses::FIREARM.key, "firearm_license");
        assert_eq!(Licenses::DRIVERS.key, "drivers_license");
    }
}
