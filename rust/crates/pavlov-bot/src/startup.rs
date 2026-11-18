//! The startup checks (C# `Program.Main`, before the container is built), in its order.
//!
//! Every FATAL problem exits 78 (`EX_CONFIG`) with its own message: a bot that starts on a
//! configuration it cannot honour fails later and further from the cause. The NOTES are said
//! on stderr and the start continues; the WARNINGS go to the log once it is up.

use std::path::{Path, PathBuf};

use pavlov_config::{BotOptions, Config, FeatureOptions, env_file_too_open};
use pavlov_core::factions::{FactionRegistry, FactionSet};
use pavlov_core::security::OwnerGuard;
use pavlov_core::servers::HostProbe;
use pavlov_core::skin;
use pavlov_host::factions::factions_file;
use pavlov_storage::game_files::inside_install;

/// The exit code for a configuration that cannot be used.
pub const EX_CONFIG: i32 = 78;

/// Why the bot will not start.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Fatal {
    pub lines: Vec<String>,
}

impl Fatal {
    fn one(line: impl Into<String>) -> Self {
        Self {
            lines: vec![line.into()],
        }
    }
}

/// What the checks settled.
#[derive(Debug, Clone)]
pub struct Settled {
    pub options: BotOptions,
    pub features: FeatureOptions,
    pub factions: FactionSet,
    /// Said on stderr now; the start continues.
    pub notes: Vec<String>,
}

/// Run the checks that decide whether the bot starts.
///
/// # Errors
/// The first fatal problem, with every line it owes the operator.
pub fn settle(config: &Config, cwd: &Path, probe: &dyn HostProbe) -> Result<Settled, Fatal> {
    // Ahead of configuration: a tampered build is not fixed by editing .env.
    if let Err(error) = OwnerGuard::verify() {
        return Err(Fatal::one(format!("OWNER GUARD FAILED: {error}")));
    }

    let options = BotOptions::bind(config, cwd);
    let problems = options.validate();
    if !problems.is_empty() {
        let mut lines = vec!["Configuration is not usable:".to_owned()];
        lines.extend(problems.into_iter().map(|p| format!("  - {p}")));
        return Err(Fatal { lines });
    }
    let mut notes = Vec::new();
    if options.servers.is_empty() {
        notes.push(
            "No RCON servers configured (no RCON_HOST_n in .env). Starting anyway so \
             /provisionserver can create one; every RCON feature is idle until a server is added."
                .to_owned(),
        );
    }

    let features = FeatureOptions::bind(config, probe);
    check_theme(&features)?;
    let factions = settle_factions(&features, &mut notes)?;
    check_rosters_writable(&features)?;

    Ok(Settled {
        options,
        features,
        factions,
        notes,
    })
}

/// `THEME`: unset is the police skin; `fallout` was removed and gets its own error; anything
/// else that is not a skin is fatal. `BOT_NAME` then overrides the skin's own brand.
fn check_theme(features: &FeatureOptions) -> Result<(), Fatal> {
    let names = skin::all().map(|s| s.name).join(", ");
    if let Some(theme) = features.theme_name.as_deref().filter(|t| !t.is_empty()) {
        if skin::is_removed(Some(theme)) {
            return Err(Fatal::one(format!(
                "THEME is \"{theme}\", but the fallout theme was removed. Delete the THEME line, \
                 or set it to one of: {names}."
            )));
        }
        if skin::named(Some(theme)).is_none() {
            return Err(Fatal::one(format!(
                "THEME is \"{theme}\", which is not a built-in theme. Use one of: {names}."
            )));
        }
    }
    if let Some(name) = features.bot_name.as_deref().filter(|n| !n.is_empty()) {
        skin::theme::set_brand_name(name);
    }
    Ok(())
}

/// `FACTION_SET` then `FACTIONS_PATH`. A name that is not a set is fatal, never a fallback:
/// quietly running the built-in factions is a bot writing somebody else's roster files. A
/// MISSING file with a valid set named falls back, with a note; a malformed one is fatal.
fn settle_factions(
    features: &FeatureOptions,
    notes: &mut Vec<String>,
) -> Result<FactionSet, Fatal> {
    let presets = FactionRegistry::PRESET_NAMES.join(", ");
    let mut factions = FactionRegistry::default_set().clone();
    let set_name = features
        .faction_set_name
        .as_deref()
        .filter(|s| !s.is_empty());
    if let Some(set_name) = set_name {
        if FactionRegistry::is_removed_preset(Some(set_name)) {
            return Err(Fatal::one(format!(
                "FACTION_SET is \"{set_name}\", but the fallout factions were removed. Delete the \
                 FACTION_SET line to run the built-in GWPD set, or set FACTIONS_PATH to load your \
                 own ladders from a file."
            )));
        }
        let Some(preset) = FactionRegistry::preset(Some(set_name)) else {
            return Err(Fatal::one(format!(
                "FACTION_SET is \"{set_name}\", which is not a built-in set. Use one of: {presets} \
                 - or set FACTIONS_PATH instead to load your own from a file."
            )));
        };
        factions = preset.clone();
    }

    let Some(path) = features.factions_path.as_deref() else {
        return Ok(factions);
    };
    let path = PathBuf::from(path);
    let loaded = factions_file::load(&path);
    match loaded.set {
        Some(set) => {
            if loaded.ignored_rank_caps > 0 {
                notes.push(format!(
                    "{} sets a cap on {} rank(s). Rank caps were removed - ranks and factions now \
                     take as many members as they are given, and these values do nothing. The file \
                     loads either way; delete them when convenient.",
                    path.display(),
                    loaded.ignored_rank_caps
                ));
            }
            Ok(set)
        }
        None if factions_file::may_fall_back_to_set(Some(&path), set_name) => {
            notes.push(format!(
                "FACTIONS_PATH points at {}, which does not exist - using the built-in \"{}\" set \
                 instead. Delete the FACTIONS_PATH line from .env to silence this.",
                path.display(),
                set_name.unwrap_or_default()
            ));
            Ok(factions)
        }
        None => {
            let mut lines = vec![format!(
                "FACTIONS_PATH is set but unusable ({}):",
                path.display()
            )];
            lines.extend(loaded.problems.iter().map(|p| format!("  - {p}")));
            lines.push(format!(
                "Restore the file, or delete the FACTIONS_PATH line and set FACTION_SET to a \
                 built-in set instead: {presets}."
            ));
            Err(Fatal { lines })
        }
    }
}

