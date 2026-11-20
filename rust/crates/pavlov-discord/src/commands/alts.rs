//! `/alts` (C# `AltsCommand.cs`): accounts that share a CONFIRMED address with a player.
//! Evidence, not proof: households, tethering and shared VPN exits all put strangers on one
//! address, and every reply that finds something says so.

use std::cmp::Reverse;
use std::collections::HashSet;
use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::evasion::AccountRecord;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::logs::ip_tracking::IpTrackingService;

use crate::access::{Access, RequiredAccess};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The most linked accounts listed.
pub const MAX_ROWS: usize = 15;
/// How many names are shown before "+N".
const NAMES_SHOWN: usize = 3;

/// What the command is wired to.
pub struct AltsCommand {
    pub tracking: Arc<IpTrackingService>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for AltsCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AltsCommand").finish_non_exhaustive()
    }
}

fn label(account: &AccountRecord) -> &str {
    account
        .names
        .first()
        .map_or(account.id.as_str(), String::as_str)
}

fn names(account: &AccountRecord) -> String {
    if account.names.is_empty() {
        return "none recorded".to_owned();
    }
    let shown: Vec<String> = account
        .names
        .iter()
        .take(NAMES_SHOWN)
        .map(|n| Sanitize::code(Some(n)))
        .collect();
    let more = account.names.len().saturating_sub(NAMES_SHOWN);
    let suffix = if more > 0 {
        format!(" +{more}")
    } else {
        String::new()
    };
    format!("{}{suffix}", shown.join(", "))
}

fn seen(account: &AccountRecord) -> String {
    account
        .last_seen
        .map_or_else(|| "unknown".to_owned(), theme::relative)
}

/// The reply for `subject` and the accounts linked to it.
#[must_use]
pub fn report(subject: &AccountRecord, alts: &[AccountRecord]) -> Embed {
    let header = format!(
        "**{}**\n`{}`",
        Sanitize::code(Some(label(subject))),
        Sanitize::code(Some(&subject.id))
    );
    let facts = |embed: Embed| {
        embed
            .field(
                "Confirmed addresses",
                subject.confirmed_ips.len().to_string(),
                true,
            )
            .field("Known names", names(subject), true)
            .field("Seen", seen(subject), true)
    };
    if alts.is_empty() {
        return facts(theme::success("No shared addresses", Some(&header))).brand(None);
    }
    let shared: HashSet<&str> = subject.confirmed_ips.iter().map(String::as_str).collect();
    // Most overlapping addresses first: one is a household, four is the same connection.
    let mut rows: Vec<(&AccountRecord, usize)> = alts
        .iter()
        .map(|a| {
            (
                a,
                a.confirmed_ips
                    .iter()
                    .filter(|ip| shared.contains(ip.as_str()))
                    .count(),
            )
        })
        .collect();
    rows.sort_by_key(|(a, overlap)| (Reverse(*overlap), Reverse(a.last_seen)));
    let lines: Vec<String> = rows
        .iter()
        .take(MAX_ROWS)
        .map(|(a, overlap)| {
            format!(
                "**{}**\n　`{}` — {overlap} shared address(es), last seen {}",
                Sanitize::code(Some(label(a))),
                Sanitize::code(Some(&a.id)),
                seen(a)
            )
        })
        .collect();
    let mut embed = facts(theme::warning(
        &format!("{} linked account(s)", alts.len()),
        Some(&format!("{header}\n\n{}", lines.join("\n"))),
    ));
    if alts.len() > MAX_ROWS {
        embed = embed.field(
            "Not shown",
            format!(
                "{} more account(s) share an address.",
                alts.len() - MAX_ROWS
            ),
            false,
        );
    }
    // The one caution a shared address needs, kept to the footer.
    embed.brand(Some("Shared addresses are evidence, not proof"))
}

impl AltsCommand {
    fn answer(&self, raw: Option<&str>) -> Embed {
        let query = Sanitize::id(raw);
        if query.is_empty() {
            return theme::failure("Nothing to look up", None).brand(None);
        }
        // By id first, then by name: an account id looks nothing like a name.
        let Some(subject) = self
            .tracking
            .account(&query)
            .or_else(|| self.tracking.account_by_name(&query))
        else {
            return theme::notice(
                "Never seen",
                Some(&format!(
                    "No account on record for **{}**.",
                    Sanitize::code(Some(&query))
                )),
            )
            .brand(None);
        };
        report(&subject, &self.tracking.alts_of(&subject.id))
    }
}

impl SlashCommand for AltsCommand {
    fn name(&self) -> &str {
        "alts"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "alts",
            "Moderation - Accounts that share an address with this player",
        )
        .option(
            OptionSpec::new(OptionKind::String, "playerid", "Player ID or username")
                .required()
                .autocomplete(),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let caller = &context.invocation.caller;
            let embed = if self.access.allows(RequiredAccess::Mod, caller) {
                self.answer(context.invocation.string("playerid"))
            } else {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Mod)),
                )
                .brand(None)
            };
            context.reply(Reply::embed(embed)).await
        })
    }
}
