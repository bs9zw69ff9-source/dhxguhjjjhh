//! `/givemenu`, `/stripmenu` and `/unlinkname` (C# `MenuCommands.cs`, moved off RCON+ on
//! 2026-11-18): in-game menu access is now a plain whitelist, five tier files in
//! `ModSave/ServerRoles` (see [`MenuTier`]), plus the permanent binding of a Discord account to
//! one in-game name.
//!
//! - ONE BINDING DECISION ([`MenuLink::decide`]): a member is bound for good to the first name
//!   they were given, so a menu cannot be cycled onto an alt; `/unlinkname` exists for a real
//!   name change.
//! - A PLAYER HOLDS ONE TIER. Giving a tier writes them into its file and takes them out of
//!   the other four, so the files never disagree about what somebody has.
//! - NOTHING IS RECORDED WHEN NOTHING LANDED: the grant is stored only after the tier file
//!   was written.
//! - Owners alone touch `owner` and `manager`: granting them, moving somebody out of them, or
//!   stripping them.
//!
//! The self-serve menu panel and `/setrconroles` went with RCON+: a staff member now picks the
//! tier.

use std::sync::Arc;

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use indexmap::IndexMap;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::moderation::{MenuClaimAction, MenuClaimDecision, MenuLink, MenuTier};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_core::time::Clock;
use pavlov_host::factions::RosterService;
use pavlov_storage::datasets::{MENU_GRANTS, MENU_LINKS};
use serde::{Deserialize, Serialize};

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// A Discord account bound for good to one in-game name (stored camelCase, the time in epoch
/// milliseconds).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MenuBinding {
    #[serde(default)]
    pub discord_id: String,
    #[serde(default)]
    pub in_game_name: String,
    #[serde(with = "pavlov_core::json::epoch_ms")]
    pub bound_at: DateTime<Utc>,
}

type Bindings = IndexMap<String, MenuBinding>;
/// Discord id to the in-game name that holds a tier (the stored shape is unchanged).
type Grants = IndexMap<String, String>;

/// Why a tier change did not land.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TierWriteFailed(pub Vec<String>);

/// The menu state and the tier files, shared by the three commands.
pub struct MenuAccess {
    /// The rosters service over `FACTION_ROLES_PATH` (`ModSave/ServerRoles`), which holds the
    /// tier files beside the rank files: one write path, one per-file lock, one guard.
    pub rosters: Arc<RosterService>,
    pub store: Arc<SerializedStore>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for MenuAccess {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MenuAccess").finish_non_exhaustive()
    }
}

impl MenuAccess {
    fn bindings(&self) -> Bindings {
        self.store
            .read(MENU_LINKS, Bindings::new())
            .unwrap_or_default()
    }

    fn grants(&self) -> Grants {
        self.store
            .read(MENU_GRANTS, Grants::new())
            .unwrap_or_default()
    }

