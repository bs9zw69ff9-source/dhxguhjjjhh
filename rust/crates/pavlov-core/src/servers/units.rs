//! systemd unit names for the game servers (the pure half of C# `Host/Servers/ServiceControl.cs`).
//!
//! These strings become arguments to a PRIVILEGED command, so they are validated even though
//! they come from `.env` rather than from a user. Nothing goes through a shell (the host runs
//! `systemctl` with an argv array), and they are still checked, because the cost of being wrong
//! about that reasoning exactly once is root.

/// The default unit names, in server order.
///
/// Server 1 is `pavlovserver`, server 2 is `pavlovserver1`, server 3 is `pavlovserver2` - the
/// off-by-one is the operator's naming, not a mistake here. It is also the order installs are
/// discovered in and the order the feeds number servers in, so "Server 2" means the same thing
/// in a join line, in a player-count channel and in a restart.
pub const DEFAULT_UNITS: [&str; 3] = ["pavlovserver", "pavlovserver1", "pavlovserver2"];

/// What an older, Docker-running build wrote in front of a unit name.
const LEGACY_DOCKER_PREFIX: &str = "docker:";

/// Parse `PAVLOV_UNITS`. Anything that is not a plausible unit name is dropped; nothing usable
/// at all (or unset) is the defaults, because an empty list would make `/rotatemap` and
/// `/serverswitch` report success for acting on nothing.
///
/// COMMA ONLY. Splitting on spaces as well looks friendlier and is wrong: it turns
/// "bad name here" into three tokens that each pass validation, so a malformed entry would
/// silently become three units instead of being rejected as one.
#[must_use]
pub fn parse_units(configured: Option<&str>) -> Vec<String> {
    let Some(configured) = configured.filter(|c| !c.trim().is_empty()) else {
        return default_units();
    };

    let mut parsed: Vec<String> = Vec::new();
    for entry in configured
        .split(',')
        .map(str::trim)
        .filter(|e| !e.is_empty())
    {
        let unit = without_legacy_docker_prefix(entry);
        if is_plausible_unit_name(unit) && !parsed.iter().any(|p| p == unit) {
            parsed.push(unit.to_owned());
        }
    }

    if parsed.is_empty() {
        default_units()
    } else {
        parsed
    }
}

/// [`DEFAULT_UNITS`] as owned strings.
#[must_use]
pub fn default_units() -> Vec<String> {
    DEFAULT_UNITS.map(str::to_owned).to_vec()
}

/// `docker:pavlovserver2` as `pavlovserver2`, the prefix matched case-insensitively.
///
/// STRIPPED IN PLACE, NOT DROPPED. An entry's position is its server number, so an entry that
/// failed validation and vanished would move every server after it down one. The container
/// names were the old unit names, so after the stripping the entry names the systemd unit that
/// server runs under now. Docker itself is no longer supported.
#[must_use]
pub fn without_legacy_docker_prefix(entry: &str) -> &str {
    match entry.get(..LEGACY_DOCKER_PREFIX.len()) {
        // ASCII comparison is exact here: no non-ASCII character upper-cases to a letter of
        // "DOCKER:" under .NET's simple mapping, so this is OrdinalIgnoreCase for this prefix.
        Some(head) if head.eq_ignore_ascii_case(LEGACY_DOCKER_PREFIX) => {
            &entry[LEGACY_DOCKER_PREFIX.len()..]
        }
        _ => entry,
    }
}

/// Whether this could be a systemd unit name: 1 to 64 of letters, digits and `- _ . @ \`.
///
/// IT MUST START WITH A LETTER OR DIGIT, and that clause is the one doing security work. The
/// rest of the alphabet has to allow `-` for names like `pavlov-ttt`, but a name that BEGINS
/// with one is not a unit, it is an OPTION: `--force` passes every "contains only safe
/// characters" check ever written and would reach `systemctl restart` as a flag rather than as
/// a target. Leading `.` and `@` go the same way: neither names a real unit.
#[must_use]
pub fn is_plausible_unit_name(name: &str) -> bool {
    let bytes = name.as_bytes();
    (1..=64).contains(&bytes.len())
        && bytes[0].is_ascii_alphanumeric()
        && bytes
            .iter()
            .all(|&b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'@' | b'\\'))
}
