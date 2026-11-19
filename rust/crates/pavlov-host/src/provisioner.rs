//! Standing up and tearing down a Pavlov dedicated server, following the Pavlov wiki (C#
//! `Servers/ServerProvisioner.cs`, `IServerProvisioner.cs`).
//!
//! PRIVILEGED AND STEP BY STEP. Every command is an argv through the [`CommandRunner`] (never a
//! shell), commands that belong to the game run as `steam` via `sudo -u steam`, and progress is
//! a checklist of fixed steps the caller renders as it changes. STEPS ADVANCE THEMSELVES:
//! nothing names its own position in the list, so inserting a step cannot leave a later one
//! pointing at the wrong row (the bug that shipped once already). `every_step_the_code_starts_is_on_the_checklist`
//! counts the `run.start(` calls in this file against the lists.

use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use chrono::Utc;
use futures_util::future::BoxFuture;
use pavlov_config::RconOptions;
use pavlov_core::provisioning::ServerProvisionSpec;
use pavlov_core::provisioning::text::{
    ENGINE_INI_KEY, ENGINE_INI_MAX_PLAYERS, ENGINE_INI_SECTION, ensure_engine_ini,
    ensure_game_ini_setting, env_with_server, env_without_server, game_ini, rcon_settings,
    systemd_unit,
};
use pavlov_core::servers::units::is_plausible_unit_name;
use pavlov_rcon::RconClient;
use pavlov_storage::atomic_file;

use crate::preserved;
use crate::process::{CommandRunner, ProcessOutcome};
use crate::supervisor::BotSupervisor;

const PAVLOV_APP_ID: &str = "622970";
const STEAM_USER: &str = "steam";
const STEAMCMD_TARBALL_URL: &str =
    "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
/// SteamCMD pulls several GB on a first install.
const STEAMCMD_TIMEOUT: Duration = Duration::from_secs(45 * 60);
const SYSTEMCTL_TIMEOUT: Duration = Duration::from_secs(90);
const QUICK_TIMEOUT: Duration = Duration::from_secs(15);
const APT_TIMEOUT: Duration = Duration::from_secs(15 * 60);
const DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(2 * 60);

/// A drop-in an earlier build installed that gave steam full root. Only ever REMOVED now.
pub const LEGACY_SUDOERS_DROP_IN: &str = "/etc/sudoers.d/pavlov-steam-full";
/// Where the wiki's libc++ link lives.
pub const LIBCXX_DIRECTORY: &str = "/usr/lib/x86_64-linux-gnu";

/// The download checklist, in order.
pub const STEP_NAMES: [&str; 16] = [
    "Pre-flight checks",
    "Operating system and dependencies",
    "Steam user account",
    "Remove legacy steam sudo grant",
    "SteamCMD (locate or bootstrap)",
    "steamclient.so (SteamCMD app 1007)",
    "SteamCMD install",
    "libc++ and the launch script",
    "Server config (RconSettings.txt, Game.ini)",
    "Restore kept player data",
    "systemd unit (write, daemon-reload, enable --now)",
    "Firewall (ufw)",
    "RCON reachability",
    "Wire into the bot (.env)",
    "Engine.ini (net.MaxPlayersOverride)",
    "Restart the bot",
];

/// The checklist when an existing install is copied instead of downloaded.
pub const COPY_STEP_NAMES: [&str; 15] = [
    "Pre-flight checks",
    "Operating system and dependencies",
    "Steam user account",
    "Remove legacy steam sudo grant",
    "Copy an existing install",
    "steamclient.so (SteamCMD app 1007)",
    "libc++ and the launch script",
    "Server config (RconSettings.txt, Game.ini)",
    "Restore kept player data",
    "systemd unit (write, daemon-reload, enable --now)",
    "Firewall (ufw)",
    "RCON reachability",
    "Wire into the bot (.env)",
    "Engine.ini (net.MaxPlayersOverride)",
    "Restart the bot",
];

/// The checklist a provision drives.
#[must_use]
pub fn steps_for(copying: bool) -> &'static [&'static str] {
    if copying {
        &COPY_STEP_NAMES
    } else {
        &STEP_NAMES
    }
}

/// The delete checklist.
pub const DELETE_STEP_NAMES: [&str; 6] = [
    "Pre-flight checks",
    "Stop and disable the service",
    "Remove the systemd unit",
    "Delete the install directory",
    "Unwire from the bot (.env)",
    "Restart the bot",
];

fn strings(parts: &[&str]) -> Vec<String> {
    parts.iter().map(|p| (*p).to_owned()).collect()
}

/// `cp -r <source>/. <destination>`: the source's CONTENTS, so it cannot nest one level down.
#[must_use]
pub fn copy_install_argv(source: &str, destination: &str) -> Vec<String> {
    vec![
        "-r".into(),
        format!("{}/.", source.trim_end_matches('/')),
        destination.to_owned(),
    ]
}

/// The wiki's SteamCMD line for the Shack beta.
#[must_use]
pub fn steamcmd_argv(install_dir: &str) -> Vec<String> {
    strings(&[
        "+force_install_dir",
        install_dir,
        "+login",
        "anonymous",
        "+app_update",
        PAVLOV_APP_ID,
        "-beta",
        "shack",
        "+exit",
    ])
}

#[must_use]
pub fn enable_argv(unit: &str) -> Vec<String> {
    strings(&["enable", "--now", unit])
}

/// `ufw allow <port>`, as the wiki does (a known risk for RCON, recorded in the docs).
#[must_use]
pub fn ufw_allow_argv(port: i32) -> Vec<String> {
    vec!["allow".into(), port.to_string()]
}

/// A real home and shell for the steam account.
#[must_use]
pub fn useradd_argv(user: &str) -> Vec<String> {
    strings(&["-m", "-s", "/bin/bash", user])
}

/// The home directory field of a `getent passwd` line, when it is an absolute path.
#[must_use]
pub fn home_from_getent(output: Option<&str>) -> Option<String> {
    let line = output?.split('\n').find(|l| !l.is_empty())?;
    let fields: Vec<&str> = line.split(':').collect();
    fields
        .get(5)
        .filter(|h| fields.len() >= 6 && h.starts_with('/'))
        .map(|h| (*h).to_owned())
}

/// Where SteamCMD is looked for under the steam home.
#[must_use]
pub fn steamcmd_candidates(home: &str) -> Vec<String> {
    vec![format!("{}/Steam/steamcmd.sh", home.trim_end_matches('/'))]
}

/// Download Valve's tarball to a FILE, never through a shell pipe.
#[must_use]
pub fn steamcmd_download_argv(tarball: &str) -> Vec<String> {
    strings(&["-sqL", STEAMCMD_TARBALL_URL, "-o", tarball])
}

#[must_use]
pub fn steamcmd_extract_argv(tarball: &str, directory: &str) -> Vec<String> {
    strings(&["-xzf", tarball, "-C", directory])
}

/// `/etc/os-release` as key/value pairs, quotes stripped.
#[must_use]
pub fn parse_os_release(text: Option<&str>) -> Vec<(String, String)> {
    text.unwrap_or_default()
        .split('\n')
        .map(str::trim)
        .filter(|l| !l.is_empty() && !l.starts_with('#'))
        .filter_map(|line| {
            let (key, value) = line.split_once('=')?;
            (!key.is_empty()).then(|| (key.to_owned(), value.trim_matches(['"', '\'']).to_owned()))
        })
        .collect()
}

