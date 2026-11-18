//! Port of `FactionTests.cs` (classes FactionRegistryTests and MembershipRulesTests, with the
//! shared `TestFactions` helpers). The C# used the NYPD name to look up the police faction; it
//! resolves to the GWPD through the former-name lookup, here as there.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use indexmap::IndexMap;
use pavlov_core::factions::{FactionDefinition, FactionRegistry, FactionSet};

/// A faction that is not in the registry, so the rule tests do not depend on which factions
/// the server happens to run.
pub fn other() -> FactionDefinition {
    let ranks = [
        "Associate",
        "Soldier",
        "Capo",
        "Consigliere",
        "Underboss",
        "Boss",
    ];
    FactionDefinition {
        name: "Other".into(),
        order: ranks.map(str::to_owned).to_vec(),
        default: "Associate".into(),
        spawn_file: "otherspawn.txt".into(),
        rank_files: ranks
            .iter()
            .map(|r| ((*r).to_owned(), format!("other{}.txt", r.to_lowercase())))
            .collect(),
        subclasses: IndexMap::new(),
        former_names: vec![],
    }
}

/// A faction with no ladder: one nominal rank, one file that is also the spawn file.
pub fn spawn_only(name: &str) -> FactionDefinition {
    let file = format!("{}spawn.txt", name.to_lowercase());
    FactionDefinition {
        name: name.into(),
        order: vec!["Member".into()],
        default: "Member".into(),
        spawn_file: file.clone(),
        rank_files: IndexMap::from([("Member".to_owned(), file)]),
        subclasses: IndexMap::new(),
        former_names: vec![],
    }
}

/// The GWPD with two spawn-only factions beside it, as a FACTIONS_PATH could configure.
pub fn with_mafias() -> FactionSet {
    FactionSet::of([
        spawn_only("Gambino"),
        spawn_only("Colombo"),
        FactionRegistry::get(Some(FactionRegistry::POLICE_NAME))
            .unwrap()
            .clone(),
    ])
}

fn nypd() -> &'static FactionDefinition {
    FactionRegistry::get(Some("NYPD")).unwrap()
}

mod faction_registry_tests {
    use super::*;
    use pavlov_core::text::case::{IgnoreCaseSet, eq_ignore_case};

    #[test]
    fn every_faction_is_internally_consistent() {
        for faction in FactionRegistry::police().all() {
            assert!(!faction.order.is_empty());
            assert!(faction.order.contains(&faction.default));
            for rank in &faction.order {
                assert!(
                    faction.rank_files.contains_key(rank),
                    "{}/{rank} has no roster file",
                    faction.name
                );
            }
            assert!(
                !faction.spawn_file.trim().is_empty(),
                "{} has no spawn file",
                faction.name
            );
        }
        assert!(FactionRegistry::police().problems().is_empty());
        assert!(with_mafias().problems().is_empty());
    }

    #[test]
    fn roster_filenames_match_the_ones_the_game_install_has() {
        // Spelled out, not derived: a rule computed from the registry would agree with it.
        assert_eq!(nypd().spawn_file, "policespawn.txt");
        let n = nypd();
        assert!(
            !n.rank_files
                .values()
                .any(|f| eq_ignore_case(f, &n.spawn_file))
        );
        assert!(
            !n.subclasses
                .values()
                .any(|f| eq_ignore_case(f, &n.spawn_file))
        );
    }

    #[test]
    fn roster_filenames_are_globally_unique() {
        let files: Vec<&String> = FactionRegistry::police()
            .all()
            .flat_map(|f| f.rank_files.values().chain(f.subclasses.values()))
            .collect();
        let distinct: IgnoreCaseSet = files.iter().map(|f| (*f).clone()).collect();
        assert_eq!(distinct.len(), files.len());
    }

    #[test]
    fn order_is_lowest_to_highest() {
        assert_eq!(nypd().lowest(), "Cadet");
        assert_eq!(nypd().highest(), "Chief of Police");
        assert!(nypd().index_of(Some("Sergeant")) > nypd().index_of(Some("Patrolman")));
    }

    #[test]
    fn faction_lookup_is_case_insensitive() {
        assert!(FactionRegistry::get(Some("nypd")).is_some());
        assert!(FactionRegistry::get(Some("NYPD")).is_some());
        assert!(FactionRegistry::get(Some("gwpd")).is_some());
        assert!(FactionRegistry::get(Some("Yakuza")).is_none());
        assert!(FactionRegistry::get(None).is_none());
    }

