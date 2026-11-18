//! Reading a caller's staff-guild roles when the interaction did not carry them (C#
//! `HomeGuildMembers.cs`): a DM, or a user-installed app in a guild, hands over no member or
//! a role-less one, and every moderator would read as PUBLIC.
//!
//! - WHICH GUILD: `HOME_GUILD_ID`, else `GUILD_ID`, else the only guild the bot is in. With
//!   several guilds and neither set, staff access outside a server is OFF (owners still work,
//!   by user id), and that is said once.
//! - A MISS IS CACHED, AN ERROR IS NOT. Somebody not in the guild costs one lookup a minute;
//!   a guild briefly unreachable is retried, not remembered as "not a member".
//! - Bounded: past [`MAX_ENTRIES`] the stale entries are swept.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, PoisonError};
use std::time::{Duration, Instant};

use futures_util::future::BoxFuture;

use crate::caller::MemberView;
use crate::interaction::DiscordError;
use crate::runner::AccessPrimer;

/// How long a lookup is trusted.
pub const WINDOW: Duration = Duration::from_secs(60);
/// The cache size past which stale entries are swept.
pub const MAX_ENTRIES: usize = 500;

/// What the gateway knows about guild membership.
pub trait MemberSource: Send + Sync {
    /// The guilds the bot is in.
    fn guild_ids(&self) -> Vec<u64>;
    /// The member, `Ok(None)` when the user is not in the guild.
    fn fetch_member(
        &self,
        guild_id: u64,
        user_id: u64,
    ) -> BoxFuture<'_, Result<Option<MemberView>, DiscordError>>;
}

struct Entry {
    member: Option<MemberView>,
    at: Instant,
}

/// The cache.
pub struct HomeGuildMembers {
    source: Arc<dyn MemberSource>,
    configured_home: Option<u64>,
    command_guild: Option<u64>,
    window: Duration,
    members: Mutex<HashMap<u64, Entry>>,
    warned: AtomicBool,
}

impl std::fmt::Debug for HomeGuildMembers {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HomeGuildMembers")
            .field("configured_home", &self.configured_home)
            .field("command_guild", &self.command_guild)
            .finish_non_exhaustive()
    }
}

impl HomeGuildMembers {
    #[must_use]
    pub fn new(
        source: Arc<dyn MemberSource>,
        configured_home: Option<u64>,
        command_guild: Option<u64>,
    ) -> Self {
        Self {
            source,
            configured_home,
            command_guild,
            window: WINDOW,
            members: Mutex::new(HashMap::new()),
            warned: AtomicBool::new(false),
        }
    }

    /// A different trust window (tests).
    #[must_use]
    pub fn with_window(mut self, window: Duration) -> Self {
        self.window = window;
        self
    }

    /// The staff guild, when it can be told.
    #[must_use]
    pub fn home(&self) -> Option<u64> {
        let guilds = self.source.guild_ids();
        if let Some(named) = self.configured_home {
            if guilds.contains(&named) {
                return Some(named);
            }
            if !self.warned.swap(true, Ordering::Relaxed) {
                tracing::warn!(
                    guild = named,
                    "HOME_GUILD_ID is not a guild this bot is in, so roles cannot be read \
                     outside a server. Set it to the guild your staff roles live in"
                );
            }
            return None;
        }
        if let Some(fallback) = self.command_guild
            && guilds.contains(&fallback)
        {
            return Some(fallback);
        }
        match guilds.as_slice() {
            [only] => Some(*only),
            [] => None,
            many => {
                if !self.warned.swap(true, Ordering::Relaxed) {
                    tracing::warn!(
                        guilds = many.len(),
                        "this bot is in several guilds and neither HOME_GUILD_ID nor GUILD_ID \
                         is set, so staff access outside a server is OFF until one is named; \
                         owners still work, because they are matched by user id"
                    );
                }
                None
            }
        }
    }

    /// The cached member, while fresh.
    #[must_use]
    pub fn member(&self, user_id: u64) -> Option<MemberView> {
        let members = self.members.lock().unwrap_or_else(PoisonError::into_inner);
        members
            .get(&user_id)
            .filter(|e| e.at.elapsed() < self.window)
            .and_then(|e| e.member.clone())
    }

    /// Look the user up in the staff guild, unless a fresh answer is cached.
    pub async fn prime_user(&self, user_id: u64) {
        let fresh = {
            let members = self.members.lock().unwrap_or_else(PoisonError::into_inner);
            members
                .get(&user_id)
                .is_some_and(|e| e.at.elapsed() < self.window)
        };
        if fresh {
            return;
        }
        let Some(guild) = self.home() else {
            return;
        };
        match self.source.fetch_member(guild, user_id).await {
            Ok(member) => self.remember(user_id, member),
            Err(error) => {
                tracing::debug!(user_id, guild, %error, "could not read staff-guild membership");
            }
        }
    }

    fn remember(&self, user_id: u64, member: Option<MemberView>) {
        let mut members = self.members.lock().unwrap_or_else(PoisonError::into_inner);
        members.insert(
            user_id,
            Entry {
                member,
                at: Instant::now(),
            },
        );
        if members.len() > MAX_ENTRIES {
            let window = self.window;
            members.retain(|_, e| e.at.elapsed() < window);
        }
    }

    /// How the staff guild is chosen, for the startup log.
    #[must_use]
    pub fn describe(&self) -> String {
        match (self.configured_home, self.command_guild) {
            (Some(named), _) => format!("HOME_GUILD_ID {named}"),
            (None, Some(fallback)) => format!("GUILD_ID {fallback} (HOME_GUILD_ID not set)"),
            (None, None) => "the only guild this bot is in, if there is exactly one (neither \
                             HOME_GUILD_ID nor GUILD_ID set)"
                .to_owned(),
        }
    }
}

impl AccessPrimer for HomeGuildMembers {
    fn prime(&self, user_id: u64) -> BoxFuture<'_, ()> {
        Box::pin(self.prime_user(user_id))
    }
}
