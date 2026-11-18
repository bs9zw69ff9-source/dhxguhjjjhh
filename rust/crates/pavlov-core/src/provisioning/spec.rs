//! The request to stand up one server (C# `Core/Provisioning/ServerProvisionSpec.cs`).

use crate::security::Secret;

/// One entry in a Pavlov map rotation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MapEntry {
    /// A built-in name (`datacenter`) or a workshop id WITH its `UGC` prefix. The prefix is the
    /// game's, not ours - a bare mod.io number will not load - so validation requires it rather
    /// than this adding it.
    pub map_id: String,
    /// The mode token: `SND`, `GUN`, `DM`, and so on.
    pub game_mode: String,
}

impl MapEntry {
    #[must_use]
    pub fn new(map_id: impl Into<String>, game_mode: impl Into<String>) -> Self {
        Self {
            map_id: map_id.into(),
            game_mode: game_mode.into(),
        }
    }
}

/// A request to stand up one new Pavlov dedicated server.
///
/// A plain data carrier: every field becomes a line in a file the game reads or an argument to a
/// privileged command, so nothing is trusted until [`super::validation::check`] has passed.
/// The three credentials are [`Secret`]s, so `Debug` prints them as `***`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerProvisionSpec {
    /// The 1-based server number this becomes for the bot: `server{slot}`.
    pub slot: i32,
    /// The systemd unit name, e.g. `pavlovserver1`.
    pub unit_name: String,
    /// The install root, e.g. `/home/steam/pavlovserver1`.
    pub install_dir: String,
    /// The public name shown in the browser. Cleaned before use.
    pub server_name: String,
    pub game_port: i32,
    pub query_port: i32,
    pub rcon_port: i32,
    /// Constrained to a charset that is safe unquoted in both files.
    pub rcon_password: Secret,
    pub max_players: i32,
    /// The rotation, lowest priority first. Never empty after validation.
    pub maps: Vec<MapEntry>,
    /// Join password for the game itself. `None` or empty means an open server.
    pub game_password: Option<Secret>,
    /// Written to Game.ini as `ServerKey=`. A credential: never shown in Discord or the log.
    pub server_key: Option<Secret>,
}

impl ServerProvisionSpec {
    /// The game password, when one was given and it is not empty.
    #[must_use]
    pub fn game_password(&self) -> Option<&str> {
        self.game_password
            .as_ref()
            .map(Secret::expose)
            .filter(|p| !p.is_empty())
    }

    /// The server key, when one was given and it is not empty.
    #[must_use]
    pub fn server_key(&self) -> Option<&str> {
        self.server_key
            .as_ref()
            .map(Secret::expose)
            .filter(|k| !k.is_empty())
    }
}
