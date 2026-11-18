//! Deciding which server number a new install becomes, and refusing when the layout is
//! ambiguous (C# `Core/Provisioning/ServerSlotPlanner.cs`).
//!
//! The RCON side is INDEXED (`RCON_HOST_{n}`, gaps allowed) but `PAVLOV_UNITS` and
//! `PAVLOV_BASES` are POSITIONAL comma lists. Appending a positional entry only stays aligned if
//! the RCON slots are contiguous `1..M` and each list has exactly M entries; anything else and an
//! append would silently bind the new unit to the wrong server, so this refuses and says why.

use std::collections::HashSet;

/// The server layout the bot is currently running, read from configuration.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerLayout {
    /// The RCON slot numbers in use. May arrive unsorted or repeated.
    pub rcon_indices: Vec<i32>,
    /// The explicit `PAVLOV_UNITS` entries, in order. Empty when unset.
    pub units: Vec<String>,
    /// The explicit `PAVLOV_BASES` entries, in order. Empty when unset.
    pub bases: Vec<String>,
}

/// Where a new server slots in, and the full lists once it has.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SlotPlan {
    pub slot: i32,
    pub final_units: Vec<String>,
    pub final_bases: Vec<String>,
}

/// Which slot to actually build: the lowest configured one with NO install on disk, or the next
/// new slot when every configured server is really there.
///
/// BEING IN `.env` IS NOT THE SAME AS EXISTING. "Configured count plus one" built server 2 on a
/// box whose server 1 had never been installed, and server 1 was then skipped forever.
#[must_use]
pub fn target_slot(configured_count: i32, slots_with_installs: &HashSet<i32>) -> i32 {
    (1..=configured_count)
        .find(|slot| !slots_with_installs.contains(slot))
        .unwrap_or(configured_count + 1)
}

/// Plan a slot, or every reason the layout cannot take it.
///
/// # Errors
/// The problems, when the layout cannot be aligned or has no room.
pub fn plan(
    layout: &ServerLayout,
    target_slot: i32,
    new_unit: &str,
    new_base: &str,
    max_servers: i32,
) -> Result<SlotPlan, Vec<String>> {
    let mut indices = layout.rcon_indices.clone();
    indices.sort_unstable();
    indices.dedup();
    let count = i32::try_from(indices.len()).unwrap_or(i32::MAX);
    let expected = indices.len();
    let units_len = layout.units.len();
    let bases_len = layout.bases.len();

    /* FILLING IN A SLOT THAT IS ALREADY CONFIGURED. Its unit and base already sit at their
    position, so this REPLACES them; every other server keeps its position. */
    if (1..=count).contains(&target_slot) {
        if units_len != expected || bases_len != expected {
            return Err(vec![format!(
                "PAVLOV_UNITS and PAVLOV_BASES must each list exactly {count} entries, one per RCON server in order, \
                 before server {target_slot} can be rebuilt (they list {units_len} and {bases_len}). \
                 Set them explicitly - see SECOND-BOT.md."
            )]);
        }
        let at = usize::try_from(target_slot - 1).unwrap_or(0);
        let mut final_units = layout.units.clone();
        let mut final_bases = layout.bases.clone();
        new_unit.clone_into(&mut final_units[at]);
        new_base.clone_into(&mut final_bases[at]);
        return Ok(SlotPlan {
            slot: target_slot,
            final_units,
            final_bases,
        });
    }

    /* Contiguous 1..M. AN EMPTY LAYOUT IS CONTIGUOUS: deleting the last server must not leave a
    box unable to grow one. */
    let contiguous =
        indices.is_empty() || (indices.first() == Some(&1) && indices.last() == Some(&count));
    let mut problems = Vec::new();
    if !contiguous {
        let listed: Vec<String> = indices.iter().map(i32::to_string).collect();
        problems.push(format!(
            "The RCON slots in use are {}, which are not a gapless 1..{count}. \
             Fill the gap first; positional PAVLOV_UNITS/PAVLOV_BASES cannot be aligned to a gapped layout.",
            listed.join(", ")
        ));
    }
    if contiguous && units_len != expected {
        problems.push(format!(
            "PAVLOV_UNITS must list exactly {count} unit(s), one per RCON server in order, before a new one can be appended \
             (it currently lists {units_len}). Set it explicitly - see SECOND-BOT.md."
        ));
    }
    if contiguous && bases_len != expected {
        problems.push(format!(
            "PAVLOV_BASES must list exactly {count} install path(s), one per RCON server in order, before a new one can be appended \
             (it currently lists {bases_len}). Set it explicitly - see SECOND-BOT.md."
        ));
    }

    let slot = count + 1;
    if contiguous && slot > max_servers {
        problems.push(format!(
            "This box already runs {count} server(s); the bot scans at most {max_servers} RCON slots, so there is no room for another."
        ));
    }

    if !problems.is_empty() {
        return Err(problems);
    }

    let mut final_units = layout.units.clone();
    final_units.push(new_unit.to_owned());
    let mut final_bases = layout.bases.clone();
    final_bases.push(new_base.to_owned());
    Ok(SlotPlan {
        slot,
        final_units,
        final_bases,
    })
}