    #[test]
    fn the_police_set_is_the_only_preset_and_fallout_is_refused_as_removed() {
        assert!(std::ptr::eq(
            FactionRegistry::police(),
            FactionRegistry::default_set()
        ));
        for name in [
            "police", "GWPD", "nypd", "default", "builtin", "built-in", " Police ",
        ] {
            let preset = FactionRegistry::preset(Some(name));
            assert!(
                preset.is_some_and(|p| std::ptr::eq(p, FactionRegistry::police())),
                "{name}"
            );
        }
        assert_eq!(FactionRegistry::PRESET_NAMES, ["police"]);

        assert!(FactionRegistry::preset(Some("fallout")).is_none());
        assert!(FactionRegistry::is_removed_preset(Some(" Fallout ")));
        assert!(!FactionRegistry::is_removed_preset(Some("falout")));
        assert!(!FactionRegistry::is_removed_preset(None));
    }

    #[test]
    fn only_nypd_has_subclasses() {
        assert!(nypd().has_subclass("Tactical Response Unit"));
        assert!(other().subclasses.is_empty());
    }
}

mod membership_rules_tests {
    use super::*;
    use pavlov_core::factions::{MembershipDecision, MembershipOutcome, MembershipRules};

    fn n() -> Option<&'static FactionDefinition> {
        Some(nypd())
    }

    fn held(list: &[&str]) -> Vec<String> {
        list.iter().map(|s| (*s).to_owned()).collect()
    }

    #[test]
    fn promotion_moves_one_step_up() {
        let d = MembershipRules::change_rank(n(), Some("Patrolman"), 1);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Corporal"));
    }

    #[test]
    fn demotion_moves_one_step_down() {
        let d = MembershipRules::change_rank(n(), Some("Corporal"), -1);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Patrolman"));
    }

    #[test]
    fn a_rank_can_be_set_several_places_away_in_one_step() {
        let d = MembershipRules::set_rank(n(), Some("Cadet"), Some("Sergeant"));
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Sergeant"));
    }

    #[test]
    fn setting_a_rank_works_downwards_too() {
        let d = MembershipRules::set_rank(n(), Some("Chief of Police"), Some("Patrolman"));
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Patrolman"));
    }

    #[test]
    fn the_target_is_absolute_not_relative_to_where_they_were() {
        for from in [Some("Cadet"), Some("Chief of Police"), None] {
            assert_eq!(
                MembershipRules::set_rank(n(), from, Some("Sergeant"))
                    .rank
                    .as_deref(),
                Some("Sergeant"),
                "from {from:?}"
            );
        }
    }

    #[test]
    fn a_rank_name_is_matched_case_insensitively_and_resolved_to_the_registrys_spelling() {
        let d = MembershipRules::set_rank(n(), Some("Cadet"), Some("  sErGeAnT  "));
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Sergeant"));
    }

    #[test]
    fn a_rank_the_faction_does_not_have_is_refused() {
        for target in [Some("Centurion"), Some(""), None] {
            assert_eq!(
                MembershipRules::set_rank(n(), Some("Cadet"), target).outcome,
                MembershipOutcome::NoSuchRank,
                "target {target:?}"
            );
        }
    }

    #[test]
    fn a_subclass_is_not_a_rank_and_cannot_be_set_as_one() {
        for subclass in nypd().subclasses.keys() {
            assert!(!nypd().order.contains(subclass));
            assert_eq!(
                MembershipRules::set_rank(n(), Some("Cadet"), Some(subclass)).outcome,
                MembershipOutcome::NoSuchRank
            );
        }
    }

    #[test]
    fn setting_the_rank_they_already_hold_writes_nothing() {
        let d = MembershipRules::set_rank(n(), Some("Sergeant"), Some("Sergeant"));
        assert!(!d.is_allowed());
        assert_eq!(d.outcome, MembershipOutcome::NoChange);
        assert_eq!(d.rank.as_deref(), Some("Sergeant"));
    }

    #[test]
    fn setting_a_rank_on_an_unknown_faction_is_refused() {
        assert_eq!(
            MembershipRules::set_rank(None, Some("Cadet"), Some("Sergeant")).outcome,
            MembershipOutcome::UnknownFaction
        );
    }

    #[test]
    fn the_ladder_ends_are_refused_rather_than_wrapping_around() {
        assert_eq!(
            MembershipRules::change_rank(n(), Some("Cadet"), -1).outcome,
            MembershipOutcome::AlreadyLowest
        );
        assert_eq!(
            MembershipRules::change_rank(n(), Some("Chief of Police"), 1).outcome,
            MembershipOutcome::AlreadyHighest
        );
    }

    #[test]
    fn a_promotion_into_a_busy_rank_is_allowed() {
        let d = MembershipRules::change_rank(n(), Some("Deputy Chief"), 1);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Chief of Police"));
    }

    #[test]
    fn a_demotion_into_a_busy_rank_is_allowed_too() {
        let d = MembershipRules::change_rank(n(), Some("Sergeant"), -1);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Corporal"));
    }

    #[test]
    fn no_rank_size_is_consulted_at_all() {
        // THE PROPERTY, checked by the compiler: the rules take no member count, so a
        // reintroduced cap would have to change these signatures, and this would stop building.
        let _change_rank: fn(Option<&FactionDefinition>, Option<&str>, i32) -> MembershipDecision =
            MembershipRules::change_rank;
        let _join: fn(Option<&FactionDefinition>, &[String]) -> MembershipDecision =
            MembershipRules::join;
    }

    #[test]
    fn an_unknown_current_rank_is_treated_as_the_lowest() {
        let d = MembershipRules::change_rank(n(), Some("Traffic Warden"), 1);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Patrolman"));
    }

    #[test]
    fn a_null_rank_behaves_the_same_way() {
        let d = MembershipRules::change_rank(n(), None, 1);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Patrolman"));
    }

    #[test]
    fn joining_starts_at_the_default_rank() {
        let d = MembershipRules::join(n(), &[]);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Cadet"));
    }

    #[test]
    fn a_player_cannot_be_in_two_factions() {
        let d = MembershipRules::join(n(), &held(&["Other"]));
        assert_eq!(d.outcome, MembershipOutcome::AlreadyInAnotherFaction);
        assert_eq!(d.conflict.as_deref(), Some("Other"));
    }

    #[test]
    fn rejoining_the_same_faction_is_a_no_change() {
        assert_eq!(
            MembershipRules::join(n(), &[nypd().name.clone()]).outcome,
            MembershipOutcome::NoChange
        );
    }

    #[test]
    fn joining_a_busy_faction_is_allowed() {
        let d = MembershipRules::join(n(), &[]);
        assert!(d.is_allowed());
        assert_eq!(d.rank.as_deref(), Some("Cadet"));
    }

    #[test]
    fn a_subclass_can_be_assigned_alongside_a_rank() {
        assert!(MembershipRules::change_subclass(n(), "Detective", &[], false).is_allowed());
    }

    #[test]
    fn only_one_subclass_at_a_time() {
        let d = MembershipRules::change_subclass(n(), "Detective", &held(&["Vice Officer"]), false);
        assert_eq!(d.outcome, MembershipOutcome::AlreadyHasSubclass);
        assert_eq!(d.conflict.as_deref(), Some("Vice Officer"));
    }

    #[test]
    fn assigning_one_already_held_is_a_no_change() {
        assert_eq!(
            MembershipRules::change_subclass(n(), "Detective", &held(&["Detective"]), false)
                .outcome,
            MembershipOutcome::NoChange
        );
    }

    #[test]
    fn removing_requires_holding_it() {
        assert!(
            MembershipRules::change_subclass(n(), "Detective", &held(&["Detective"]), true)
                .is_allowed()
        );
        assert_eq!(
            MembershipRules::change_subclass(n(), "Detective", &[], true).outcome,
            MembershipOutcome::NoChange
        );
    }

    #[test]
    fn a_faction_without_subclasses_refuses_them_all() {
        assert_eq!(
            MembershipRules::change_subclass(Some(&other()), "Detective", &[], false).outcome,
            MembershipOutcome::NoSuchSubclass
        );
    }

    #[test]
    fn an_unknown_faction_is_refused_everywhere() {
        assert_eq!(
            MembershipRules::change_rank(None, Some("Cadet"), 1).outcome,
            MembershipOutcome::UnknownFaction
        );
        assert_eq!(
            MembershipRules::join(None, &[]).outcome,
            MembershipOutcome::UnknownFaction
        );
        assert_eq!(
            MembershipRules::change_subclass(None, "x", &[], false).outcome,
            MembershipOutcome::UnknownFaction
        );
    }
}
