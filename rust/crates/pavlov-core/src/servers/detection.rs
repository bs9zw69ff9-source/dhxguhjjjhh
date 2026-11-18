//! Matching installs to the systemd units that run them (the pure half of C#
//! `Host/Servers/PavlovServerDetection.cs`). Reading the unit files is `pavlov-host`'s job.

use std::path::Path;

/// A Pavlov server systemd runs: its unit name and the install it runs from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DetectedServer {
    pub unit: String,
    pub install: String,
}

/// The unit for each install, in the same order: the unit systemd runs it with, or the
/// install's folder name - the `/provisionserver` convention - when no unit was found for it.
#[must_use]
pub fn units_for(installs: &[String], detected: &[DetectedServer]) -> Vec<String> {
    installs
        .iter()
        .map(|install| {
            let install = install.trim_end_matches('/');
            detected
                .iter()
                .find(|d| d.install == install)
                .map(|d| d.unit.clone())
                .unwrap_or_else(|| {
                    Path::new(install)
                        .file_name()
                        .map(|n| n.to_string_lossy().into_owned())
                        .unwrap_or_default()
                })
        })
        .collect()
}
