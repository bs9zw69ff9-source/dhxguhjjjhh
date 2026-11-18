//! `/whitelist` (C# `WhitelistCommand`) and its wipe confirmation (C# `WhitelistWipe`).
//!
//! THE DISCORD USER IS THE HANDLE EVERYWHERE BUT `add`: adding somebody is the one moment their
//! in-game name is unknown to the bot, so it is asked for once, recorded against the account,
//! and never typed again. THE ROSTER FILE IS THE AUTHORITY, NOT THE INDEX: an index entry
//! outlives a hand-edited roster, and acting on it would move somebody who is not in the
//! faction. Every reply is private.

use std::sync::Arc;

use chrono::Utc;
use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::embed::Embed;
use pavlov_core::factions::{FactionDefinition, MembershipDecision, MembershipOutcome};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{cmp_ignore_case, eq_ignore_case};
use pavlov_host::factions::roster::RosterService;
use pavlov_host::factions::{FactionMember, FactionMembers, RosterWipe};
use pavlov_host::moderation::AuditLog;

use super::membership_reply;
use super::whitelist::{LIST_PAGE_LENGTH, describe, roster_lines, subclass_lines};
use crate::access::{Access, RequiredAccess};
use crate::boards::Boards;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::component::{ActionRow, Button, ButtonStyle};
use crate::interaction::{ComponentInvocation, ComponentResponder, Reply};
use crate::paged::{ComponentHandler, Paged};
use crate::prefixes::WHITELIST_WIPE;
use crate::slash::{CommandContext, CommandError, SlashCommand};

fn code(text: &str) -> String {
    Sanitize::code(Some(text))
}

fn mention(id: u64) -> String {
    format!("<@{id}>")
}

fn ranks_list(faction: &FactionDefinition) -> String {
    faction
        .order
        .iter()
        .map(|r| format!("**{r}**"))
        .collect::<Vec<_>>()
        .join(", ")
}

/// What `/whitelist` is built from.
pub struct WhitelistCommand {
    pub rosters: Arc<RosterService>,
    pub members: Arc<FactionMembers>,
    pub access: Arc<Access>,
    pub boards: Arc<Boards>,
    pub audit: Arc<AuditLog>,
    pub paged: Option<Arc<Paged>>,
}

impl std::fmt::Debug for WhitelistCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WhitelistCommand").finish_non_exhaustive()
    }
}

fn faction_option(names: &[String]) -> OptionSpec {
    names.iter().fold(
        OptionSpec::new(OptionKind::String, "faction", "Which faction").required(),
        |option, name| option.choice(Choice::string(name.clone(), name.clone())),
    )
}

fn member_option() -> OptionSpec {
    OptionSpec::new(OptionKind::User, "member", "The Discord account").required()
}

impl WhitelistCommand {
    fn faction(&self, context: &CommandContext<'_>) -> Option<FactionDefinition> {
        self.rosters
            .factions()
            .get(context.invocation.string("faction"))
            .cloned()
    }

    async fn reply(context: &CommandContext<'_>, embed: Embed) -> Result<(), CommandError> {
        context.reply(Reply::embed(embed.brand(None))).await
    }

    async fn refuse(
        &self,
        context: &CommandContext<'_>,
        required: RequiredAccess,
    ) -> Result<bool, CommandError> {
        if self.access.allows(required, &context.invocation.caller) {
            return Ok(false);
        }
        Self::reply(
            context,
            theme::denied(
                "Not allowed",
                Some(
                    &self
                        .access
                        .explain_refusal(&context.invocation.caller, required),
                ),
            ),
        )
        .await?;
        Ok(true)
    }

    async fn not_yours(
        &self,
        context: &CommandContext<'_>,
        faction: &FactionDefinition,
    ) -> Result<bool, CommandError> {
        if self
            .access
            .can_manage(&context.invocation.caller, &faction.name)
        {
            return Ok(false);
        }
        Self::reply(
            context,
            theme::denied(
                "Not your roster",
                Some(&format!(
                    "You do not manage the **{}** whitelist.",
                    faction.name
                )),
            ),
        )
        .await?;
        Ok(true)
    }

