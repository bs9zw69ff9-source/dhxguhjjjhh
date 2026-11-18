//! `/staffactivity`, `/staffleaderboard`, `/banlist` and `/flush` (C# `StaffCommands.cs`;
//! `/subclass` lives with the faction commands, `/stripmenu` with the menu commands).

use std::cmp::Reverse;
use std::io::Read;
use std::sync::Arc;

use chrono::TimeDelta;
use futures_util::future::BoxFuture;
use indexmap::IndexMap;
use pavlov_core::embed::Embed;
use pavlov_core::moderation::BanRecord;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::time::Clock;
use pavlov_host::moderation::bans::{BanService, MasterNames};
use pavlov_host::moderation::{AuditLog, ModAction};
use pavlov_host::registry::RconRegistry;

use crate::access::{Access, RequiredAccess};
use crate::boards::Boards;
use crate::caller::Caller;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Recent actions listed by `/staffactivity`.
const RECENT_ACTIONS: usize = 10;

fn refuse(access: &Access, caller: &Caller, required: RequiredAccess) -> Option<Embed> {
    (!access.allows(required, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, required)),
        )
    })
}

// ---- /staffactivity ----

pub struct StaffActivityCommand {
    pub audit: Arc<AuditLog>,
    pub access: Arc<Access>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for StaffActivityCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StaffActivityCommand")
            .finish_non_exhaustive()
    }
}

/// One staff member's actions: by KIND first (what do they mostly do), then the recent detail.
/// `actions` is newest first.
#[must_use]
pub fn activity(username: &str, actions: &[ModAction]) -> Embed {
    let mut kinds: IndexMap<String, (String, usize)> = IndexMap::new();
    for action in actions {
        let entry = kinds
            .entry(action.action.to_lowercase())
            .or_insert_with(|| (action.action.clone(), 0));
        entry.1 += 1;
    }
    let mut counted: Vec<(String, usize)> = kinds.into_values().collect();
    counted.sort_by_key(|(_, n)| Reverse(*n));
    let by_kind: Vec<String> = counted
        .iter()
        .map(|(k, n)| format!("`{k}` × {n}"))
        .collect();
    let recent: Vec<String> = actions
        .iter()
        .take(RECENT_ACTIONS)
        .map(|a| {
            let why = a
                .reason
                .as_deref()
                .filter(|r| !r.is_empty())
                .map(|r| {
                    format!(
                        " — {}",
                        Sanitize::code(Some(&Sanitize::redact_private(Some(r))))
                    )
                })
                .unwrap_or_default();
            format!(
                "{} — `{}` **{}**{why}",
                theme::relative(a.at),
                a.action,
                Sanitize::code(Some(&a.player))
            )
        })
        .collect();
    theme::notice(
        &format!("{username} — {} action(s)", actions.len()),
        Some(&by_kind.join("  ")),
    )
    .field("Most recent", recent.join("\n"), false)
}

impl SlashCommand for StaffActivityCommand {
    fn name(&self) -> &str {
        "staffactivity"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("staffactivity", "Admin - Audit one staff member's actions")
            .option(OptionSpec::new(OptionKind::User, "staff", "Whose actions to list").required())
            .option(
                OptionSpec::new(
                    OptionKind::String,
                    "period",
                    "How far back (default: all time)",
                )
                .choice(Choice::string("7 days", "7d"))
                .choice(Choice::string("30 days", "30d"))
                .choice(Choice::string("All time", "all")),
            )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = if let Some(refusal) =
                refuse(&self.access, &invocation.caller, RequiredAccess::Admin)
            {
                refusal
            } else if let Some(staff) = invocation.user("staff") {
                let since = match invocation.string("period").unwrap_or("all") {
                    "7d" => Some(self.clock.now() - TimeDelta::days(7)),
                    "30d" => Some(self.clock.now() - TimeDelta::days(30)),
                    _ => None,
                };
                let actions = self.audit.by(&staff.username, since);
                if actions.is_empty() {
                    let period = if since.is_some() {
                        " in that period"
                    } else {
                        ""
                    };
                    theme::notice(
                        "No actions recorded",
                        Some(&format!(
                            "<@{}> has taken no moderation actions{period}.",
                            staff.id
                        )),
                    )
                } else {
                    activity(&staff.username, &actions)
                }
            } else {
                theme::failure("Need a staff member", None)
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /staffleaderboard ----

/// The SAME builder the auto-posted board uses: two views that can disagree is worse than one.
pub struct StaffLeaderboardCommand {
    pub boards: Arc<Boards>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for StaffLeaderboardCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StaffLeaderboardCommand")
            .finish_non_exhaustive()
    }
}

impl SlashCommand for StaffLeaderboardCommand {
    fn name(&self) -> &str {
        "staffleaderboard"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "staffleaderboard",
            "Admin - Rank staff by moderation actions",
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(
                &self.access,
                &context.invocation.caller,
                RequiredAccess::Admin,
            ) {
                Some(refusal) => refusal.brand(None),
                None => self
                    .boards
                    .build_staff_board()
                    .unwrap_or_else(|| theme::notice("No actions recorded yet", None).brand(None)),
            };
            context.reply(Reply::embed(embed)).await
        })
    }
}

// ---- /banlist ----

pub struct BanListCommand {
    pub bans: Arc<BanService>,
}

impl std::fmt::Debug for BanListCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BanListCommand").finish_non_exhaustive()
    }
}

