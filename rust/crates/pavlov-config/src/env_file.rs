//! Whether the `.env` is readable by anyone but its owner (C# `Program.EnvFileTooOpen`).

/// Group or other read/write permission bits.
const SHARED_ACCESS: u32 = 0o066;

/// True when a secrets file with Unix permission `mode` can be read or written by anyone but
/// its owner. The `.env` holds the Discord token and the RCON passwords, so startup warns and
/// says `chmod 600`.
#[must_use]
pub fn env_file_too_open(mode: u32) -> bool {
    mode & SHARED_ACCESS != 0
}
