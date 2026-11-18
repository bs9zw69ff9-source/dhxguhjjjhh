//! A stand-in for Pavlov's RCON listener, for tests (C# `tests/FakeRconServer.cs`).
//!
//! It speaks the real handshake (password prompt, MD5 challenge, `Authenticated=1`, then JSON
//! replies) and RECORDS THE EXACT BYTES it receives, because RCON bugs in this codebase have
//! repeatedly been the gap between what the code reads like and what the server receives.
//!
//! It counts CONNECTIONS separately from COMMANDS: a client that opens a socket per command
//! looks identical from the command count alone.

use std::collections::HashSet;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use md5::{Digest, Md5};
use pavlov_config::{RconOptions, Secret};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinHandle;
use tokio::time::sleep;

/// How the fake answers. Every field can be changed while it runs.
#[derive(Debug, Clone, Default)]
pub struct Behaviour {
    /// Close the socket right after the next reply, simulating a drop.
    pub drop_after_next_command: bool,
    /// Reject the password.
    pub reject_auth: bool,
    /// Wait this long before replying.
    pub reply_delay: Duration,
    /// Deliver the password prompt and the auth line in two TCP writes each.
    pub split_handshake: bool,
    /// Answer every command as if it were this verb: a server that misattributes.
    pub answer_everything_as: Option<String>,
    /// Reply without a Command field, which some Pavlov replies genuinely do.
    pub omit_command_field: bool,
    /// Answer every command with `"Successful": false`, as a refusal does.
    pub refuse_everything: bool,
    /// Reply without a Successful field at all - unknown, not failed.
    pub omit_successful_field: bool,
    /// Answer with bare text plus CRLF rather than JSON.
    pub plain_text_reply: Option<String>,
    /// Answer with this exact body, verbatim.
    pub exact_reply: Option<String>,
    /// Write the reply as two writes, split after this many BYTES - to split mid-character.
    pub split_reply_after_bytes: Option<usize>,
    /// Verbs the server ACCEPTS AND NEVER ANSWERS: a hung game thread, with the connection up.
    pub swallow: HashSet<String>,
    /// Who is online, as `(name, unique id)`: the `PlayerList` every reply carries.
    pub players: Vec<(String, String)>,
}

#[derive(Debug, Default)]
struct Recorded {
    /// Each command as received, trimmed.
    commands: Vec<String>,
    /// Each command read exactly as it arrived on the wire.
    wire: Vec<Vec<u8>>,
    /// Each password answer exactly as it arrived.
    logins: Vec<Vec<u8>>,
}