    /// Whether the tier files can be written at all.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.rosters.enabled()
    }

    /// The binding decision.
    #[must_use]
    pub fn decide(&self, self_id: &str, requested: &str) -> MenuClaimDecision {
        let bindings = self.bindings();
        let bound = bindings.get(self_id).map(|b| b.in_game_name.clone());
        let owner = bindings
            .values()
            .find(|b| eq_ignore_case(&b.in_game_name, requested))
            .map(|b| b.discord_id.clone());
        MenuLink::decide(
            bound.as_deref(),
            Some(requested),
            owner.as_deref(),
            self_id,
            self.grants().contains_key(self_id),
        )
    }

    /// The name `self_id` is bound to.
    #[must_use]
    pub fn bound_name(&self, self_id: &str) -> Option<String> {
        self.bindings().get(self_id).map(|b| b.in_game_name.clone())
    }

    /// The name holding `self_id`'s grant.
    #[must_use]
    pub fn granted_name(&self, self_id: &str) -> Option<String> {
        self.grants().get(self_id).cloned()
    }

    /// The highest tier a name is listed in.
    #[must_use]
    pub fn tier_of(&self, name: &str) -> Option<MenuTier> {
        MenuTier::ALL
            .into_iter()
            .find(|t| self.rosters.file_holds(&t.file(), name))
    }

    /// Put `name` in `tier` and in no other.
    ///
    /// # Errors
    /// The files that could not be written. The tier file is written FIRST, so a failure to
    /// clear another tier never leaves somebody with no menu at all.
    pub async fn place(&self, name: &str, tier: MenuTier) -> Result<(), TierWriteFailed> {
        let names = [name.to_owned()];
        if self.rosters.add_names(&tier.file(), &names).await.is_none() {
            return Err(TierWriteFailed(vec![tier.file()]));
        }
        let mut failed = Vec::new();
        for other in MenuTier::ALL.into_iter().filter(|t| *t != tier) {
            if self
                .rosters
                .remove_names(&other.file(), &names)
                .await
                .is_none()
            {
                failed.push(other.file());
            }
        }
        if failed.is_empty() {
            Ok(())
        } else {
            Err(TierWriteFailed(failed))
        }
    }

    /// Take `name` out of every tier: how many files held it.
    ///
    /// # Errors
    /// The files that could not be written.
    pub async fn remove(&self, name: &str) -> Result<usize, TierWriteFailed> {
        let names = [name.to_owned()];
        let mut removed = 0;
        let mut failed = Vec::new();
        for tier in MenuTier::ALL {
            match self.rosters.remove_names(&tier.file(), &names).await {
                Some(n) => removed += usize::from(n > 0),
                None => failed.push(tier.file()),
            }
        }
        if failed.is_empty() {
            Ok(removed)
        } else {
            Err(TierWriteFailed(failed))
        }
    }

    /// Record (or with `None`, drop) a member's grant.
    ///
    /// # Errors
    /// When the store fails.
    pub async fn set_grant(&self, self_id: &str, player: Option<&str>) -> Result<(), CommandError> {
        self.store
            .update(MENU_GRANTS, Grants::new, |mut grants: Grants| {
                match player {
                    Some(p) => {
                        grants.insert(self_id.to_owned(), p.to_owned());
                    }
                    None => {
                        grants.shift_remove(self_id);
                    }
                }
                Some(grants)
            })
            .await?;
        Ok(())
    }

    /// Bind a member to a name for good.
    ///
    /// # Errors
    /// When the store fails.
    pub async fn bind(&self, self_id: &str, name: &str) -> Result<(), CommandError> {
        let binding = MenuBinding {
            discord_id: self_id.to_owned(),
            in_game_name: name.to_owned(),
            bound_at: self.clock.now(),
        };
        self.store
            .update(MENU_LINKS, Bindings::new, |mut links: Bindings| {
                links.insert(self_id.to_owned(), binding);
                Some(links)
            })
            .await?;
        Ok(())
    }
}

fn refuse(access: &Access, caller: &Caller) -> Option<Embed> {
    (!access.allows(RequiredAccess::Admin, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, RequiredAccess::Admin)),
        )
    })
}

fn owners_only(tier: MenuTier, what: &str) -> Embed {
    theme::denied(
        "Owners only",
        Some(&format!(
            "Only a bot owner can {what} the **{}** menu.",
            tier.label()
        )),
    )
}

fn not_written(failed: &TierWriteFailed) -> Embed {
    theme::failure(
        "Not written",
        Some(&format!(
            "The menu files could not be updated ({}). Check that `FACTION_ROLES_PATH` points at \
             `ModSave/ServerRoles` and that the bot can write there.",
            failed.0.join(", ")
        )),
    )
}

fn unavailable() -> Embed {
    theme::failure(
        "Not available",
        Some(
            "`FACTION_ROLES_PATH` is not set or does not exist, so there are no menu files to \
             write. It should point at `ModSave/ServerRoles`.",
        ),
    )
}

// ---- /givemenu ----

pub struct GiveMenuCommand {
    pub menus: Arc<MenuAccess>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for GiveMenuCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GiveMenuCommand").finish_non_exhaustive()
    }
}

