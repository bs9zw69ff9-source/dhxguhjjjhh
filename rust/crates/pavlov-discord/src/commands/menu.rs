//! `/givemenu`, `/unlinkname` and the self-serve menu panel (C# `MenuCommands.cs`,
//! `MenuPanel.cs`): RCON+ menu access, and the permanent binding of a Discord account to one
//! in-game name.
//!
//! - ONE DECISION for both entry points ([`MenuLink::decide`]): they must never disagree about
//!   who owns a name.
//! - NOTHING IS RECORDED WHEN NOTHING LANDED. A grant the server never received leaves the bot
//!   believing in access that does not exist, and the next claim then reads as a release. The
//!   RCON reply is not the only witness: Pavlov.log shows what ran, so it is asked before a
//!   grant is called a failure.
//! - A release drops the GRANT and keeps the BINDING, or a menu could be cycled onto an alt.
//! - The panel's BLACKLIST ROLE WINS EVEN OVER AN OWNER: it is a deliberate revocation.

use std::sync::Arc;
use std::time::Duration;

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use indexmap::IndexMap;
use pavlov_config::FeatureOptions;
use pavlov_core::component_id::ComponentId;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::moderation::{MenuClaimAction, MenuClaimDecision, MenuLink, MenuRoleMap};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_core::time::Clock;
use pavlov_host::confirmations::RconConfirmations;
use pavlov_host::moderation::AuditLog;
use pavlov_host::registry::RconRegistry;
use pavlov_storage::datasets::{MENU_GRANTS, MENU_LINKS, MENU_ROLES};
use serde::{Deserialize, Serialize};

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::component::{ActionRow, Button, ButtonStyle, Modal, TextInput};
use crate::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply};
use crate::paged::ComponentHandler;
use crate::prefixes::MENU_PANEL;
use crate::rcon_failure;
use crate::rcon_menu;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// How long Pavlov.log is watched for a grant RCON did not acknowledge.
const CONFIRM_WITHIN: Duration = Duration::from_secs(5);

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
type Grants = IndexMap<String, String>;

/// The menu state and the RCON+ verbs, shared by `/givemenu` and the panel.
pub struct MenuAccess {
    pub rcon: Arc<RconRegistry>,
    pub store: Arc<SerializedStore>,
    pub confirmations: Arc<RconConfirmations>,
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

    /// The one decision both entry points use.
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

    /// Whether `self_id` already has a name bound.
    #[must_use]
    pub fn bound_name(&self, self_id: &str) -> Option<String> {
        self.bindings().get(self_id).map(|b| b.in_game_name.clone())
    }

    async fn send(&self, server: &str, line: &str) -> Result<(), String> {
        self.rcon.send(server, line).await.map(|_| ()).map_err(|e| {
            tracing::warn!(server, verb = line.split(' ').next().unwrap_or(""), error = %e, "menu command failed");
            e.to_string()
        })
    }

    /// Send the grant everywhere. `Ok(servers)` when it landed (or Pavlov.log confirms it ran),
    /// else the per-server failures, and NOTHING has been recorded.
    pub async fn grant(
        &self,
        name: &str,
        tier: &str,
    ) -> Result<usize, Vec<(String, Option<String>)>> {
        let sent_at = self.clock.now();
        let (mut delivered, mut attempts) = (0, Vec::new());
        for server in self.rcon.servers() {
            let (mut ok, mut problem) = (false, None);
            for line in rcon_menu::grant(name, tier) {
                match self.send(&server, &line).await {
                    Ok(()) => ok = true,
                    Err(e) => problem = problem.or(Some(e)),
                }
            }
            if ok {
                delivered += 1;
            } else {
                attempts.push((server, problem));
            }
        }
        if delivered == 0 {
            let confirm = format!("GiveMenu {name} {}", rcon_menu::menu_id(Some(tier)));
            if !self
                .confirmations
                .confirmed(None, &confirm, sent_at, CONFIRM_WITHIN)
                .await
            {
                return Err(attempts);
            }
            tracing::info!(
                player = name,
                "no RCON ack but Pavlov.log confirms the menu grant ran"
            );
        }
        Ok(delivered)
    }

