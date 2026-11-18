//! A file's numeric owner, and whether this process may give a file away (C#
//! `Storage/UnixFileOwnership.cs`).
//!
//! WHY IT MATTERS. The game servers run as `steam`. A file replaced by writing a temp file and
//! renaming it takes the temp file's owner - the bot's user - and the game can then no longer
//! write it: in-game earnings stop saving, in-game bans stop persisting, and nothing fails
//! loudly. The C# needed `statx` and `chown` through P/Invoke; Rust's standard library has both.

use std::fs;
use std::io;
use std::os::unix::fs::MetadataExt;
use std::path::Path;

/// The owner and group of `path`.
///
/// # Errors
/// When the file cannot be stat'ed.
pub fn get(path: &Path) -> io::Result<(u32, u32)> {
    let metadata = fs::metadata(path)?;
    Ok((metadata.uid(), metadata.gid()))
}

/// Give `path` this owner and group; `None` leaves that id as it is.
///
/// # Errors
/// When the change is refused - only root can give a file to another user.
pub fn set(path: &Path, uid: Option<u32>, gid: Option<u32>) -> io::Result<()> {
    std::os::unix::fs::chown(path, uid, gid)
}

/// Whether this process runs as root. `/proc/self` is owned by the process's EFFECTIVE uid
/// (proc(5)), which is the one that decides whether a `chown` to another user succeeds.
#[must_use]
pub fn is_privileged() -> bool {
    get(Path::new("/proc/self")).is_ok_and(|(uid, _)| uid == 0)
}