fn field<'a>(fields: &'a [(String, String)], key: &str) -> &'a str {
    fields
        .iter()
        .rev()
        .find(|(k, _)| k == key)
        .map_or("", |(_, v)| v.as_str())
}

/// Whether `version` ("22.04") is before `major.minor`.
#[must_use]
pub fn version_before(version: &str, major: u32, minor: u32) -> bool {
    let mut parts = version.split('.');
    match (
        parts.next().and_then(|p| p.parse::<u32>().ok()),
        parts.next().and_then(|p| p.parse::<u32>().ok()),
    ) {
        (Some(m), Some(n)) => m < major || (m == major && n < minor),
        _ => false,
    }
}

/// The wiki's package line for this release, or `None` on a system that is not Debian-like
/// (the wiki's Rocky/RHEL commands switch SELinux to permissive for the whole machine).
#[must_use]
pub fn dependency_packages(os_release: Option<&str>) -> Option<Vec<String>> {
    let fields = parse_os_release(os_release);
    let id = field(&fields, "ID").to_lowercase();
    let like = field(&fields, "ID_LIKE").to_lowercase();
    let like: Vec<&str> = like.split(' ').filter(|s| !s.is_empty()).collect();
    if !matches!(id.as_str(), "ubuntu" | "debian")
        && !like.contains(&"debian")
        && !like.contains(&"ubuntu")
    {
        return None;
    }
    let older_ubuntu = id == "ubuntu" && version_before(field(&fields, "VERSION_ID"), 22, 10);
    Some(strings(&[
        "gdb",
        "curl",
        if older_ubuntu {
            "lib32gcc1"
        } else {
            "lib32gcc-s1"
        },
        "libc++-dev",
        "unzip",
    ]))
}

/// apt through `env`, non-interactive.
#[must_use]
pub fn apt_update_argv() -> Vec<String> {
    strings(&["DEBIAN_FRONTEND=noninteractive", "apt-get", "update"])
}

#[must_use]
pub fn apt_install_argv(packages: &[String]) -> Vec<String> {
    let mut argv = strings(&["DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y"]);
    argv.extend(packages.iter().cloned());
    argv
}

#[must_use]
pub fn steam_client_update_argv() -> Vec<String> {
    strings(&["+login", "anonymous", "+app_update", "1007", "+quit"])
}

/// Where SteamCMD leaves `steamclient.so`.
#[must_use]
pub fn steam_client_source(steamcmd: &str) -> String {
    let dir = Path::new(steamcmd)
        .parent()
        .unwrap_or_else(|| Path::new("/"));
    dir.join("steamapps/common/Steamworks SDK Redist/linux64/steamclient.so")
        .to_string_lossy()
        .into_owned()
}

#[must_use]
pub fn steam_client_sdk_directory(home: &str) -> String {
    format!("{}/.steam/sdk64", home.trim_end_matches('/'))
}

#[must_use]
pub fn steam_client_install_path(install_dir: &str) -> String {
    format!(
        "{}/Pavlov/Binaries/Linux/steamclient.so",
        install_dir.trim_end_matches('/')
    )
}

/// The wiki's `rm -f .../libc++.so`.
#[must_use]
pub fn libcxx_remove_argv() -> Vec<String> {
    libcxx_remove_argv_in(Path::new(LIBCXX_DIRECTORY))
}

/// The wiki's `ln -s .../libc++.so.1 .../libc++.so`.
#[must_use]
pub fn libcxx_link_argv() -> Vec<String> {
    libcxx_link_argv_in(Path::new(LIBCXX_DIRECTORY))
}

fn libcxx_remove_argv_in(dir: &Path) -> Vec<String> {
    vec![
        "-f".into(),
        dir.join("libc++.so").to_string_lossy().into_owned(),
    ]
}

fn libcxx_link_argv_in(dir: &Path) -> Vec<String> {
    vec![
        "-s".into(),
        dir.join("libc++.so.1").to_string_lossy().into_owned(),
        dir.join("libc++.so").to_string_lossy().into_owned(),
    ]
}

#[must_use]
pub fn launch_script_argv(install_dir: &str) -> Vec<String> {
    vec![
        "+x".into(),
        format!("{}/PavlovServer.sh", install_dir.trim_end_matches('/')),
    ]
}

#[must_use]
pub fn docker_exists_argv(container: &str) -> Vec<String> {
    strings(&["container", "inspect", "--format", "{{.Id}}", container])
}

#[must_use]
pub fn docker_remove_argv(container: &str) -> Vec<String> {
    strings(&["rm", "-f", container])
}

/// The directories the wiki has every server create.
#[must_use]
pub fn config_directories(install_dir: &str) -> Vec<String> {
    let saved = Path::new(install_dir).join("Pavlov/Saved");
    ["Logs", "Config/LinuxServer", "maps"]
        .iter()
        .map(|d| saved.join(d).to_string_lossy().into_owned())
        .collect()
}

/// The files the wiki has every server create, empty.
#[must_use]
pub fn optional_config_files(install_dir: &str) -> Vec<String> {
    let config = Path::new(install_dir).join("Pavlov/Saved/Config");
    ["mods.txt", "blacklist.txt", "whitelist.txt"]
        .iter()
        .map(|f| config.join(f).to_string_lossy().into_owned())
        .collect()
}

/// Whether an existing install holds nothing but a server's `Pavlov/Saved` data, which is the
/// only kind of folder a provision may build into.
///
/// # Errors
/// When the folder cannot be listed.
pub fn holds_only_saved_data(install_dir: &Path) -> std::io::Result<bool> {
    let names = |dir: &Path| -> std::io::Result<Vec<String>> {
        fs::read_dir(dir)?
            .map(|e| e.map(|e| e.file_name().to_string_lossy().into_owned()))
            .collect()
    };
    let top = names(install_dir)?;
    if top.is_empty() {
        return Ok(true);
    }
    if top != ["Pavlov"] {
        return Ok(false);
    }
    Ok(names(&install_dir.join("Pavlov"))?
        .iter()
        .all(|n| n == "Saved"))
}

/// Why a path must not be `rm -rf`'d as one server's install, or `None`: absolute, no `..`, and
/// at least three components deep.
#[must_use]
pub fn deletable_install_problem(install_dir: Option<&str>) -> Option<String> {
    let dir = install_dir.unwrap_or_default();
    if dir.trim().is_empty() || !dir.starts_with('/') || dir.contains("..") {
        return Some(format!(
            "\"{dir}\" is not an absolute, traversal-free path - refusing to delete it."
        ));
    }
    let depth = dir
        .trim_end_matches('/')
        .split('/')
        .filter(|p| !p.is_empty())
        .count();
    (depth < 3).then(|| {
        format!("\"{dir}\" is too close to the root of the filesystem to be one server's install - refusing to delete it.")
    })
}

/// The END of a command's output, where the reason is.
#[must_use]
pub fn tail(text: Option<&str>, limit: usize) -> String {
    let trimmed = text.unwrap_or_default().trim();
    let count = trimmed.chars().count();
    if count <= limit {
        trimmed.to_owned()
    } else {
        format!(
            "…{}",
            trimmed.chars().skip(count - limit).collect::<String>()
        )
    }
}

