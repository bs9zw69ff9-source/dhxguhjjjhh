//! A recording command runner, so tests assert the exact argv without running anything.

#![allow(dead_code)]

use std::sync::Mutex;
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_host::process::{CommandRunner, ProcessOutcome};

/// Records every call and answers from a script: the first matching `(file, argv prefix)` wins,
/// otherwise a clean exit 0 with no output.
#[derive(Debug, Default)]
pub struct FakeRunner {
    pub calls: Mutex<Vec<(String, Vec<String>)>>,
    pub answers: Mutex<Vec<(String, Vec<String>, ProcessOutcome)>>,
}

impl FakeRunner {
    pub fn answer(&self, file: &str, prefix: &[&str], outcome: ProcessOutcome) {
        self.answers.lock().unwrap().push((
            file.to_owned(),
            prefix.iter().map(|s| (*s).to_owned()).collect(),
            outcome,
        ));
    }

    pub fn calls(&self) -> Vec<(String, Vec<String>)> {
        self.calls.lock().unwrap().clone()
    }
}

pub fn exited(code: i32, stdout: &str, stderr: &str) -> ProcessOutcome {
    ProcessOutcome {
        started: true,
        timed_out: false,
        exit_code: code,
        stdout: stdout.to_owned(),
        stderr: stderr.to_owned(),
    }
}

impl CommandRunner for FakeRunner {
    fn run<'a>(
        &'a self,
        file: &'a str,
        argv: &'a [String],
        _timeout: Duration,
        _stdin: Option<&'a str>,
    ) -> BoxFuture<'a, ProcessOutcome> {
        self.calls
            .lock()
            .unwrap()
            .push((file.to_owned(), argv.to_vec()));
        let answer = self
            .answers
            .lock()
            .unwrap()
            .iter()
            .find(|(f, prefix, _)| f == file && argv.starts_with(prefix))
            .map(|(_, _, o)| o.clone())
            .unwrap_or_else(|| exited(0, "", ""));
        Box::pin(async move { answer })
    }
}
