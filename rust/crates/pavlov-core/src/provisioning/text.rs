//! Every file the provisioner writes, generated as an exact string (C#
//! `Core/Provisioning/ProvisionText.cs`).
//!
//! Pure text, so the format is pinned by golden-string tests rather than discovered on a live
//! box. Lines end in `\n`: these are Linux server files and the bot's own `.env`.

use std::borrow::Cow;

use chrono::NaiveDate;

use super::{ServerProvisionSpec, env_file};
use crate::text::case::eq_ignore_case;

/// The mod line of the wiki's Game.ini block: "take its resource ID, adding 'UGC' to the front".
pub const ADDITIONAL_MODS: &str = "UGC3462586";
/// The section every dedicated-server setting lives under.
pub const GAME_INI_SECTION: &str = "[/Script/Pavlov.DedicatedServer]";
/// The section of `Engine.ini` the player-cap override lives in.
pub const ENGINE_INI_SECTION: &str = "[ConsoleVariables]";
/// The Engine.ini key that lifts the engine's player cap.
pub const ENGINE_INI_KEY: &str = "net.MaxPlayersOverride";
/// The value of [`ENGINE_INI_KEY`].
pub const ENGINE_INI_MAX_PLAYERS: i32 = 32;
/// The longest server name written, so a pasted essay cannot run the line away.
const MAX_SERVER_NAME: usize = 63;

/// `RconSettings.txt`: the two lines the game reads to enable and secure RCON. The password is
/// charset-checked by validation, so it is safe on one line unquoted.
#[must_use]
pub fn rcon_settings(password: &str, rcon_port: i32) -> String {
    format!("Password={password}\nPort={rcon_port}\n")
}

/// The wiki's Game.ini block, with this server's name, capacity, rotation and PIN filled in.
///
/// THE WIKI'S BLOCK, NOT A HOUSE VERSION: nothing is added and nothing else is changed.
/// `bVerboseLogging=false` (the kill feed needs `true`) and `TickRate=90` are the wiki's values
/// anyway. THE NAME IS UNQUOTED: a quoted name was shown with its quotes in the browser.
#[must_use]
pub fn game_ini(spec: &ServerProvisionSpec) -> String {
    let mut out = String::new();
    out.push_str(GAME_INI_SECTION);
    out.push('\n');
    out.push_str("bEnabled=true\n");
    out.push_str(&format!(
        "ServerName={}\n",
        clean_server_name(Some(&spec.server_name))
    ));
    out.push_str(&format!("MaxPlayers={}\n", spec.max_players));
    for line in [
        "bSecured=true",
        "bCustomServer=true",
        "bVerboseLogging=false",
        "bEnableBots=false",
        "bCompetitive=false #This only works for SND",
        "bWhitelist=false",
        "RefreshListTime=120",
        "LimitedAmmoType=0",
        "TickRate=90",
        "TimeLimit=60",
        "AFKTimeLimit=300",
    ] {
        out.push_str(line);
        out.push('\n');
    }

    /* The wiki: "remove or comment out the Password=0000 line to make a public server with no
    pin" - so no PIN is that line, still commented. */
    match spec.game_password() {
        Some(pin) => out.push_str(&format!("Password={pin}\n")),
        None => out.push_str("#Password=0000\n"),
    }
    out.push_str("#BalanceTableURL=\"vankruptgames/BalancingTable/main\"\n");

    for map in &spec.maps {
        out.push_str(&format!(
            "MapRotation=(MapId=\"{}\", GameMode=\"{}\")\n",
            map.map_id, map.game_mode
        ));
    }
    out.push_str(&format!("AdditionalMods={ADDITIONAL_MODS}\n"));

    // The wiki: "add this line: ServerKey=<your server key>". Never written empty.
    if let Some(key) = spec.server_key() {
        out.push_str(&format!("ServerKey={key}\n"));
    }
    out
}