/// Every ban in force: permanent first, then soonest to expire, since the ones about to lapse
/// are what a moderator scanning the list wants.
#[must_use]
pub fn ban_list(active: &[BanRecord]) -> Embed {
    if active.is_empty() {
        return theme::success("Nobody is banned", None).brand(None);
    }
    let mut sorted: Vec<&BanRecord> = active.iter().collect();
    sorted.sort_by_key(|b| {
        (
            !b.permanent,
            b.expires
                .unwrap_or(chrono::DateTime::<chrono::Utc>::MAX_UTC),
        )
    });
    let lines: Vec<String> = sorted
        .iter()
        .map(|b| {
            let when = if b.permanent {
                "**Permanent**".to_owned()
            } else {
                format!(
                    "expires {}",
                    b.expires
                        .map_or_else(|| "unknown".to_owned(), theme::relative)
                )
            };
            format!(
                "`{}` — {}\n{} {when} · by {}",
                Sanitize::code(Some(&b.player_id)),
                Sanitize::code(Some(&Sanitize::redact_private(Some(
                    b.reason.as_deref().unwrap_or("no reason")
                )))),
                theme::DOT,
                b.moderator.as_deref().unwrap_or("unknown")
            )
        })
        .collect();
    let pages = theme::paginate(lines.iter().map(String::as_str), theme::MAX_DESCRIPTION);
    let footer = (pages.len() > 1).then(|| format!("Page 1 of {}", pages.len()));
    theme::punishment(
        &format!("{} {} active ban(s)", theme::DENY, active.len()),
        pages.first().map(String::as_str),
    )
    .brand(footer.as_deref())
}

impl SlashCommand for BanListCommand {
    fn name(&self) -> &str {
        "banlist"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("banlist", "Show every ban currently in force")
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            context
                .reply(Reply::embed(ban_list(&self.bans.active_bans())))
                .await
        })
    }
}

// ---- /flush ----

/// A uniformly random index below `count`, from the OS random source. `None` when it cannot
/// be read: the flush is refused rather than made predictable.
fn random_index(count: usize) -> Option<usize> {
    let count = u32::try_from(count).ok().filter(|c| *c > 0)?;
    let mut source = std::fs::File::open("/dev/urandom").ok()?;
    let zone = u32::MAX - (u32::MAX % count);
    let mut bytes = [0u8; 4];
    loop {
        source.read_exact(&mut bytes).ok()?;
        let value = u32::from_le_bytes(bytes);
        if value < zone {
            return usize::try_from(value % count).ok();
        }
    }
}

/// `/flush`: kick ONE randomly chosen player to free a slot. Masters, protected and exempt
/// players are never chosen; a kick, never a ban.
pub struct FlushCommand {
    pub rcon: Arc<RconRegistry>,
    pub bans: Arc<BanService>,
    pub masters: Arc<dyn MasterNames>,
    pub audit: Arc<AuditLog>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for FlushCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("FlushCommand").finish_non_exhaustive()
    }
}

impl FlushCommand {
    async fn run(&self, username: &str, server: &str) -> Result<Embed, CommandError> {
        let roster = self.rcon.roster(server);
        if roster.taken_at.is_none() {
            return Ok(theme::failure(
                "No roster yet",
                Some(&format!(
                    "**{server}** has not reported who is online, so there is nobody to choose from."
                )),
            ));
        }
        let candidates: Vec<_> = roster
            .players
            .into_iter()
            .filter(|p| {
                !p.name.is_empty()
                    && !self.masters.is_master(&p.name)
                    && !self.masters.is_protected(&p.name)
                    && !self.masters.is_exempt(&p.name)
            })
            .collect();
        if candidates.is_empty() {
            return Ok(theme::notice(
                "Nobody to flush",
                Some("Everyone online is protected or the server is empty."),
            ));
        }
        let Some(chosen) = random_index(candidates.len()).and_then(|i| candidates.get(i)) else {
            return Ok(theme::failure(
                "Not kicked",
                Some("A player could not be chosen at random."),
            ));
        };
        let unique_id = Some(chosen.unique_id.as_str()).filter(|id| !id.is_empty());
        let result = self
            .bans
            .hard_enforce(&chosen.name, unique_id, false, true)
            .await;
        self.audit
            .record(
                "flush",
                username,
                &chosen.name,
                Some(&format!("slot freed on {server}")),
            )
            .await?;
        tracing::info!(server, chosen = %chosen.name, by = username, accepted = result.servers, "flush");
        Ok(if result.landed() {
            theme::success(
                "Slot freed",
                Some(&format!(
                    "**{}** was chosen at random and kicked from **{server}**.",
                    Sanitize::code(Some(&chosen.name))
                )),
            )
            .field(
                "Chosen from",
                format!("{} eligible player(s)", candidates.len()),
                true,
            )
        } else {
            theme::failure("Not kicked", Some("No server accepted the command."))
        })
    }
}

impl SlashCommand for FlushCommand {
    fn name(&self) -> &str {
        "flush"
    }

    fn build(&self) -> CommandSpec {
        let server = self.rcon.servers().into_iter().fold(
            OptionSpec::new(
                OptionKind::String,
                "server",
                "Which server to free a slot on",
            )
            .required(),
            |option, name| option.choice(Choice::string(name.clone(), name)),
        );
        CommandSpec::new(
            "flush",
            "Mod - Kick one randomly chosen player to free a slot",
        )
        .option(server)
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match refuse(&self.access, &invocation.caller, RequiredAccess::Mod) {
                Some(refusal) => refusal,
                None => {
                    self.run(
                        &invocation.username,
                        invocation.string("server").unwrap_or(""),
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
