//! One authenticated TCP session to a Pavlov RCON server (C# `RconConnection.cs`).
//!
//! RCON is strictly request/response over a single stream, so commands MUST be serialised: two
//! concurrent writers would interleave and each read the other's reply - the bug that looks like
//! "the bot occasionally reports the wrong player". The mutex is what makes a shared session safe.
//!
//! AN UNFINISHED EXCHANGE POISONS THE SESSION. The C# drops the socket in a `finally` when an
//! exchange did not settle; here the session is marked dirty before the command is written and
//! clean only once its reply has been read, so a caller that times out (its future is dropped
//! mid-read) leaves the mark behind and the next command reconnects instead of reading the
//! orphaned reply as its own. That is drop-safe by construction, which a `finally` is not.
//!
//! MD5 is not a security decision: it is what the protocol specifies.

use std::fmt;
use std::time::Duration;

use md5::{Digest, Md5};
use pavlov_config::RconOptions;
use serde::de::IgnoredAny;
use socket2::{SockRef, TcpKeepalive};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::sync::Mutex;
use tokio::time::{Instant, sleep, timeout};

use crate::RconError;

/// How far one exchange got, so a failure can be judged safe to repeat or not.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct ExchangeProgress {
    /// The command was handed to the socket (not necessarily received).
    pub written: bool,
    /// Reply bytes read on the latest attempt. Any at all means the server was listening.
    pub reply_bytes: usize,
}

/// The handshake text is bounded; a peer sending more is not a Pavlov server.
const MAX_HANDSHAKE_BYTES: usize = 4096;
const READ_BUFFER: usize = 4096;
/// How long shutdown waits for an exchange in flight before closing anyway.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(5);

struct State {
    stream: Option<TcpStream>,
    last_used: Instant,
    /// An exchange began and has not finished: the stream holds an unknown number of unread
    /// bytes and must not be handed to the next command.
    dirty: bool,
}

/// The session, behind the gate that serialises it.
pub(crate) struct Connection {
    options: RconOptions,
    state: Mutex<State>,
}

impl fmt::Debug for Connection {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Connection")
            .field("name", &self.options.name)
            .field("host", &self.options.host)
            .field("port", &self.options.port)
            .finish_non_exhaustive()
    }
}