fn tail600(text: &str) -> String {
    tail(Some(text), 600)
}

// ---- the checklist ----

/// A step's state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProvisionStatus {
    Pending,
    Running,
    Ok,
    Failed,
    Skipped,
}

/// One row of the checklist.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProvisionStep {
    pub name: String,
    pub status: ProvisionStatus,
    pub detail: String,
}

/// Every step, and whether the bot restart was queued.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProvisionOutcome {
    pub steps: Vec<ProvisionStep>,
    pub restart_queued: bool,
}

impl ProvisionOutcome {
    #[must_use]
    pub fn ok(&self) -> bool {
        self.steps
            .iter()
            .all(|s| matches!(s.status, ProvisionStatus::Ok | ProvisionStatus::Skipped))
    }

    /// Whether `.env` was changed (so the caller knows a restart matters).
    #[must_use]
    pub fn changed_env(&self) -> bool {
        self.steps
            .iter()
            .any(|s| s.status == ProvisionStatus::Ok && s.name.ends_with("(.env)"))
    }
}

/// Called with a snapshot of every step on each change.
pub type Progress = Arc<dyn Fn(Vec<ProvisionStep>) -> BoxFuture<'static, ()> + Send + Sync>;

/// The cursor over a fixed checklist.
pub struct Run {
    steps: Vec<ProvisionStep>,
    current: Option<usize>,
    on_progress: Progress,
}

impl fmt::Debug for Run {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Run")
            .field("steps", &self.steps)
            .field("current", &self.current)
            .finish_non_exhaustive()
    }
}

impl Run {
    #[must_use]
    pub fn new(names: &[&str], on_progress: Progress) -> Self {
        Self {
            steps: names
                .iter()
                .map(|n| ProvisionStep {
                    name: (*n).to_owned(),
                    status: ProvisionStatus::Pending,
                    detail: String::new(),
                })
                .collect(),
            current: None,
            on_progress,
        }
    }

    /// Move to the next step and mark it running.
    pub async fn start(&mut self, detail: &str) {
        let next = self.current.map_or(0, |c| c + 1);
        self.current = Some(next);
        self.set(ProvisionStatus::Running, detail).await;
    }

    pub async fn ok(&mut self, detail: &str) {
        self.set(ProvisionStatus::Ok, detail).await;
    }

    pub async fn fail(&mut self, detail: &str) {
        self.set(ProvisionStatus::Failed, detail).await;
    }

    /// A step that did not do its job and does not stop the run.
    pub async fn soft_fail(&mut self, detail: &str) {
        self.set(ProvisionStatus::Failed, detail).await;
    }

    /// Skip everything after the current step; the failure itself stays as it is.
    pub async fn abort(&mut self) -> ProvisionOutcome {
        let from = self.current.map_or(0, |c| c + 1);
        for step in self.steps.iter_mut().skip(from) {
            step.status = ProvisionStatus::Skipped;
            "skipped - an earlier step failed".clone_into(&mut step.detail);
        }
        self.emit().await;
        self.finish(false)
    }

    #[must_use]
    pub fn finish(&self, restart_queued: bool) -> ProvisionOutcome {
        ProvisionOutcome {
            steps: self.steps.clone(),
            restart_queued,
        }
    }

    async fn set(&mut self, status: ProvisionStatus, detail: &str) {
        // A start past the end is a checklist bug; it is reported, never an index panic.
        let Some(step) = self.current.and_then(|c| self.steps.get_mut(c)) else {
            tracing::error!(
                detail,
                "a provisioning step ran that is not on the checklist"
            );
            return;
        };
        step.status = status;
        detail.clone_into(&mut step.detail);
        self.emit().await;
    }

    async fn emit(&self) {
        (self.on_progress)(self.steps.clone()).await;
    }
}

// ---- requests ----

/// Everything a provision needs.
#[derive(Debug, Clone)]
pub struct ProvisionRequest {
    pub spec: ServerProvisionSpec,
    pub final_pavlov_units: Vec<String>,
    pub final_pavlov_bases: Vec<String>,
    pub final_player_count_channels: Option<Vec<String>>,
    pub env_path: PathBuf,
    pub rcon_host: String,
    /// Copy this install instead of downloading one.
    pub copy_from_install_dir: Option<String>,
    /// Filling in a configured slot: an existing unit file may be overwritten.
    pub rebuilding_existing_slot: bool,
}

impl ProvisionRequest {
    /// A download into a new slot, RCON on loopback.
    #[must_use]
    pub fn new(
        spec: ServerProvisionSpec,
        final_pavlov_units: Vec<String>,
        final_pavlov_bases: Vec<String>,
        final_player_count_channels: Option<Vec<String>>,
        env_path: impl Into<PathBuf>,
    ) -> Self {
        Self {
            spec,
            final_pavlov_units,
            final_pavlov_bases,
            final_player_count_channels,
            env_path: env_path.into(),
            rcon_host: "127.0.0.1".into(),
            copy_from_install_dir: None,
            rebuilding_existing_slot: false,
        }
    }
}

/// Everything a delete needs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeleteRequest {
    pub slot: i32,
    pub unit_name: String,
    pub install_dir: String,
    pub final_pavlov_units: Vec<String>,
    pub final_pavlov_bases: Vec<String>,
    pub env_path: PathBuf,
}

/// The seam the commands drive (C# `IServerProvisioner`), so they are testable without root.
pub trait Provisioner: Send + Sync {
    fn provision<'a>(
        &'a self,
        request: &'a ProvisionRequest,
        on_progress: Progress,
    ) -> BoxFuture<'a, ProvisionOutcome>;

    fn delete<'a>(
        &'a self,
        request: &'a DeleteRequest,
        on_progress: Progress,
    ) -> BoxFuture<'a, ProvisionOutcome>;
}

/// The machine paths the provisioner touches, injected so tests can point them at a sandbox.
#[derive(Debug, Clone)]
pub struct ProvisionerPaths {
    pub unit_directory: PathBuf,
    pub legacy_sudoers_drop_in: PathBuf,
    pub os_release: PathBuf,
    pub libcxx_directory: PathBuf,
}

impl Default for ProvisionerPaths {
    fn default() -> Self {
        Self {
            unit_directory: PathBuf::from("/etc/systemd/system"),
            legacy_sudoers_drop_in: PathBuf::from(LEGACY_SUDOERS_DROP_IN),
            os_release: PathBuf::from("/etc/os-release"),
            libcxx_directory: PathBuf::from(LIBCXX_DIRECTORY),
        }
    }
}

/// The provisioner.
pub struct ServerProvisioner {
    runner: Arc<dyn CommandRunner>,
    paths: ProvisionerPaths,
    privileged: bool,
    supervisor: BotSupervisor,
}

impl fmt::Debug for ServerProvisioner {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ServerProvisioner")
            .field("paths", &self.paths)
            .field("privileged", &self.privileged)
            .finish_non_exhaustive()
    }
}

type Note = (bool, String);

