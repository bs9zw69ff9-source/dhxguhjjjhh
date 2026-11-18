//! `/license remove` (C# `LicenseCommand.cs`): take a driver's or firearm licence out of a
//! player's ModSave file. Police only, and it lands in the police log.

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

/// The audit action, which the staff log routes to the police channel.
pub const AUDIT_ACTION: &str = "license-remove";

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
        if invocation.subcommand() != Some("remove") {
            return reply(context, theme::failure("Unknown subcommand", None)).await;
        }
        let player = Sanitize::id(invocation.string("player"));
        let licenses = Licenses::named(invocation.string("license"));
        let reason = invocation
            .string("reason")
            .map(str::trim)
            .filter(|r| !r.is_empty());
        if player.is_empty() {
            return reply(
                context,
                theme::failure("That name has nothing usable in it", None),
            )
            .await;
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
                theme::failure(
                    "Not available",
                    Some("This bot is not reading the game's save files, so it has no licenses to change."),
                ),
            )
            .await;
        };
        let result = saves.remove_licenses(&player, &licenses);
        let name = Sanitize::code(Some(&player));
        match result.outcome {
            LicenseOutcome::Removed => {
                let removed: Vec<&str> = result.removed.iter().map(|l| l.label).collect();
                let removed = removed.join(", ");
                let detail = match reason {
                    Some(reason) => format!("{removed} | {reason}"),
                    None => removed.clone(),
                };
                self.audit
                    .record(AUDIT_ACTION, &invocation.username, &player, Some(&detail))
                    .await?;
                tracing::info!(player = %player, licenses = %removed, by = %invocation.username, reason = reason.unwrap_or(""), "license remove");
                let mut embed = theme::success(
                    "License revoked",
                    Some(&format!("**{name}** no longer holds: **{removed}**.")),
                )
                .field("By", invocation.username.clone(), true);
                if let Some(reason) = reason {
                    embed = embed.field("Reason", Sanitize::code(Some(reason)), true);
                }
                reply(context, embed).await
            }
            LicenseOutcome::NoneHeld => {
                reply(
                    context,
                    theme::notice(
                        "Nothing to revoke",
                        Some(&format!(
                            "**{name}** does not hold {}. Their save file is unchanged.",
                            describe(&result.known)
                        )),
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
        let which = Licenses::ALL.iter().fold(
            OptionSpec::new(OptionKind::String, "license", "Which license to take away").required(),
            |option, license| option.choice(Choice::string(license.label, license.choice)),
        );
        CommandSpec::new("license", "Police - Player licenses").option(
            OptionSpec::sub(
                "remove",
                "Police - Revoke a player's driver's or firearm license",
            )
            .option(
                OptionSpec::new(OptionKind::String, "player", "Their in-game name")
                    .required()
                    .autocomplete(),
            )
            .option(which.choice(Choice::string("Both", Licenses::EVERY)))
            .option(OptionSpec::new(
                OptionKind::String,
                "reason",
                "Why, for the audit log",
            )),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move { self.run(&context).await })
    }
}
