//! `pavlov-discord`'s seams over Discord's REST API (C# `GatewayAutoPostTarget`,
//! `GatewayChannelRenamer`, `GatewayGuildDirectory`, the `HomeGuildMembers` fetch and the
//! command registration calls). Thin on purpose: what is worth testing lives behind the
//! traits; this is only the calls and the classification of their failures.

use std::sync::{Arc, PoisonError, RwLock};
use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_discord::caller::MemberView;
use pavlov_discord::command::CommandSpec;
use pavlov_discord::component::ActionRow;
use pavlov_discord::guild_directory::{GuildDirectory, GuildInvite};
use pavlov_discord::home_guild::MemberSource;
use pavlov_discord::interaction::DiscordError;
use pavlov_discord::player_count::{ChannelRenamer, RenameOutcome};
use pavlov_discord::post::{ChannelPoster, PostEdit};
use pavlov_discord::registration::{CommandRegistry, GuildRef};
use serenity::all::{
    Cache, Channel, ChannelId, ChannelType, Command, CreateInvite, CreateMessage, EditChannel,
    GuildChannel, GuildId, Http, MessageId, UserId,
};

use crate::convert::{self, CANNOT_DM, UNKNOWN_MESSAGE, http_failure};

/// Discord's "Unknown Member".
const UNKNOWN_MEMBER: isize = 10007;
/// Discord's "Unknown Channel".
const UNKNOWN_CHANNEL: isize = 10003;

/// How long a rename may wait. Discord allows two renames per channel per ten minutes, and
/// the HTTP client's rate limiter WAITS OUT a spent window: a third rename would block for up
/// to ten minutes, which is what once made `/counts` spin forever. A rename that cannot happen
/// now says so; the next tick applies the count anyway.
pub const RENAME_WAIT: Duration = Duration::from_secs(10);

fn discord_error(error: &serenity::Error) -> DiscordError {
    DiscordError(error.to_string())
}

/// Channel posts: the boards, the feeds, the command log.
pub struct RestPoster {
    http: Arc<Http>,
}

impl std::fmt::Debug for RestPoster {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("RestPoster")
    }
}

impl RestPoster {
    #[must_use]
    pub fn new(http: Arc<Http>) -> Self {
        Self { http }
    }
}

impl ChannelPoster for RestPoster {
    fn edit(
        &self,
        channel_id: u64,
        message_id: u64,
        embed: Embed,
        components: Vec<ActionRow>,
    ) -> BoxFuture<'_, PostEdit> {
        Box::pin(async move {
            let edit = convert::channel_edit(&embed, &components);
            match ChannelId::new(channel_id)
                .edit_message(&*self.http, MessageId::new(message_id), edit)
                .await
            {
                Ok(_) => PostEdit::Edited,
                // ONLY "Unknown Message" earns a re-post. A missing CHANNEL, a 403, a 500 or a
                // websocket blip say nothing about the message, and treating them as deleted
                // is what once posted a second board beside the first.
                Err(error) if http_failure(&error).is_some_and(|(_, c)| c == UNKNOWN_MESSAGE) => {
                    PostEdit::Missing
                }
                Err(error) => {
                    tracing::warn!(channel_id, message_id, %error,
                        "could not edit the board message; leaving it in place");
                    PostEdit::Failed
                }
            }
        })
    }

    fn send(
        &self,
        channel_id: u64,
        embed: Embed,
        components: Vec<ActionRow>,
    ) -> BoxFuture<'_, Result<Option<u64>, DiscordError>> {
        Box::pin(async move {
            let message: CreateMessage = convert::channel_message(&embed, &components);
            match ChannelId::new(channel_id)
                .send_message(&*self.http, message)
                .await
            {
                Ok(sent) => Ok(Some(sent.id.get())),
                Err(error) => match http_failure(&error) {
                    Some((403, _)) => {
                        tracing::warn!(
                            channel_id,
                            "cannot post: the bot needs View Channel, Send Messages and Embed Links there"
                        );
                        Ok(None)
                    }
                    Some((404, _)) | Some((_, UNKNOWN_CHANNEL)) => {
                        tracing::warn!(channel_id, "cannot post: no such channel");
                        Ok(None)
                    }
                    _ => Err(discord_error(&error)),
                },
            }
        })
    }
}

