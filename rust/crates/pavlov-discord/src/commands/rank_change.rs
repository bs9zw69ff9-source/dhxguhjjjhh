//! `/promotion`, `/demotion` (C# `RankChangeCommand`) and `/subclasses` (C#
//! `SubclassesCommand`). One class for both directions: a demotion is if anything the one
//! that must not misfire.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::factions::MembershipOutcome;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::factions::FactionMembers;
use pavlov_host::factions::roster::RosterService;
use pavlov_host::moderation::AuditLog;

use super::membership_reply;
use super::whitelist::{LIST_PAGE_LENGTH, subclass_lines};
use crate::access::{Access, RequiredAccess};
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::paged::Paged;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// `/promotion` or `/demotion`.
pub struct RankChangeCommand {
    rosters: Arc<RosterService>,
    members: Arc<FactionMembers>,
    access: Arc<Access>,
    audit: Arc<AuditLog>,
    name: &'static str,
    direction: i32,
}

impl std::fmt::Debug for RankChangeCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("RankChangeCommand")
            .field("name", &self.name)
            .finish_non_exhaustive()
    }
}

async fn reply(context: &CommandContext<'_>, embed: Embed) -> Result<(), CommandError> {
    context.reply(Reply::embed(embed.brand(None))).await
}

impl RankChangeCommand {
    #[must_use]
    pub fn promotion(
        rosters: Arc<RosterService>,
        members: Arc<FactionMembers>,
        access: Arc<Access>,
        audit: Arc<AuditLog>,
    ) -> Self {
        Self {
            rosters,
            members,
            access,
            audit,
            name: "promotion",
            direction: 1,
        }
    }

    #[must_use]
    pub fn demotion(
        rosters: Arc<RosterService>,
        members: Arc<FactionMembers>,
        access: Arc<Access>,
        audit: Arc<AuditLog>,
    ) -> Self {
        Self {
            rosters,
            members,
            access,
            audit,
            name: "demotion",
            direction: -1,
        }
    }

    async fn run(&self, context: &CommandContext<'_>) -> Result<(), CommandError> {
        let caller = &context.invocation.caller;
        // GATED BEFORE ANYTHING IS READ OR WRITTEN: resolving the member discloses their name
        // and deletes a stale entry, and neither is for somebody who manages nothing.
        if !self.access.allows(RequiredAccess::FactionLeader, caller) {
            let refusal = self
                .access
                .explain_refusal(caller, RequiredAccess::FactionLeader);
            return reply(context, theme::denied("Not allowed", Some(&refusal))).await;
        }
        let Some(member) = context.invocation.id("member") else {
            return reply(context, theme::failure("No member given", None)).await;
        };
        let Some(recorded) = self.members.of(member) else {
            return reply(
                context,
                theme::failure(
                    "No membership on record",
                    Some(&format!(
                        "<@{member}> has no faction on record. Add them with `/whitelist add` first."
                    )),
                ),
            )
            .await;
        };
        let player = recorded.name;
        let code = Sanitize::code(Some(&player));
        // THE ROSTER FILE IS THE AUTHORITY: a stale entry is dropped on the way past.
        let Some(membership) = self.rosters.find(&player) else {
            self.members.forget(member).await?;
            return reply(
                context,
                theme::failure(
                    "Not whitelisted",
                    Some(&format!(
                        "<@{member}> is recorded as **{code}**, who is not on any roster. The record was cleared."
                    )),
                ),
            )
            .await;
        };
        if !self.access.can_manage(caller, &membership.faction) {
            return reply(
                context,
                theme::denied(
                    "Not your roster",
                    Some(&format!(
                        "You do not manage the **{}** whitelist.",
                        membership.faction
                    )),
                ),
            )
            .await;
        }
        let Some(faction) = self
            .rosters
            .factions()
            .get(Some(&membership.faction))
            .cloned()
        else {
            return reply(context, theme::failure("That faction is gone", None)).await;
        };
        if !faction.has_ranks() {
            return reply(
                context,
                theme::failure(
                    &format!("{} has no ranks", faction.name),
                    Some(&format!(
                        "**{}** is spawn access only. Use `/whitelist remove` to take access away.",
                        faction.name
                    )),
                ),
            )
            .await;
        }
        let decision = self
            .rosters
            .change_rank(&faction, &player, self.direction)
            .await;
        let after = decision.rank.clone().unwrap_or_default();
        tracing::info!(command = self.name, player = %player, faction = %faction.name, by = %context.invocation.username, outcome = ?decision.outcome, rank = %after, "rank change");
        if decision.outcome == MembershipOutcome::Allowed {
            let detail = format!("{} {} -> {after}", faction.name, membership.rank);
            self.audit
                .record(
                    self.name,
                    &context.invocation.username,
                    &player,
                    Some(&detail),
                )
                .await?;
        }
        let embed = match decision.outcome {
            MembershipOutcome::Allowed => theme::success(
                if self.direction > 0 {
                    "Promoted"
                } else {
                    "Demoted"
                },
                Some(&format!("**{code}** — {} → **{after}**", membership.rank)),
            ),
            MembershipOutcome::AlreadyHighest => theme::notice(
                "Already at the top",
                Some(&format!(
                    "**{code}** is **{after}**, the highest {} rank.",
                    faction.name
                )),
            ),
            MembershipOutcome::AlreadyLowest => theme::notice(
                "Already at the bottom",
                Some(&format!(
                    "**{code}** is **{after}**. Use `/whitelist remove` instead."
                )),
            ),
            _ => membership_reply::fallback(&decision),
        };
        reply(context, embed).await
    }
}

impl SlashCommand for RankChangeCommand {
    fn name(&self) -> &str {
        self.name
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let way = if self.direction > 0 { "up" } else { "down" };
        CommandSpec::new(
            self.name,
            format!("Whitelist Leader - Move a member one rank {way}"),
        )
        .option(OptionSpec::new(OptionKind::User, "member", "The Discord account").required())
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move { self.run(&context).await })
    }
}

/// `/subclasses`: who holds each of a faction's sub-classes.
pub struct SubclassesCommand {
    pub rosters: Arc<RosterService>,
    pub paged: Arc<Paged>,
}

impl std::fmt::Debug for SubclassesCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SubclassesCommand").finish_non_exhaustive()
    }
}

impl SlashCommand for SubclassesCommand {
    fn name(&self) -> &str {
        "subclasses"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        // Only factions that have sub-classes are offered.
        let factions = self.rosters.factions();
        let option = factions.all().filter(|f| !f.subclasses.is_empty()).fold(
            OptionSpec::new(OptionKind::String, "faction", "Which faction").required(),
            |o, f| o.choice(Choice::string(f.name.clone(), f.name.clone())),
        );
        CommandSpec::new("subclasses", "Who holds each of a faction's sub-classes").option(option)
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let Some(faction) = self
                .rosters
                .factions()
                .get(context.invocation.string("faction"))
                .cloned()
            else {
                return reply(&context, theme::failure("Unknown faction", None)).await;
            };
            let roster = self.rosters.roster(&faction);
            let held = self.rosters.subclasses(&faction);
            let lines = subclass_lines(&faction, &roster, &held);
            let pages = theme::paginate(lines.iter().map(String::as_str), LIST_PAGE_LENGTH);
            Ok(self
                .paged
                .send(
                    context.responder,
                    context.invocation.caller.id,
                    &format!("{} sub-classes · {} held", faction.name, held.len()),
                    pages,
                )
                .await?)
        })
    }
}