impl ServerProvisioner {
    /// The provisioner for this machine.
    #[must_use]
    pub fn new(runner: Arc<dyn CommandRunner>) -> Self {
        Self::with(
            runner,
            ProvisionerPaths::default(),
            pavlov_storage::ownership::is_privileged(),
            BotSupervisor::current(),
        )
    }

    /// Everything injected.
    #[must_use]
    pub fn with(
        runner: Arc<dyn CommandRunner>,
        paths: ProvisionerPaths,
        privileged: bool,
        supervisor: BotSupervisor,
    ) -> Self {
        Self {
            runner,
            paths,
            privileged,
            supervisor,
        }
    }

    fn unit_path(&self, unit: &str) -> PathBuf {
        self.paths.unit_directory.join(format!("{unit}.service"))
    }

    /// Run a command, as `user` (via `sudo -u`) when given.
    async fn run(
        &self,
        as_user: Option<&str>,
        file: &str,
        argv: Vec<String>,
        timeout: Duration,
    ) -> ProcessOutcome {
        let (exe, args) = match as_user {
            Some(user) => {
                let mut args = vec!["-u".to_owned(), user.to_owned(), file.to_owned()];
                args.extend(argv);
                ("sudo".to_owned(), args)
            }
            None => (file.to_owned(), argv),
        };
        self.runner.run(&exe, &args, timeout, None).await
    }

    fn preflight(
        &self,
        spec: &ServerProvisionSpec,
        unit_path: &Path,
        rebuilding: bool,
    ) -> Option<String> {
        if !self.privileged {
            return Some("the bot is not running as root, so it cannot write a systemd unit, enable a service, create the steam account or run SteamCMD as it. Run the bot as root for this command (its other privileged actions use a narrow sudoers line; this one does not fit that).".into());
        }
        if !is_plausible_unit_name(&spec.unit_name) {
            return Some(format!(
                "\"{}\" is not a usable systemd unit name.",
                spec.unit_name
            ));
        }
        if !rebuilding && unit_path.exists() {
            return Some(format!(
                "{} already exists - refusing to overwrite an existing unit.",
                unit_path.display()
            ));
        }
        let dir = Path::new(&spec.install_dir);
        if dir.is_dir() {
            match holds_only_saved_data(dir) {
                Ok(true) => {}
                Ok(false) => {
                    return Some(format!(
                        "{} already exists and holds more than a server's Pavlov/Saved data - refusing to install over it.",
                        spec.install_dir
                    ));
                }
                Err(error) => {
                    return Some(format!("could not inspect {}: {error}", spec.install_dir));
                }
            }
        }
        None
    }