/// Voice-channel renames for the player counts.
pub struct RestRenamer {
    http: Arc<Http>,
    wait: Duration,
}

impl std::fmt::Debug for RestRenamer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("RestRenamer")
    }
}

impl RestRenamer {
    #[must_use]
    pub fn new(http: Arc<Http>) -> Self {
        Self {
            http,
            wait: RENAME_WAIT,
        }
    }
}

async fn guild_channel(http: &Http, channel_id: u64) -> Option<GuildChannel> {
    match ChannelId::new(channel_id).to_channel(http).await {
        Ok(Channel::Guild(channel)) => Some(channel),
        Ok(_) => None,
        Err(error) => {
            tracing::debug!(channel_id, %error, "could not read the channel");
            None
        }
    }
}

impl ChannelRenamer for RestRenamer {
    fn name_of(&self, channel_id: u64) -> BoxFuture<'_, Option<String>> {
        Box::pin(async move { guild_channel(&self.http, channel_id).await.map(|c| c.name) })
    }

    fn rename<'a>(&'a self, channel_id: u64, name: &'a str) -> BoxFuture<'a, RenameOutcome> {
        Box::pin(async move {
            let edit = EditChannel::new().name(name);
            let call = ChannelId::new(channel_id).edit(&*self.http, edit);
            match tokio::time::timeout(self.wait, call).await {
                Ok(Ok(_)) => RenameOutcome::Renamed,
                Err(_) => {
                    tracing::info!(
                        channel_id,
                        "channel rename is rate limited (two per ten minutes per channel); the next tick applies it"
                    );
                    RenameOutcome::RateLimited
                }
                Ok(Err(error)) => match http_failure(&error) {
                    Some((429, _)) => RenameOutcome::RateLimited,
                    Some((403, _)) => {
                        tracing::warn!(
                            channel_id,
                            "cannot rename the channel: the bot needs Manage Channels on it"
                        );
                        RenameOutcome::Forbidden
                    }
                    Some((404, _)) => RenameOutcome::NotVisible,
                    _ => {
                        tracing::warn!(channel_id, %error, "could not rename the channel");
                        RenameOutcome::Failed
                    }
                },
            }
        })
    }
}

/// Invites and direct messages.
pub struct RestDirectory {
    http: Arc<Http>,
    cache: Arc<Cache>,
}

impl std::fmt::Debug for RestDirectory {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("RestDirectory")
    }
}

impl RestDirectory {
    #[must_use]
    pub fn new(http: Arc<Http>, cache: Arc<Cache>) -> Self {
        Self { http, cache }
    }

    /// The first text channel the bot may view AND create invites in, read from the cache
    /// and cloned out (a cache guard must not be held across an await).
    fn invite_channel(&self, guild_id: GuildId) -> Result<(String, i64, ChannelId), String> {
        let me = self.cache.current_user().id;
        let Some(guild) = self.cache.guild(guild_id) else {
            return Err("the guild is not cached yet - try again in a moment".to_owned());
        };
        let name = guild.name.clone();
        let members = i64::try_from(guild.member_count).unwrap_or(i64::MAX);
        let Some(member) = guild.members.get(&me) else {
            return Err(
                "the bot's own membership is not cached yet - try again in a moment".to_owned(),
            );
        };
        let mut channels: Vec<&GuildChannel> = guild
            .channels
            .values()
            .filter(|c| c.kind == ChannelType::Text)
            .filter(|c| {
                let permissions = guild.user_permissions_in(c, member);
                permissions.view_channel() && permissions.create_instant_invite()
            })
            .collect();
        channels.sort_by_key(|c| c.position);
        channels
            .first()
            .map(|c| (name, members, c.id))
            .ok_or_else(|| {
                "no channel here lets the bot create an invite (needs Create Invite)".into()
            })
    }
}

