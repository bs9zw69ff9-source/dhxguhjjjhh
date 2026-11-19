//! The Discord roles that grant each tier (C# `Discord/Access.cs` `RoleMap`), stored as the
//! `roles` dataset.
//!
//! TWO KEY SPELLINGS ARE READ, ONE IS WRITTEN. The Node bot stored `modRoleId`, `adminRoleId`,
//! `gambinoRoleId` and the rest, as strings; this type's own keys are unsuffixed numbers. An
//! imported Node file keeps working, the canonical key wins when both are present, and every
//! write normalises the dataset to the unsuffixed names. The two mafia roles become separate
//! faction entries.

use serde::ser::SerializeMap;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::{Map, Value};

use crate::factions::FactionRegistry;
use crate::text::case::{IgnoreCaseMap, eq_ignore_case};

/// The role map. `None` is "no role set".
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RoleMap {
    pub mod_role: Option<u64>,
    pub admin_role: Option<u64>,
    pub faction_leader_role: Option<u64>,
    pub police_role: Option<u64>,
    /// Faction name -> role, compared case-insensitively.
    pub faction_roles: IgnoreCaseMap<u64>,
    /// The legacy shared mafia slot.
    pub mafia_role: Option<u64>,
    /// The legacy police slot.
    pub nypd_role: Option<u64>,
}

/// A snowflake as either bot stored it: a number, or a numeric string. Zero, empty and
/// anything unparseable are "no role" - never an unreadable dataset.
fn id(value: Option<&Value>) -> Option<u64> {
    match value? {
        Value::Number(n) => n.as_u64(),
        Value::String(s) => s.trim().parse().ok(),
        _ => None,
    }
    .filter(|id| *id != 0)
}

fn either(root: &Map<String, Value>, canonical: &str, legacy: &str) -> Option<u64> {
    id(root.get(canonical)).or_else(|| id(root.get(legacy)))
}

impl RoleMap {
    /// Read the stored document, in either shape.
    #[must_use]
    pub fn from_value(value: &Value) -> Self {
        let Some(root) = value.as_object() else {
            return Self::default();
        };
        let mut faction_roles = IgnoreCaseMap::new();
        if let Some(Value::Object(roles)) = root.get("factionRoles") {
            for (name, role) in roles {
                if let Some(role) = id(Some(role)) {
                    faction_roles.insert(name.clone(), role);
                }
            }
        }
        for (faction, legacy) in [("Gambino", "gambinoRoleId"), ("Colombo", "colomboRoleId")] {
            if let Some(role) = id(root.get(legacy))
                && !faction_roles.contains_key(faction)
            {
                faction_roles.insert(faction, role);
            }
        }
        Self {
            mod_role: either(root, "modRole", "modRoleId"),
            admin_role: either(root, "adminRole", "adminRoleId"),
            faction_leader_role: either(root, "factionLeaderRole", "factionLeaderRoleId"),
            police_role: either(root, "policeRole", "policeRoleId"),
            faction_roles,
            mafia_role: id(root.get("mafiaRole")),
            nypd_role: either(root, "nypdRole", "nypdRoleId"),
        }
    }

    /// The role for a faction: its own entry, then the legacy mafia and police slots.
    #[must_use]
    pub fn role_for(&self, faction: Option<&str>) -> Option<u64> {
        let faction = faction.filter(|f| !f.trim().is_empty())?;
        if let Some(role) = self.faction_roles.get(faction) {
            return Some(*role);
        }
        if eq_ignore_case(faction, "Gambino") || eq_ignore_case(faction, "Colombo") {
            return self.mafia_role;
        }
        // The legacy police slot, under the police faction's name or the one it had before.
        let is_police = FactionRegistry::police()
            .get(Some(faction))
            .is_some_and(|f| f.name == FactionRegistry::POLICE_NAME);
        if is_police { self.nypd_role } else { None }
    }

    /// One line for `/diagnose`: every tier, and the per-faction roles by name.
    #[must_use]
    pub fn describe(&self) -> String {
        let tier = |label: &str, id: Option<u64>| {
            format!(
                "{label} {}",
                id.map_or_else(|| "unset".to_owned(), |v| v.to_string())
            )
        };
        let tiers = [
            tier("mod", self.mod_role),
            tier("admin", self.admin_role),
            tier("whitelist leader", self.faction_leader_role),
            tier("police", self.police_role),
        ]
        .join(", ");
        let factions = if self.faction_roles.is_empty() {
            "no per-faction roles".to_owned()
        } else {
            let mut names: Vec<&str> = self.faction_roles.keys().collect();
            names.sort_by(|a, b| crate::text::case::cmp_ignore_case(a, b));
            format!("{} per-faction role(s): {}", names.len(), names.join(", "))
        };
        format!("{tiers}; {factions}")
    }
}

impl<'de> Deserialize<'de> for RoleMap {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        Ok(Self::from_value(&Value::deserialize(deserializer)?))
    }
}

impl Serialize for RoleMap {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        let mut map = serializer.serialize_map(Some(7))?;
        map.serialize_entry("modRole", &self.mod_role)?;
        map.serialize_entry("adminRole", &self.admin_role)?;
        map.serialize_entry("factionLeaderRole", &self.faction_leader_role)?;
        map.serialize_entry("policeRole", &self.police_role)?;
        map.serialize_entry("factionRoles", &self.faction_roles)?;
        map.serialize_entry("mafiaRole", &self.mafia_role)?;
        map.serialize_entry("nypdRole", &self.nypd_role)?;
        map.end()
    }
}