    /// Provision a server, reporting each step to `on_progress`.
    #[allow(clippy::too_many_lines)]
    pub async fn provision(
        &self,
        request: &ProvisionRequest,
        on_progress: Progress,
    ) -> ProvisionOutcome {
        let copying = request
            .copy_from_install_dir
            .as_deref()
            .is_some_and(|c| !c.is_empty());
        let mut run = Run::new(steps_for(copying), on_progress);
        let spec = &request.spec;
        let unit_path = self.unit_path(&spec.unit_name);

        run.start("checking privileges and free slots…").await;
        if let Some(problem) = self.preflight(spec, &unit_path, request.rebuilding_existing_slot) {
            run.fail(&problem).await;
            return run.abort().await;
        }
        run.ok("root, unit name and slot are usable.").await;

        run.start("checking the operating system and installing the dependencies…")
            .await;
        match self.install_dependencies().await {
            Err(problem) => {
                run.fail(&problem).await;
                return run.abort().await;
            }
            Ok((true, text)) => run.soft_fail(&text).await,
            Ok((false, text)) => run.ok(&text).await,
        }

        run.start("checking for the steam account…").await;
        let created = match self.ensure_steam_user().await {
            Ok(created) => created,
            Err(problem) => {
                run.fail(&problem).await;
                return run.abort().await;
            }
        };
        /* AN OLD SERVER'S SAVED DATA IS NOT steam'S: a containerised server left it owned by the
        container's user, and the game and SteamCMD run as steam and must write there. */
        let mut handed_over = false;
        if Path::new(&spec.install_dir).is_dir() {
            let chown = self
                .run(
                    None,
                    "chown",
                    vec![
                        "-R".into(),
                        format!("{STEAM_USER}:{STEAM_USER}"),
                        spec.install_dir.clone(),
                    ],
                    STEAMCMD_TIMEOUT,
                )
                .await;
            if !chown.ok() {
                run.fail(&format!(
                    "could not hand {} to {STEAM_USER}: {}",
                    spec.install_dir,
                    tail600(&chown.combined())
                ))
                .await;
                return run.abort().await;
            }
            handed_over = true;
        }
        let mut account = if created {
            "created with a locked password - reachable only from root (sudo -u steam / su - steam).".to_owned()
        } else {
            "already existed - left untouched.".to_owned()
        };
        if handed_over {
            account.push_str(&format!(
                " The existing {} (a server's saved data) now belongs to it.",
                spec.install_dir
            ));
        }
        run.ok(&account).await;

        run.start(&format!(
            "checking for {}…",
            self.paths.legacy_sudoers_drop_in.display()
        ))
        .await;
        match self.remove_legacy_sudo_grant() {
            Err(problem) => {
                run.fail(&problem).await;
                return run.abort().await;
            }
            Ok(true) => {
                run.ok(&format!(
                    "removed {} - steam no longer has sudo.",
                    self.paths.legacy_sudoers_drop_in.display()
                ))
                .await;
            }
            Ok(false) => {
                run.ok("none present - steam has no sudo grant from this bot.")
                    .await
            }
        }

        // ---- the game files: copied from a working install, or downloaded with SteamCMD ----
        let mut steamcmd: Option<String> = None;
        if copying {
            let source = request.copy_from_install_dir.clone().unwrap_or_default();
            run.start(&format!("copying {source}…")).await;
            if !Path::new(&source).is_dir() {
                run.fail(&format!(
                    "{source} does not exist - there is nothing to copy from."
                ))
                .await;
                return run.abort().await;
            }
            let mk = self
                .run(
                    Some(STEAM_USER),
                    "mkdir",
                    vec!["-p".into(), spec.install_dir.clone()],
                    QUICK_TIMEOUT,
                )
                .await;
            if !mk.ok() {
                run.fail(&format!(
                    "could not create {}: {}",
                    spec.install_dir,
                    tail600(&mk.combined())
                ))
                .await;
                return run.abort().await;
            }
            // As slow as the disk and as large as the install, so it gets SteamCMD's budget.
            let copy = self
                .run(
                    Some(STEAM_USER),
                    "cp",
                    copy_install_argv(&source, &spec.install_dir),
                    STEAMCMD_TIMEOUT,
                )
                .await;
            if let Some(problem) =
                failure("cp", &copy, "the copy did not finish within 45 minutes.")
            {
                run.fail(&problem).await;
                return run.abort().await;
            }
            run.ok(&format!("copied from {source}.")).await;
        } else {
            run.start("looking for steamcmd…").await;
            let (found, bootstrapped) = match self.ensure_steamcmd().await {
                Ok(found) => found,
                Err(problem) => {
                    run.fail(&problem).await;
                    return run.abort().await;
                }
            };
            run.ok(&if bootstrapped {
                format!("not installed, so it was unpacked to {found}.")
            } else {
                format!("found at {found}.")
            })
            .await;
            steamcmd = Some(found);
        }

        /* THE STEAMCLIENT STEP, RIGHT AFTER STEAMCMD IS THERE: in a download run straight after
        it is located, in a copy run (where it was never looked for) found here. One start for
        both branches, which is what the checklist count test expects of a shared step. */
        run.start("fetching steamclient.so with SteamCMD (app 1007) and copying it into place…")
            .await;
        let steamcmd = match steamcmd {
            Some(found) => found,
            None => match self.ensure_steamcmd().await {
                Ok((found, _)) => found,
                Err(problem) => {
                    run.fail(&problem).await;
                    return run.abort().await;
                }
            },
        };
        match self.steam_client(spec, &steamcmd).await {
            Ok(note) => run.ok(&note).await,
            Err(problem) => {
                run.fail(&problem).await;
                return run.abort().await;
            }
        }

        if !copying {
            run.start("downloading the dedicated server (several GB - this is slow)…")
                .await;
            let _ = self
                .run(
                    Some(STEAM_USER),
                    "mkdir",
                    vec!["-p".into(), spec.install_dir.clone()],
                    QUICK_TIMEOUT,
                )
                .await;
            let steam = self
                .run(
                    Some(STEAM_USER),
                    &steamcmd,
                    steamcmd_argv(&spec.install_dir),
                    STEAMCMD_TIMEOUT,
                )
                .await;
            if !steam.started {
                run.fail(&format!("could not execute {steamcmd}.")).await;
                return run.abort().await;
            }
            // What SteamCMD said, and nothing invented on top of it.
            if let Some(problem) = failure(
                "SteamCMD",
                &steam,
                "SteamCMD did not finish within 45 minutes.",
            ) {
                run.fail(&problem).await;
                return run.abort().await;
            }
            run.ok("installed.").await;
        }

        // ---- the wiki's remaining post-installation steps ----
        run.start("fixing libc++ and making PavlovServer.sh executable…")
            .await;
        match self.launch_prep(spec).await {
            Err(problem) => {
                run.fail(&problem).await;
                return run.abort().await;
            }
            Ok((true, text)) => run.soft_fail(&text).await,
            Ok((false, text)) => run.ok(&text).await,
        }

        run.start("writing RconSettings.txt and Game.ini…").await;
        match self.write_config(spec).await {
            Err(problem) => {
                run.fail(&problem).await;
                return run.abort().await;
            }
            Ok(note) => run.ok(&note).await,
        }

        /* ALWAYS A STEP, even with nothing to restore: a start that only happens sometimes shifts
        every later step by one. */
        run.start("looking for player data kept when this server was deleted…")
            .await;
        let kept = preserved::preserved_dir(&spec.install_dir);
        if kept.is_dir() {
            match preserved::restore(&spec.install_dir) {
                Ok(restored) => {
                    let summary = format!(
                        "restored {}",
                        if restored.moved.is_empty() {
                            "nothing".to_owned()
                        } else {
                            restored.moved.join(", ")
                        }
                    );
                    if restored.skipped.is_empty() {
                        run.ok(&format!("{summary}.")).await;
                    } else {
                        run.soft_fail(&format!(
                            "{summary}; left in {}: {}",
                            kept.display(),
                            restored.skipped.join(", ")
                        ))
                        .await;
                    }
                }
                // Not fatal: the server is fine, and the data is still where it was kept.
                Err(error) => {
                    run.soft_fail(&format!(
                        "could not restore it ({error}); it is still at {}.",
                        kept.display()
                    ))
                    .await;
                }
            }
        } else {
            run.ok("none kept - nothing to restore.").await;
        }

        run.start("installing and enabling the service…").await;
        if let Some(problem) = self.install_unit(spec, &unit_path).await {
            run.fail(&problem).await;
            return run.abort().await;
        }
        run.ok(&format!("{} enabled and started.", spec.unit_name))
            .await;

        run.start("opening the game, query and RCON ports…").await;
        match self.open_ports(spec).await {
            None => {
                run.ok(&format!(
                    "allowed {}, {} and {} (ufw allow <port>, as the wiki does).",
                    spec.game_port, spec.query_port, spec.rcon_port
                ))
                .await;
            }
            Some(problem) => run.soft_fail(&problem).await,
        }

        run.start("asking the new server for ServerInfo…").await;
        if self.rcon_reachable(spec).await {
            run.ok("answered RCON.").await;
        } else {
            run.soft_fail("no RCON answer yet - the server may still be starting; check /health after a minute.").await;
        }

        run.start("appending the server to .env…").await;
        if let Some(problem) = append_env(request).await {
            run.fail(&problem).await;
            return run.abort().await;
        }
        run.ok(&format!("added as server {}.", spec.slot)).await;

        /* Last of the provision's own work, and BEFORE the bot restarts itself: the game reads
        Engine.ini only at start, so the server is restarted for the setting to count. */
        run.start("adding net.MaxPlayersOverride=32 to Engine.ini if it exists…")
            .await;
        match self.write_engine_ini(spec).await {
            Err(problem) => {
                run.fail(&problem).await;
                return run.abort().await;
            }
            Ok((true, text)) => run.soft_fail(&text).await,
            Ok((false, text)) => run.ok(&text).await,
        }

        self.restart(&mut run, &format!("to bring server {} online", spec.slot))
            .await
    }