impl GuildDirectory for RestDirectory {
    fn invite_to_every_guild(
        &self,
        max_age: Duration,
        max_uses: u32,
    ) -> BoxFuture<'_, Vec<GuildInvite>> {
        Box::pin(async move {
            let mut invites = Vec::new();
            for guild_id in self.cache.guilds() {
                let invite = |name: String, members: i64, url, problem| GuildInvite {
                    guild_id: guild_id.get(),
                    guild_name: name,
                    member_count: members,
                    url,
                    problem,
                };
                match self.invite_channel(guild_id) {
                    Err(problem) => {
                        let name = self
                            .cache
                            .guild(guild_id)
                            .map(|g| g.name.clone())
                            .unwrap_or_else(|| guild_id.to_string());
                        invites.push(invite(name, 0, None, Some(problem)));
                    }
                    Ok((name, members, channel)) => {
                        let request = CreateInvite::new()
                            .max_age(u32::try_from(max_age.as_secs()).unwrap_or(u32::MAX))
                            .max_uses(u8::try_from(max_uses).unwrap_or(u8::MAX))
                            .temporary(false)
                            .unique(true);
                        match channel.create_invite(&*self.http, request).await {
                            Ok(created) => {
                                invites.push(invite(name, members, Some(created.url()), None));
                            }
                            Err(error) => {
                                tracing::warn!(guild = guild_id.get(), %error, "could not create an invite");
                                invites.push(invite(name, members, None, Some(error.to_string())));
                            }
                        }
                    }
                }
            }
            invites
        })
    }

    fn send_direct_message(
        &self,
        user_id: u64,
        embed: Embed,
    ) -> BoxFuture<'_, Result<bool, DiscordError>> {
        Box::pin(async move {
            let channel = match UserId::new(user_id).create_dm_channel(&*self.http).await {
                Ok(channel) => channel,
                Err(error) => return closed_or_error(user_id, &error),
            };
            match channel
                .send_message(&*self.http, convert::channel_message(&embed, &[]))
                .await
            {
                Ok(_) => Ok(true),
                Err(error) => closed_or_error(user_id, &error),
            }
        })
    }
}

/// Closed DMs (and no shared server) are the ordinary case, not a fault.
fn closed_or_error(user_id: u64, error: &serenity::Error) -> Result<bool, DiscordError> {
    match http_failure(error) {
        Some((_, CANNOT_DM)) | Some((403, _)) => {
            tracing::warn!(
                user_id,
                "could not DM: their DMs are closed or no server is shared"
            );
            Ok(false)
        }
        _ => Err(discord_error(error)),
    }
}

/// Staff-guild membership, for [`pavlov_discord::home_guild::HomeGuildMembers`].
pub struct RestMembers {
    http: Arc<Http>,
    cache: Arc<Cache>,
}

impl std::fmt::Debug for RestMembers {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("RestMembers")
    }
}

impl RestMembers {
    #[must_use]
    pub fn new(http: Arc<Http>, cache: Arc<Cache>) -> Self {
        Self { http, cache }
    }
}

impl MemberSource for RestMembers {
    fn guild_ids(&self) -> Vec<u64> {
        self.cache.guilds().into_iter().map(GuildId::get).collect()
    }

    fn fetch_member(
        &self,
        guild_id: u64,
        user_id: u64,
    ) -> BoxFuture<'_, Result<Option<MemberView>, DiscordError>> {
        Box::pin(async move {
            match GuildId::new(guild_id)
                .member(&*self.http, UserId::new(user_id))
                .await
            {
                Ok(member) => Ok(Some(convert::member_view(&member))),
                Err(error) => match http_failure(&error) {
                    Some((_, UNKNOWN_MEMBER)) | Some((404, _)) => Ok(None),
                    _ => Err(discord_error(&error)),
                },
            }
        })
    }
}

