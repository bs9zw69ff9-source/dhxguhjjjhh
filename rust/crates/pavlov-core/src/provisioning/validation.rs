//! Every problem with a [`ServerProvisionSpec`], collected at once (C#
//! `Core/Provisioning/ProvisionValidation.cs`).
//!
//! An operator fixing a provision request one rejection at a time, with a slow SteamCMD run
//! wasted between each, is how a two-minute command becomes an afternoon, so every field is
//! checked and the whole list returned.
//!
//! THE RCON PASSWORD CHARSET IS THE LOAD-BEARING RULE. The value is written UNQUOTED into both
//! `RconSettings.txt` (line-oriented) and the bot's `.env` (an unquoted value ends at `#`, and
//! dotenv's quote-unescaping is lossy). An alphabet that means nothing to either format lets one
//! password round-trip through both untouched.
//!
//! Every pattern is anchored at the very end of the text. .NET's `$` also matches before a final
//! `\n`, so the C# accepted a password or key with one trailing newline - which splits the very
//! file the rule protects. Rust's `$` without `(?m)` does not, so that gap is closed here.

use std::collections::HashSet;

use super::ServerProvisionSpec;
use crate::patterns::static_regex;

/// The highest RCON slot the bot scans, matching `BotOptions` `MAX_SERVERS`.
pub const MAX_SERVERS: i32 = 9;

/// Shack's hard player cap. The PC build allows 50; this bot provisions Shack.
pub const MAX_SHACK_PLAYERS: i32 = 24;

// No `#`, quotes, backtick, `$` or whitespace, so the value survives both file formats unquoted.
static_regex!(PASSWORD_SHAPE = r"^[A-Za-z0-9_\-.!@%^*+=]{8,64}$");
// One unquoted line of Game.ini: nothing that could comment, end the line or start a setting.
static_regex!(SERVER_KEY_SHAPE = r"^[A-Za-z0-9_\-.!@%^*+=/:]{1,128}$");
// A plain name or a UGC-prefixed workshop id. ASCII digits only.
static_regex!(MAP_ID_SHAPE = r"^(?:UGC[0-9]{1,20}|[A-Za-z0-9_\-]{1,64})$");
static_regex!(GAME_MODE_SHAPE = r"^[A-Za-z0-9_\-]{1,32}$");
// Must START WITH `/` so it can never be read as an option by mkdir/chown.
static_regex!(INSTALL_DIR_SHAPE = r"^/[A-Za-z0-9_./\-]{1,200}$");

/// Whether a string is a server key safe to write on one line of Game.ini.
#[must_use]
pub fn is_valid_server_key(key: Option<&str>) -> bool {
    key.is_some_and(|k| SERVER_KEY_SHAPE.is_match(k))
}

/// Whether a string is a syntactically valid RCON password for this bot.
#[must_use]
pub fn is_valid_password(password: Option<&str>) -> bool {
    password.is_some_and(|p| PASSWORD_SHAPE.is_match(p))
}

/// Check a spec against the ports, units and install dirs already in use. Every problem found;
/// empty when the spec is usable. The unit name must already have been checked for systemd
/// plausibility by the caller.
#[must_use]
pub fn check(
    spec: &ServerProvisionSpec,
    rcon_ports_in_use: &HashSet<i32>,
    units_in_use: &[String],
    install_dirs_in_use: &[String],
) -> Vec<String> {
    let mut problems = Vec::new();

    // ---- ports ----
    port(&mut problems, "game port", spec.game_port);
    port(&mut problems, "query port", spec.query_port);
    port(&mut problems, "RCON port", spec.rcon_port);

    // Two of the three sharing a number fails at bind time with a message naming neither.
    if spec.game_port == spec.query_port
        || spec.game_port == spec.rcon_port
        || spec.query_port == spec.rcon_port
    {
        problems
            .push("The game, query and RCON ports must all be different from each other.".into());
    }

    // Only RCON ports are known to the bot; game/query collisions with other servers cannot be
    // seen from here and are the operator's to keep unique.
    for p in [spec.game_port, spec.query_port, spec.rcon_port] {
        if rcon_ports_in_use.contains(&p) {
            problems.push(format!(
                "Port {p} is already an RCON port of another server on this box."
            ));
        }
    }

    // ---- password ----
    if !is_valid_password(Some(spec.rcon_password.expose())) {
        problems.push(
            "The RCON password must be 8-64 characters from letters, digits and _-.!@%^*+= only \
             (no spaces, quotes or #), so it is safe to write unquoted into both the server config and .env."
                .into(),
        );
    }

    // ---- server key (optional) ----
    if let Some(key) = spec.server_key()
        && !is_valid_server_key(Some(key))
    {
        problems.push(
            "The server key must be 1-128 characters from letters, digits and _-.!@%^*+=/: only \
                 (no spaces, quotes or #), so it is safe to write unquoted into Game.ini."
                .into(),
        );
    }

    // ---- game password (optional) ----
    // Not checked in the C#. It goes on one unquoted line of Game.ini, so a line break in it
    // would start a setting of its own.
    if spec
        .game_password()
        .is_some_and(|p| p.contains(['\n', '\r']))
    {
        problems.push(
            "The game password must fit on one line - a line break would add a setting to Game.ini."
                .into(),
        );
    }

    // ---- capacity: 24 is SHACK'S HARD LIMIT, not a preference ----
    if !(1..=MAX_SHACK_PLAYERS).contains(&spec.max_players) {
        problems.push(format!(
            "Max players must be between 1 and {MAX_SHACK_PLAYERS} (got {}) - {MAX_SHACK_PLAYERS} is Shack's hard cap.",
            spec.max_players
        ));
    }

    // ---- server name ----
    if spec.server_name.trim().is_empty() {
        problems.push(
            "The server name is empty after cleaning - give it a name with letters or digits in it."
                .into(),
        );
    }

    // ---- maps ----
    if spec.maps.is_empty() {
        problems.push("At least one map is required for the rotation.".into());
    }
    for map in &spec.maps {
        if !MAP_ID_SHAPE.is_match(&map.map_id) {
            problems.push(format!(
                "Map id \"{}\" is not usable - use a plain name or a UGC id like UGC1758245796.",
                map.map_id
            ));
        }
        if !GAME_MODE_SHAPE.is_match(&map.game_mode) {
            problems.push(format!(
                "Game mode \"{}\" is not a usable mode token (e.g. SND, GUN, DM).",
                map.game_mode
            ));
        }
    }

    // ---- install directory ----
    // It becomes an argv element to mkdir/chown/steamcmd, so a leading '-' or a ".." is refused
    // before anything privileged runs.
    if !INSTALL_DIR_SHAPE.is_match(&spec.install_dir) || spec.install_dir.contains("..") {
        problems.push(format!(
            "The install directory \"{}\" must be an absolute path (no \"..\") using letters, digits and _-./ only.",
            spec.install_dir
        ));
    }

    // ---- collisions with what is already configured ----
    if units_in_use.contains(&spec.unit_name) {
        problems.push(format!(
            "The systemd unit \"{}\" is already configured for another server.",
            spec.unit_name
        ));
    }
    if install_dirs_in_use.contains(&spec.install_dir) {
        problems.push(format!(
            "The install directory \"{}\" is already configured for another server.",
            spec.install_dir
        ));
    }

    problems
}

/// The same 1..65535 rule `BotOptions::validate` uses.
fn port(problems: &mut Vec<String>, label: &str, value: i32) {
    if !(1..=65535).contains(&value) {
        problems.push(format!(
            "The {label} is not a valid port (got {value}); it must be between 1 and 65535."
        ));
    }
}