/// Add `key=value` to an existing Game.ini only if no active line for that key is there. An
/// existing `ServerKey=` is left as it is, whatever its value: it is somebody's key. A
/// commented-out line is not the setting, so it does not count.
///
/// Borrowed (unchanged) when nothing needed adding.
#[must_use]
pub fn ensure_game_ini_setting<'a>(existing: &'a str, key: &str, value: &str) -> Cow<'a, str> {
    ensure_ini_setting(existing, GAME_INI_SECTION, key, value)
}

/// `[ConsoleVariables]` / `net.MaxPlayersOverride=32` added to an Engine.ini, touching nothing
/// else and leaving a key already set there as it is.
#[must_use]
pub fn ensure_engine_ini(existing: &str) -> Cow<'_, str> {
    ensure_ini_setting(
        existing,
        ENGINE_INI_SECTION,
        ENGINE_INI_KEY,
        &ENGINE_INI_MAX_PLAYERS.to_string(),
    )
}

/// The active (uncommented) `key` on this line, if it is one: `Some(value_and_note)`.
fn active_setting<'l>(line: &'l str, key: &str) -> Option<&'l str> {
    let trimmed = line.trim_start();
    if trimmed.starts_with('#') || trimmed.starts_with(';') {
        return None;
    }
    let equals = trimmed.find('=').filter(|&i| i > 0)?;
    eq_ignore_case(trimmed[..equals].trim(), key).then(|| &trimmed[equals + 1..])
}

fn ensure_ini_setting<'a>(
    existing: &'a str,
    section: &str,
    key: &str,
    value: &str,
) -> Cow<'a, str> {
    // A blank key names no setting; leaving the file alone is the only safe answer.
    if key.trim().is_empty() {
        return Cow::Borrowed(existing);
    }
    let normalised = existing.replace("\r\n", "\n");
    let mut lines: Vec<&str> = normalised.split('\n').collect();

    if lines.iter().any(|l| active_setting(l, key).is_some()) {
        return Cow::Borrowed(existing);
    }

    let setting = format!("{key}={value}");
    let Some(header) = lines.iter().position(|l| eq_ignore_case(l.trim(), section)) else {
        let mut body = existing.to_owned();
        if !body.is_empty() && !body.ends_with('\n') {
            body.push('\n');
        }
        if !body.is_empty() {
            body.push('\n');
        }
        body.push_str(&format!("{section}\n{setting}\n"));
        return Cow::Owned(body);
    };

    // The end of the section: the next header or the end of the file, before the blank lines
    // that close it.
    let mut end = lines
        .iter()
        .skip(header + 1)
        .position(|l| l.trim_start().starts_with('['))
        .map_or(lines.len(), |i| i + header + 1);
    while end > header + 1 && lines[end - 1].trim().is_empty() {
        end -= 1;
    }
    lines.insert(end, &setting);
    let mut text = lines.join("\n");
    if !text.ends_with('\n') {
        text.push('\n');
    }
    Cow::Owned(text)
}

/// The wiki's systemd unit, as written, for this server's install directory.
///
/// THE WIKI'S FILE AND NOTHING ELSE, down to `WantedBy = multi-user.target` with its spaces. The
/// install directory is filled in, and every server carries its own `-PORT=` on `ExecStart`, the
/// first one included: without it every extra server would bind the first one's ports.
#[must_use]
pub fn systemd_unit(spec: &ServerProvisionSpec) -> String {
    let dir = spec.install_dir.trim_end_matches('/');
    format!(
        "[Unit]\n\
         Description=Pavlov VR dedicated server\n\
         [Service]\n\
         Type=simple\n\
         WorkingDirectory={dir}\n\
         ExecStart={dir}/PavlovServer.sh -PORT={port}\n\
         RestartSec=1\n\
         Restart=always\n\
         User=steam\n\
         Group=steam\n\
         [Install]\n\
         WantedBy = multi-user.target\n",
        port = spec.game_port
    )
}

