//! Port of `LogTailerLockingTests.cs`: reading Pavlov.log while the game server holds an
//! exclusive `flock` on it. The C# lost its whole log pipeline to .NET taking an advisory lock on
//! open; Rust's `File::open` takes none, and this pins that.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::process::{Command, Stdio};
use std::time::Duration;

use pavlov_host::logs::LogTailer;

#[test]
fn a_locked_log_is_still_readable() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("Pavlov.log");
    std::fs::write(&path, "[2026.08.01-06.00.00:000][  0]LogNet: hello\n").unwrap();
    let path_text = path.to_str().unwrap();

    // Exactly what Pavlov does to its own log, reproduced with flock(1).
    let Ok(mut holder) = Command::new("flock")
        .args(["-x", path_text, "-c", "sleep 3"])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
    else {
        eprintln!("flock(1) is not installed; the lock cannot be reproduced here");
        return;
    };
    std::thread::sleep(Duration::from_millis(300)); // let it acquire the lock

    let tailer = LogTailer::default();
    tailer.poll(path_text, false); // primes at end-of-file
    std::fs::OpenOptions::new()
        .append(true)
        .open(&path)
        .and_then(|mut f| {
            use std::io::Write;
            f.write_all(b"[2026.08.01-06.00.01:000][  0]LogNet: second line\n")
        })
        .unwrap();

    let lines = tailer.poll(path_text, false);
    let _ = holder.kill();
    let _ = holder.wait();

    assert_eq!(lines.len(), 1);
    assert!(lines[0].text.contains("second line"));
}
