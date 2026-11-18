//! Running an external command (C# `Servers/ProcessRunner.cs`).
//!
//! Three rules, each a production bug once:
//!
//! - A DEADLINE KILLS THE WHOLE PROCESS TREE. The child runs in its own process group, and the
//!   group is killed on a timeout AND when the caller goes away (its future dropped): `sudo` and
//!   `sh -c` leave the real work in a grandchild, and killing only the child left it running.
//! - BOTH PIPES ARE DRAINED CONCURRENTLY. Reading stdout to the end before stderr deadlocks as
//!   soon as the child fills its stderr pipe (about 64 KB).
//! - OUTPUT WRITTEN BEFORE THE DEADLINE SURVIVES IT. Killing the child closes its pipes, which
//!   ends the reads naturally; on a timeout that output is the most useful thing there is.
//!
//! No shell is involved, so there is nothing to inject into.

use std::fmt;
use std::process::Stdio;
use std::time::Duration;

use futures_util::future::BoxFuture;
use nix::sys::signal::{Signal, killpg};
use nix::unistd::Pid;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::process::Command;

/// How a command went.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessOutcome {
    pub started: bool,
    pub timed_out: bool,
    /// -1 when it did not start, timed out, or was killed by a signal.
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}

impl ProcessOutcome {
    /// It ran, finished in time, and exited 0.
    #[must_use]
    pub fn ok(&self) -> bool {
        self.started && !self.timed_out && self.exit_code == 0
    }

    /// stdout then stderr, trimmed: what callers report.
    #[must_use]
    pub fn combined(&self) -> String {
        format!("{}{}", self.stdout, self.stderr).trim().to_owned()
    }

    /// The command could not be started at all.
    #[must_use]
    pub fn not_started(reason: impl Into<String>) -> Self {
        Self {
            started: false,
            timed_out: false,
            exit_code: -1,
            stdout: String::new(),
            stderr: reason.into(),
        }
    }
}

/// The seam every external command goes through, so tests can record the exact argv.
pub trait CommandRunner: Send + Sync + fmt::Debug {
    /// Run `file` with `argv`, killed after `timeout`; `stdin` is written and closed first.
    fn run<'a>(
        &'a self,
        file: &'a str,
        argv: &'a [String],
        timeout: Duration,
        stdin: Option<&'a str>,
    ) -> BoxFuture<'a, ProcessOutcome>;
}

/// The real runner.
#[derive(Debug, Clone, Copy, Default)]
pub struct SystemRunner;

/// Kills the child's process group when dropped, unless disarmed: covers the timeout and the
/// caller's future being dropped alike.
struct GroupKill {
    pgid: Option<Pid>,
}

impl GroupKill {
    fn kill(&mut self) {
        if let Some(pgid) = self.pgid.take() {
            // ESRCH (already gone) is the common, harmless case.
            let _ = killpg(pgid, Signal::SIGKILL);
        }
    }
}

impl Drop for GroupKill {
    fn drop(&mut self) {
        self.kill();
    }
}

async fn read_all(pipe: Option<impl tokio::io::AsyncRead + Unpin>) -> String {
    let mut bytes = Vec::new();
    if let Some(mut pipe) = pipe {
        // Whatever arrived is worth having even if the read ends in an error.
        let _ = pipe.read_to_end(&mut bytes).await;
    }
    String::from_utf8_lossy(&bytes).into_owned()
}

/// Run a command for real. See the module docs for the rules.
pub async fn run(
    file: &str,
    argv: &[String],
    timeout: Duration,
    stdin: Option<&str>,
) -> ProcessOutcome {
    let mut command = Command::new(file);
    command
        .args(argv)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .stdin(if stdin.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        // Its own process group, so the whole tree can be killed at once.
        .process_group(0)
        .kill_on_drop(true);
    let mut child = match command.spawn() {
        Ok(child) => child,
        Err(error) => {
            tracing::debug!(file, %error, "could not start a command");
            return ProcessOutcome::not_started(error.to_string());
        }
    };
    let mut guard = GroupKill {
        pgid: child
            .id()
            .and_then(|id| i32::try_from(id).ok())
            .map(Pid::from_raw),
    };

    /* Written and closed BEFORE the output reads start: the payload is a handful of bytes, far
    under any pipe buffer. Closing is what tells a program like chpasswd the input is complete. */
    if let (Some(text), Some(mut pipe)) = (stdin, child.stdin.take()) {
        let _ = pipe.write_all(text.as_bytes()).await;
        drop(pipe);
    }

    let stdout = tokio::spawn(read_all(child.stdout.take()));
    let stderr = tokio::spawn(read_all(child.stderr.take()));

    let (timed_out, exit_code) = match tokio::time::timeout(timeout, child.wait()).await {
        Ok(Ok(status)) => (false, status.code().unwrap_or(-1)),
        Ok(Err(error)) => {
            tracing::debug!(file, %error, "waiting for a command failed");
            (false, -1)
        }
        Err(_) => {
            guard.kill();
            let _ = child.wait().await;
            (true, -1)
        }
    };
    // The process (and, on a timeout, its group) is gone: nothing left to kill.
    guard.pgid = None;

    ProcessOutcome {
        started: true,
        timed_out,
        exit_code,
        stdout: stdout.await.unwrap_or_default(),
        stderr: stderr.await.unwrap_or_default(),
    }
}

impl CommandRunner for SystemRunner {
    fn run<'a>(
        &'a self,
        file: &'a str,
        argv: &'a [String],
        timeout: Duration,
        stdin: Option<&'a str>,
    ) -> BoxFuture<'a, ProcessOutcome> {
        Box::pin(run(file, argv, timeout, stdin))
    }
}

/// Turn string literals into an argv.
#[must_use]
pub fn argv(parts: &[&str]) -> Vec<String> {
    parts.iter().map(|p| (*p).to_owned()).collect()
}
