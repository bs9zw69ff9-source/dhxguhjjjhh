//! The bot's entry point. This stage runs the startup checks only: the object graph and the
//! gateway are wired in the next milestone step, and until then the binary says so and exits
//! rather than pretending to run.

use std::process::ExitCode;

use pavlov_bot::startup::{EX_CONFIG, settle};
use pavlov_config::Config;
use pavlov_storage::installs::DiskProbe;

fn main() -> ExitCode {
    let cwd = match std::env::current_dir() {
        Ok(cwd) => cwd,
        Err(error) => {
            eprintln!("cannot read the working directory: {error}");
            return ExitCode::from(1);
        }
    };
    let config = match Config::load(&cwd.join(".env"), std::env::vars_os()) {
        Ok(config) => config,
        Err(error) => {
            eprintln!("{error}");
            return ExitCode::from(EX_CONFIG as u8);
        }
    };
    match settle(&config, &cwd, &DiskProbe::default()) {
        Err(fatal) => {
            for line in fatal.lines {
                eprintln!("{line}");
            }
            ExitCode::from(EX_CONFIG as u8)
        }
        Ok(settled) => {
            for note in &settled.notes {
                eprintln!("{note}");
            }
            eprintln!(
                "configuration accepted; the Rust bot's object graph is not wired yet, so it \
                 does not start (run the C# bot)"
            );
            ExitCode::from(1)
        }
    }
}
