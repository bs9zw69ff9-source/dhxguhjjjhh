//! `/license give` and `/license remove` (C# `LicenseCommand.cs`, which could only remove):
//! set a driver's or firearm licence in a player's save (`<name>.json`). Police only, and both
//! land in the police log. Works while the player is in game: the mod syncs the save live.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::licensing::{License, Licenses};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::moderation::AuditLog;
use pavlov_storage::ledger_store::{LedgerFileStore, LicenseOutcome};

use crate::access::{Access, RequiredAccess, refusal};
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

const REQUIRED: RequiredAccess = RequiredAccess::Police;

/// The audit action for a removal, which the staff log routes to the police channel.
pub const AUDIT_ACTION: &str = "license-remove";
/// The audit action for a give, routed the same way.
pub const GIVE_ACTION: &str = "license-give";

/// The command. `saves` is `None` on a bot that does not read the game's save files.
pub struct LicenseCommand {
    pub saves: Option<Arc<LedgerFileStore>>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
}

impl std::fmt::Debug for LicenseCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("LicenseCommand").finish_non_exhaustive()
    }
}

fn describe(licenses: &[License]) -> String {
    match licenses {
        [one] => format!("a {}", one.label.to_lowercase()),
        _ => "either license".to_owned(),
    }
}

async fn reply(context: &CommandContext<'_>, embed: Embed) -> Result<(), CommandError> {
    context.reply(Reply::embed(embed.brand(None))).await
}

impl LicenseCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Result<(), CommandError> {
        let invocation = context.invocation;
        if !self.access.allows(REQUIRED, &invocation.caller) {
            return reply(
                context,
                theme::denied("Not allowed", Some(&refusal(REQUIRED))),
            )
            .await;
        }
        let give = match invocation.subcommand() {
            Some("give") => true,
            Some("remove") => false,
            _ => return reply(context, theme::failure("Unknown subcommand", None)).await,
        };
        let player = Sanitize::id(invocation.string("player"));
        let licenses = Licenses::named(invocation.string("license"));
        let reason = invocation
            .string("reason")
            .map(str::trim)
            .filter(|r| !r.is_empty());
        if player.is_empty() {
            return reply(context, theme::failure("Invalid name", None)).await;
        }
        if licenses.is_empty() {
            let all: Vec<&str> = Licenses::ALL.iter().map(|l| l.label).collect();
            return reply(
                context,
                theme::failure(
                    "Pick a license",
                    Some(&format!("Choose one of: {}, or both.", all.join(", "))),
                ),
            )
            .await;
        }
        let Some(saves) = &self.saves else {
            return reply(
                context,
                theme::failure("Not available", Some("`MODSAVE_PATH` is not set.")),
            )
            .await;
        };
        let result = saves.set_licenses(&player, &licenses, give);
        let name = Sanitize::code(Some(&player));
        let (action, title, verb) = if give {
            (GIVE_ACTION, "License granted", "now holds")
        } else {
            (AUDIT_ACTION, "License revoked", "no longer holds")
        };
        match result.outcome {
            LicenseOutcome::Changed => {
                let removed: Vec<&str> = result.changed.iter().map(|l| l.label).collect();
                let removed = removed.join(", ");
                let detail = match reason {
                    Some(reason) => format!("{removed} | {reason}"),
                    None => removed.clone(),
                };
                self.audit
                    .record(action, &invocation.username, &player, Some(&detail))
                    .await?;
                tracing::info!(player = %player, licenses = %removed, by = %invocation.username, reason = reason.unwrap_or(""), action);
                let mut embed =
                    theme::success(title, Some(&format!("**{name}** {verb}: **{removed}**.")))
                        .field("By", invocation.username.clone(), true);
                if let Some(reason) = reason {
                    embed = embed.field("Reason", Sanitize::code(Some(reason)), true);
                }
                reply(context, embed).await
            }
            LicenseOutcome::AlreadySo => {
                let state = if give {
                    "already holds"
                } else {
                    "does not hold"
                };
                reply(
                    context,
                    theme::notice(
                        "No change",
                        Some(&format!("**{name}** {state} {}.", describe(&licenses))),
                    ),
                )
                .await
            }
            _ => {
                reply(
                    context,
                    theme::failure(
                        "Not applied",
                        Some(&format!(
                            "**{name}**'s licenses are unchanged — {}.",
                            result.reason.as_deref().unwrap_or("the change was refused")
                        )),
                    ),
                )
                .await
            }
        }
    }
}

impl SlashCommand for LicenseCommand {
    fn name(&self) -> &str {
        "license"
    }

    fn build(&self) -> CommandSpec {
        let sub = |name: &str, description: &str, which: &str| {
            let licenses = Licenses::ALL.iter().fold(
                OptionSpec::new(OptionKind::String, "license", which).required(),
                |option, license| option.choice(Choice::string(license.label, license.choice)),
            );
            OptionSpec::sub(name, description)
                .option(
                    OptionSpec::new(OptionKind::String, "player", "Their in-game name")
                        .required()
                        .autocomplete(),
                )
                .option(licenses.choice(Choice::string("Both", Licenses::EVERY)))
                .option(OptionSpec::new(
                    OptionKind::String,
                    "reason",
                    "Why, for the audit log",
                ))
        };
        CommandSpec::new("license", "Police - Player licenses")
            .option(sub(
                "give",
                "Police - Grant a player a driver's or firearm license",
                "Which license to grant",
            ))
            .option(sub(
                "remove",
                "Police - Revoke a player's driver's or firearm license",
                "Which license to take away",
            ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move { self.run(&context).await })
    }
}
