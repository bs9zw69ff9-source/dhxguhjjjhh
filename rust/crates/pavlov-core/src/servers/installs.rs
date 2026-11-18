//! Where the game installs are, and the files inside one (C# `Host/Storage/PavlovInstalls.cs`).
//!
//! The DECISION of which installs exist lives here, pure. What it needs from the machine -
//! which systemd units run Pavlov, which folders sit beside the first install - comes through
//! [`HostProbe`], implemented against the real filesystem by `pavlov-host`.

use std::io;
use std::path::Path;

use super::DetectedServer;

/// The install a box set up by the wiki's guide has, and the fallback when nothing says
/// otherwise.
pub const DEFAULT_BASE: &str = "/home/steam/pavlovserver";

/// What discovery needs from the machine. The only I/O seam in this module.
pub trait HostProbe {
    /// Every Pavlov server with a systemd unit, in server order (C#
    /// `PavlovServerDetection.FromSystemd`). Empty when systemd has none.
    fn systemd_servers(&self) -> Vec<DetectedServer>;

    /// The folders in `parent` whose name starts with `prefix` and that hold a `Pavlov`
    /// directory - a real install, not a backup copy beside one - as full paths, sorted
    /// ordinally. Empty when `parent` does not exist.
    ///
    /// # Errors
    /// When `parent` exists but cannot be listed.
    fn installs_beside(&self, parent: &str, prefix: &str) -> io::Result<Vec<String>>;

    /// Whether a directory exists at `path`.
    fn directory_exists(&self, path: &str) -> bool;
}

/// Where a discovered list of installs came from, so the caller can say so in its log.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallSource {
    /// `PAVLOV_BASES` named them.
    Listed,
    /// systemd units run them.
    Systemd,
    /// Folders beside the first install.
    Siblings,
    /// Nothing was found, so the one configured (or default) install is assumed.
    Fallback,
}

/// The installs, and how they were found.
#[derive(Debug)]
pub struct InstallDiscovery {
    /// Never empty.
    pub roots: Vec<String>,
    pub source: InstallSource,
    /// Set when the sibling scan failed and the fallback was used instead; the caller logs it.
    pub scan_error: Option<io::Error>,
}

/// Resolve the install roots.
///
/// - `explicit_bases` is `PAVLOV_BASES`, comma or colon separated, and wins outright.
/// - With `first_base` (`PAVLOV_BASE_1`) unset, systemd is asked next: every service that runs
///   `PavlovServer.sh` names its install, so a box set up by `/provisionserver` needs neither
///   setting.
/// - Then the folders beside the first base (or [`DEFAULT_BASE`]) that hold an install.
/// - NEVER EMPTY: with nothing found it is the one configured base. An empty list would make
///   every cross-install write silently succeed at doing nothing.
pub fn discover(
    explicit_bases: Option<&str>,
    first_base: Option<&str>,
    probe: &dyn HostProbe,
) -> InstallDiscovery {
    let mut listed: Vec<String> = Vec::new();
    for base in explicit_bases
        .unwrap_or_default()
        .split([',', ':'])
        .map(str::trim)
        .filter(|b| !b.is_empty())
    {
        if !listed.iter().any(|l| l == base) {
            listed.push(base.to_owned());
        }
    }
    if !listed.is_empty() {
        return found(listed, InstallSource::Listed);
    }

    let first_base = first_base.filter(|b| !b.trim().is_empty());
    if first_base.is_none() {
        let detected = probe.systemd_servers();
        if !detected.is_empty() {
            return found(
                detected.into_iter().map(|d| d.install).collect(),
                InstallSource::Systemd,
            );
        }
    }

    let root = first_base.map_or(DEFAULT_BASE, |b| b.trim_end_matches('/'));
    let path = Path::new(root);
    let parent = path.parent().and_then(Path::to_str).unwrap_or_default();
    let prefix = path
        .file_name()
        .and_then(|n| n.to_str())
        .unwrap_or_default();

    let mut scan_error = None;
    if !parent.is_empty() && !prefix.is_empty() {
        match probe.installs_beside(parent, prefix) {
            Ok(siblings) if !siblings.is_empty() => {
                return found(siblings, InstallSource::Siblings);
            }
            Ok(_) => {}
            Err(error) => scan_error = Some(error),
        }
    }

    InstallDiscovery {
        roots: vec![root.to_owned()],
        source: InstallSource::Fallback,
        scan_error,
    }
}

fn found(roots: Vec<String>, source: InstallSource) -> InstallDiscovery {
    InstallDiscovery {
        roots,
        source,
        scan_error: None,
    }
}

fn under(install_root: &str, parts: &[&str]) -> String {
    let mut path = std::path::PathBuf::from(install_root);
    path.extend(parts);
    path.to_string_lossy().into_owned()
}