    async fn pages(
        &self,
        context: &CommandContext<'_>,
        title: &str,
        lines: &[String],
    ) -> Result<(), CommandError> {
        let pages = theme::paginate(lines.iter().map(String::as_str), LIST_PAGE_LENGTH);
        if let Some(paged) = &self.paged {
            return Ok(paged
                .send(
                    context.responder,
                    context.invocation.caller.id,
                    title,
                    pages,
                )
                .await?);
        }
        let footer =
            (pages.len() > 1).then(|| format!("Showing the first of {} pages", pages.len()));
        context
            .reply(Reply::embed(
                theme::notice(title, pages.first().map(String::as_str)).brand(footer.as_deref()),
            ))
            .await
    }

    async fn read_only(&self, context: &CommandContext<'_>, sub: &str) -> Result<(), CommandError> {
        let Some(faction) = self.faction(context) else {
            return Self::reply(context, theme::failure("Unknown faction", None)).await;
        };
        if sub == "playtime" {
            return Self::reply(context, self.playtime(&faction)).await;
        }
        let roster = self.rosters.roster(&faction);
        let subclasses = self.rosters.subclasses(&faction);
        let (title, lines) = if sub == "list" {
            (
                format!("{} whitelist · {} member(s)", faction.name, roster.len()),
                roster_lines(&faction, &roster, &subclasses),
            )
        } else {
            (
                format!("{} sub-classes · {} held", faction.name, subclasses.len()),
                subclass_lines(&faction, &roster, &subclasses),
            )
        };
        self.pages(context, &title, &lines).await
    }