    /// Delete a server, keeping its player data aside first.
    pub async fn delete(&self, request: &DeleteRequest, on_progress: Progress) -> ProvisionOutcome {
        let mut run = Run::new(&DELETE_STEP_NAMES, on_progress);
        let unit_path = self.unit_path(&request.unit_name);

        run.start("checking privileges and the unit name…").await;
        if !self.privileged {
            run.fail(
                "the bot is not running as root, so it cannot stop a service or remove its unit.",
            )
            .await;
            return run.abort().await;
        }
        if !is_plausible_unit_name(&request.unit_name) {
            run.fail(&format!(
                "\"{}\" is not a usable systemd unit name.",
                request.unit_name
            ))
            .await;
            return run.abort().await;
        }
        run.ok("root, and the unit name is usable.").await;

        // Neither is fatal: already stopped, disabled, or absent is the state we want to end in.
        run.start(&format!("stopping {}…", request.unit_name)).await;
        let stop = self
            .run(
                None,
                "systemctl",
                strings(&["disable", "--now", &request.unit_name]),
                SYSTEMCTL_TIMEOUT,
            )
            .await;
        let stop_note = if stop.ok() {
            "stopped and disabled.".to_owned()
        } else {
            format!(
                "already stopped or absent ({}).",
                tail(Some(&stop.combined()), 200)
            )
        };
        // A server an older, Docker-running build made is removed too.
        let (removed, problem) = self.remove_legacy_container(&request.unit_name).await;
        if let Some(problem) = problem {
            run.fail(&problem).await;
            return run.abort().await;
        }
        run.ok(&if removed {
            format!(
                "{stop_note} Docker container {} stopped and removed too.",
                request.unit_name
            )
        } else {
            stop_note
        })
        .await;

        run.start(&format!("removing {}…", unit_path.display()))
            .await;
        if unit_path.exists()
            && let Err(error) = fs::remove_file(&unit_path)
        {
            run.fail(&format!(
                "could not remove {}: {error}",
                unit_path.display()
            ))
            .await;
            return run.abort().await;
        }
        let reload = self
            .run(
                None,
                "systemctl",
                strings(&["daemon-reload"]),
                SYSTEMCTL_TIMEOUT,
            )
            .await;
        if !reload.ok() {
            run.fail(&format!(
                "systemctl daemon-reload failed: {}",
                tail600(&reload.combined())
            ))
            .await;
            return run.abort().await;
        }
        run.ok("removed, and systemd reloaded.").await;

        run.start(&format!("deleting {}…", request.install_dir))
            .await;
        if let Some(problem) = deletable_install_problem(Some(&request.install_dir)) {
            run.fail(&problem).await;
            return run.abort().await;
        }
        // THE PLAYER DATA GOES FIRST, and the delete does not happen if it cannot.
        let kept_at = preserved::preserved_dir(&request.install_dir);
        match preserved::preserve(&request.install_dir, Utc::now()) {
            Ok(kept) if !kept.moved.is_empty() => tracing::warn!(
                items = %kept.moved.join(", "),
                install = %request.install_dir,
                at = %kept_at.display(),
                "preserved player data"
            ),
            Ok(_) => {}
            Err(error) => {
                run.fail(&format!("could not move the player data (ModSave, mods/whitelist/blacklist) out to {}, so nothing was deleted: {error}", kept_at.display())).await;
                return run.abort().await;
            }
        }
        let remove = self
            .run(
                None,
                "rm",
                strings(&["-rf", &request.install_dir]),
                STEAMCMD_TIMEOUT,
            )
            .await;
        if !remove.ok() {
            run.fail(&format!(
                "could not delete {}: {}",
                request.install_dir,
                tail600(&remove.combined())
            ))
            .await;
            return run.abort().await;
        }
        tracing::warn!(path = %request.install_dir, slot = request.slot, "DELETED a server install");
        run.ok(&if kept_at.is_dir() {
            format!("deleted. Player data (rosters, ledgers, mod saves) kept at `{}` and restored by the next /provisionserver into this slot.", kept_at.display())
        } else {
            "deleted.".to_owned()
        })
        .await;

        run.start("clearing it from .env…").await;
        let existing = fs::read_to_string(&request.env_path).unwrap_or_default();
        let updated = env_without_server(
            &existing,
            request.slot,
            &request.final_pavlov_units,
            &request.final_pavlov_bases,
            Utc::now().date_naive(),
        );
        if let Err(error) = write_file(&request.env_path, &updated).await {
            run.fail(&format!(
                "could not update {}: {error}",
                request.env_path.display()
            ))
            .await;
            return run.abort().await;
        }
        run.ok(&format!("server {} cleared.", request.slot)).await;

        // THE LAST SERVER RESTARTS LIKE ANY OTHER: the bot starts with none now.
        self.restart(&mut run, &format!("to drop server {}", request.slot))
            .await
    }

    async fn remove_legacy_container(&self, name: &str) -> (bool, Option<String>) {
        let exists = self
            .run(None, "docker", docker_exists_argv(name), SYSTEMCTL_TIMEOUT)
            .await;
        if !exists.started || !exists.ok() {
            return (false, None);
        }
        let remove = self
            .run(None, "docker", docker_remove_argv(name), STEAMCMD_TIMEOUT)
            .await;
        if remove.ok() {
            (true, None)
        } else {
            (
                false,
                Some(format!(
                    "Docker container {name} exists but could not be removed: {}",
                    tail600(&remove.combined())
                )),
            )
        }
    }

    async fn ensure_steam_user(&self) -> Result<bool, String> {
        if self
            .run(None, "id", strings(&[STEAM_USER]), QUICK_TIMEOUT)
            .await
            .ok()
        {
            return Ok(false);
        }
        let add = self
            .run(None, "useradd", useradd_argv(STEAM_USER), QUICK_TIMEOUT)
            .await;
        if add.ok() {
            Ok(true)
        } else {
            Err(format!(
                "useradd {STEAM_USER} failed: {}",
                tail600(&add.combined())
            ))
        }
    }

    fn remove_legacy_sudo_grant(&self) -> Result<bool, String> {
        let path = &self.paths.legacy_sudoers_drop_in;
        if !path.exists() {
            return Ok(false);
        }
        fs::remove_file(path).map_err(|error| {
            format!(
                "could not remove {}, which gives steam full root: {error}",
                path.display()
            )
        })?;
        tracing::warn!(path = %path.display(), "removed the legacy steam sudo grant");
        Ok(true)
    }

    async fn steam_home(&self) -> String {
        let getent = self
            .run(
                None,
                "getent",
                strings(&["passwd", STEAM_USER]),
                QUICK_TIMEOUT,
            )
            .await;
        getent
            .ok()
            .then(|| home_from_getent(Some(&getent.stdout)))
            .flatten()
            .unwrap_or_else(|| format!("/home/{STEAM_USER}"))
    }

    /// SteamCMD's path and whether it had to be bootstrapped from Valve's tarball.
    async fn ensure_steamcmd(&self) -> Result<(String, bool), String> {
        let home = self.steam_home().await;
        if let Some(found) = steamcmd_candidates(&home)
            .into_iter()
            .find(|c| Path::new(c).exists())
        {
            return Ok((found, false));
        }
        let steam_dir = format!("{}/Steam", home.trim_end_matches('/'));
        let tarball = format!("{steam_dir}/steamcmd_linux.tar.gz");
        let mk = self
            .run(
                Some(STEAM_USER),
                "mkdir",
                vec!["-p".into(), steam_dir.clone()],
                QUICK_TIMEOUT,
            )
            .await;
        if !mk.ok() {
            return Err(format!(
                "could not create {steam_dir}: {}",
                tail600(&mk.combined())
            ));
        }
        let download = self
            .run(
                Some(STEAM_USER),
                "curl",
                steamcmd_download_argv(&tarball),
                DOWNLOAD_TIMEOUT,
            )
            .await;
        if !download.started {
            return Err("steamcmd is not installed and curl is not available to fetch it - install either one.".into());
        }
        if !download.ok() {
            return Err(format!(
                "could not download SteamCMD: {}",
                tail600(&download.combined())
            ));
        }
        let extract = self
            .run(
                Some(STEAM_USER),
                "tar",
                steamcmd_extract_argv(&tarball, &steam_dir),
                DOWNLOAD_TIMEOUT,
            )
            .await;
        if !extract.started {
            return Err("downloaded SteamCMD but tar is not available to unpack it.".into());
        }
        if !extract.ok() {
            return Err(format!(
                "could not unpack SteamCMD: {}",
                tail600(&extract.combined())
            ));
        }
        let path = format!("{steam_dir}/steamcmd.sh");
        if !Path::new(&path).exists() {
            return Err(format!(
                "unpacked SteamCMD but {path} is not there - the tarball layout was not what was expected."
            ));
        }
        tracing::warn!(%path, "SteamCMD was not installed; unpacked Valve's tarball");
        Ok((path, true))
    }

