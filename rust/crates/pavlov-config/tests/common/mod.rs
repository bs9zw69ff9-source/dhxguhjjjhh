//! Shared by the configuration test files.

#![allow(dead_code)] // each test file uses its own subset

use std::io;
use std::path::Path;

use pavlov_config::{BotOptions, Config, FeatureOptions};
use pavlov_core::servers::{DetectedServer, HostProbe};

/// A machine with no Pavlov on it: no systemd units, no installs, no directories - the same
/// machine the C# tests ran on, where `/home/steam` does not exist.
pub struct EmptyBox;

impl HostProbe for EmptyBox {
    fn systemd_servers(&self) -> Vec<DetectedServer> {
        Vec::new()
    }
    fn installs_beside(&self, _: &str, _: &str) -> io::Result<Vec<String>> {
        Ok(Vec::new())
    }
    fn directory_exists(&self, _: &str) -> bool {
        false
    }
}

pub fn config(pairs: &[(&str, &str)]) -> Config {
    Config::from_pairs(pairs.iter().copied())
}

pub fn bot(pairs: &[(&str, &str)]) -> BotOptions {
    BotOptions::bind(&config(pairs), Path::new("/srv/bot"))
}

pub fn features(pairs: &[(&str, &str)]) -> FeatureOptions {
    FeatureOptions::bind(&config(pairs), &EmptyBox)
}