    /// Revoke the menu everywhere (best effort, each failure logged).
    pub async fn revoke(&self, name: &str) {
        for server in self.rcon.servers() {
            for line in rcon_menu::revoke(name, true) {
                let _ = self.send(&server, &line).await;
            }
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

    fn not_granted(&self, attempts: &[(String, Option<String>)]) -> Embed {
        rcon_failure::explain(
            "Not granted",
            "No server accepted the command, and Pavlov.log never showed it run. Nothing was recorded.",
            &self.rcon,
            attempts,
        )
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
        username: &str,
        member: Option<u64>,
        raw: Option<&str>,
        tier: &str,
    ) -> Result<Embed, CommandError> {
        let requested = Sanitize::id(raw);
        let Some(member) = member.filter(|_| !requested.is_empty()) else {
            return Ok(theme::failure(
                "Need a member and a usable in-game name",
                None,
            ));
        };
        let self_id = member.to_string();
        let menus = &self.menus;
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
            MenuClaimAction::Release => {
                menus.set_grant(&self_id, None).await?;
                menus.revoke(&requested).await;
                tracing::info!(member = %self_id, player = %requested, by = username, "stripmenu");
                return Ok(theme::success(
                    "Menu access removed",
                    Some(&format!(
                        "<@{member}> no longer has menu access. Their name binding is unchanged."
                    )),
                ));
            }
            MenuClaimAction::Grant => {}
        }
        let delivered = match menus.grant(&requested, tier).await {
            Ok(delivered) => delivered,
            Err(attempts) => return Ok(menus.not_granted(&attempts)),
        };
        menus.set_grant(&self_id, Some(&requested)).await?;
        if bound.is_none() {
            menus.bind(&self_id, &requested).await?;
        }
        tracing::info!(member = %self_id, player = %requested, by = username, servers = delivered, "givemenu");
        let label = if rcon_menu::is_high_staff(Some(tier)) {
            "High Staff"
        } else {
            "Staff"
        };
        Ok(theme::success(
            &format!("{label} menu granted"),
            Some(&format!(
                "<@{member}> → `{}`",
                Sanitize::code(Some(&requested))
            )),
        )
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
        CommandSpec::new("givemenu", "Admin - Grant RCON menu access to a player")
            .option(OptionSpec::new(OptionKind::User, "member", "Which Discord member").required())
            .option(
                OptionSpec::new(OptionKind::String, "playerid", "Their in-game name")
                    .required()
                    .autocomplete(),
            )
            .option(
                OptionSpec::new(OptionKind::String, "menu", "Which menu to grant")
                    .required()
                    .choice(Choice::string("Staff", rcon_menu::STAFF))
                    .choice(Choice::string("High Staff", rcon_menu::HIGH_STAFF)),
            )
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
                        &invocation.username,
                        invocation.id("member"),
                        invocation.string("playerid"),
                        invocation.string("menu").unwrap_or(rcon_menu::STAFF),
                    )
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

// ---- the panel ----

/// What a member may claim from the panel.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PanelTier {
    /// Holds the blacklist role: revoked, and told so.
    Blacklisted,
    Menu(&'static str),
}

/// The self-serve panel: a "Get menu" button opening a name modal.
pub struct MenuPanel {
    pub menus: Arc<MenuAccess>,
    pub features: Arc<FeatureOptions>,
    pub audit: Arc<AuditLog>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for MenuPanel {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MenuPanel").finish_non_exhaustive()
    }
}

fn panel_id(step: &str) -> String {
    ComponentId::encode(MENU_PANEL, &[step]).unwrap_or_else(|_| MENU_PANEL.to_owned())
}

impl MenuPanel {
    /// Whether a panel channel is configured at all.
    #[must_use]
    pub fn enabled(&self) -> bool {
        self.features.menu_panel_channel.is_some()
    }

    /// The panel message the bot posts.
    #[must_use]
    pub fn panel() -> Reply {
        Reply::embed(
            theme::success(
                "RCON menu access",
                Some(
                    "Tools for trusted staff.\n\nPress **Get menu** and enter your **exact** Pavlov in-game name. You get \
                     the menu matching your highest staff role.\n\nOne name per Discord account. Enter it again to \
                     remove your menu.",
                ),
            )
            .brand(None),
        )
        .with_components(vec![ActionRow::Buttons(vec![Button::new(ButtonStyle::Success, "Get menu", panel_id("start"))])])
    }

    fn roles(&self) -> MenuRoleMap {
        let stored: MenuRoleMap = self
            .menus
            .store
            .read(MENU_ROLES, MenuRoleMap::default())
            .unwrap_or_default();
        stored.over(
            self.features.menu_role_high_staff,
            self.features.menu_role_staff,
            self.features.menu_role_blacklist,
        )
    }

    /// The tier `caller` may claim: blacklist first, even over an owner; then owners get the
    /// top menu by id, member or not; then the role map's highest-wins rule.
    #[must_use]
    pub fn tier_of(&self, caller: &Caller) -> Option<PanelTier> {
        let roles = self.roles();
        let held: &[u64] = caller
            .member
            .as_ref()
            .map_or(&[], |m| m.role_ids.as_slice());
        if roles.blacklist.is_some_and(|b| held.contains(&b)) {
            return Some(PanelTier::Blacklisted);
        }
        if self.access.is_owner(caller) {
            return Some(PanelTier::Menu(rcon_menu::HIGH_STAFF));
        }
        caller.member.as_ref()?;
        roles.tier_for(held).map(|t| PanelTier::Menu(t.as_str()))
    }

    async fn submitted(&self, invocation: &ComponentInvocation) -> Result<Embed, CommandError> {
        let tier = match self.tier_of(&invocation.caller) {
            None => {
                return Ok(theme::denied(
                    "Not eligible",
                    Some(
                        "Your roles do not grant menu access. Ask an admin if you think that is wrong.",
                    ),
                ));
            }
            Some(PanelTier::Blacklisted) => {
                tracing::info!(member = %invocation.username, "menu claim blocked by blacklist role");
                return Ok(theme::denied(
                    "Menu access revoked",
                    Some(
                        "Your self-serve menu access has been revoked. Contact an admin if you believe this is a mistake.",
                    ),
                ));
            }
            Some(PanelTier::Menu(tier)) => tier,
        };
        let requested = Sanitize::id(
            invocation
                .field("name")
                .or_else(|| invocation.field("menu_name")),
        );
        if requested.is_empty() {
            return Ok(theme::failure("Enter your exact in-game name", None));
        }
        let self_id = invocation.caller.id.to_string();
        let menus = &self.menus;
        let decision = menus.decide(&self_id, &requested);
        match decision.action {
            MenuClaimAction::Locked => Ok(theme::denied(
                "Name locked",
                Some(&format!(
                    "Your account is linked to `{}`, and that link is permanent.\n\nIf you genuinely changed your \
                     Pavlov name, ask an admin to unlink you.",
                    Sanitize::code(decision.bound_name.as_deref())
                )),
            )),
            MenuClaimAction::Taken => Ok(theme::denied(
                "Name taken",
                Some(&format!(
                    "`{}` is already linked to another Discord account. If that is your name, ask an admin to sort it out.",
                    Sanitize::code(Some(&requested))
                )),
            )),
            MenuClaimAction::Release => {
                menus.revoke(&requested).await;
                // The GRANT goes, the BINDING stays.
                menus.set_grant(&self_id, None).await?;
                self.audit
                    .record(
                        "menu-release",
                        &invocation.username,
                        &requested,
                        Some("self-service"),
                    )
                    .await?;
                Ok(theme::success(
                    "Menu removed",
                    Some(&format!(
                        "Removed the menu from `{}`.",
                        Sanitize::code(Some(&requested))
                    )),
                ))
            }
            MenuClaimAction::Grant => {
                let delivered = match menus.grant(&requested, tier).await {
                    Ok(delivered) => delivered,
                    Err(attempts) => return Ok(menus.not_granted(&attempts)),
                };
                menus.bind(&self_id, &requested).await?;
                menus.set_grant(&self_id, Some(&requested)).await?;
                self.audit
                    .record("menu-claim", &invocation.username, &requested, Some(tier))
                    .await?;
                tracing::info!(member = %invocation.username, player = %requested, tier, servers = delivered, "menu claimed");
                Ok(theme::success(
                    "Menu granted",
                    Some(&format!(
                        "`{}` now has the **{tier}** menu on **{delivered}** server(s).",
                        Sanitize::code(Some(&requested))
                    )),
                ))
            }
        }
    }

    async fn dispatch(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) -> Result<(), DiscordError> {
        match ComponentId::parse(&invocation.custom_id).argument(0) {
            // No acknowledgement first: a modal cannot open on one.
            Some("start") => {
                responder
                    .show_modal(Modal {
                        custom_id: panel_id("submit"),
                        title: "Get RCON menu access".into(),
                        inputs: vec![TextInput {
                            custom_id: "name".into(),
                            label: "Your exact Pavlov in-game name".into(),
                            paragraph: false,
                            required: true,
                            placeholder: None,
                            value: None,
                            min_length: None,
                            max_length: Some(64),
                        }],
                    })
                    .await
            }
            Some("submit") => {
                responder.defer_reply(true).await?;
                let embed = match self.submitted(invocation).await {
                    Ok(embed) => embed,
                    Err(error) => {
                        tracing::error!(%error, "menu claim failed");
                        theme::failure("That failed", Some("The error has been logged."))
                    }
                };
                responder
                    .followup(Reply::embed(embed.brand(None)), true)
                    .await
            }
            _ => {
                responder
                    .respond(Reply::text("That control is no longer supported."), true)
                    .await
            }
        }
    }
}

impl ComponentHandler for MenuPanel {
    fn prefix(&self) -> &str {
        MENU_PANEL
    }

    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            if let Err(error) = self.dispatch(invocation, responder).await {
                tracing::warn!(%error, "could not answer a menu-panel interaction");
            }
        })
    }
}
