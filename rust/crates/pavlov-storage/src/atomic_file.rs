//! Replace a file so a reader sees the old contents or the new ones, never half (C#
//! `Storage/AtomicFile.cs`).
//!
//! A temp file beside the target, flushed to disk, then renamed over it - atomic within a
//! directory. Three details that each caused or risked real damage:
//!
//! - OWNER AND MODE ARE CARRIED OVER. A rename replaces the inode, so the result used to belong
//!   to whoever the bot runs as, and a game that cannot write its own ledger or ban file loses
//!   what it saves. A file that did not exist yet takes its directory's owner.
//! - FLUSHED BEFORE THE RENAME, or a power loss can leave a renamed but empty whitelist.
//! - A UNIQUE TEMP NAME, so two writers to one path cannot clobber each other's temp. It ends in
//!   `.tmp`, so nothing scanning for `*.txt` mistakes it for the real file.

use std::fs::{self, OpenOptions, Permissions};
use std::io::{self, Write};
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::ownership;

/// Group read and write, added when an unprivileged bot hands a file to the game's group.
const GROUP_READ_WRITE: u32 = 0o060;

/// The ownership change a replacement needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OwnershipPlan {
    /// `None` leaves the owner as it is.
    pub uid: Option<u32>,
    pub gid: u32,
    /// Make the file group-writable, so the group's game can still save it.
    pub group_write: bool,
}

/// The change a replacement needs, or `None` when it already matches.
///
/// ROOT gives the file straight back to its owner. AN UNPRIVILEGED BOT cannot give a file away,
/// but it can set the group to one it belongs to - so a bot in the `steam` group hands the file
/// to that group and makes it group-writable.
#[must_use]
pub fn plan(privileged: bool, reference: (u32, u32), mine: (u32, u32)) -> Option<OwnershipPlan> {
    if reference == mine {
        return None;
    }
    Some(if privileged {
        OwnershipPlan {
            uid: Some(reference.0),
            gid: reference.1,
            group_write: false,
        }
    } else {
        OwnershipPlan {
            uid: None,
            gid: reference.1,
            group_write: reference.0 != mine.0,
        }
    })
}

/// The temp file used for one write of `path`: unique per process, per write.
fn temp_path_for(path: &Path) -> PathBuf {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_nanos());
    let mut name = path.as_os_str().to_owned();
    name.push(format!(
        ".bot.{}.{nanos:x}.{}.tmp",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    PathBuf::from(name)
}

/// Replace `path` with `contents`.
///
/// # Errors
/// When the temp file cannot be written or renamed. The temp file is removed on failure.
pub fn write(path: &Path, contents: &[u8]) -> io::Result<()> {
    if path.as_os_str().is_empty() {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "an empty path"));
    }
    let temp = temp_path_for(path);
    let result = (|| {
        {
            let mut file = OpenOptions::new()
                .write(true)
                .create_new(true)
                .open(&temp)?;
            file.write_all(contents)?;
            file.sync_all()?;
        }
        carry_metadata(path, &temp)?;
        fs::rename(&temp, path)
    })();
    if result.is_err() {
        // The original error matters more than a failure to clean up after it.
        let _ = fs::remove_file(&temp);
    }
    result
}

/// [`write`] for text (UTF-8, no BOM).
///
/// # Errors
/// As for [`write`].
pub fn write_text(path: &Path, contents: &str) -> io::Result<()> {
    write(path, contents.as_bytes())
}

/// [`write_text`] off the async runtime's threads.
///
/// # Errors
/// As for [`write`], or when the blocking task cannot run.
pub async fn write_text_async(path: PathBuf, contents: String) -> io::Result<()> {
    tokio::task::spawn_blocking(move || write_text(&path, &contents))
        .await
        .map_err(io::Error::other)?
}

/// Give `temp` the mode `path` has, and its owner - or, for a new file, its directory's owner.
fn carry_metadata(path: &Path, temp: &Path) -> io::Result<()> {
    let existing = fs::metadata(path).ok();
    let mut mode = match &existing {
        Some(m) => m.permissions().mode(),
        None => fs::metadata(temp)?.permissions().mode(),
    } & 0o7777;

    let reference = if existing.is_some() {
        Some(path.to_path_buf())
    } else {
        std::path::absolute(path)
            .ok()
            .and_then(|p| p.parent().map(Path::to_path_buf))
    };
    if let Some(reference) = reference
        && let (Ok(owner), Ok(mine)) = (ownership::get(&reference), ownership::get(temp))
        && let Some(plan) = plan(ownership::is_privileged(), owner, mine)
    {
        match ownership::set(temp, plan.uid, Some(plan.gid)) {
            Ok(()) if plan.group_write => mode |= GROUP_READ_WRITE,
            Ok(()) => {}
            Err(error) => tracing::warn!(
                path = %path.display(),
                %error,
                "could not give the replaced file back to its owner; the game may not be able to save it"
            ),
        }
    }
    fs::set_permissions(temp, Permissions::from_mode(mode))
}