impl GiveMenuCommand {
    async fn run(
        &self,
        caller: &Caller,
        username: &str,
        member: Option<u64>,
        raw: Option<&str>,
        tier: Option<MenuTier>,
    ) -> Result<Embed, CommandError> {
        let requested = Sanitize::id(raw);
        let (Some(member), Some(tier)) = (member.filter(|_| !requested.is_empty()), tier) else {
            return Ok(theme::failure(
                "Need a member, a usable in-game name and a tier",
                None,
            ));
        };
        let is_owner = self.access.is_owner(caller);
        if !tier.may_grant(is_owner, true) {
            return Ok(owners_only(tier, "give"));
        }
        let menus = &self.menus;
        if !menus.enabled() {
            return Ok(unavailable());
        }
        let self_id = member.to_string();
        let bound = menus.bound_name(&self_id);
        let decision = menus.decide(&self_id, &requested);
        match decision.action {
            MenuClaimAction::Locked => {
                return Ok(theme::denied(
                    "Bound to another name",
                    Some(&format!(
                        "<@{member}> is permanently bound to `{}`. Use `/unlinkname` if they genuinely changed their \
                         Pavlov name.",
                        Sanitize::code(decision.bound_name.as_deref())
                    )),
                ));
            }
            MenuClaimAction::Taken => {
                return Ok(theme::denied(
                    "That name belongs to someone else",
                    Some(&format!(
                        "`{}` is bound to <@{}>.",
                        Sanitize::code(Some(&requested)),
                        decision.owner_id.unwrap_or_default()
                    )),
                ));
            }
            // Giving a tier to somebody who already has one moves them; it never toggles off.
            MenuClaimAction::Grant | MenuClaimAction::Release => {}
        }
        let current = menus.tier_of(&requested);
        if let Some(current) = current.filter(|c| c.owner_only() && !is_owner) {
            return Ok(owners_only(current, "move somebody out of"));
        }
        if let Err(failed) = menus.place(&requested, tier).await {
            return Ok(not_written(&failed));
        }
        menus.set_grant(&self_id, Some(&requested)).await?;
        if bound.is_none() {
            menus.bind(&self_id, &requested).await?;
        }
        tracing::info!(member = %self_id, player = %requested, tier = tier.key(), by = username, "givemenu");
        let change = match current {
            Some(was) if was != tier => format!("moved from {}", was.label()),
            Some(_) => "already held it".to_owned(),
            None => "newly granted".to_owned(),
        };
        Ok(theme::success(
            &format!("{} menu granted", tier.label()),
            Some(&format!(
                "<@{member}> → `{}`",
                Sanitize::code(Some(&requested))
            )),
        )
        .field("Change", change, true)
        .field(
            "Binding",
            if bound.is_none() {
                "newly bound to this name"
            } else {
                "already bound"
            },
            true,
        ))
    }
}

impl SlashCommand for GiveMenuCommand {
    fn name(&self) -> &str {
        "givemenu"
    }

