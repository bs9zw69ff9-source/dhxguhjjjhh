//! Port of `LogTailerTests.cs`: the situations a naive "read from where I stopped" tailer gets
//! wrong. All of them lose data SILENTLY: the tailer keeps reporting healthy and sees nothing.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::fs::OpenOptions;
use std::io::Write;
use std::path::{Path, PathBuf};

use pavlov_host::logs::LogTailer;
use pavlov_host::logs::tailer::{self, DEFAULT_MAX_BACKFILL};

struct Fixture {
    dir: tempfile::TempDir,
    path: PathBuf,
}

impl Fixture {
    fn new() -> Self {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("Pavlov.log");
        Self { dir, path }
    }

    fn path(&self) -> &str {
        self.path.to_str().unwrap()
    }

    fn append(&self, text: &str) {
        self.append_bytes(text.as_bytes());
    }

    fn append_bytes(&self, bytes: &[u8]) {
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)
            .unwrap();
        file.write_all(bytes).unwrap();
    }

    fn replace(&self, text: &str) {
        std::fs::write(&self.path, text).unwrap();
    }
}

fn new_tailer() -> LogTailer {
    LogTailer::new(DEFAULT_MAX_BACKFILL)
}

fn texts(tailer: &LogTailer, path: &str) -> Vec<String> {
    tailer
        .poll(path, false)
        .into_iter()
        .map(|l| l.text)
        .collect()
}

#[test]
fn the_first_pass_starts_at_the_end_not_the_beginning() {
    // Starting at zero would re-announce every join in the file.
    let f = Fixture::new();
    f.append("[2026.07.15-12.00.00:000]old line\n");
    let tailer = new_tailer();

    assert!(tailer.poll(f.path(), false).is_empty());

    f.append("[2026.07.15-12.00.01:000]new line\n");
    assert_eq!(tailer.poll(f.path(), false).len(), 1);
}

#[test]
fn a_new_log_that_outgrew_the_old_offset_is_read_from_its_start() {
    // A restart with a SMALL old log: the new one is already longer than the old offset by
    // the next poll, so "it shrank" never fires and the first joins used to be skipped.
    let f = Fixture::new();
    f.append("Log file open, 07/15/26 12:00:00\nold\n");
    let tailer = new_tailer();
    tailer.poll(f.path(), false);

    let joins: String = (1..=20).map(|i| format!("join {i}\n")).collect();
    f.replace(&format!("Log file open, 07/15/26 13:30:00\n{joins}"));

    let lines = texts(&tailer, f.path());
    assert!(lines.contains(&"join 1".to_owned()));
    assert!(lines.contains(&"join 20".to_owned()));
}

#[test]
fn a_growing_log_is_not_mistaken_for_a_rotation() {
    let f = Fixture::new();
    f.append("Log file open, 07/15/26 12:00:00\n");
    let tailer = new_tailer();
    tailer.poll(f.path(), false);

    f.append("a\n");
    assert_eq!(texts(&tailer, f.path()), ["a"]);
    f.append("b\n");
    assert_eq!(texts(&tailer, f.path()), ["b"]);
}

#[test]
fn a_multi_byte_character_split_across_polls_survives() {
    // "é" is two bytes in UTF-8; split between two reads it used to decode as two
    // replacement characters, and a name with one in it stopped matching its flags.
    let f = Fixture::new();
    f.append("start\n");
    let tailer = new_tailer();
    tailer.poll(f.path(), false);

    let bytes = "René joined\n".as_bytes();
    f.append_bytes(&bytes[..4]); // ends mid-"é"
    tailer.poll(f.path(), false);
    f.append_bytes(&bytes[4..]);

    assert_eq!(texts(&tailer, f.path()), ["René joined"]);
}

#[test]
fn only_new_lines_come_back_on_each_poll() {
    let f = Fixture::new();
    f.append("first\n");
    let tailer = new_tailer();
    tailer.poll(f.path(), false);

    f.append("second\nthird\n");
    assert_eq!(texts(&tailer, f.path()), ["second", "third"]);
    assert!(tailer.poll(f.path(), false).is_empty());
}

#[test]
fn a_partial_line_is_held_until_its_newline_arrives() {
    let f = Fixture::new();
    f.append("complete\n");
    let tailer = new_tailer();
    tailer.poll(f.path(), false);

    f.append("half a li");
    assert!(tailer.poll(f.path(), false).is_empty());

    f.append("ne here\n");
    assert_eq!(texts(&tailer, f.path()), ["half a line here"]);
}