/// The bot's `.env` with the new server wired in: its RCON triple as INDEXED keys, and the
/// positional lists rewritten IN FULL, because a positional list only has one meaning as a whole.
/// `PLAYER_COUNT_CHANNELS` only when the operator supplied a channel, so an untouched one is
/// neither blanked nor rewritten.
#[must_use]
pub fn env_with_server(
    existing: &str,
    spec: &ServerProvisionSpec,
    rcon_host: &str,
    final_units: &[String],
    final_bases: &[String],
    final_player_count_channels: Option<&[String]>,
    date: NaiveDate,
) -> String {
    let slot = spec.slot;
    let mut values = vec![
        (format!("RCON_HOST_{slot}"), Some(rcon_host.to_owned())),
        (
            format!("RCON_PORT_{slot}"),
            Some(spec.rcon_port.to_string()),
        ),
        (
            format!("RCON_PASSWORD_{slot}"),
            Some(spec.rcon_password.expose().to_owned()),
        ),
        ("PAVLOV_UNITS".to_owned(), Some(final_units.join(","))),
        ("PAVLOV_BASES".to_owned(), Some(final_bases.join(","))),
    ];
    if let Some(channels) = final_player_count_channels.filter(|c| !c.is_empty()) {
        values.push(("PLAYER_COUNT_CHANNELS".to_owned(), Some(channels.join(","))));
    }
    env_file::set(
        existing,
        &values,
        &env_file::note(
            &format!("server {slot} ({}) provisioned", spec.unit_name),
            date,
        ),
    )
}

/// The bot's `.env` with a server taken back OUT. NONE MEANS GONE, not blank: the slot's lines
/// are removed outright, so a deleted server's host is not left sitting in the file.
#[must_use]
pub fn env_without_server(
    existing: &str,
    slot: i32,
    final_units: &[String],
    final_bases: &[String],
    date: NaiveDate,
) -> String {
    let values = [
        (format!("RCON_HOST_{slot}"), None),
        (format!("RCON_PORT_{slot}"), None),
        (format!("RCON_PASSWORD_{slot}"), None),
        ("PAVLOV_UNITS".to_owned(), Some(final_units.join(","))),
        ("PAVLOV_BASES".to_owned(), Some(final_bases.join(","))),
    ];
    env_file::set(
        existing,
        &values,
        &env_file::note(&format!("server {slot} removed"), date),
    )
}

/// Change one setting in an existing Game.ini, leaving the rest of the file alone.
///
/// A LINE EDIT, NOT A REWRITE: regenerating from [`game_ini`] would discard whatever the
/// operator has tuned by hand. A COMMENTED-OUT LINE IS NOT THE SETTING, so it is left where it
/// is and an active line is written. An inline note on the changed line is kept, because in this
/// file those explain the setting itself.
#[must_use]
pub fn set_game_ini_value(existing: &str, key: &str, value: &str) -> String {
    let normalised = existing.replace("\r\n", "\n");
    let mut lines: Vec<String> = normalised.split('\n').map(str::to_owned).collect();
    if key.trim().is_empty() {
        return lines.join("\n");
    }

    for line in &mut lines {
        if let Some(rest) = active_setting(line, key) {
            let note = rest
                .find('#')
                .map_or_else(String::new, |hash| format!(" {}", rest[hash..].trim()));
            *line = format!("{key}={value}{note}");
            return lines.join("\n");
        }
    }

    // Not there at all: under the section header, or at the end if there is none.
    let setting = format!("{key}={value}");
    match lines
        .iter()
        .position(|l| eq_ignore_case(l.trim(), GAME_INI_SECTION))
    {
        Some(header) => lines.insert(header + 1, setting),
        None => lines.push(setting),
    }
    lines.join("\n")
}

/// A server name reduced to something safe on one line of an INI value: no double quote, no
/// line breaks or other control characters, at most 63 characters.
#[must_use]
pub fn clean_server_name(raw: Option<&str>) -> String {
    let text: String = raw
        .unwrap_or_default()
        .chars()
        .filter(|&c| c != '"')
        .map(|c| if c == '\r' || c == '\n' { ' ' } else { c })
        .filter(|c| !c.is_control())
        .collect();
    let text = text.trim();
    if text.chars().count() > MAX_SERVER_NAME {
        text.chars()
            .take(MAX_SERVER_NAME)
            .collect::<String>()
            .trim()
            .to_owned()
    } else {
        text.to_owned()
    }
}
