//! Port of `PavlovServerDetectionTests.cs`, the half that reads unit files from disk (the pure
//! half, `units_for`, is in the core crate's file of this name).

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs;
use std::path::Path;

use pavlov_core::servers::{installs::discover, units_for};
use pavlov_storage::installs::{DiskProbe, from_systemd};

struct Box_ {
    root: tempfile::TempDir,
}

impl Box_ {
    fn new() -> Self {
        let root = tempfile::tempdir().unwrap();
        fs::create_dir_all(root.path().join("systemd")).unwrap();
        Self { root }
    }

    fn units(&self) -> std::path::PathBuf {
        self.root.path().join("systemd")
    }

    fn server(&self, unit: &str, folder: Option<&str>, installed: bool) -> String {
        let install = self.root.path().join("steam").join(folder.unwrap_or(unit));
        if installed {
            fs::create_dir_all(install.join("Pavlov")).unwrap();
        }
        let install = install.to_string_lossy().into_owned();
        fs::write(
            self.units().join(format!("{unit}.service")),
            format!(
                "[Unit]\nDescription=Pavlov VR Server\n\n[Service]\nUser=steam\nWorkingDirectory={install}\n\
                 ExecStart={install}/PavlovServer.sh -PORT=7777\nRestart=on-failure\n"
            ),
        )
        .unwrap();
        install
    }
}

fn names(dir: &Path) -> Vec<String> {
    from_systemd(dir).into_iter().map(|d| d.unit).collect()
}

#[test]
fn every_pavlov_unit_is_found_in_server_order() {
    let b = Box_::new();
    let three = b.server("pavlovserver2", None, true);
    let one = b.server("pavlovserver", None, true);
    let two = b.server("pavlovserver1", None, true);

    let found = from_systemd(&b.units());

    let units: Vec<&str> = found.iter().map(|f| f.unit.as_str()).collect();
    let installs: Vec<&str> = found.iter().map(|f| f.install.as_str()).collect();
    assert_eq!(units, ["pavlovserver", "pavlovserver1", "pavlovserver2"]);
    assert_eq!(installs, [one, two, three]);
}

#[test]
fn ten_sorts_after_two() {
    let b = Box_::new();
    b.server("pavlovserver10", None, true);
    b.server("pavlovserver2", None, true);

    assert_eq!(names(&b.units()), ["pavlovserver2", "pavlovserver10"]);
}

#[test]
fn other_services_and_deleted_installs_are_ignored() {
    let b = Box_::new();
    b.server("pavlovserver", None, true);
    b.server("pavlovserver1", None, false); // unit left behind after the install was deleted
    fs::write(
        b.units().join("nginx.service"),
        "[Service]\nExecStart=/usr/sbin/nginx\n",
    )
    .unwrap();

    assert_eq!(names(&b.units()), ["pavlovserver"]);
}

#[test]
fn a_unit_named_differently_from_its_folder_keeps_its_own_name() {
    let b = Box_::new();
    let install = b.server("pavlov-fallout", Some("pavlovserver"), true);

    let found = from_systemd(&b.units());

    assert_eq!(found.len(), 1);
    assert_eq!(found[0].unit, "pavlov-fallout");
    assert_eq!(units_for(&[install], &found), ["pavlov-fallout"]);
}

#[test]
fn with_nothing_configured_the_installs_come_from_systemd() {
    let b = Box_::new();
    let one = b.server("pavlovserver", None, true);
    let two = b.server("pavlovserver1", None, true);

    let found = discover(None, None, &DiskProbe::new(b.units()));

    assert_eq!(found.roots, [one, two]);
}

#[test]
fn an_explicit_setting_still_wins() {
    let b = Box_::new();
    b.server("pavlovserver", None, true);

    let found = discover(Some("/srv/a,/srv/b"), None, &DiskProbe::new(b.units()));

    assert_eq!(found.roots, ["/srv/a", "/srv/b"]);
}

#[test]
fn an_exec_start_with_systemd_prefixes_and_a_relative_launcher_is_read() {
    use pavlov_storage::installs::parse_unit;
    assert_eq!(
        parse_unit("[Service]\nExecStart=-/srv/p1/PavlovServer.sh -PORT=7778\n").as_deref(),
        Some("/srv/p1")
    );
    assert_eq!(
        parse_unit("WorkingDirectory=-/srv/p2\nExecStart=./PavlovServer.sh\n").as_deref(),
        Some("/srv/p2")
    );
    assert_eq!(parse_unit("ExecStart=/usr/sbin/nginx\n"), None);
}