    fn playtime(&self, faction: &FactionDefinition) -> Embed {
        let roster = self.rosters.roster(faction);
        if roster.is_empty() {
            return theme::notice(
                &format!("{} playtime", faction.name),
                Some("Nobody is on this roster."),
            );
        }
        let playtime = self.boards.playtime();
        let mut rows: Vec<(String, i64)> = roster
            .iter()
            .map(|m| {
                (
                    m.player.clone(),
                    playtime.get(&m.player).map_or(0, |e| e.minutes),
                )
            })
            .collect();
        rows.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| cmp_ignore_case(&a.0, &b.0)));
        let lines: Vec<String> = rows
            .iter()
            .enumerate()
            .map(|(i, (player, minutes))| {
                let time = if *minutes >= 60 {
                    format!("{}h {}m", minutes / 60, minutes % 60)
                } else {
                    format!("{minutes}m")
                };
                format!("`{:>2}.` **{}** — {time}", i + 1, code(player))
            })
            .collect();
        let pages = theme::paginate(lines.iter().map(String::as_str), theme::MAX_DESCRIPTION);
        let footer =
            (pages.len() > 1).then(|| format!("Showing the first of {} pages", pages.len()));
        let embed = theme::notice(
            &format!("{} playtime — {} member(s)", faction.name, roster.len()),
            pages.first().map(String::as_str),
        );
        match footer {
            Some(footer) => embed.footer(footer),
            None => embed,
        }
    }

    async fn add(&self, context: &CommandContext<'_>, member: u64) -> Result<(), CommandError> {
        let invocation = context.invocation;
        let Some(faction) = self.faction(context) else {
            return Self::reply(context, theme::failure("Unknown faction", None)).await;
        };
        if self.not_yours(context, &faction).await? {
            return Ok(());
        }
        let player = Sanitize::id(invocation.string("ingame_name"));
        if player.is_empty() {
            return Self::reply(
                context,
                theme::failure("That name has nothing usable in it", None),
            )
            .await;
        }
        // CHECKED BEFORE ANYTHING IS WRITTEN: a bad rank found after the join would leave them
        // added at the bottom with the command reporting a failure.
        let requested = invocation
            .string("rank")
            .map(str::trim)
            .unwrap_or("")
            .to_owned();
        if !requested.is_empty() {
            if !faction.has_ranks() {
                return Self::reply(
                    context,
                    theme::failure(
                        &format!("{} has no ranks", faction.name),
                        Some(&format!(
                            "**{}** is spawn access only - there is no ladder to start anybody on. Leave the rank out.",
                            faction.name
                        )),
                    ),
                )
                .await;
            }
            if !faction.order.iter().any(|r| eq_ignore_case(r, &requested)) {
                return Self::reply(
                    context,
                    theme::failure(
                        "No such rank",
                        Some(&format!(
                            "**{}** has no rank called **{}**.\n\nIts ranks are: {}.",
                            faction.name,
                            code(&requested),
                            ranks_list(&faction)
                        )),
                    ),
                )
                .await;
            }
        }
        let result = self.rosters.join(&faction, &player).await;
        let joined = matches!(
            result.outcome,
            MembershipOutcome::Allowed | MembershipOutcome::NoChange
        );
        // NOT SUPPLIED IS NOT FALSE: leaving the box unticked must not overwrite a preference.
        let existing = self.members.of(member);
        let hold_ranks = invocation
            .boolean("hold_ranks")
            .or(existing.as_ref().map(|e| e.holds_all_ranks))
            .unwrap_or(false);
        // RECORDED ON A NO-CHANGE TOO, which is what makes the preference settable at all.
        if joined {
            let record = FactionMember {
                faction: faction.name.clone(),
                name: if result.outcome == MembershipOutcome::Allowed {
                    player.clone()
                } else {
                    existing
                        .as_ref()
                        .map_or_else(|| player.clone(), |e| e.name.clone())
                },
                at: existing.as_ref().map_or_else(Utc::now, |e| e.at),
                by: existing
                    .as_ref()
                    .map_or_else(|| invocation.username.clone(), |e| e.by.clone()),
                holds_all_ranks: hold_ranks,
            };
            self.members.remember(member, record).await?;
        }
        // AFTER the membership is recorded: placing the rank reads the hold-ranks preference.
        let mut rank = result.rank.clone();
        let mut moved = false;
        if !requested.is_empty() && joined {
            let placed = self.rosters.set_rank(&faction, &player, &requested).await;
            if matches!(
                placed.outcome,
                MembershipOutcome::Allowed | MembershipOutcome::NoChange
            ) {
                rank = placed.rank.clone();
                moved = placed.outcome == MembershipOutcome::Allowed;
            } else {
                tracing::warn!(player = %player, faction = %faction.name, rank = %requested, outcome = ?placed.outcome, "whitelist add: added but could not be placed");
                let embed = membership_reply::fallback(&placed).field(
                    format!("{} They were still added", theme::WARN),
                    format!(
                        "**{}** is on the **{}** roster at **{}**. Set the rank with `/whitelist setrank`.",
                        code(&player),
                        faction.name,
                        result.rank.as_deref().unwrap_or("")
                    ),
                    false,
                );
                return Self::reply(context, embed).await;
            }
        }
        tracing::info!(member, player = %player, faction = %faction.name, rank = ?rank, by = %invocation.username, outcome = ?result.outcome, "whitelist add");
        if result.outcome == MembershipOutcome::Allowed || moved {
            let detail = format!("{} {}", faction.name, rank.as_deref().unwrap_or(""))
                .trim()
                .to_owned();
            self.audit
                .record(
                    "whitelist-add",
                    &invocation.username,
                    &player,
                    Some(&detail),
                )
                .await?;
        }
        // THE RANK THEY ENDED AT, and "nothing to do" stops being true when a rank moved them.
        let mut reply = if moved && result.outcome == MembershipOutcome::NoChange {
            theme::success(
                "Whitelist updated",
                Some(&format!(
                    "**{}** — {} **{}**",
                    code(&player),
                    faction.name,
                    rank.as_deref().unwrap_or("")
                )),
            )
        } else {
            describe(
                &MembershipDecision {
                    rank: rank.clone(),
                    ..result.clone()
                },
                &player,
                &faction,
            )
        };
        // STATED EITHER WAY: off is what somebody reporting it "not working" is looking at.
        if joined {
            let (name, value) = if hold_ranks {
                (
                    "Holds every rank",
                    "They keep the ranks below too, so they hold every loadout up to their own. A demotion still takes back anything above.",
                )
            } else {
                (
                    "Holds one rank",
                    "A promotion moves them up and takes the rank below away. Run this again with `hold_ranks:true` to keep them all.",
                )
            };
            reply = reply.field(name, value, false);
        }
        Self::reply(context, reply).await
    }

    async fn set_rank(
        &self,
        context: &CommandContext<'_>,
        member: u64,
    ) -> Result<(), CommandError> {
        // GATED FIRST: resolving the member discloses their name and may delete a stale entry.
        if self.refuse(context, RequiredAccess::FactionLeader).await? {
            return Ok(());
        }
        let Some(faction) = self.faction(context) else {
            return Self::reply(context, theme::failure("Unknown faction", None)).await;
        };
        if self.not_yours(context, &faction).await? {
            return Ok(());
        }
        if !faction.has_ranks() {
            return Self::reply(
                context,
                theme::failure(
                    &format!("{} has no ranks", faction.name),
                    Some(&format!(
                        "**{}** is spawn access only - there is no ladder to place {} on.",
                        faction.name,
                        mention(member)
                    )),
                ),
            )
            .await;
        }
        let Some(recorded) = self.members.of(member) else {
            return Self::reply(
                context,
                theme::failure(
                    "No membership on record",
                    Some(&format!(
                        "{} has no faction on record. Add them with `/whitelist add` first.",
                        mention(member)
                    )),
                ),
            )
            .await;
        };
        let player = recorded.name;
        let Some(before) = self.rosters.find(&player) else {
            self.members.forget(member).await?;
            return Self::reply(
                context,
                theme::failure(
                    "Not whitelisted",
                    Some(&format!(
                        "{} is recorded as **{}**, who is not on any roster. The stale record has been cleared.",
                        mention(member),
                        code(&player)
                    )),
                ),
            )
            .await;
        };
        if !eq_ignore_case(&before.faction, &faction.name) {
            // REFUSED RATHER THAN MOVED: switching faction would strip the old roster.
            return Self::reply(
                context,
                theme::failure(
                    "Wrong faction",
                    Some(&format!(
                        "{} is in **{}**, not **{}**. Use `/whitelist remove` then `/whitelist add` to move them.",
                        mention(member),
                        before.faction,
                        faction.name
                    )),
                ),
            )
            .await;
        }
        let rank = context.invocation.string("rank").unwrap_or("").to_owned();
        let decision = self.rosters.set_rank(&faction, &player, &rank).await;
        tracing::info!(player = %player, faction = %faction.name, by = %context.invocation.username, outcome = ?decision.outcome, rank = ?decision.rank, "setrank");
        let after = decision.rank.clone().unwrap_or_default();
        if decision.outcome == MembershipOutcome::Allowed {
            let detail = format!("{} {} -> {after}", faction.name, before.rank);
            self.audit
                .record(
                    "setrank",
                    &context.invocation.username,
                    &player,
                    Some(&detail),
                )
                .await?;
        }
        let embed = match decision.outcome {
            MembershipOutcome::Allowed => theme::success(
                &format!("{} Rank set", theme::RANK),
                Some(&format!(
                    "**{}** — {} → **{after}**",
                    code(&player),
                    before.rank
                )),
            ),
            MembershipOutcome::NoChange => theme::notice(
                "Already there",
                Some(&format!(
                    "**{}** is already **{after}**. Nothing was written.",
                    code(&player)
                )),
            ),
            MembershipOutcome::NoSuchRank => theme::failure(
                "No such rank",
                Some(&format!(
                    "**{}** has no rank called **{}**.\n\nIts ranks are: {}.",
                    faction.name,
                    code(&rank),
                    ranks_list(&faction)
                )),
            ),
            _ => membership_reply::fallback(&decision),
        };
        Self::reply(context, embed).await
    }

    async fn remove(&self, context: &CommandContext<'_>, member: u64) -> Result<(), CommandError> {
        // Gated up front: resolving the member tells the caller whether they are whitelisted.
        if self.refuse(context, RequiredAccess::FactionLeader).await? {
            return Ok(());
        }
        let Some(recorded) = self.members.of(member) else {
            return Self::reply(
                context,
                theme::failure(
                    "No membership on record",
                    Some(&format!(
                        "{} has no faction on record. They may still be whitelisted in game under a name the bot \
                         does not know — `/whitelist add` links the two, then remove.",
                        mention(member)
                    )),
                ),
            )
            .await;
        };
        let Some(faction) = self
            .rosters
            .factions()
            .get(Some(&recorded.faction))
            .cloned()
        else {
            return Self::reply(
                context,
                theme::failure(
                    "That faction is gone",
                    Some(&format!(
                        "{} is recorded in **{}**, which no longer exists.",
                        mention(member),
                        code(&recorded.faction)
                    )),
                ),
            )
            .await;
        };
        if self.not_yours(context, &faction).await? {
            return Ok(());
        }
        let result = self.rosters.leave(&faction, &recorded.name).await;
        self.members.forget(member).await?;
        tracing::info!(member, player = %recorded.name, faction = %faction.name, by = %context.invocation.username, outcome = ?result.outcome, "whitelist remove");
        if result.outcome == MembershipOutcome::Allowed {
            self.audit
                .record(
                    "whitelist-remove",
                    &context.invocation.username,
                    &recorded.name,
                    Some(&faction.name),
                )
                .await?;
        }
        Self::reply(context, describe(&result, &recorded.name, &faction)).await
    }

    async fn offer_wipe(&self, context: &CommandContext<'_>) -> Result<(), CommandError> {
        let Some(faction) = self.faction(context) else {
            return Self::reply(context, theme::failure("Unknown faction", None)).await;
        };
        if self.refuse(context, RequiredAccess::Owner).await? {
            return Ok(());
        }
        let roster = self.rosters.roster(&faction);
        if roster.is_empty() {
            return Self::reply(
                context,
                theme::notice(
                    "Nothing to wipe",
                    Some(&format!("**{}** has no members.", faction.name)),
                ),
            )
            .await;
        }
        let embed = theme::denied(
            &format!("Wipe the {} whitelist?", faction.name),
            Some(&format!(
                "Clears **{}** member(s) from every **{}** roster file, spawn file included. They lose access \
                 in game as soon as it is written.\n\nBackups are kept on the server, so it is recoverable by \
                 hand — not from Discord.",
                roster.len(),
                faction.name
            )),
        );
        context
            .reply(
                Reply::embed(embed.brand(None))
                    .with_components(wipe_controls(&faction.name, context.invocation.caller.id)),
            )
            .await
    }
}

