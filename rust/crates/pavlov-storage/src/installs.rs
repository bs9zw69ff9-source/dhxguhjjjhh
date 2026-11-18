//! The real machine behind `pavlov_core::servers::HostProbe`: systemd unit files and install
//! directories on disk (C# `Storage/PavlovInstalls.cs`, `Servers/PavlovServerDetection.cs`).
//!
//! THE UNIT FILES ALREADY SAY IT. Every server is a service whose ExecStart runs
//! `PavlovServer.sh` out of its install, so the unit name and the install are both on disk -
//! typing them into `.env` as well was a second copy that went stale the moment a server was
//! added, deleted or renamed.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use pavlov_core::servers::{DetectedServer, HostProbe};

/// Where administrator-installed units live, and where `/provisionserver` writes them.
pub const UNIT_DIRECTORY: &str = "/etc/systemd/system";

const LAUNCHER: &str = "PavlovServer.sh";

/// The probe over this machine's disk.
#[derive(Debug, Clone)]
pub struct DiskProbe {
    unit_directory: PathBuf,
}

impl Default for DiskProbe {
    fn default() -> Self {
        Self::new(UNIT_DIRECTORY)
    }
}

impl DiskProbe {
    /// A probe reading unit files from `unit_directory`.
    #[must_use]
    pub fn new(unit_directory: impl Into<PathBuf>) -> Self {
        Self {
            unit_directory: unit_directory.into(),
        }
    }
}

fn has_pavlov(install: &Path) -> bool {
    install.join("Pavlov").is_dir()
}

fn value<'a>(line: &'a str, key: &str) -> Option<&'a str> {
    line.strip_prefix(key).map(str::trim)
}

/// The install a unit file runs Pavlov from, or `None` when it does not run Pavlov.
#[must_use]
pub fn parse_unit(contents: &str) -> Option<String> {
    let mut working_directory = None;
    let mut launcher = None;
    for raw in contents.lines() {
        let line = raw.trim();
        if let Some(dir) = value(line, "WorkingDirectory=") {
            working_directory = Some(dir.trim_start_matches('-').to_owned());
        } else if let Some(exec) = value(line, "ExecStart=") {
            // ExecStart may carry systemd prefixes (-, @, +, !, :) before the path.
            let program = exec
                .trim_start_matches(['-', '@', '+', '!', ':'])
                .split(' ')
                .next()
                .unwrap_or_default();
            if program.ends_with(LAUNCHER) {
                launcher = Some(program.to_owned());
            }
        }
    }
    let launcher = launcher?;
    // The script sits in the install root; WorkingDirectory is used when the path is relative.
    let install = if launcher.starts_with('/') {
        Path::new(&launcher)
            .parent()
            .map(|p| p.to_string_lossy().into_owned())
    } else {
        working_directory
    };
    install.filter(|i| !i.trim().is_empty())
}

fn stem(unit: &str) -> &str {
    unit.trim_end_matches(|c: char| c.is_ascii_digit())
}

fn number(unit: &str) -> u64 {
    unit[stem(unit).len()..].parse().unwrap_or(0)
}

/// Every Pavlov server with a unit file in `unit_directory`, in server order: by the name, then
/// by the NUMBER on it, so `pavlovserver10` sorts after `pavlovserver2`. Only a unit whose
/// install holds a `Pavlov` directory counts, so a unit left behind by a deleted server is not.
#[must_use]
pub fn from_systemd(unit_directory: &Path) -> Vec<DetectedServer> {
    let Ok(entries) = fs::read_dir(unit_directory) else {
        return Vec::new();
    };
    let mut found: Vec<DetectedServer> = Vec::new();
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|e| e.to_str()) != Some("service") {
            continue;
        }
        let Some(unit) = path.file_stem().and_then(|s| s.to_str()) else {
            continue;
        };
        let Some(install) = fs::read_to_string(&path).ok().and_then(|c| parse_unit(&c)) else {
            continue;
        };
        let install = install.trim_end_matches('/').to_owned();
        if has_pavlov(Path::new(&install)) && !found.iter().any(|f| f.install == install) {
            found.push(DetectedServer {
                unit: unit.to_owned(),
                install,
            });
        }
    }
    found.sort_by(|a, b| {
        stem(&a.unit)
            .cmp(stem(&b.unit))
            .then_with(|| number(&a.unit).cmp(&number(&b.unit)))
    });
    found
}

impl HostProbe for DiskProbe {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        from_systemd(&self.unit_directory)
    }

    fn installs_beside(&self, parent: &str, prefix: &str) -> io::Result<Vec<String>> {
        let entries = match fs::read_dir(parent) {
            Ok(entries) => entries,
            Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(Vec::new()),
            Err(e) => return Err(e),
        };
        let mut found = Vec::new();
        for entry in entries {
            let path = entry?.path();
            let named = path
                .file_name()
                .and_then(|n| n.to_str())
                .is_some_and(|n| n.starts_with(prefix));
            // A REAL install, not a backup copy or an unpacked archive sitting beside one.
            if named && path.is_dir() && has_pavlov(&path) {
                found.push(path.to_string_lossy().into_owned());
            }
        }
        found.sort();
        Ok(found)
    }

    fn directory_exists(&self, path: &str) -> bool {
        Path::new(path).is_dir()
    }
}
