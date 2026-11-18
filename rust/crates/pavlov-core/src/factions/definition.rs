//! One faction's ladder, and a set of factions (C# `FactionDefinition`, `FactionSet`).

use indexmap::IndexMap;

use crate::text::case::{IgnoreCaseSet, eq_ignore_case, fold_case};

/// One faction's rank ladder and roster files. Pure data.
///
/// Ranks are a LIST, lowest to highest, so promotion is "one index up" and demotion "one
/// index down": one piece of logic walking a shape, and adding a rank is a line of data.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FactionDefinition {
    pub name: String,
    /// Lowest to highest. Order IS the hierarchy.
    pub order: Vec<String>,
    /// The rank a member starts at when first whitelisted.
    pub default: String,
    /// Rank to the `Config/*.txt` roster the game reads for it.
    pub rank_files: IndexMap<String, String>,
    /// The file that grants SPAWN ACCESS, holding every member regardless of rank, for a game
    /// setup that has one; EMPTY when the rank files alone are enough (the built-in GWPD since
    /// 2026-11-18). When set, a member in a rank file but not here could not spawn, so every
    /// join and promotion keeps them listed (the C# port once dropped that and reported success).
    pub spawn_file: String,
    /// Sub-classes are NOT ranks: a member keeps their rank and may also hold one of these.
    pub subclasses: IndexMap<String, String>,
    /// What this faction used to be called: looked up as this faction, never listed as one,
    /// because membership records and suspensions are filed under the name.
    pub former_names: Vec<String>,
}

impl FactionDefinition {
    /// The lowest rank, or "" for a faction with no ranks (which [`FactionSet::problems`]
    /// reports at startup).
    #[must_use]
    pub fn lowest(&self) -> &str {
        self.order.first().map_or("", String::as_str)
    }

    /// The highest rank, or "".
    #[must_use]
    pub fn highest(&self) -> &str {
        self.order.last().map_or("", String::as_str)
    }

    /// Whether there is a ladder at all. DERIVED from the data, so it cannot disagree with it:
    /// a one-rank faction is spawn access and nothing else.
    #[must_use]
    pub fn has_ranks(&self) -> bool {
        self.order.len() > 1
    }

    /// Position in the ladder, ignoring case, or `None` when the rank is not part of it.
    #[must_use]
    pub fn index_of(&self, rank: Option<&str>) -> Option<usize> {
        let rank = rank?;
        self.order.iter().position(|r| eq_ignore_case(r, rank))
    }

    /// Whether this faction has the sub-class (exact name, as the C# dictionary did).
    #[must_use]
    pub fn has_subclass(&self, subclass: &str) -> bool {
        self.subclasses.contains_key(subclass)
    }
}

/// The factions one bot runs, with lookup by current or former name, ignoring case.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FactionSet {
    /// Folded name to faction, in the order the set was built.
    all: IndexMap<String, FactionDefinition>,
    /// Folded former name to the folded current name.
    former: IndexMap<String, String>,
}

impl FactionSet {
    /// A set from definitions. A later faction with the same name replaces an earlier one in
    /// its position, as the C# dictionary assignment did.
    #[must_use]
    pub fn of(factions: impl IntoIterator<Item = FactionDefinition>) -> Self {
        let mut set = Self::default();
        for faction in factions {
            let key = fold_case(&faction.name);
            for old in &faction.former_names {
                set.former.insert(fold_case(old), key.clone());
            }
            set.all.insert(key, faction);
        }
        set
    }

    /// Every faction, in order.
    pub fn all(&self) -> impl Iterator<Item = &FactionDefinition> {
        self.all.values()
    }

    /// Every current faction name, in order.
    pub fn names(&self) -> impl Iterator<Item = &str> {
        self.all.values().map(|f| f.name.as_str())
    }

    /// A faction by current name, then by former name, ignoring case.
    #[must_use]
    pub fn get(&self, faction: Option<&str>) -> Option<&FactionDefinition> {
        let key = fold_case(faction?);
        self.all.get(&key).or_else(|| {
            self.former
                .get(&key)
                .and_then(|current| self.all.get(current))
        })
    }

    /// Former name to current name, for carrying stored records over.
    #[must_use]
    pub fn renames(&self) -> IndexMap<String, String> {
        let mut out = IndexMap::new();
        for faction in self.all.values() {
            for old in &faction.former_names {
                out.insert(old.clone(), faction.name.clone());
            }
        }
        out
    }

    /// Everything wrong with the set, all at once, so a bad `FACTIONS_PATH` is refused at
    /// startup instead of writing roster files nobody reads.
    #[must_use]
    pub fn problems(&self) -> Vec<String> {
        let mut problems = Vec::new();
        if self.all.is_empty() {
            problems.push("No factions are defined.".to_owned());
        }

        // Folded file name to (folded owner, display owner).
        let mut files: IndexMap<String, (String, String)> = IndexMap::new();
        for faction in self.all.values() {
            if faction.order.is_empty() {
                problems.push(format!("{} has no ranks.", faction.name));
                continue;
            }
            if !faction
                .order
                .iter()
                .any(|r| eq_ignore_case(r, &faction.default))
            {
                problems.push(format!(
                    "{}'s default rank \"{}\" is not one of its ranks.",
                    faction.name, faction.default
                ));
            }
            for rank in &faction.order {
                if !faction.rank_files.contains_key(rank) {
                    problems.push(format!("{}/{} has no roster file.", faction.name, rank));
                }
            }

            /* THE SPAWN FILE MAY BE A RANK FILE, only for a faction with no ladder: that is how a
            spawn-only faction is expressed. Across factions, any shared file is a collision. */
            let owned: IgnoreCaseSet = faction
                .rank_files
                .values()
                .chain(faction.subclasses.values())
                .chain(std::iter::once(&faction.spawn_file))
                .filter(|f| !f.trim().is_empty())
                .cloned()
                .collect();
            let me = fold_case(&faction.name);
            for file in owned.iter() {
                let key = fold_case(file);
                match files.get(&key) {
                    Some((owner, owner_name)) if *owner != me => {
                        problems.push(format!("{} and {owner_name} both use \"{file}\"; two factions sharing a roster file merge their members in game.", faction.name));
                    }
                    Some(_) => {}
                    None => {
                        files.insert(key, (me.clone(), faction.name.clone()));
                    }
                }
            }
        }

        // A FORMER NAME MUST POINT AT ONE FACTION, or memberships go to the wrong roster.
        let mut claimed: IndexMap<String, (String, String)> = IndexMap::new();
        for faction in self.all.values() {
            let me = fold_case(&faction.name);
            for old in &faction.former_names {
                let key = fold_case(old);
                if self.all.contains_key(&key) {
                    problems.push(format!(
                        "{} lists \"{old}\" as a former name, but that is a faction of its own.",
                        faction.name
                    ));
                } else if let Some((_, other_name)) =
                    claimed.get(&key).filter(|(other, _)| *other != me)
                {
                    problems.push(format!(
                        "{} and {other_name} both list \"{old}\" as a former name.",
                        faction.name
                    ));
                } else {
                    claimed.insert(key, (me.clone(), faction.name.clone()));
                }
            }
        }
        problems
    }
}