/// The guilds one application is in: filled from READY (complete, unlike the cache at that
/// moment) and kept current by guild events.
#[derive(Debug, Default)]
pub struct KnownGuilds {
    guilds: RwLock<Vec<GuildRef>>,
}

impl KnownGuilds {
    pub fn replace(&self, guilds: Vec<GuildRef>) {
        *self.guilds.write().unwrap_or_else(PoisonError::into_inner) = guilds;
    }

    /// Add or rename one; returns true when it was not known before.
    pub fn upsert(&self, guild: GuildRef) -> bool {
        let mut guilds = self.guilds.write().unwrap_or_else(PoisonError::into_inner);
        match guilds.iter_mut().find(|g| g.id == guild.id) {
            Some(known) => {
                known.name = guild.name;
                false
            }
            None => {
                guilds.push(guild);
                true
            }
        }
    }

    pub fn remove(&self, guild_id: u64) {
        self.guilds
            .write()
            .unwrap_or_else(PoisonError::into_inner)
            .retain(|g| g.id != guild_id);
    }

    #[must_use]
    pub fn all(&self) -> Vec<GuildRef> {
        self.guilds
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    #[must_use]
    pub fn contains(&self, guild_id: u64) -> bool {
        self.guilds
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .iter()
            .any(|g| g.id == guild_id)
    }
}

/// Command registration for one application.
pub struct RestRegistry {
    http: Arc<Http>,
    guilds: Arc<KnownGuilds>,
}

impl std::fmt::Debug for RestRegistry {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("RestRegistry")
    }
}

impl RestRegistry {
    #[must_use]
    pub fn new(http: Arc<Http>, guilds: Arc<KnownGuilds>) -> Self {
        Self { http, guilds }
    }
}

impl CommandRegistry for RestRegistry {
    fn guilds(&self) -> Vec<GuildRef> {
        self.guilds.all()
    }

    fn overwrite_guild<'a>(
        &'a self,
        guild_id: u64,
        specs: &'a [CommandSpec],
    ) -> BoxFuture<'a, Result<(), DiscordError>> {
        Box::pin(async move {
            GuildId::new(guild_id)
                .set_commands(&*self.http, specs.iter().map(convert::command).collect())
                .await
                .map(|_| ())
                .map_err(|e| discord_error(&e))
        })
    }

    fn overwrite_global<'a>(
        &'a self,
        specs: &'a [CommandSpec],
    ) -> BoxFuture<'a, Result<(), DiscordError>> {
        Box::pin(async move {
            Command::set_global_commands(&*self.http, specs.iter().map(convert::command).collect())
                .await
                .map(|_| ())
                .map_err(|e| discord_error(&e))
        })
    }

    fn global_count(&self) -> BoxFuture<'_, Result<usize, DiscordError>> {
        Box::pin(async move {
            Command::get_global_commands(&*self.http)
                .await
                .map(|c| c.len())
                .map_err(|e| discord_error(&e))
        })
    }

    fn guild_count(&self, guild_id: u64) -> BoxFuture<'_, Result<usize, DiscordError>> {
        Box::pin(async move {
            GuildId::new(guild_id)
                .get_commands(&*self.http)
                .await
                .map(|c| c.len())
                .map_err(|e| discord_error(&e))
        })
    }
}

impl pavlov_discord::monitor_alerts::AlertPoster for RestPoster {
    fn post_alert(
        &self,
        channel_id: u64,
        role: Option<u64>,
        embed: Embed,
    ) -> BoxFuture<'_, Result<(), DiscordError>> {
        Box::pin(async move {
            let mut message = convert::channel_message(&embed, &[]);
            if let Some(role) = role {
                // Exactly this role may be pinged; nothing else in the message can.
                message = message.content(format!("<@&{role}>")).allowed_mentions(
                    serenity::all::CreateAllowedMentions::new()
                        .roles(vec![serenity::all::RoleId::new(role)]),
                );
            }
            ChannelId::new(channel_id)
                .send_message(&*self.http, message)
                .await
                .map(|_| ())
                .map_err(|e| discord_error(&e))
        })
    }
}
