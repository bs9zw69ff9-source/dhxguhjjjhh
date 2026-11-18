//! Fakes shared by the storage tests.

#![allow(dead_code)]

use std::fs::File;
use std::path::Path;
use std::time::{Duration, SystemTime};

use pavlov_rcon::OnlineRoster;

/// A roster that says exactly what it is told.
#[derive(Debug, Clone)]
pub struct FakeRoster {
    pub trustworthy: bool,
    pub online: Vec<String>,
}

impl FakeRoster {
    pub fn new(trustworthy: bool, online: &[&str]) -> Self {
        Self {
            trustworthy,
            online: online.iter().map(|s| (*s).to_owned()).collect(),
        }
    }
}

impl OnlineRoster for FakeRoster {
    fn online(&self) -> Vec<String> {
        self.online.clone()
    }
    fn is_trustworthy(&self) -> bool {
        self.trustworthy
    }
}

/// Set a file's modified time to `offset` from now (negative is the past).
pub fn touch(path: &Path, offset_minutes: i64) {
    let now = SystemTime::now();
    let delta = Duration::from_secs(offset_minutes.unsigned_abs() * 60);
    let at = if offset_minutes >= 0 {
        now + delta
    } else {
        now - delta
    };
    File::options()
        .write(true)
        .open(path)
        .expect("the file exists")
        .set_modified(at)
        .expect("the time can be set");
}
