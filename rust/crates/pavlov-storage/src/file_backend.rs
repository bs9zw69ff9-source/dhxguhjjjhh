//! One JSON file per dataset, written atomically (C# `Storage/FileKeyValueBackend.cs`).
//!
//! Keys are dataset names, not paths: anything that could escape the data directory is REFUSED
//! rather than sanitised, because a silently-renamed dataset reads as empty and looks exactly
//! like data loss.

use std::fs;
use std::io;
use std::path::PathBuf;

use pavlov_core::data::KeyValueBackend;

use crate::atomic_file;

/// The file backend, rooted at one directory.
#[derive(Debug)]
pub struct FileKeyValueBackend {
    directory: PathBuf,
}

impl FileKeyValueBackend {
    /// Use `directory`, creating it if needed.
    ///
    /// # Errors
    /// When the directory cannot be created.
    pub fn new(directory: impl Into<PathBuf>) -> io::Result<Self> {
        let directory = directory.into();
        if directory.as_os_str().is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "no data directory",
            ));
        }
        fs::create_dir_all(&directory)?;
        Ok(Self { directory })
    }

    fn path_for(&self, key: &str) -> io::Result<PathBuf> {
        if key.trim().is_empty() || key.contains(['/', '\0']) || key.contains("..") {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("invalid dataset name \"{key}\""),
            ));
        }
        Ok(self.directory.join(format!("{key}.json")))
    }
}

impl KeyValueBackend for FileKeyValueBackend {
    /// Absent is `None`. Any other failure is an ERROR, not "absent": the C# read a transient
    /// I/O failure as absent, so the next update wrote the fallback over the real file.
    fn read(&self, key: &str) -> io::Result<Option<String>> {
        match fs::read_to_string(self.path_for(key)?) {
            Ok(text) => Ok(Some(text)),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(e),
        }
    }

    fn write(&self, key: &str, json: &str) -> io::Result<()> {
        atomic_file::write_text(&self.path_for(key)?, json)
    }
}
