//! `/givemoney`, `/adjustmoney` and `/wages` (C# `EconomyCommands.cs`).
//!
//! - Give only ever ADDS: a minus is refused rather than its absolute value taken, because a
//!   moderator who typed one meant something and guessing which is worse than asking.
//! - A failed write is reported with ITS REASON, never with the intended balance: the usual
//!   cause is a connected player, whose file the game rewrites from memory.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::skin::{lore, police, theme};
use pavlov_core::text::Sanitize;
use pavlov_host::economy::payroll::{Payroll, SharedLedger};
use pavlov_host::moderation::AuditLog;
use pavlov_storage::ledger_store::LedgerFileStore;

use crate::access::{Access, RequiredAccess, refusal};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// `/givemoney` (Mod, adds only) or `/adjustmoney` (Admin, either way).
pub struct MoneyCommand {
    name: &'static str,
    allow_negative: bool,
    ledger: Arc<SharedLedger>,
    audit: Arc<AuditLog>,
    access: Arc<Access>,
    /// The ledger files, when that is the store: they can say WHY a write was refused.
    files: Option<Arc<LedgerFileStore>>,
}

impl std::fmt::Debug for MoneyCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MoneyCommand")
            .field("name", &self.name)
            .finish_non_exhaustive()
    }
}

fn signed(amount: i64) -> String {
    if amount > 0 {
        format!("+{}", lore::amount(amount))
    } else {
        lore::amount(amount)
    }
}

impl MoneyCommand {
    #[must_use]
    pub fn give(
        ledger: Arc<SharedLedger>,
        audit: Arc<AuditLog>,
        access: Arc<Access>,
        files: Option<Arc<LedgerFileStore>>,
    ) -> Self {
        Self {
            name: police().words.give_money_command,
            allow_negative: false,
            ledger,
            audit,
            access,
            files,
        }
    }

    #[must_use]
    pub fn adjust(
        ledger: Arc<SharedLedger>,
        audit: Arc<AuditLog>,
        access: Arc<Access>,
        files: Option<Arc<LedgerFileStore>>,
    ) -> Self {
        Self {
            name: police().words.adjust_money_command,
            allow_negative: true,
            ledger,
            audit,
            access,
            files,
        }
    }

    fn required(&self) -> RequiredAccess {
        if self.allow_negative {
            RequiredAccess::Admin
        } else {
            RequiredAccess::Mod
        }
    }

    async fn run(&self, context: &CommandContext<'_>) -> Result<Embed, CommandError> {
        let invocation = context.invocation;
        let player = Sanitize::id(invocation.string("playerid"));
        let amount = invocation.integer("amount").unwrap_or(0);
        if player.is_empty() {
            return Ok(theme::failure("That name has nothing usable in it", None));
        }
        if amount == 0 {
            return Ok(theme::failure(
                "Nothing to move",
                Some("An amount of zero does nothing."),
            ));
        }
        let words = &police().words;
        if !self.allow_negative && amount < 0 {
            return Ok(theme::failure(
                "That is a deduction, not a payout",
                Some(&format!(
                    "`/{}` only ever adds. Use `/{}` to correct a balance downwards.",
                    words.give_money_command, words.adjust_money_command
                )),
            ));
        }
        let change = self.ledger.credit(&player, amount).await;
        if !change.ok {
            let why = self
                .files
                .as_ref()
                .and_then(|f| f.refusal(&player))
                .unwrap_or_else(|| "their ledger did not accept the write".into());
            return Ok(theme::failure(
                "Not applied",
                Some(&format!(
                    "**{}**'s balance is unchanged — {why}.",
                    Sanitize::code(Some(&player))
                )),
            ));
        }
        let detail = if amount > 0 {
            format!("+{amount}")
        } else {
            amount.to_string()
        };
        self.audit
            .record(self.name, &invocation.username, &player, Some(&detail))
            .await?;
        tracing::info!(command = self.name, player = %player, before = change.before, after = change.after, by = %invocation.username, "money moved");
        Ok(theme::success(
            &format!("{} {}", words.money_glyph, words.money_moved),
            Some(&format!(
                "**{}** — {}",
                Sanitize::code(Some(&player)),
                signed(amount)
            )),
        )
        .field("Was", lore::amount(change.before), true)
        .field("Now", lore::amount(change.after), true)
        .field("By", invocation.username.clone(), true))
    }
}

impl SlashCommand for MoneyCommand {
    fn name(&self) -> &str {
        self.name
    }

    fn build(&self) -> CommandSpec {
        let (description, amount) = if self.allow_negative {
            (
                "Admin - Correct a player's balance up or down",
                "How much to add, or a negative number to take away",
            )
        } else {
            ("Mod - Give money to a player", "How much to give")
        };
        CommandSpec::new(self.name, description)
            .option(
                OptionSpec::new(OptionKind::String, "playerid", "Player ID or username")
                    .required()
                    .autocomplete(),
            )
            .option(OptionSpec::new(OptionKind::Integer, "amount", amount).required())
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let required = self.required();
            let embed = if self.access.allows(required, &context.invocation.caller) {
                self.run(&context).await?
            } else {
                theme::denied("Not allowed", Some(&refusal(required)))
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

/// `/wages`: what an on-duty officer has earned, banked and unbanked, and WHY unbanked money
/// is held, so it never reads as money gone missing.
pub struct WagesCommand {
    pub payroll: Arc<Payroll>,
}

impl std::fmt::Debug for WagesCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WagesCommand").finish_non_exhaustive()
    }
}

/// The wages card, from the payroll's own figures.
#[must_use]
pub fn wages_card(payroll: &Payroll, player: &str) -> Embed {
    let period = payroll.period().as_secs() / 60;
    let carried = payroll.earned_toward_next_period(player).as_secs() / 60;
    theme::notice(
        &format!("{} Wages — {}", police().words.money_glyph, Sanitize::code(Some(player))),
        None,
    )
    .field("Earned, not yet banked", lore::amount(payroll.owed_to(player)), true)
    .field("Banked to date", lore::amount(payroll.total_paid_to(player)), true)
    .field("Toward the next payment", format!("{carried} of {period} minutes"), true)
    .field(
        "How pay works",
        format!(
            "**{}** per **{period}** minutes on duty. Part periods carry forward rather than rounding.\n\nIt banks to \
             the in-game balance when they go off duty.",
            lore::amount(payroll.amount())
        ),
        false,
    )
}

impl SlashCommand for WagesCommand {
    fn name(&self) -> &str {
        "wages"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "wages",
            "What an on-duty officer has earned, banked and unbanked",
        )
        .option(
            OptionSpec::new(OptionKind::String, "player", "Their in-game name")
                .required()
                .autocomplete(),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = if !self.payroll.enabled() {
                theme::warning(
                    "Payroll is off",
                    Some(
                        "`PAYROLL_AMOUNT` is unset or the faction rosters are unreachable, so nothing is being earned.",
                    ),
                )
            } else {
                let player = Sanitize::id(context.invocation.string("player"));
                if player.is_empty() {
                    theme::failure("That name has nothing usable in it", None)
                } else {
                    wages_card(&self.payroll, &player)
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
