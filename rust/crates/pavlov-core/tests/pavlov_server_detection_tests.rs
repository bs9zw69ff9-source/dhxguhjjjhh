//! Port of `PavlovServerDetectionTests.cs`, the decision half: which installs win, and which
//! unit each one gets. The tests that write real unit files to a temporary directory and read
//! them back exercise the filesystem probe, and arrive with it in `pavlov-host` (M5).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::io;

use pavlov_core::servers::installs::discover;
use pavlov_core::servers::{DetectedServer, HostProbe, InstallSource, units_for};

/// A machine whose systemd runs exactly these servers, and nothing else.
struct Systemd(Vec<DetectedServer>);

impl HostProbe for Systemd {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        self.0.clone()
    }
    fn installs_beside(&self, _: &str, _: &str) -> io::Result<Vec<String>> {
        Ok(Vec::new())
    }
    fn directory_exists(&self, _: &str) -> bool {
        false
    }
}

fn server(unit: &str) -> DetectedServer {
    DetectedServer {
        unit: unit.to_owned(),
        install: format!("/home/steam/{unit}"),
    }
}

#[test]
fn with_nothing_configured_the_installs_come_from_systemd() {
    let host = Systemd(vec![server("pavlovserver"), server("pavlovserver1")]);

    let found = discover(None, None, &host);

    assert_eq!(
        found.roots,
        ["/home/steam/pavlovserver", "/home/steam/pavlovserver1"]
    );
    assert_eq!(found.source, InstallSource::Systemd);
}

#[test]
fn an_explicit_setting_still_wins() {
    let host = Systemd(vec![server("pavlovserver")]);

    assert_eq!(
        discover(Some("/srv/a,/srv/b"), None, &host).roots,
        ["/srv/a", "/srv/b"]
    );
}

#[test]
fn an_install_with_no_unit_falls_back_to_its_folder_name() {
    assert_eq!(
        units_for(&["/home/steam/pavlovserver7".to_owned()], &[]),
        ["pavlovserver7"]
    );
}

/// The C# `AUnitNamedDifferentlyFromItsFolderKeepsItsOwnName`, matching half: a unit that
/// systemd names differently from its folder keeps systemd's name, trailing slash or not.
#[test]
fn a_detected_unit_wins_over_the_folder_name() {
    let detected = [DetectedServer {
        unit: "pavlov-gwpd".into(),
        install: "/home/steam/pavlovserver".into(),
    }];
    assert_eq!(
        units_for(&["/home/steam/pavlovserver/".to_owned()], &detected),
        ["pavlov-gwpd"]
    );
}