impl SlashCommand for WhitelistCommand {
    fn name(&self) -> &str {
        "whitelist"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        // From the registry, so adding a faction is not also an edit here.
        let names: Vec<String> = self.rosters.factions().names().map(str::to_owned).collect();
        let faction = || faction_option(&names);
        let rank = |description: &str, required: bool| {
            let option = OptionSpec::new(OptionKind::String, "rank", description).autocomplete();
            if required { option.required() } else { option }
        };
        CommandSpec::new("whitelist", "Manage a faction whitelist")
            .option(
                OptionSpec::sub("add", "Whitelist Leader - Add a member to a faction")
                    .option(faction())
                    .option(member_option())
                    .option(
                        OptionSpec::new(OptionKind::String, "ingame_name", "Their exact in-game name")
                            .required()
                            .autocomplete(),
                    )
                    .option(rank("Start them here instead of the bottom rank", false))
                    .option(OptionSpec::new(
                        OptionKind::Boolean,
                        "hold_ranks",
                        "Keep every rank at or below theirs on promotion, not just the one they hold",
                    )),
            )
            .option(
                OptionSpec::sub("setrank", "Whitelist Leader - Put a member at a rank directly")
                    .option(faction())
                    .option(member_option())
                    .option(rank("Which rank - suggestions follow the faction you picked", true)),
            )
            .option(OptionSpec::sub("remove", "Whitelist Leader - Remove a member from their faction").option(member_option()))
            .option(OptionSpec::sub("list", "Show a faction's roster").option(faction()))
            .option(OptionSpec::sub("subclasses", "Who holds each of a faction's sub-classes").option(faction()))
            .option(OptionSpec::sub("playtime", "Whitelisted members' playtime, highest to lowest").option(faction()))
            .option(OptionSpec::sub("wipe", "Owner - Clear every member from a faction's whitelist").option(faction()))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let sub = context.invocation.subcommand().unwrap_or("").to_owned();
            match sub.as_str() {
                "list" | "subclasses" | "playtime" => return self.read_only(&context, &sub).await,
                "wipe" => return self.offer_wipe(&context).await,
                _ => {}
            }
            let Some(member) = context.invocation.id("member") else {
                return Self::reply(&context, theme::failure("No member given", None)).await;
            };
            match sub.as_str() {
                "remove" => self.remove(&context, member).await,
                "setrank" => self.set_rank(&context, member).await,
                _ => self.add(&context, member).await,
            }
        })
    }
}

