//! `/setroles` (C# `SetRolesCommand` in `ConfigCommands.cs`): map Discord roles to the bot's
//! tiers and to each faction's roster.
//!
//! - ONLY WHAT WAS SUPPLIED CHANGES: rebuilding the whole map from one invocation would clear
//!   every role the caller did not mention.
//! - The police slot is offered only while a police command is enabled: otherwise the role
//!   grants nothing and the option lies.
//! - Discord rejects the bulk overwrite WHOLE past 25 options, so the cap is enforced here
//!   and the factions left without an option are named.
//! - Each line says whether the bot can see the caller holding the role: a role the bot cannot
//!   see grants exactly as much as one never set, and the two look identical otherwise.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::factions::FactionSet;
use pavlov_core::roles::RoleMap;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{cmp_ignore_case, eq_ignore_case};
use pavlov_storage::datasets::ROLES;

use crate::access::{Access, RequiredAccess, RoleStanding};
use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::option_name::slug;
use crate::slash::{CommandContext, CommandError, SlashCommand};

const MAX_OPTIONS: usize = 25;
const FIXED_OPTIONS: usize = 3;

/// The commands `RequiredAccess::Police` gates. The police role does nothing without them.
pub const POLICE_COMMANDS: [&str; 4] = ["warrant", "arrest", "backgroundcheck", "bail"];

/// The command.
pub struct SetRolesCommand {
    store: Arc<SerializedStore>,
    access: Arc<Access>,
    faction_options: Vec<(String, String)>,
    unnameable: Vec<String>,
    police: bool,
}

impl std::fmt::Debug for SetRolesCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SetRolesCommand")
            .field("police", &self.police)
            .finish_non_exhaustive()
    }
}

fn standing(standing: RoleStanding) -> &'static str {
    match standing {
        RoleStanding::Held => "  ✅ *you hold this*",
        RoleStanding::NotHeld => "  ❌ *the bot does not see this on you*",
        RoleStanding::NoMember => "  ⚠️ *your roles could not be read*",
        RoleStanding::Unset => "",
    }
}

impl SetRolesCommand {
    /// `disabled` is `COMMANDS_DISABLED`'s whole-command entries.
    #[must_use]
    pub fn new(
        store: Arc<SerializedStore>,
        access: Arc<Access>,
        factions: &FactionSet,
        disabled: &[String],
    ) -> Self {
        let police = POLICE_COMMANDS
            .iter()
            .any(|name| !disabled.iter().any(|d| eq_ignore_case(d, name)));
        let mut names: Vec<&str> = factions.names().collect();
        names.sort_by(|a, b| cmp_ignore_case(a, b));
        let mut named: Vec<(String, String)> = Vec::new();
        let mut unnameable = Vec::new();
        for faction in names {
            match slug(Some(faction), "_role") {
                Some(option) if !named.iter().any(|(o, _)| eq_ignore_case(o, &option)) => {
                    named.push((option, faction.to_owned()));
                }
                _ => unnameable.push(faction.to_owned()),
            }
        }
        let room = MAX_OPTIONS - FIXED_OPTIONS - usize::from(police);
        if named.len() > room {
            unnameable.extend(named.drain(room..).map(|(_, f)| f));
        }
        if !unnameable.is_empty() {
            tracing::warn!(count = unnameable.len(), factions = %unnameable.join(", "), "setroles cannot offer a role option for these factions; their rosters are manageable only by the whitelist leader role");
        }
        Self {
            store,
            access,
            faction_options: named,
            unnameable,
            police,
        }
    }