    async fn write_config(&self, spec: &ServerProvisionSpec) -> Result<String, String> {
        let config = Path::new(&spec.install_dir).join("Pavlov/Saved/Config");
        for directory in config_directories(&spec.install_dir) {
            let mk = self
                .run(
                    Some(STEAM_USER),
                    "mkdir",
                    vec!["-p".into(), directory.clone()],
                    QUICK_TIMEOUT,
                )
                .await;
            if !mk.ok() {
                return Err(format!(
                    "could not create {directory}: {}",
                    tail600(&mk.combined())
                ));
            }
        }
        for file in optional_config_files(&spec.install_dir) {
            let touch = self
                .run(Some(STEAM_USER), "touch", vec![file.clone()], QUICK_TIMEOUT)
                .await;
            if !touch.ok() {
                return Err(format!(
                    "could not create {file}: {}",
                    tail600(&touch.combined())
                ));
            }
        }
        let rcon_path = config.join("RconSettings.txt");
        write_file(
            &rcon_path,
            &rcon_settings(spec.rcon_password.expose(), spec.rcon_port),
        )
        .await
        .map_err(|e| e.to_string())?;
        /* AN EXISTING GAME.INI IS LEFT ALONE: it is the owner's file. The one thing added is
        ServerKey, and only when a key was given and the file has none. */
        let game_ini_path = config.join("LinuxServer/Game.ini");
        let existing = fs::read_to_string(&game_ini_path).unwrap_or_default();
        let note = if existing.trim().is_empty() {
            write_file(&game_ini_path, &game_ini(spec))
                .await
                .map_err(|e| e.to_string())?;
            "RconSettings.txt and Game.ini (the wiki's) written, owned by steam.".to_owned()
        } else {
            let added = match spec.server_key() {
                Some(key) => {
                    let updated = ensure_game_ini_setting(&existing, "ServerKey", key);
                    if updated != existing.as_str() {
                        write_file(&game_ini_path, &updated)
                            .await
                            .map_err(|e| e.to_string())?;
                        true
                    } else {
                        false
                    }
                }
                None => false,
            };
            format!(
                "RconSettings.txt written; the existing Game.ini was kept{}, owned by steam.",
                if added {
                    " and ServerKey added to it"
                } else {
                    ""
                }
            )
        };
        for path in [&rcon_path, &game_ini_path] {
            let chown = self
                .run(
                    None,
                    "chown",
                    vec![STEAM_USER.into(), path.to_string_lossy().into_owned()],
                    QUICK_TIMEOUT,
                )
                .await;
            if !chown.ok() {
                return Err(format!(
                    "could not chown {} to {STEAM_USER}: {}",
                    path.display(),
                    tail600(&chown.combined())
                ));
            }
        }
        Ok(note)
    }

    async fn write_engine_ini(&self, spec: &ServerProvisionSpec) -> Result<Note, String> {
        let path = Path::new(&spec.install_dir).join("Pavlov/Saved/Config/LinuxServer/Engine.ini");
        if !path.exists() {
            return Ok((
                true,
                format!(
                    "{} does not exist, so it was not created and net.MaxPlayersOverride was not added.",
                    path.display()
                ),
            ));
        }
        let existing = fs::read_to_string(&path)
            .map_err(|e| format!("could not write {}: {e}", path.display()))?;
        let updated = ensure_engine_ini(&existing);
        if updated != existing.as_str() {
            write_file(&path, &updated)
                .await
                .map_err(|e| format!("could not write {}: {e}", path.display()))?;
        }
        let chown = self
            .run(
                None,
                "chown",
                vec![STEAM_USER.into(), path.to_string_lossy().into_owned()],
                QUICK_TIMEOUT,
            )
            .await;
        if !chown.ok() {
            return Err(format!(
                "could not chown {} to {STEAM_USER}: {}",
                path.display(),
                tail600(&chown.combined())
            ));
        }
        let restart = self
            .run(
                None,
                "systemctl",
                strings(&["restart", &spec.unit_name]),
                SYSTEMCTL_TIMEOUT,
            )
            .await;
        Ok(if restart.ok() {
            (
                false,
                format!(
                    "{ENGINE_INI_SECTION} {ENGINE_INI_KEY}={ENGINE_INI_MAX_PLAYERS} is in Engine.ini and {} was restarted to read it.",
                    spec.unit_name
                ),
            )
        } else {
            (
                true,
                format!(
                    "{ENGINE_INI_KEY} is in Engine.ini, but restarting {} failed ({}) - restart it yourself for the setting to apply.",
                    spec.unit_name,
                    tail(Some(&restart.combined()), 200)
                ),
            )
        })
    }

    async fn install_dependencies(&self) -> Result<Note, String> {
        let os_release = fs::read_to_string(&self.paths.os_release).unwrap_or_default();
        let fields = parse_os_release(Some(&os_release));
        let name = match field(&fields, "PRETTY_NAME") {
            "" => "an unknown system".to_owned(),
            n => n.to_owned(),
        };
        let Some(packages) = dependency_packages(Some(&os_release)) else {
            return Ok((
                true,
                format!(
                    "{name} is not Debian or Ubuntu, so the wiki's package step was not run: its Rocky/RHEL commands switch SELinux to permissive for the whole machine. If the server will not start, do step 2 of the wiki for this system by hand."
                ),
            ));
        };
        let update = self.run(None, "env", apt_update_argv(), APT_TIMEOUT).await;
        if !update.started {
            return Err("could not run apt-get.".into());
        }
        if !update.ok() {
            return Err(format!(
                "apt-get update failed: {}",
                tail600(&update.combined())
            ));
        }
        let install = self
            .run(None, "env", apt_install_argv(&packages), APT_TIMEOUT)
            .await;
        if !install.ok() {
            return Err(format!(
                "apt-get install {} failed: {}",
                packages.join(" "),
                tail600(&install.combined())
            ));
        }
        Ok((false, format!("{name}: {} installed.", packages.join(", "))))
    }

    async fn steam_client(
        &self,
        spec: &ServerProvisionSpec,
        steamcmd: &str,
    ) -> Result<String, String> {
        let home = self.steam_home().await;
        let update = self
            .run(
                Some(STEAM_USER),
                steamcmd,
                steam_client_update_argv(),
                STEAMCMD_TIMEOUT,
            )
            .await;
        if !update.started {
            return Err(format!("could not execute {steamcmd}."));
        }
        if !update.ok() {
            return Err(format!(
                "SteamCMD could not fetch the Steamworks SDK Redist (app 1007): {}",
                tail600(&update.combined())
            ));
        }
        let source = steam_client_source(steamcmd);
        if !Path::new(&source).exists() {
            return Err(format!("SteamCMD finished but {source} is not there."));
        }
        let sdk = steam_client_sdk_directory(&home);
        let installed = steam_client_install_path(&spec.install_dir);
        let installed_dir = Path::new(&installed)
            .parent()
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_default();
        for directory in [&sdk, &installed_dir] {
            let mk = self
                .run(
                    Some(STEAM_USER),
                    "mkdir",
                    vec!["-p".into(), directory.clone()],
                    QUICK_TIMEOUT,
                )
                .await;
            if !mk.ok() {
                return Err(format!(
                    "could not create {directory}: {}",
                    tail600(&mk.combined())
                ));
            }
        }
        for target in [format!("{sdk}/steamclient.so"), installed.clone()] {
            let copy = self
                .run(
                    Some(STEAM_USER),
                    "cp",
                    vec![source.clone(), target.clone()],
                    QUICK_TIMEOUT,
                )
                .await;
            if !copy.ok() {
                return Err(format!(
                    "could not copy steamclient.so to {target}: {}",
                    tail600(&copy.combined())
                ));
            }
        }
        Ok(format!("steamclient.so copied to {sdk} and {installed}."))
    }