const CONFIRM: &str = "go";
const CANCEL: &str = "no";

/// The wipe prompt's buttons, bound to the faction and to whoever asked.
#[must_use]
pub fn wipe_controls(faction: &str, owner: u64) -> Vec<ActionRow> {
    let who = owner.to_string();
    let id = |action: &str| {
        ComponentId::encode(WHITELIST_WIPE, &[action, faction, &who]).unwrap_or_default()
    };
    vec![ActionRow::Buttons(vec![
        Button::new(ButtonStyle::Danger, "Wipe the roster", id(CONFIRM)),
        Button::new(ButtonStyle::Secondary, "Cancel", id(CANCEL)),
    ])]
}

/// The wipe confirmation's handler.
pub struct WhitelistWipe {
    pub rosters: Arc<RosterService>,
    pub members: Arc<FactionMembers>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
}

impl std::fmt::Debug for WhitelistWipe {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WhitelistWipe").finish_non_exhaustive()
    }
}

fn wipe_reply(result: &RosterWipe, faction: &FactionDefinition) -> Embed {
    match result.outcome {
        MembershipOutcome::Allowed => theme::success(
            &format!("{} whitelist wiped", faction.name),
            Some(&format!(
                "**{}** member(s) cleared from every **{}** roster file. A pre-write copy of each file is in the backup directory.",
                result.removed, faction.name
            )),
        ),
        MembershipOutcome::RosterUnavailable => membership_reply::fallback(&MembershipDecision {
            outcome: MembershipOutcome::RosterUnavailable,
            rank: None,
            conflict: None,
        }),
        // NAMED, not counted: the list says which rosters still hold members.
        _ => theme::warning(
            "Partly wiped",
            Some(&format!(
                "**{}** cleared. These files could not be written and still hold members: {}.\n\nThe log has the reason. \
                 Run the wipe again after fixing it.",
                result.removed,
                result
                    .failed
                    .iter()
                    .map(|f| format!("`{}`", code(f)))
                    .collect::<Vec<_>>()
                    .join(", ")
            )),
        ),
    }
}