    async fn run(&self, context: &CommandContext<'_>) -> Result<(), CommandError> {
        let invocation = context.invocation;
        let caller = &invocation.caller;
        if !self.access.allows(RequiredAccess::Owner, caller) {
            let refusal = self.access.explain_refusal(caller, RequiredAccess::Owner);
            return context
                .reply(Reply::embed(
                    theme::denied("Not allowed", Some(&refusal)).brand(None),
                ))
                .await;
        }
        let role = |name: &str| invocation.id(name);
        let options = self.faction_options.clone();
        let picked: Vec<(String, u64)> = options
            .iter()
            .filter_map(|(option, faction)| role(option).map(|id| (faction.clone(), id)))
            .collect();
        let (moderation, admin, leader, police) = (
            role("mod_role"),
            role("admin_role"),
            role("whitelist_leader_role"),
            role("police_role"),
        );
        let updated = self
            .store
            .update(ROLES, RoleMap::default, move |mut current: RoleMap| {
                for (faction, id) in picked {
                    current.faction_roles.insert(faction, id);
                }
                current.mod_role = moderation.or(current.mod_role);
                current.admin_role = admin.or(current.admin_role);
                current.faction_leader_role = leader.or(current.faction_leader_role);
                current.police_role = police.or(current.police_role);
                Some(current)
            })
            .await?;
        tracing::info!(by = %invocation.username, "setroles");
        let map = updated.value;
        let mut tiers: Vec<(String, Option<u64>)> = vec![
            ("Moderator".into(), map.mod_role),
            ("Admin".into(), map.admin_role),
            ("Whitelist leader".into(), map.faction_leader_role),
        ];
        if self.police {
            tiers.push(("Police".into(), map.police_role));
        }
        tiers.extend(
            self.faction_options
                .iter()
                .map(|(_, f)| (f.clone(), map.role_for(Some(f)))),
        );
        let lines: Vec<String> = tiers
            .iter()
            .map(|(label, id)| {
                let shown = id.map_or_else(|| "*not set*".to_owned(), |id| format!("<@&{id}>"));
                format!(
                    "**{label}** — {shown}{}",
                    standing(self.access.standing_on(caller, *id))
                )
            })
            .collect();
        let mut embed = theme::success("Roles updated", Some(&lines.join("\n"))).field(
            "Owners",
            "Set through the environment, never a role.",
            false,
        );
        match self.access.visible_roles(caller) {
            None => {
                embed = embed.field(
                    format!("{} The bot cannot read your roles here", theme::WARN),
                    "Nothing above can grant you anything in this context - only owner access, which goes by \
                     user id, is checked. In a DM your roles come from the staff guild; set `HOME_GUILD_ID` to \
                     the guild your staff roles live in.",
                    false,
                );
            }
            Some(visible) if visible.is_empty() => {
                embed = embed.field(
                    format!("{} The bot sees you with no roles at all", theme::WARN),
                    "It can see you as a member and reads zero roles, which usually means the **Server Members \
                     Intent** is off in the Discord developer portal.",
                    false,
                );
            }
            Some(_) => {}
        }
        if !self.unnameable.is_empty() {
            let names: Vec<String> = self
                .unnameable
                .iter()
                .map(|n| Sanitize::code(Some(n)))
                .collect();
            embed = embed.field(
                "No option available",
                format!(
                    "{} — manageable only by the whitelist leader role.",
                    names.join(", ")
                ),
                false,
            );
        }
        context.reply(Reply::embed(embed.brand(None))).await
    }
}

impl SlashCommand for SetRolesCommand {
    fn name(&self) -> &str {
        "setroles"
    }

    fn build(&self) -> CommandSpec {
        let role =
            |name: &str, description: &str| OptionSpec::new(OptionKind::Role, name, description);
        let mut spec = CommandSpec::new(
            "setroles",
            "Owner - Map Discord roles to the bot's permission tiers",
        )
        .option(role("mod_role", "Moderator"))
        .option(role("admin_role", "Admin"))
        .option(role("whitelist_leader_role", "Manages every whitelist"));
        if self.police {
            spec = spec.option(role("police_role", "Police officer"));
        }
        for (option, faction) in &self.faction_options {
            spec = spec.option(role(option, &format!("Manages the {faction} whitelist")));
        }
        spec
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move { self.run(&context).await })
    }
}