/// A roster directory inside `IGNORE_PATHS` could never be written: every `/whitelist add`
/// would fail while the startup summary said the feature was on.
fn check_rosters_writable(features: &FeatureOptions) -> Result<(), Fatal> {
    if let Some(directory) = features.roster_directory.as_deref()
        && !features.ignored_paths.is_empty()
        && inside_install(directory, &features.ignored_paths)
    {
        return Err(Fatal {
            lines: vec![
                format!(
                    "FACTION_ROLES_PATH ({directory}) is inside IGNORE_PATHS, so this bot could \
                     never write a roster - every /whitelist add would fail."
                ),
                "  - Ignore the specific files another bot owns (the ban list, the whitelist) \
                 rather than a directory the rosters live under."
                    .to_owned(),
                "  - Or leave FACTION_ROLES_PATH unset if this bot is not meant to manage rosters."
                    .to_owned(),
            ],
        });
    }
    Ok(())
}

/// The warnings logged once the bot is up (C# the tail of `Main`). Each takes what it reads,
/// so they are tested without a process.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Environment {
    /// The `.env` file's mode, when it exists.
    pub env_mode: Option<u32>,
    pub env_path: PathBuf,
    pub running_as_root: bool,
    /// `*.dll` files in the old `plugins/` folder.
    pub stray_plugins: bool,
}

/// The warnings, in the C#'s order.
#[must_use]
pub fn warnings(features: &FeatureOptions, env: &Environment) -> Vec<String> {
    let mut out = Vec::new();
    if !features.ignored_plugin_settings.is_empty() || env.stray_plugins {
        let settings = if features.ignored_plugin_settings.is_empty() {
            "no settings".to_owned()
        } else {
            features.ignored_plugin_settings.join(", ")
        };
        let folder = if env.stray_plugins {
            "; *.dll files in plugins/"
        } else {
            ""
        };
        out.push(format!(
            "Plugins are not supported in the Rust build and nothing is loaded. Ignored: \
             {settings}{folder}. Delete the PLUGIN_* lines to silence this."
        ));
    }
    if features.leaderboard_channel.is_some()
        && features.leaderboard_channel == features.arrest_board_channel
    {
        out.push(format!(
            "LEADERBOARD_CHANNEL and ARREST_LEADERBOARD_CHANNEL are both {}. Each board edits its \
             own message, so both will post there; set them apart if that is not intended.",
            features.leaderboard_channel.unwrap_or_default()
        ));
    }
    if features.master_names.is_empty() {
        out.push(
            "MASTER_NAMES is not set - only the built-in master account is exempt from bans and \
             the evasion checks."
                .to_owned(),
        );
    }
    if env.env_mode.is_some_and(env_file_too_open) {
        out.push(format!(
            "{} holds the Discord token and RCON passwords and is readable by other users on this \
             machine. Run: chmod 600 {}",
            env.env_path.display(),
            env.env_path.display()
        ));
    }
    if env.running_as_root {
        out.push(
            "The bot is running as ROOT. Anything that compromises it - a library bug, a bad \
             command input - then has the whole machine. Kept by owner decision; noted here so \
             it is never a surprise."
                .to_owned(),
        );
    }
    if features.owners.is_empty() && features.super_owners.is_empty() {
        out.push(
            "OWNER_IDS and SUPER_OWNER_IDS are both unset - only the built-in super owner has \
             owner access."
                .to_owned(),
        );
    }
    out
}

/// What this process can see of its environment.
#[must_use]
pub fn environment(cwd: &Path) -> Environment {
    use std::os::unix::fs::MetadataExt;
    let env_path = cwd.join(".env");
    let env_mode = std::fs::metadata(&env_path).ok().map(|m| m.mode());
    let stray_plugins = std::fs::read_dir(cwd.join("plugins")).is_ok_and(|entries| {
        entries
            .flatten()
            .any(|e| e.path().extension().is_some_and(|x| x == "dll"))
    });
    Environment {
        env_mode,
        env_path,
        running_as_root: nix::unistd::geteuid().is_root(),
        stray_plugins,
    }
}