#[derive(Debug)]
struct Shared {
    password: String,
    behaviour: Mutex<Behaviour>,
    recorded: Mutex<Recorded>,
    connections: AtomicUsize,
    serving: Mutex<Vec<JoinHandle<()>>>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

fn md5_hex(input: &str) -> String {
    Md5::digest(input.as_bytes())
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

/// The fake server, listening on a loopback port. Stops when dropped.
#[derive(Debug)]
pub struct FakeRconServer {
    address: SocketAddr,
    shared: Arc<Shared>,
    accept: JoinHandle<()>,
}

impl FakeRconServer {
    /// Listen with the password "secret".
    ///
    /// # Errors
    /// When no loopback port can be bound.
    pub async fn start() -> std::io::Result<Self> {
        Self::with_password("secret").await
    }

    /// Listen with this password.
    ///
    /// # Errors
    /// When no loopback port can be bound.
    pub async fn with_password(password: &str) -> std::io::Result<Self> {
        let listener = TcpListener::bind(("127.0.0.1", 0)).await?;
        let address = listener.local_addr()?;
        let shared = Arc::new(Shared {
            password: password.to_owned(),
            behaviour: Mutex::new(Behaviour::default()),
            recorded: Mutex::new(Recorded::default()),
            connections: AtomicUsize::new(0),
            serving: Mutex::new(Vec::new()),
        });
        let accepting = Arc::clone(&shared);
        let accept = tokio::spawn(async move {
            while let Ok((stream, _)) = listener.accept().await {
                accepting.connections.fetch_add(1, Ordering::SeqCst);
                let serving = Arc::clone(&accepting);
                let task = tokio::spawn(async move { serve(serving, stream).await });
                lock(&accepting.serving).push(task);
            }
        });
        Ok(Self {
            address,
            shared,
            accept,
        })
    }

    #[must_use]
    pub fn port(&self) -> u16 {
        self.address.port()
    }

    #[must_use]
    pub fn password(&self) -> &str {
        &self.shared.password
    }

    /// Options pointing at this server with the C# test defaults: no spacing (asserted in its own
    /// test), a 3 s timeout, the default read cache.
    #[must_use]
    pub fn options(&self) -> RconOptions {
        let mut options = RconOptions::new(
            "server1",
            "127.0.0.1",
            i32::from(self.port()),
            Secret::new(self.password()),
        );
        options.command_spacing = Duration::ZERO;
        options
    }

    /// Change how it answers.
    pub fn configure(&self, change: impl FnOnce(&mut Behaviour)) {
        change(&mut lock(&self.shared.behaviour));
    }

    /// TCP connections accepted so far.
    #[must_use]
    pub fn connections(&self) -> usize {
        self.shared.connections.load(Ordering::SeqCst)
    }

    /// Every command received, trimmed, in order.
    #[must_use]
    pub fn commands(&self) -> Vec<String> {
        lock(&self.shared.recorded).commands.clone()
    }

    /// Every command read exactly as the bytes arrived.
    #[must_use]
    pub fn wire(&self) -> Vec<Vec<u8>> {
        lock(&self.shared.recorded).wire.clone()
    }

    /// Every password answer exactly as the bytes arrived.
    #[must_use]
    pub fn logins(&self) -> Vec<Vec<u8>> {
        lock(&self.shared.recorded).logins.clone()
    }
}

impl Drop for FakeRconServer {
    fn drop(&mut self) {
        self.accept.abort();
        for task in lock(&self.shared.serving).drain(..) {
            task.abort();
        }
    }
}

async fn write_maybe_split(stream: &mut TcpStream, text: &str, split: bool) -> std::io::Result<()> {
    let bytes = text.as_bytes();
    if !split || bytes.len() <= 4 {
        return stream.write_all(bytes).await;
    }
    stream.write_all(&bytes[..4]).await?;
    stream.flush().await?;
    sleep(Duration::from_millis(50)).await;
    stream.write_all(&bytes[4..]).await
}

async fn serve(shared: Arc<Shared>, mut stream: TcpStream) {
    // Any I/O error ends this one connection, which is what a real server does too.
    let _ = serve_inner(&shared, &mut stream).await;
}

async fn serve_inner(shared: &Shared, stream: &mut TcpStream) -> std::io::Result<()> {
    let mut buffer = vec![0u8; 4096];
    let behaviour = || lock(&shared.behaviour).clone();

    write_maybe_split(stream, "Password: ", behaviour().split_handshake).await?;

    let n = stream.read(&mut buffer).await?;
    lock(&shared.recorded).logins.push(buffer[..n].to_vec());
    let supplied = String::from_utf8_lossy(&buffer[..n]).trim().to_owned();
    let ok = !behaviour().reject_auth && supplied == md5_hex(&shared.password);
    write_maybe_split(
        stream,
        &format!("Authenticated={}\r\n", u8::from(ok)),
        behaviour().split_handshake,
    )
    .await?;
    if !ok {
        return Ok(());
    }

    loop {
        let n = stream.read(&mut buffer).await?;
        if n == 0 {
            return Ok(());
        }
        let command = String::from_utf8_lossy(&buffer[..n]).trim().to_owned();
        {
            let mut recorded = lock(&shared.recorded);
            recorded.commands.push(command.clone());
            recorded.wire.push(buffer[..n].to_vec());
        }

        let delay = behaviour().reply_delay;
        if !delay.is_zero() {
            sleep(delay).await;
        }
        let b = behaviour();
        let verb = command.split(' ').next().unwrap_or_default().to_owned();
        if b.swallow.iter().any(|s| s.eq_ignore_ascii_case(&verb)) {
            continue;
        }

        let answering = b.answer_everything_as.clone().unwrap_or(verb);
        let players = serde_json::to_string(
            &b.players
                .iter()
                .map(|(name, id)| serde_json::json!({ "Username": name, "UniqueId": id }))
                .collect::<Vec<_>>(),
        )
        .unwrap_or_else(|_| "[]".into());
        let outcome = if b.refuse_everything { "false" } else { "true" };
        let reply = if let Some(exact) = &b.exact_reply {
            exact.clone()
        } else if let Some(plain) = &b.plain_text_reply {
            format!("{plain}\r\n")
        } else if b.omit_command_field {
            format!("{{\"Successful\":{outcome},\"PlayerList\":{players}}}\r\n")
        } else if b.omit_successful_field {
            format!("{{\"Command\":\"{answering}\",\"PlayerList\":{players}}}\r\n")
        } else {
            format!(
                "{{\"Command\":\"{answering}\",\"Successful\":{outcome},\"PlayerList\":{players}}}\r\n"
            )
        };

        let bytes = reply.as_bytes();
        match b.split_reply_after_bytes {
            Some(at) if at > 0 && at < bytes.len() => {
                stream.write_all(&bytes[..at]).await?;
                stream.flush().await?;
                // Long enough that the client's read cannot coalesce the two writes.
                sleep(Duration::from_millis(50)).await;
                stream.write_all(&bytes[at..]).await?;
            }
            _ => stream.write_all(bytes).await?,
        }

        if b.drop_after_next_command {
            lock(&shared.behaviour).drop_after_next_command = false;
            return Ok(());
        }
    }
}
