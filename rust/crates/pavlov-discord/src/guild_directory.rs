//! The guilds the bot is in, and direct messages (C# `IGuildDirectory.cs`): what the security
//! alerts and `/guildinvites` need from the gateway, as a seam the tests fake.

use std::time::Duration;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::number::group_thousands;

/// An invite to one guild, or why there is none.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GuildInvite {
    pub guild_id: u64,
    pub guild_name: String,
    pub member_count: i64,
    pub url: Option<String>,
    pub problem: Option<String>,
}

/// The gateway's view of the guilds, and its direct messages.
pub trait GuildDirectory: Send + Sync {
    /// One invite per guild the bot is in.
    fn invite_to_every_guild(
        &self,
        max_age: Duration,
        max_uses: u32,
    ) -> BoxFuture<'_, Vec<GuildInvite>>;

    /// `true` when Discord accepted the DM. `false` is a closed inbox or no shared server;
    /// `Err` is anything else.
    fn send_direct_message(
        &self,
        user_id: u64,
        embed: Embed,
    ) -> BoxFuture<'_, Result<bool, crate::interaction::DiscordError>>;
}

/// Invites admit somebody to servers other people administer, so they expire on their own.
pub const INVITE_LIFETIME: Duration = Duration::from_secs(24 * 3600);
pub const INVITE_USES: u32 = 1;

/// The invite list in embed-sized pages (C# `GuildInvitesCommand.Pages`). Guilds WITHOUT an
/// invite are listed with the reason: the bot is in them whether or not it may invite anyone.
/// The expiry is said once, on the first page, and the first page is cut short enough to hold
/// it (the C# packed a full page and then prefixed it).
#[must_use]
pub fn invite_pages(invites: &[GuildInvite], lifetime: Duration) -> Vec<String> {
    let lines: Vec<String> = invites
        .iter()
        .map(|i| {
            let name = Sanitize::code(Some(&i.guild_name));
            let members = group_thousands(i.member_count);
            match i.url.as_deref().filter(|u| !u.is_empty()) {
                Some(url) => format!("{} **{name}** ({members}) — {url}", theme::OK),
                None => format!(
                    "{} **{name}** ({members}) — {}",
                    theme::BAD,
                    Sanitize::code(Some(i.problem.as_deref().unwrap_or("no invite")))
                ),
            }
        })
        .collect();
    let header = format!(
        "Single-use, expire in {}h.\n\n",
        lifetime.as_secs().div_ceil(3600)
    );
    let limit = theme::MAX_DESCRIPTION - header.chars().count();
    let mut pages = theme::paginate(lines.iter().map(String::as_str), limit);
    if let Some(first) = pages.first_mut() {
        first.insert_str(0, &header);
    }
    pages
}
