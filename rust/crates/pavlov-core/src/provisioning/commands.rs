//! The pure halves of `/provisionserver` and `/deleteserver` (C# `ProvisionServerCommand.Off`,
//! `ParseMaps`, `DefaultMap` and `DeleteServerCommand.Problem`), kept here so the command layer
//! is only Discord plumbing.

use super::MapEntry;

/// The unit-name suffix for a slot: off by one, like the box. Server 1 is `pavlovserver`,
/// server 2 is `pavlovserver1`.
#[must_use]
pub fn unit_suffix(slot: i32) -> String {
    if slot <= 1 {
        String::new()
    } else {
        (slot - 1).to_string()
    }
}

/// The rotation a server gets when none is named: this network's own map, from the config that
/// is actually running, rather than a stock one nobody here plays.
#[must_use]
pub fn default_map() -> MapEntry {
    MapEntry::new("UGC5616264", "CUSTOM")
}

/// `UGC123:GUN, datacenter:SND` as map entries. A bare map defaults to `SND`; naming nothing
/// gets [`default_map`].
#[must_use]
pub fn parse_maps(raw: Option<&str>) -> Vec<MapEntry> {
    let maps: Vec<MapEntry> = raw
        .unwrap_or_default()
        .split(',')
        .map(str::trim)
        .filter(|item| !item.is_empty())
        .filter_map(|item| {
            let (map_id, mode) = match item.split_once(':') {
                Some((map, mode)) => (map.trim(), mode.trim()),
                None => (item, ""),
            };
            let mode = if mode.is_empty() { "SND" } else { mode };
            (!map_id.is_empty()).then(|| MapEntry::new(map_id, mode))
        })
        .collect();
    if maps.is_empty() {
        vec![default_map()]
    } else {
        maps
    }
}

/// Why this slot may not be deleted, or `None` when it may.
///
/// Servers are positional as well as indexed, so only the highest can go: removing one from the
/// middle would re-point every later unit at the wrong server. The LAST one, server 1 included,
/// may go: the bot starts with none configured.
#[must_use]
pub fn delete_problem(
    slot: i32,
    used_rcon_indices: &[i32],
    units: &[String],
    bases: &[String],
) -> Option<String> {
    let count = used_rcon_indices.len();
    // Compared as wide signed numbers, so a zero or negative slot is "not the top", never "fine".
    let (wide_slot, wide_count) = (i64::from(slot), i64::try_from(count).unwrap_or(i64::MAX));
    if wide_slot > wide_count {
        return Some(format!(
            "there is no server {slot} - this box has {count} configured. Nothing was deleted."
        ));
    }
    if wide_slot != wide_count {
        return Some(format!(
            "only server {count} can be deleted. Servers are positional - the unit for server N is the Nth \
             entry of PAVLOV_UNITS - so removing server {slot} would re-point the rest at the wrong servers.\n\
             Delete from the top down: server {count} first."
        ));
    }
    if units.len() != count || bases.len() != count {
        return Some(format!(
            "PAVLOV_UNITS and PAVLOV_BASES must each list exactly {count} entries before a server can be removed \
             (they list {} and {}). Set them explicitly first.",
            units.len(),
            bases.len()
        ));
    }
    None
}
