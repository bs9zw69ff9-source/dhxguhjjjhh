//! Re-filing stored records when a faction is renamed (C# `Factions/FactionRenames.cs`).
//!
//! Membership records, suspensions, the faction role and the payroll clock are all filed under
//! the faction's NAME. After a rename (NYPD to GWPD) they would otherwise be orphaned: the role
//! stops granting, the pay clock splits in two. Each dataset is re-filed once at startup; a
//! second run changes nothing, and a bot with no former names touches nothing at all.

use std::sync::Arc;

use pavlov_core::data::{SerializedStore, StoreError, UpdateResult};
use pavlov_core::factions::FactionSet;
use pavlov_core::roles::RoleMap;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_storage::datasets::{FACTION_MEMBERS, PAYROLL_STATE, RANK_SUSPENSIONS, ROLES};

use super::members::MemberIndex;
use super::rank_restorer::Suspensions;
use crate::economy::PayrollState;

/// Former name to current name, ignoring case.
pub type Renames = IgnoreCaseMap<String>;

/// Re-file every dataset that names a faction. How many datasets changed.
pub async fn carry_over(store: &Arc<SerializedStore>, factions: &FactionSet) -> usize {
    let mut renames = Renames::new();
    for (old, now) in factions.renames() {
        renames.insert(old, now);
    }
    if renames.is_empty() {
        return 0;
    }
    let shown: Vec<String> = renames.iter().map(|(o, n)| format!("{o} -> {n}")).collect();
    let shown = shown.join(", ");

    let mut changed = 0;

    let r = renames.clone();
    changed += applied(
        FACTION_MEMBERS,
        &shown,
        store
            .update(
                FACTION_MEMBERS,
                MemberIndex::new,
                move |mut map: MemberIndex| {
                    let moved = refile(map.values_mut().map(|m| &mut m.faction), &r);
                    (moved > 0).then_some(map)
                },
            )
            .await,
    );

    let r = renames.clone();
    changed += applied(
        RANK_SUSPENSIONS,
        &shown,
        store
            .update(
                RANK_SUSPENSIONS,
                Suspensions::new,
                move |mut map: Suspensions| {
                    let moved = refile(map.values_mut().map(|s| &mut s.faction), &r);
                    (moved > 0).then_some(map)
                },
            )
            .await,
    );

    let r = renames.clone();
    changed += applied(
        ROLES,
        &shown,
        store
            .update(ROLES, RoleMap::default, move |mut roles: RoleMap| {
                (rename_keys(&mut roles.faction_roles, &r) > 0).then_some(roles)
            })
            .await,
    );

    let r = renames;
    changed += applied(
        PAYROLL_STATE,
        &shown,
        store
            .update(
                PAYROLL_STATE,
                PayrollState::default,
                move |mut state: PayrollState| {
                    let moved = rename_keys(&mut state.last_paid, &r)
                        + rename_keys(&mut state.previous, &r);
                    (moved > 0).then_some(state)
                },
            )
            .await,
    );

    changed
}

/// Point each faction field that holds a former name at the current one. How many moved.
fn refile<'a>(fields: impl Iterator<Item = &'a mut String>, renames: &Renames) -> usize {
    let mut moved = 0;
    for faction in fields {
        if let Some(now) = renames.get(faction) {
            faction.clone_from(now);
            moved += 1;
        }
    }
    moved
}

/// Move each entry keyed by a former name to the current name. An entry ALREADY under the
/// current name is kept: it was set after the rename, so deliberately. How many moved.
pub fn rename_keys<T: Clone>(map: &mut IgnoreCaseMap<T>, renames: &Renames) -> usize {
    let former: Vec<(String, String)> = map
        .keys()
        .filter_map(|k| renames.get(k).map(|now| (k.to_owned(), now.clone())))
        .collect();
    for (old, now) in &former {
        if let Some(value) = map.remove(old) {
            if !map.contains_key(now) {
                map.insert(now.clone(), value);
            }
        }
    }
    former.len()
}

fn applied<T>(dataset: &str, renames: &str, result: Result<UpdateResult<T>, StoreError>) -> usize {
    match result {
        Ok(r) if r.ok => {
            tracing::info!(dataset, renames, "faction rename: dataset re-filed");
            1
        }
        Ok(r) if r.is_vetoed() => 0,
        Ok(r) => {
            tracing::warn!(
                dataset,
                renames,
                error = r.error.as_deref().unwrap_or(""),
                "faction rename: dataset was NOT re-filed - records under the old name will not be found until it is"
            );
            0
        }
        Err(error) => {
            tracing::warn!(dataset, renames, %error, "faction rename: dataset could not be re-filed");
            0
        }
    }
}