#[test]
fn a_rotated_log_is_re_read_from_the_start() {
    // A naive tailer seeks past the end of the SHORTER new file and reads nothing forever.
    let f = Fixture::new();
    f.append(&format!("{}\n", "x".repeat(500)));
    let tailer = new_tailer();
    tailer.poll(f.path(), false);

    f.replace("line after rotation\n");
    assert_eq!(texts(&tailer, f.path()), ["line after rotation"]);
}

#[test]
fn carriage_returns_are_stripped() {
    let f = Fixture::new();
    f.append("a\n");
    let tailer = new_tailer();
    tailer.poll(f.path(), false);

    f.append("windows line\r\n");
    assert_eq!(texts(&tailer, f.path()), ["windows line"]);
}

#[test]
fn a_missing_file_is_not_an_error() {
    let f = Fixture::new();
    let missing = f.dir.path().join("nope.log");
    assert!(
        new_tailer()
            .poll(missing.to_str().unwrap(), false)
            .is_empty()
    );
}

#[test]
fn a_huge_backfill_is_capped() {
    let f = Fixture::new();
    f.append(&format!("{}\nthe tail line\n", "a".repeat(4096)));

    let lines = LogTailer::new(64).poll(f.path(), true);

    assert!(lines.iter().any(|l| l.text == "the tail line"));
    assert!(!lines.iter().any(|l| l.text.len() > 1000));
}

#[test]
fn reading_from_the_start_sees_everything_under_the_cap() {
    let f = Fixture::new();
    f.append("one\ntwo\nthree\n");
    let lines: Vec<String> = new_tailer()
        .poll(f.path(), true)
        .into_iter()
        .map(|l| l.text)
        .collect();
    assert_eq!(lines, ["one", "two", "three"]);
}

#[test]
fn each_line_knows_which_file_it_came_from() {
    // Two servers write two logs, and correlation is per file.
    let f = Fixture::new();
    f.append("x\n");
    assert_eq!(new_tailer().poll(f.path(), true)[0].file, f.path());
}

#[test]
fn a_configured_path_that_does_not_exist_yields_nothing() {
    assert!(tailer::discover(Some("/no/such/file.log"), &[]).is_empty());
}

#[test]
fn configured_paths_are_comma_separated_and_filtered() {
    let f = Fixture::new();
    f.append("x\n");
    let found = tailer::discover(Some(&format!("{}, /no/such/file.log", f.path())), &[]);
    assert_eq!(found, [f.path()]);
}

fn log_under(root: &Path) -> PathBuf {
    root.join("Pavlov")
        .join("Saved")
        .join("Logs")
        .join("Pavlov.log")
}

#[test]
fn auto_discovery_finds_a_log_under_every_install() {
    // THE MULTI-SERVER BUG: the old fixed list skipped pavlovserver1.
    let f = Fixture::new();
    let mut roots = Vec::new();
    for name in ["pavlovserver", "pavlovserver1", "pavlovserver2"] {
        let root = f.dir.path().join(name);
        let log = log_under(&root);
        std::fs::create_dir_all(log.parent().unwrap()).unwrap();
        std::fs::write(&log, "x\n").unwrap();
        roots.push(root.to_str().unwrap().to_owned());
    }

    let found = tailer::discover(None, &roots);

    assert_eq!(found.len(), 3);
    for root in &roots {
        let expected = log_under(Path::new(root));
        assert!(found.contains(&expected.to_str().unwrap().to_owned()));
    }
}

#[test]
fn an_explicit_configured_path_still_wins_over_install_discovery() {
    let f = Fixture::new();
    f.append("x\n");
    let roots = [f
        .dir
        .path()
        .join("pavlovserver1")
        .to_str()
        .unwrap()
        .to_owned()];

    assert_eq!(tailer::discover(Some(f.path()), &roots), [f.path()]);
}

#[test]
fn reset_forgets_the_position() {
    // Rust-only: a reset re-primes, so the next poll starts at the end again.
    let f = Fixture::new();
    f.append("a\n");
    let tailer = new_tailer();
    tailer.poll(f.path(), false);
    assert_eq!(tailer.offset_of(f.path()), 2);

    tailer.reset(Some(f.path()));
    assert_eq!(tailer.offset_of(f.path()), 0);
    f.append("b\n");
    assert!(tailer.poll(f.path(), false).is_empty());
}
