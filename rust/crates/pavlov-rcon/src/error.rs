//! Every way an RCON exchange fails (C# `RconException` and its subclasses).
//!
//! The kinds are distinct because the callers act on them differently: a refused password is
//! never retried, a refused command needs a different argument, an unconfirmed one may have
//! run, and a mismatched reply means the session is out of step.

use std::fmt;
use std::time::Duration;

/// An RCON failure. Cloneable, because a coalesced read hands one outcome to every waiter.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RconError {
    /// The command was more than one line, or blank, and never reached the wire. The protocol
    /// is line-based, so an embedded newline would send a SECOND command.
    InvalidCommand(&'static str),
    /// The connection could not be made, died, or closed mid-exchange.
    Transport(String),
    /// The exchange took longer than the command timeout.
    Timeout(Duration),
    /// The peer did not speak the protocol (no password prompt).
    Protocol(String),
    /// The server rejected the password. Never worth retrying.
    Auth(String),
    /// The server answered a different command than the one sent.
    Mismatched { asked: String, answered: String },
    /// The server understood the command and refused it (`"Successful": false`). NOT a transport
    /// failure: a refused Ban needs a different argument, an unreachable server needs looking at.
    Rejected { command: String, reply: String },
    /// A state-changing command failed after it was sent: the server may have carried it out,
    /// so it was not sent again.
    Unconfirmed {
        message: String,
        cause: Box<RconError>,
    },
    /// Every attempt failed; `cause` is the last one.
    Failed {
        message: String,
        cause: Box<RconError>,
    },
}

impl RconError {
    /// The connection died or could not be made - the case where nothing coming back proves the
    /// command was never delivered.
    #[must_use]
    pub fn is_connection_lost(&self) -> bool {
        matches!(self, Self::Transport(_))
    }

    /// The failure this one wraps, if any, all the way down.
    #[must_use]
    pub fn root(&self) -> &Self {
        match self {
            Self::Unconfirmed { cause, .. } | Self::Failed { cause, .. } => cause.root(),
            other => other,
        }
    }

    pub(crate) fn transport(error: &std::io::Error) -> Self {
        Self::Transport(error.to_string())
    }
}

impl fmt::Display for RconError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidCommand(why) => f.write_str(why),
            Self::Transport(m) | Self::Protocol(m) | Self::Auth(m) => f.write_str(m),
            Self::Timeout(after) => {
                write!(f, "no reply within {} ms", after.as_millis())
            }
            Self::Mismatched { asked, answered } => write!(
                f,
                "sent \"{asked}\" but the server answered \"{answered}\" - the reply belongs to another command"
            ),
            Self::Rejected { command, reply } => {
                write!(f, "the server refused \"{command}\": {reply}")
            }
            Self::Unconfirmed { message, .. } | Self::Failed { message, .. } => {
                f.write_str(message)
            }
        }
    }
}

impl std::error::Error for RconError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Unconfirmed { cause, .. } | Self::Failed { cause, .. } => Some(cause.as_ref()),
            _ => None,
        }
    }
}
