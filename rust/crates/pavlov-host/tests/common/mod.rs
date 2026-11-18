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

type Answer = Box<
    dyn Fn(
            &pavlov_host::http::HttpRequest,
        ) -> Result<pavlov_host::http::HttpResponse, pavlov_host::http::HttpError>
        + Send
        + Sync,
>;

/// An HTTP client answering from a closure, recording every request (the C# test
/// `HttpMessageHandler`s).
pub struct FakeHttp {
    pub requests: Mutex<Vec<pavlov_host::http::HttpRequest>>,
    answer: Answer,
    hang: bool,
}

impl FakeHttp {
    pub fn new(
        answer: impl Fn(
            &pavlov_host::http::HttpRequest,
        )
            -> Result<pavlov_host::http::HttpResponse, pavlov_host::http::HttpError>
        + Send
        + Sync
        + 'static,
    ) -> Self {
        Self {
            requests: Mutex::new(Vec::new()),
            answer: Box::new(answer),
            hang: false,
        }
    }

    /// Never answers: the caller has to give up.
    pub fn hanging() -> Self {
        Self {
            hang: true,
            ..Self::canned(200, "")
        }
    }

    /// Always this response.
    pub fn responding(response: pavlov_host::http::HttpResponse) -> Self {
        Self::new(move |_| Ok(response.clone()))
    }

    pub fn last_url(&self) -> Option<String> {
        self.requests.lock().unwrap().last().map(|r| r.url.clone())
    }

    /// Always this status and body.
    pub fn canned(status: u16, body: &str) -> Self {
        let body = body.to_owned();
        Self::new(move |_| Ok(pavlov_host::http::HttpResponse::new(status, body.clone())))
    }

    pub fn calls(&self) -> usize {
        self.requests.lock().unwrap().len()
    }

    pub fn urls(&self) -> Vec<String> {
        self.requests
            .lock()
            .unwrap()
            .iter()
            .map(|r| r.url.clone())
            .collect()
    }
}

impl pavlov_host::http::HttpClient for FakeHttp {
    fn send(
        &self,
        request: pavlov_host::http::HttpRequest,
    ) -> BoxFuture<'_, Result<pavlov_host::http::HttpResponse, pavlov_host::http::HttpError>> {
        let answer = (self.answer)(&request);
        self.requests.lock().unwrap().push(request);
        if self.hang {
            return Box::pin(futures_util::future::pending());
        }
        Box::pin(async move { answer })
    }
}

/// A store over the file backend in `dir`, as the C# tests build one.
pub fn file_store(dir: &std::path::Path) -> std::sync::Arc<pavlov_core::data::SerializedStore> {
    let backend = pavlov_storage::FileKeyValueBackend::new(dir).unwrap();
    std::sync::Arc::new(pavlov_core::data::SerializedStore::new(
        std::sync::Arc::new(backend),
    ))
}