    async fn launch_prep(&self, spec: &ServerProvisionSpec) -> Result<Note, String> {
        let dir = &self.paths.libcxx_directory;
        let mut libcxx = format!(
            "libc++ link skipped: this system keeps its libraries somewhere other than {}.",
            dir.display()
        );
        let mut soft = true;
        if dir.is_dir() {
            if !dir.join("libc++.so.1").exists() {
                return Err(format!(
                    "{}/libc++.so.1 is not there, so libc++.so cannot be linked to it - the libc++-dev package should have provided it.",
                    dir.display()
                ));
            }
            let rm = self
                .run(None, "rm", libcxx_remove_argv_in(dir), QUICK_TIMEOUT)
                .await;
            if !rm.ok() {
                return Err(format!(
                    "could not remove {}/libc++.so: {}",
                    dir.display(),
                    tail600(&rm.combined())
                ));
            }
            let link = self
                .run(None, "ln", libcxx_link_argv_in(dir), QUICK_TIMEOUT)
                .await;
            if !link.ok() {
                return Err(format!(
                    "could not link libc++.so: {}",
                    tail600(&link.combined())
                ));
            }
            libcxx = "libc++.so linked to libc++.so.1".into();
            soft = false;
        }
        let chmod = self
            .run(
                Some(STEAM_USER),
                "chmod",
                launch_script_argv(&spec.install_dir),
                QUICK_TIMEOUT,
            )
            .await;
        if !chmod.ok() {
            return Err(format!(
                "could not make PavlovServer.sh executable: {}",
                tail600(&chmod.combined())
            ));
        }
        Ok((soft, format!("{libcxx}; PavlovServer.sh is executable.")))
    }

    async fn install_unit(&self, spec: &ServerProvisionSpec, unit_path: &Path) -> Option<String> {
        if let Err(error) = write_file(unit_path, &systemd_unit(spec)).await {
            return Some(format!("could not write {}: {error}", unit_path.display()));
        }
        let reload = self
            .run(
                None,
                "systemctl",
                strings(&["daemon-reload"]),
                SYSTEMCTL_TIMEOUT,
            )
            .await;
        if !reload.ok() {
            return Some(format!(
                "systemctl daemon-reload failed: {}",
                tail600(&reload.combined())
            ));
        }
        let enable = self
            .run(
                None,
                "systemctl",
                enable_argv(&spec.unit_name),
                SYSTEMCTL_TIMEOUT,
            )
            .await;
        (!enable.ok()).then(|| {
            format!(
                "systemctl enable --now {} failed: {}",
                spec.unit_name,
                tail600(&enable.combined())
            )
        })
    }

    async fn open_ports(&self, spec: &ServerProvisionSpec) -> Option<String> {
        for port in [spec.game_port, spec.query_port, spec.rcon_port] {
            let run = self
                .run(None, "ufw", ufw_allow_argv(port), QUICK_TIMEOUT)
                .await;
            if !run.started {
                return Some(
                    "ufw is not installed or not on PATH - open the ports yourself.".into(),
                );
            }
            if !run.ok() {
                return Some(format!(
                    "ufw allow {port} did not succeed: {}",
                    tail600(&run.combined())
                ));
            }
        }
        None
    }

    async fn rcon_reachable(&self, spec: &ServerProvisionSpec) -> bool {
        let mut options = RconOptions::new(
            format!("provision-{}", spec.slot),
            "127.0.0.1",
            spec.rcon_port,
            spec.rcon_password.clone(),
        );
        options.max_attempts = 1;
        options.command_timeout = Duration::from_secs(3);
        let client = RconClient::new(options);
        match tokio::time::timeout(Duration::from_secs(8), client.send("ServerInfo")).await {
            Ok(Ok(reply)) => !reply.trim().is_empty(),
            Ok(Err(error)) => {
                tracing::debug!(slot = spec.slot, %error, "the new server did not answer RCON on its first probe");
                false
            }
            Err(_) => false,
        }
    }

    async fn restart(&self, run: &mut Run, why: &str) -> ProvisionOutcome {
        run.start("checking how to bring the bot back…").await;
        let Some((file, args)) = self.supervisor.restart_command() else {
            run.ok(&format!(
                "not running under pm2 or systemd - restart the bot yourself {why}."
            ))
            .await;
            return run.finish(false);
        };
        run.ok(&format!(
            "restarting {} now {why}…",
            self.supervisor.describe()
        ))
        .await;
        let outcome = run.finish(true);
        tracing::warn!(supervisor = %self.supervisor.describe(), why, "restarting the bot");
        // Fire and forget: the restart stops this process.
        let runner = Arc::clone(&self.runner);
        tokio::spawn(async move {
            let _ = runner.run(&file, &args, QUICK_TIMEOUT, None).await;
        });
        outcome
    }
}

/// An atomic write, off the runtime's worker threads.
async fn write_file(path: &Path, text: &str) -> std::io::Result<()> {
    atomic_file::write_text_async(path.to_path_buf(), text.to_owned()).await
}

/// The failure of a long command that must exit 0, if it failed.
fn failure(name: &str, outcome: &ProcessOutcome, timed_out: &str) -> Option<String> {
    if !outcome.started {
        return Some(format!("could not start {name}."));
    }
    if outcome.timed_out {
        return Some(timed_out.to_owned());
    }
    (outcome.exit_code != 0).then(|| {
        format!(
            "{name} exited {}: {}",
            outcome.exit_code,
            tail600(&outcome.combined())
        )
    })
}

async fn append_env(request: &ProvisionRequest) -> Option<String> {
    let existing = fs::read_to_string(&request.env_path).unwrap_or_default();
    let updated = env_with_server(
        &existing,
        &request.spec,
        &request.rcon_host,
        &request.final_pavlov_units,
        &request.final_pavlov_bases,
        request.final_player_count_channels.as_deref(),
        Utc::now().date_naive(),
    );
    match write_file(&request.env_path, &updated).await {
        Ok(()) => {
            tracing::warn!(slot = request.spec.slot, path = %request.env_path.display(), "wrote the server into .env");
            None
        }
        Err(error) => Some(format!(
            "could not update {}: {error}",
            request.env_path.display()
        )),
    }
}

impl Provisioner for ServerProvisioner {
    fn provision<'a>(
        &'a self,
        request: &'a ProvisionRequest,
        on_progress: Progress,
    ) -> BoxFuture<'a, ProvisionOutcome> {
        Box::pin(ServerProvisioner::provision(self, request, on_progress))
    }

    fn delete<'a>(
        &'a self,
        request: &'a DeleteRequest,
        on_progress: Progress,
    ) -> BoxFuture<'a, ProvisionOutcome> {
        Box::pin(ServerProvisioner::delete(self, request, on_progress))
    }
}
