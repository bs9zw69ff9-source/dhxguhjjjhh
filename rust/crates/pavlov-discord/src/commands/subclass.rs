//! `/subclass`'s picker (C# `SubclassCommand.SubclassChoices` and `ParseValue`): every
//! sub-class as a fixed choice, labelled with its faction, so two factions sharing a name
//! stay two distinct choices.

use pavlov_core::factions::FactionSet;
use pavlov_core::text::case::cmp_ignore_case;

/// Discord's choice limit per option.
pub const MAX_CHOICES: usize = 25;
/// Discord's limit on a choice's label and value.
pub const MAX_CHOICE_LENGTH: usize = 100;
/// Between the faction and the sub-class in a choice's value.
pub const VALUE_SEPARATOR: char = ':';

/// One picker entry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SubclassChoice {
    pub faction: String,
    pub name: String,
}

fn cut(text: String) -> String {
    if text.chars().count() <= MAX_CHOICE_LENGTH {
        text
    } else {
        text.chars().take(MAX_CHOICE_LENGTH).collect()
    }
}

impl SubclassChoice {
    /// `NCR - Patrol Ranger`.
    #[must_use]
    pub fn label(&self) -> String {
        cut(format!("{} - {}", self.faction, self.name))
    }

    /// `NCR:Patrol Ranger`.
    #[must_use]
    pub fn value(&self) -> String {
        cut(format!("{}{VALUE_SEPARATOR}{}", self.faction, self.name))
    }
}

/// Every sub-class, grouped by faction in faction order, sorted by name within one. A choice
/// whose label or value would repeat one already taken (only possible after truncation) is
/// left out, since Discord rejects the whole command over a duplicate.
#[must_use]
pub fn subclass_choices(factions: &FactionSet) -> Vec<SubclassChoice> {
    let mut labels = std::collections::HashSet::new();
    let mut values = std::collections::HashSet::new();
    let mut choices = Vec::new();
    for faction in factions.all() {
        let mut names: Vec<&String> = faction.subclasses.keys().collect();
        names.sort_by(|a, b| cmp_ignore_case(a, b));
        for name in names {
            let choice = SubclassChoice {
                faction: faction.name.clone(),
                name: name.clone(),
            };
            if labels.contains(&choice.label()) || values.contains(&choice.value()) {
                continue;
            }
            labels.insert(choice.label());
            values.insert(choice.value());
            choices.push(choice);
        }
    }
    choices
}

/// A choice's value back into its faction and name. A bare name, from a registration older
/// than the faction prefix, parses with no faction.
#[must_use]
pub fn parse_value(value: &str) -> (Option<String>, String) {
    match value.split_once(VALUE_SEPARATOR) {
        Some((faction, name)) => (Some(faction.trim().to_owned()), name.trim().to_owned()),
        None => (None, value.trim().to_owned()),
    }
}

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::factions::MembershipOutcome;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_host::factions::FactionMembers;
use pavlov_host::factions::roster::RosterService;

use super::membership_reply;
use crate::access::{Access, RequiredAccess};
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// `/subclass`: assign or remove a sub-class, BY DISCORD ACCOUNT like every command acting
/// on an existing member (a misspelt Pavlov name reads as "not whitelisted", not as a typo).
pub struct SubclassCommand {
    pub rosters: Arc<RosterService>,
    pub members: Arc<FactionMembers>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for SubclassCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SubclassCommand").finish_non_exhaustive()
    }
}

async fn reply(context: &CommandContext<'_>, embed: Embed) -> Result<(), CommandError> {
    context.reply(Reply::embed(embed.brand(None))).await
}

impl SubclassCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Result<(), CommandError> {
        let invocation = context.invocation;
        let caller = &invocation.caller;
        // GATED BEFORE ANYTHING IS READ OR WRITTEN.
        if !self.access.allows(RequiredAccess::FactionLeader, caller) {
            let refusal = self
                .access
                .explain_refusal(caller, RequiredAccess::FactionLeader);
            return reply(context, theme::denied("Not allowed", Some(&refusal))).await;
        }
        let (chosen_faction, subclass) = parse_value(invocation.string("subclass").unwrap_or(""));
        let removing = invocation.boolean("remove").unwrap_or(false);
        let Some(member) = invocation.id("member") else {
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
        // Two factions can share a name: the choice says whose sub-class it is.
        if let Some(chosen) = chosen_faction
            .as_deref()
            .filter(|c| !eq_ignore_case(c, &membership.faction))
        {
            return reply(
                context,
                theme::failure(
                    "Wrong faction's sub-class",
                    Some(&format!(
                        "That is a **{chosen}** sub-class; **{code}** is in **{}**.",
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
        let decision = self
            .rosters
            .change_subclass(&faction, &player, &subclass, removing)
            .await;
        tracing::info!(action = if removing { "remove" } else { "assign" }, player = %player, subclass = %subclass, by = %invocation.username, outcome = ?decision.outcome, "subclass");
        let embed = match decision.outcome {
            MembershipOutcome::Allowed => theme::success(
                if removing {
                    "Sub-class removed"
                } else {
                    "Sub-class assigned"
                },
                Some(&format!("**{code}** — {subclass}")),
            )
            .field("Rank", membership.rank.clone(), true),
            MembershipOutcome::AlreadyHasSubclass => theme::denied(
                "Already holds a sub-class",
                Some(&format!(
                    "**{code}** is **{}**. A member may hold at most one sub-class.",
                    decision.conflict.as_deref().unwrap_or("")
                )),
            ),
            MembershipOutcome::NoSuchSubclass => theme::failure(
                "Unknown sub-class",
                Some(&format!(
                    "**{}** does not define `{}`.",
                    faction.name,
                    Sanitize::code(Some(&subclass))
                )),
            ),
            MembershipOutcome::NoChange => theme::notice(
                "No change",
                Some(if removing {
                    "They do not hold it."
                } else {
                    "They already hold it."
                }),
            ),
            _ => membership_reply::fallback(&decision),
        };
        reply(context, embed).await
    }
}

impl SlashCommand for SubclassCommand {
    fn name(&self) -> &str {
        "subclass"
    }

    fn build(&self) -> CommandSpec {
        let choices = subclass_choices(self.rosters.factions());
        if choices.len() > MAX_CHOICES {
            let dropped: Vec<String> = choices
                .iter()
                .skip(MAX_CHOICES)
                .map(SubclassChoice::label)
                .collect();
            tracing::warn!(count = choices.len(), max = MAX_CHOICES, dropped = %dropped.join(", "), "/subclass has more sub-classes than Discord allows choices; these are left off the picker");
        }
        let subclass = choices.iter().take(MAX_CHOICES).fold(
            OptionSpec::new(OptionKind::String, "subclass", "Which sub-class").required(),
            |option, choice| option.choice(Choice::string(choice.label(), choice.value())),
        );
        CommandSpec::new(
            "subclass",
            "Whitelist Leader - Assign or remove a sub-class",
        )
        .option(OptionSpec::new(OptionKind::User, "member", "The Discord account").required())
        .option(subclass)
        .option(OptionSpec::new(
            OptionKind::Boolean,
            "remove",
            "Remove it instead of assigning",
        ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move { self.run(&context).await })
    }
}