fn md5_hex(input: &str) -> String {
    Md5::digest(input.as_bytes())
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

fn truncate(s: &str) -> String {
    if s.chars().count() <= 60 {
        s.to_owned()
    } else {
        format!("{}...", s.chars().take(60).collect::<String>())
    }
}

/// The auth line has arrived far enough to read its 0 or 1.
fn has_auth_verdict(text: &str) -> bool {
    const MARKER: &str = "Authenticated=";
    text.find(MARKER)
        .is_some_and(|at| text.len() > at + MARKER.len())
}

fn looks_like_json(text: &str) -> bool {
    text.trim_start().starts_with(['{', '['])
}

fn is_complete_json(text: &str) -> bool {
    serde_json::from_str::<IgnoredAny>(text).is_ok()
}

/// Ask the OS to probe the peer while idle, so a crashed server is noticed in about a minute
/// rather than at the next command. Best effort: a session without it still works, and the
/// command-level retry is the backstop.
fn keep_alive(stream: &TcpStream) {
    let settings = TcpKeepalive::new()
        .with_time(Duration::from_secs(30))
        .with_interval(Duration::from_secs(10))
        .with_retries(3);
    if let Err(error) = SockRef::from(stream).set_tcp_keepalive(&settings) {
        tracing::debug!(%error, "RCON: TCP keepalive is not available on this socket");
    }
}

/// UTF-8 decoded incrementally. TCP splits wherever it likes, including through the middle of a
/// multi-byte character - and a player name is exactly where those live - so an incomplete
/// sequence at the end of a chunk is held over to the next one rather than replaced.
#[derive(Default)]
struct Utf8Accumulator {
    text: String,
    pending: Vec<u8>,
}

impl Utf8Accumulator {
    fn push(&mut self, bytes: &[u8]) {
        self.pending.extend_from_slice(bytes);
        let mut start = 0;
        loop {
            match std::str::from_utf8(&self.pending[start..]) {
                Ok(valid) => {
                    self.text.push_str(valid);
                    self.pending.clear();
                    return;
                }
                Err(error) => {
                    let valid_end = start + error.valid_up_to();
                    // The prefix was just validated, so this cannot fail; an empty push if it did.
                    self.text.push_str(
                        std::str::from_utf8(&self.pending[start..valid_end]).unwrap_or(""),
                    );
                    match error.error_len() {
                        // Truly invalid: replaced, as .NET's decoder does, and decoding goes on.
                        Some(len) => {
                            self.text.push(char::REPLACEMENT_CHARACTER);
                            start = valid_end + len;
                        }
                        // Incomplete: keep the tail for the next chunk.
                        None => {
                            self.pending.drain(..valid_end);
                            return;
                        }
                    }
                }
            }
        }
    }
}

async fn write(stream: &mut TcpStream, text: &str) -> Result<(), RconError> {
    stream
        .write_all(text.as_bytes())
        .await
        .map_err(|e| RconError::transport(&e))?;
    stream.flush().await.map_err(|e| RconError::transport(&e))
}

/// Read until `done` holds, the peer closes, or the handshake cap is reached.
async fn read_until(
    stream: &mut TcpStream,
    done: impl Fn(&str) -> bool,
) -> Result<String, RconError> {
    let mut buffer = [0u8; 1024];
    let mut bytes = Vec::new();
    while bytes.len() < MAX_HANDSHAKE_BYTES {
        let read = stream
            .read(&mut buffer)
            .await
            .map_err(|e| RconError::transport(&e))?;
        if read == 0 {
            break;
        }
        bytes.extend_from_slice(&buffer[..read]);
        if done(&String::from_utf8_lossy(&bytes)) {
            break;
        }
    }
    Ok(String::from_utf8_lossy(&bytes).into_owned())
}

/// Write one command and read its whole reply.
async fn exchange(
    stream: &mut TcpStream,
    command: &str,
    progress: &mut ExchangeProgress,
) -> Result<String, RconError> {
    progress.reply_bytes = 0;
    write(stream, &format!("{command}\n")).await?;
    progress.written = true;

    let mut buffer = vec![0u8; READ_BUFFER];
    let mut reply = Utf8Accumulator::default();
    loop {
        let read = stream
            .read(&mut buffer)
            .await
            .map_err(|e| RconError::transport(&e))?;
        if read == 0 {
            return Err(RconError::Transport(
                "connection closed while awaiting a reply".into(),
            ));
        }
        progress.reply_bytes += read;
        reply.push(&buffer[..read]);
        let text = &reply.text;

        /* Settle as soon as the text is a complete JSON document, parsed only when it could END
        one: parsing every chunk of a long roster was quadratic for no benefit. */
        if text.trim_end().ends_with(['}', ']']) && looks_like_json(text) && is_complete_json(text)
        {
            return Ok(reply.text);
        }

        /* A non-JSON answer settles on its trailing CRLF - but WHITESPACE IS NOT A REPLY. A stray
        "\r\n" returned as the answer left the real reply unread for the next command, and every
        command after it read one behind, permanently. Not returning it lets the timeout fire,
        which leaves the session dirty, which reconnects. */
        if text.ends_with("\r\n") && !looks_like_json(text) && !text.trim().is_empty() {
            return Ok(reply.text);
        }
    }
}

impl Connection {
    pub(crate) fn new(options: RconOptions) -> Self {
        Self {
            options,
            state: Mutex::new(State {
                stream: None,
                last_used: Instant::now(),
                dirty: false,
            }),
        }
    }

    async fn connect(&self, state: &mut State) -> Result<(), RconError> {
        state.stream = None;
        let o = &self.options;
        let port = u16::try_from(o.port)
            .map_err(|_| RconError::Transport(format!("{} is not a valid port", o.port)))?;
        let mut stream = TcpStream::connect((o.host.as_str(), port))
            .await
            .map_err(|e| RconError::Transport(format!("{}:{port}: {e}", o.host)))?;
        // A three-byte command must not wait on Nagle.
        stream
            .set_nodelay(true)
            .map_err(|e| RconError::transport(&e))?;
        keep_alive(&stream);

        // The server speaks first. TCP may deliver "Pass" and "word: " separately.
        let prompt =
            read_until(&mut stream, |t| t.to_ascii_lowercase().contains("password")).await?;
        if !prompt.to_ascii_lowercase().contains("password") {
            return Err(RconError::Protocol(format!(
                "expected a password prompt from {}:{}, got \"{}\"",
                o.host,
                o.port,
                truncate(&prompt)
            )));
        }

        write(&mut stream, &md5_hex(o.password.expose())).await?;
        let auth = read_until(&mut stream, has_auth_verdict).await?;

        /* ONLY AN ANSWER OF 0 IS A WRONG PASSWORD. A server mid-restart accepts the connection and
        closes it before answering; calling that a rejected password made it never retried. */
        if !has_auth_verdict(&auth) {
            return Err(RconError::Transport(format!(
                "{} closed the connection before answering the login",
                o.name
            )));
        }
        if !auth.contains("Authenticated=1") {
            return Err(RconError::Auth(format!(
                "{} rejected the RCON password",
                o.name
            )));
        }

        state.stream = Some(stream);
        state.last_used = Instant::now();
        Ok(())
    }

    /// Send one command and return the raw reply, queued behind any command in flight.
    ///
    /// `idempotent`: whether sending it twice is harmless. One that is not is re-sent after a
    /// dropped session ONLY when no reply byte came back - a dead socket cannot have delivered it.
    pub(crate) async fn send(
        &self,
        command: &str,
        idempotent: bool,
        progress: &mut ExchangeProgress,
    ) -> Result<String, RconError> {
        let mut state = self.state.lock().await;
        if state.dirty {
            state.stream = None;
            state.dirty = false;
        }

        let stale = state.last_used.elapsed() > self.options.idle_timeout;
        if state.stream.is_none() || stale {
            self.connect(&mut state).await?;
        }

        /* Spacing is applied INSIDE the gate, so it paces the shared session - the only way it
        limits the rate the server sees. */
        let since = state.last_used.elapsed();
        if since < self.options.command_spacing {
            sleep(self.options.command_spacing - since).await;
        }

        state.dirty = true;
        let mut result = match state.stream.as_mut() {
            Some(stream) => exchange(stream, command, progress).await,
            None => Err(RconError::Transport("not connected".into())),
        };
        if let Err(error) = &result {
            /* The session died mid-command - a restart, a map change, an idle reap. Rebuild once
            and retry: from the caller's side a transient blip. */
            if error.is_connection_lost() && (idempotent || progress.reply_bytes == 0) {
                result = match self.connect(&mut state).await {
                    Ok(()) => match state.stream.as_mut() {
                        Some(stream) => exchange(stream, command, progress).await,
                        None => Err(RconError::Transport("not connected".into())),
                    },
                    Err(e) => Err(e),
                };
            }
        }

        state.last_used = Instant::now();
        if result.is_err() {
            state.stream = None;
        }
        state.dirty = false;
        result
    }

    /// Tear the session down so the next command starts a fresh one. For a fault the transport
    /// cannot see: a reply naming another command is a perfectly clean exchange.
    pub(crate) async fn reset(&self) {
        let mut state = self.state.lock().await;
        state.stream = None;
        state.dirty = false;
    }

    /// Close the session, waiting a bounded time for an exchange in flight.
    pub(crate) async fn shutdown(&self) {
        if let Ok(mut state) = timeout(SHUTDOWN_GRACE, self.state.lock()).await {
            state.stream = None;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn md5_is_lowercase_hex() {
        assert_eq!(md5_hex("secret"), "5ebe2294ecd0e0f08eab7690d2a6ee69");
    }

    #[test]
    fn a_character_split_across_chunks_is_held_over() {
        let bytes = "Réçkeré".as_bytes();
        let mut acc = Utf8Accumulator::default();
        acc.push(&bytes[..2]); // "R" and the first byte of "é"
        assert_eq!(acc.text, "R");
        acc.push(&bytes[2..]);
        assert_eq!(acc.text, "Réçkeré");
    }

    #[test]
    fn an_invalid_byte_is_replaced_and_decoding_continues() {
        let mut acc = Utf8Accumulator::default();
        acc.push(b"a\xffb");
        assert_eq!(acc.text, "a\u{FFFD}b");
    }

    #[test]
    fn the_auth_verdict_needs_its_digit() {
        assert!(!has_auth_verdict("Authenticated="));
        assert!(has_auth_verdict("Authenticated=0"));
        assert!(has_auth_verdict("x Authenticated=1\r\n"));
    }
}
