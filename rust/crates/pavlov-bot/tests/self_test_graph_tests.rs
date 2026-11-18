//! Port of `SelfTestGraphTests.cs`: the deploy gate, run for real. The whole graph builds from
//! a minimal `.env` in a fresh directory, `--selftest` exits 0 within a minute, and it says
//! "graph built". A wiring mistake (a registration cycle in the C#, a duplicate service or an
//! unknown dependency here) fails this instead of a deploy.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

#[test]
fn the_whole_graph_builds_within_a_minute() {
    let dir = tempfile::tempdir().unwrap();
    let data = dir.path().join("data");
    std::fs::create_dir_all(&data).unwrap();
    std::fs::write(
        dir.path().join(".env"),
        format!(
            "DISCORD_TOKEN=x\nCLIENT_ID=1\nGUILD_ID=1\nDATA_DIR={}\n",
            data.display()
        ),
    )
    .unwrap();

    let started = Instant::now();
    let mut child = Command::new(env!("CARGO_BIN_EXE_pavlov-bot"))
        .arg("--selftest")
        .current_dir(dir.path())
        .env_remove("DISCORD_TOKEN")
        .env_remove("DATA_DIR")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let deadline = Duration::from_secs(60);
    loop {
        if child.try_wait().unwrap().is_some() {
            break;
        }
        if started.elapsed() > deadline {
            child.kill().unwrap();
            panic!("--selftest did not finish within 60s: the graph never finished building");
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    let output = child.wait_with_output().unwrap();
    let text = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    assert!(output.status.success(), "{text}");
    assert!(text.contains("selftest: graph built"), "{text}");
    assert!(
        data.join("bot.db").exists(),
        "the store was opened in DATA_DIR"
    );
}

#[test]
fn an_unusable_configuration_exits_78_with_every_problem() {
    let dir = tempfile::tempdir().unwrap();
    std::fs::write(dir.path().join(".env"), "RCON_HOST_1=127.0.0.1\n").unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_pavlov-bot"))
        .arg("--selftest")
        .current_dir(dir.path())
        .env_remove("DISCORD_TOKEN")
        .output()
        .unwrap();
    assert_eq!(output.status.code(), Some(78));
    assert!(String::from_utf8_lossy(&output.stderr).contains("DISCORD_TOKEN"));
}