/// The whitelist file inside an install.
#[must_use]
pub fn whitelist_path(install_root: &str) -> String {
    under(
        install_root,
        &["Pavlov", "Saved", "Config", "whitelist.txt"],
    )
}

/// The `Pavlov.log` the server writes, inside an install. Every install writes its own.
#[must_use]
pub fn log_path(install_root: &str) -> String {
    under(install_root, &["Pavlov", "Saved", "Logs", "Pavlov.log"])
}

/// The blacklist (ban) file the server reads: `Config/blacklist.txt` beside the whitelist -
/// NOT the mod's `ModSave/banlist.txt`, which the server does not enforce.
#[must_use]
pub fn blacklist_path(install_root: &str) -> String {
    under(
        install_root,
        &["Pavlov", "Saved", "Config", "blacklist.txt"],
    )
}

/// The `ModSave` tree inside an install: ledgers, faction roles, gamemode saves, everything
/// the cross-install sync keeps identical.
#[must_use]
pub fn mod_save_path(install_root: &str) -> String {
    under(install_root, &["Pavlov", "Saved", "Config", "ModSave"])
}

/// The server's own `Game.ini`, under `LinuxServer` - the platform directory the game reads
/// on this box, so the platform is part of the path rather than something to guess at.
#[must_use]
pub fn game_ini_path(install_root: &str) -> String {
    under(
        install_root,
        &["Pavlov", "Saved", "Config", "LinuxServer", "Game.ini"],
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A machine with fixed answers and no logic of its own.
    #[derive(Default)]
    struct Machine {
        systemd: Vec<DetectedServer>,
        siblings: Vec<String>,
        unlistable: bool,
    }

    impl HostProbe for Machine {
        fn systemd_servers(&self) -> Vec<DetectedServer> {
            self.systemd.clone()
        }
        fn installs_beside(&self, _: &str, _: &str) -> io::Result<Vec<String>> {
            if self.unlistable {
                Err(io::Error::from(io::ErrorKind::PermissionDenied))
            } else {
                Ok(self.siblings.clone())
            }
        }
        fn directory_exists(&self, _: &str) -> bool {
            false
        }
    }

    fn detected(unit: &str, install: &str) -> DetectedServer {
        DetectedServer {
            unit: unit.into(),
            install: install.into(),
        }
    }

    #[test]
    fn an_explicit_list_wins_and_is_split_on_commas_and_colons() {
        let host = Machine {
            systemd: vec![detected("pavlovserver", "/home/steam/pavlovserver")],
            ..Machine::default()
        };
        let found = discover(Some(" /srv/a, /srv/b:/srv/a "), None, &host);
        assert_eq!(found.roots, ["/srv/a", "/srv/b"]);
        assert_eq!(found.source, InstallSource::Listed);
    }

    #[test]
    fn systemd_is_asked_only_when_no_first_base_is_given() {
        let host = Machine {
            systemd: vec![detected("pavlovserver", "/opt/p")],
            siblings: vec!["/data/pavlov1".into()],
            ..Machine::default()
        };
        assert_eq!(discover(None, None, &host).roots, ["/opt/p"]);
        assert_eq!(
            discover(None, Some(" "), &host).source,
            InstallSource::Systemd
        );

        let configured = discover(None, Some("/data/pavlov"), &host);
        assert_eq!(configured.roots, ["/data/pavlov1"]);
        assert_eq!(configured.source, InstallSource::Siblings);
    }

    #[test]
    fn nothing_found_is_the_configured_base_never_an_empty_list() {
        let empty = Machine::default();
        let default = discover(None, None, &empty);
        assert_eq!(default.roots, [DEFAULT_BASE]);
        assert_eq!(default.source, InstallSource::Fallback);

        assert_eq!(
            discover(None, Some("/srv/pavlov/"), &empty).roots,
            ["/srv/pavlov"]
        );
    }

    #[test]
    fn a_failed_scan_falls_back_and_says_why() {
        let host = Machine {
            unlistable: true,
            ..Machine::default()
        };
        let found = discover(None, Some("/srv/pavlov"), &host);
        assert_eq!(found.roots, ["/srv/pavlov"]);
        assert!(found.scan_error.is_some());
    }

    #[test]
    fn the_files_inside_an_install() {
        let root = "/home/steam/pavlovserver";
        assert_eq!(
            blacklist_path(root),
            "/home/steam/pavlovserver/Pavlov/Saved/Config/blacklist.txt"
        );
        assert_eq!(
            whitelist_path(root),
            "/home/steam/pavlovserver/Pavlov/Saved/Config/whitelist.txt"
        );
        assert_eq!(
            log_path(root),
            "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log"
        );
        assert_eq!(
            mod_save_path(root),
            "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave"
        );
        assert_eq!(
            game_ini_path(root),
            "/home/steam/pavlovserver/Pavlov/Saved/Config/LinuxServer/Game.ini"
        );
    }
}