    fn build(&self) -> CommandSpec {
        let tiers = MenuTier::ALL.into_iter().fold(
            OptionSpec::new(OptionKind::String, "tier", "Which menu").required(),
            |option, tier| option.choice(Choice::string(tier.label(), tier.key())),
        );
        CommandSpec::new("givemenu", "Admin - Give a player an in-game menu tier")
            .option(OptionSpec::new(OptionKind::User, "member", "Which Discord member").required())
            .option(
                OptionSpec::new(OptionKind::String, "playerid", "Their in-game name")
                    .required()
                    .autocomplete(),
            )
            .option(tiers)
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match refuse(&self.access, &invocation.caller) {
                Some(refusal) => refusal,
                None => {
                    self.run(
                        &invocation.caller,
                        &invocation.username,
                        invocation.id("member"),
                        invocation.string("playerid"),
                        MenuTier::parse(invocation.string("tier")),
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /stripmenu ----

/// `/stripmenu`: take a member's name out of every tier file and drop the grant.
pub struct StripMenuCommand {
    pub menus: Arc<MenuAccess>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for StripMenuCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("StripMenuCommand").finish_non_exhaustive()
    }
}

impl StripMenuCommand {
    async fn run(
        &self,
        caller: &Caller,
        username: &str,
        member: u64,
    ) -> Result<Embed, CommandError> {
        let id = member.to_string();
        let menus = &self.menus;
        let Some(player) = menus.granted_name(&id).or_else(|| menus.bound_name(&id)) else {
            return Ok(theme::notice(
                "No menu access",
                Some(&format!("<@{member}> does not hold a menu.")),
            ));
        };
        if !menus.enabled() {
            return Ok(unavailable());
        }
        if let Some(tier) = menus
            .tier_of(&player)
            .filter(|t| t.owner_only() && !self.access.is_owner(caller))
        {
            return Ok(owners_only(tier, "strip"));
        }
        let removed = match menus.remove(&player).await {
            Ok(removed) => removed,
            Err(failed) => return Ok(not_written(&failed)),
        };
        menus.set_grant(&id, None).await?;
        tracing::info!(member = %id, player = %player, by = username, "stripmenu");
        Ok(theme::success(
            "Menu access removed",
            Some(&format!(
                "<@{member}> (`{}`) no longer has menu access. Their name binding is unchanged.",
                Sanitize::code(Some(&player))
            )),
        )
        .field(
            "Files",
            if removed == 0 {
                "was on no menu file".to_owned()
            } else {
                format!("taken off {removed}")
            },
            true,
        ))
    }
}

impl SlashCommand for StripMenuCommand {
    fn name(&self) -> &str {
        "stripmenu"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("stripmenu", "Admin - Remove a member's in-game menu access")
            .option(OptionSpec::new(OptionKind::User, "member", "Which Discord member").required())
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match (
                refuse(&self.access, &invocation.caller),
                invocation.id("member"),
            ) {
                (Some(refusal), _) => refusal,
                (None, None) => theme::failure("Need a member", None),
                (None, Some(member)) => {
                    self.run(&invocation.caller, &invocation.username, member)
                        .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /unlinkname ----

/// The ONLY way a binding is removed: it exists for a real name change.
pub struct UnlinkNameCommand {
    pub store: Arc<SerializedStore>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for UnlinkNameCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UnlinkNameCommand").finish_non_exhaustive()
    }
}

impl UnlinkNameCommand {
    async fn run(&self, username: &str, member: u64) -> Result<Embed, CommandError> {
        let id = member.to_string();
        let existing: Option<MenuBinding> = self
            .store
            .read::<Bindings>(MENU_LINKS, Bindings::new())
            .unwrap_or_default()
            .get(&id)
            .cloned();
        let Some(existing) = existing else {
            return Ok(theme::notice(
                "Nothing to unlink",
                Some(&format!("<@{member}> is not bound to a name.")),
            ));
        };
        self.store
            .update(MENU_LINKS, Bindings::new, |mut links: Bindings| {
                links.shift_remove(&id);
                Some(links)
            })
            .await?;
        tracing::warn!(member = %id, was = %existing.in_game_name, by = username, "unlinkname");
        Ok(theme::success(
            "Binding broken",
            Some(&format!(
                "<@{member}> was bound to `{}` and may now bind to a new name.",
                Sanitize::code(Some(&existing.in_game_name))
            )),
        )
        .field(
            format!("{} Note", theme::WARN),
            "This is the only way a binding is removed. It exists for a real name change.",
            false,
        ))
    }
}

impl SlashCommand for UnlinkNameCommand {
    fn name(&self) -> &str {
        "unlinkname"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "unlinkname",
            "Admin - Break a member's permanent in-game name binding",
        )
        .option(OptionSpec::new(OptionKind::User, "member", "Which Discord member").required())
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match (
                refuse(&self.access, &invocation.caller),
                invocation.id("member"),
            ) {
                (Some(refusal), _) => refusal,
                (None, None) => theme::failure("Need a member", None),
                (None, Some(member)) => self.run(&invocation.username, member).await?,
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