impl WhitelistWipe {
    /// The press handler, callable directly (the router goes through [`ComponentHandler`]).
    pub async fn press_for_test(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) {
        self.press(invocation, responder).await;
    }

    async fn close(responder: &dyn ComponentResponder, embed: Embed) {
        if let Err(error) = responder.update(Reply::embed(embed.brand(None))).await {
            tracing::warn!(%error, "could not close the wipe prompt");
        }
    }

    async fn press(&self, invocation: &ComponentInvocation, responder: &dyn ComponentResponder) {
        let id = ComponentId::parse(&invocation.custom_id);
        let owner = id
            .argument(2)
            .and_then(|o| o.parse::<u64>().ok())
            .unwrap_or(0);
        // THE PROMPT IS VISIBLE TO THE CHANNEL; only whoever asked may finish it.
        if invocation.caller.id != owner {
            let _ = responder
                .respond(
                    Reply::text("That confirmation belongs to whoever ran the command."),
                    true,
                )
                .await;
            return;
        }
        // RE-CHECKED ON THE PRESS: an owner can have been demoted since the prompt.
        if !self
            .access
            .allows(RequiredAccess::Owner, &invocation.caller)
        {
            let refusal = self
                .access
                .explain_refusal(&invocation.caller, RequiredAccess::Owner);
            let _ = responder.respond(Reply::text(refusal), true).await;
            return;
        }
        let Some(faction) = self.rosters.factions().get(id.argument(1)).cloned() else {
            return Self::close(
                responder,
                theme::failure(
                    "That faction is gone",
                    Some("It was removed from the bot between the prompt and the press. Nothing was changed."),
                ),
            )
            .await;
        };
        if id.argument(0) != Some(CONFIRM) {
            return Self::close(
                responder,
                theme::notice(
                    "Cancelled",
                    Some(&format!(
                        "The **{}** whitelist was not touched.",
                        faction.name
                    )),
                ),
            )
            .await;
        }
        // ACKNOWLEDGE BEFORE THE WORK, and take the buttons away: a confirm still pressable
        // during the wipe is a second wipe waiting for a double-click.
        Self::close(
            responder,
            theme::notice(
                "Wiping…",
                Some(&format!("Clearing the **{}** whitelist.", faction.name)),
            ),
        )
        .await;
        let result = self.rosters.wipe(&faction).await;
        // THE INDEX FOLLOWS THE FILES, even on a partial wipe.
        let forgotten = self
            .members
            .forget_faction(&faction.name)
            .await
            .unwrap_or(0);
        let detail = format!("{} member(s) cleared", result.removed);
        if let Err(error) = self
            .audit
            .record(
                "whitelist-wipe",
                &invocation.username,
                &faction.name,
                Some(&detail),
            )
            .await
        {
            tracing::error!(%error, "could not record the wipe");
        }
        tracing::warn!(faction = %faction.name, by = %invocation.username, removed = result.removed, records = forgotten, outcome = ?result.outcome, "whitelist wipe");
        if let Err(error) = responder
            .edit_original(Reply::embed(wipe_reply(&result, &faction).brand(None)))
            .await
        {
            tracing::warn!(%error, "could not report the wipe");
        }
    }
}

impl ComponentHandler for WhitelistWipe {
    fn prefix(&self) -> &str {
        WHITELIST_WIPE
    }
    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        Box::pin(self.press(invocation, responder))
    }
}
