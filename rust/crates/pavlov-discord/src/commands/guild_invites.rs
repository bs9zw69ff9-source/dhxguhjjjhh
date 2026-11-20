//! `/guildinvites` (C# `GuildInvitesCommand.cs`): DM the super owner an invite to every Discord
//! server the bot is in.
//!
//! - "GUILD", NOT "SERVER": everywhere else a server is a Pavlov server.
//! - THE RECIPIENT IS COMPILED IN (`OwnerGuard::SUPER_OWNER_ID`), never an option: a typed
//!   recipient would make this a way to mail working invites to anyone.
//! - The invites are fresh, single-use and expire in a day.
//! - When the DM does not land, the list goes in this (ephemeral, owner-only) reply instead,
//!   rather than invites being made and thrown away.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::security::OwnerGuard;
use pavlov_core::skin::theme;
use pavlov_host::moderation::AuditLog;

use crate::access::{Access, RequiredAccess};
use crate::command::CommandSpec;
use crate::guild_directory::{GuildDirectory, INVITE_LIFETIME, INVITE_USES, invite_pages};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// What the command is wired to.
pub struct GuildInvitesCommand {
    pub directory: Arc<dyn GuildDirectory>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
}

impl std::fmt::Debug for GuildInvitesCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GuildInvitesCommand")
            .finish_non_exhaustive()
    }
}

impl GuildInvitesCommand {
    async fn run(&self, username: &str) -> Result<Embed, CommandError> {
        let invites = self
            .directory
            .invite_to_every_guild(INVITE_LIFETIME, INVITE_USES)
            .await;
        if invites.is_empty() {
            return Ok(theme::notice("Not in any Discord servers", None));
        }
        let recipient = OwnerGuard::SUPER_OWNER_ID;
        self.audit
            .record(
                "guildinvites",
                username,
                &recipient.to_string(),
                Some(&format!("{} guild(s)", invites.len())),
            )
            .await?;
        tracing::warn!(user = %username, count = invites.len(), recipient, "GUILDINVITES");
        let pages = invite_pages(&invites, INVITE_LIFETIME);
        let title = format!("Discord servers this bot is in ({})", invites.len());
        let mut delivered = 0;
        for page in &pages {
            let embed = theme::notice(&title, Some(page)).brand(None);
            match self.directory.send_direct_message(recipient, embed).await {
                Ok(true) => delivered += 1,
                Ok(false) => {}
                Err(error) => tracing::warn!(%error, "could not DM the guild invites"),
            }
        }
        let hours = INVITE_LIFETIME.as_secs() / 3600;
        if delivered == pages.len() {
            let made = invites.iter().filter(|i| i.url.is_some()).count();
            return Ok(theme::success(
                &format!("Sent {} server(s)", invites.len()),
                Some(&format!(
                    "DM'd to <@{recipient}>. Single-use, expire in {hours}h."
                )),
            )
            .field(
                "Made an invite for",
                format!("{made} of {}", invites.len()),
                true,
            ));
        }
        let first = pages.first().map_or("", String::as_str);
        Ok(theme::warning(
            "Could not DM the super owner",
            Some(&format!(
                "<@{recipient}> did not receive it; their DMs are likely closed.\n\n{first}"
            )),
        ))
    }
}

impl SlashCommand for GuildInvitesCommand {
    fn name(&self) -> &str {
        "guildinvites"
    }

    /// It carries working invites, and is the fallback when the DM fails.
    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "guildinvites",
            "Owner - DM the super owner an invite to every Discord server this bot is in",
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::Owner, caller) {
                self.run(&context.invocation.username).await?
            } else {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Owner)),
                )
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
