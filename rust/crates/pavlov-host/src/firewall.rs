//! Blocking an address at the box's firewall with ufw (C# `Servers/Firewall.cs`).
//!
//! THE RULE THE FEATURE TURNS ON: a deny goes IN FRONT of the port allows (`insert 1`), or
//! ufw's first-match evaluation lets the "blocked" address straight through the game port.

use std::net::IpAddr;
use std::sync::Arc;
use std::time::Duration;

use futures_util::future::BoxFuture;

use crate::process::CommandRunner;

const TIMEOUT: Duration = Duration::from_secs(10);

/// What ufw said, and whether it worked.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FirewallResult {
    pub ok: bool,
    pub detail: String,
}

impl FirewallResult {
    fn new(ok: bool, detail: impl Into<String>) -> Self {
        Self {
            ok,
            detail: detail.into(),
        }
    }
}

/// The firewall seam.
pub trait Firewall: Send + Sync {
    fn deny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult>;
    fn undeny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult>;
    fn status(&self) -> BoxFuture<'_, FirewallResult>;
}

#[must_use]
pub fn insert_deny_argv(canonical_ip: &str) -> Vec<String> {
    ["insert", "1", "deny", "from", canonical_ip]
        .map(str::to_owned)
        .to_vec()
}

#[must_use]
pub fn append_deny_argv(canonical_ip: &str) -> Vec<String> {
    ["deny", "from", canonical_ip].map(str::to_owned).to_vec()
}

#[must_use]
pub fn delete_deny_argv(canonical_ip: &str) -> Vec<String> {
    ["delete", "deny", "from", canonical_ip]
        .map(str::to_owned)
        .to_vec()
}

/// ufw directly as root, else through NON-INTERACTIVE sudo: `-n` makes a missing grant fail
/// instead of hanging on a password prompt nobody will answer.
#[must_use]
pub fn invocation(ufw_argv: &[String], privileged: bool) -> (String, Vec<String>) {
    if privileged {
        ("ufw".into(), ufw_argv.to_vec())
    } else {
        let mut argv = vec!["-n".to_owned(), "ufw".to_owned()];
        argv.extend(ufw_argv.iter().cloned());
        ("sudo".into(), argv)
    }
}

/// The one sudoers line the bot needs, and nothing wider.
#[must_use]
pub fn sudoers_advice(user: &str) -> String {
    format!(
        "The bot is not root and sudo refused ufw. Grant it with `visudo`:\n```\n{user} ALL=(root) NOPASSWD: /usr/sbin/ufw\n```"
    )
}

/// The address in its canonical text, or `None`. Strict: `1` or `1.2.3` are not addresses
/// here, though .NET's parser reads them as `0.0.0.1` and `1.2.0.3`.
fn canonical(ip: &str) -> Option<String> {
    ip.trim().parse::<IpAddr>().ok().map(|a| a.to_string())
}

fn not_an_address(ip: &str) -> FirewallResult {
    FirewallResult::new(false, format!("not an address: {ip}"))
}

/// The real firewall.
pub struct UfwFirewall {
    runner: Arc<dyn CommandRunner>,
    privileged: bool,
    user: String,
}

impl std::fmt::Debug for UfwFirewall {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UfwFirewall")
            .field("privileged", &self.privileged)
            .field("user", &self.user)
            .finish_non_exhaustive()
    }
}

impl UfwFirewall {
    /// For this process.
    #[must_use]
    pub fn new(runner: Arc<dyn CommandRunner>) -> Self {
        let user = nix::unistd::User::from_uid(nix::unistd::getuid())
            .ok()
            .flatten()
            .map_or_else(|| "the-bot-user".to_owned(), |u| u.name);
        Self::with(runner, pavlov_storage::ownership::is_privileged(), user)
    }

    #[must_use]
    pub fn with(runner: Arc<dyn CommandRunner>, privileged: bool, user: impl Into<String>) -> Self {
        Self {
            runner,
            privileged,
            user: user.into(),
        }
    }

    async fn run_ufw(&self, argv: Vec<String>) -> FirewallResult {
        let (file, args) = invocation(&argv, self.privileged);
        let run = self.runner.run(&file, &args, TIMEOUT, None).await;
        if !run.started {
            return FirewallResult::new(false, format!("{file} is not installed or not on PATH"));
        }
        if run.timed_out {
            return FirewallResult::new(false, "ufw did not answer within 10s and was stopped");
        }
        let combined = run.combined();
        let mut output = if combined.is_empty() {
            "(no output)".to_owned()
        } else {
            combined
        };
        if !self.privileged
            && run.exit_code != 0
            && output.to_lowercase().contains("password is required")
        {
            output = format!("{output}\n{}", sudoers_advice(&self.user));
        }
        FirewallResult::new(run.exit_code == 0, output)
    }
}

impl Firewall for UfwFirewall {
    fn deny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        Box::pin(async move {
            let Some(canonical) = canonical(ip) else {
                return not_an_address(ip);
            };
            /* DELETE ANY EXISTING DENY FIRST, wherever it sits: ufw will not MOVE a rule it
            already has ("Skipping adding existing rule"), so an old deny appended behind the
            port allows would stay there doing nothing. Deleting an absent rule is a no-op. */
            let _ = self.run_ufw(delete_deny_argv(&canonical)).await;
            let inserted = self.run_ufw(insert_deny_argv(&canonical)).await;
            if inserted.ok {
                return inserted;
            }
            /* `insert 1` fails on an EMPTY rule set ("Invalid position 1"); with nothing to sit
            in front of, an append is equivalent. Absent or unprivileged ufw fails the same way
            here, and that real reason is what comes back. */
            self.run_ufw(append_deny_argv(&canonical)).await
        })
    }

    fn undeny<'a>(&'a self, ip: &'a str) -> BoxFuture<'a, FirewallResult> {
        Box::pin(async move {
            match canonical(ip) {
                Some(canonical) => self.run_ufw(delete_deny_argv(&canonical)).await,
                None => not_an_address(ip),
            }
        })
    }

    fn status(&self) -> BoxFuture<'_, FirewallResult> {
        Box::pin(self.run_ufw(vec!["status".into(), "numbered".into()]))
    }
}
