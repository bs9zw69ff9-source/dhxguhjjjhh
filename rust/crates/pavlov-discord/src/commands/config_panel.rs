//! `/configure` (C# `ConfigPanel.cs`): the owner's control panel, a select of every
//! [`OwnerActions`] action.
//!
//! - OWNER ACCESS IS RE-CHECKED ON EVERY INTERACTION, not only when the panel opened: the message
//!   persists, and an owner being removed is exactly who would still click it.
//! - A destructive action needs its WORD TYPED, and only those are audited, so the record is not
//!   buried under "somebody looked at the blacklist".
//! - An action needing a value or a word opens a modal, so it must not acknowledge first.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::moderation::AuditLog;
use pavlov_host::moderation::owner_actions::{OwnerActionResult, OwnerActions};

use crate::access::{Access, RequiredAccess, refusal};
use crate::command::CommandSpec;
use crate::component::{ActionRow, Modal, SelectMenu, SelectOption, TextInput};
use crate::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply};
use crate::paged::ComponentHandler;
use crate::prefixes::CONFIG_PANEL;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Discord's cap on a modal title and a text input label.
const MODAL_TEXT: usize = 45;

/// One panel action: its key, label, emoji, description, the value it asks for, and the word
/// a destructive one needs typed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PanelAction {
    pub key: &'static str,
    pub label: &'static str,
    pub emoji: &'static str,
    pub description: &'static str,
    pub needs_value: Option<&'static str>,
    pub confirm: Option<&'static str>,
}

const fn action(
    key: &'static str,
    label: &'static str,
    emoji: &'static str,
    description: &'static str,
) -> PanelAction {
    PanelAction {
        key,
        label,
        emoji,
        description,
        needs_value: None,
        confirm: None,
    }
}

const fn asks(mut a: PanelAction, prompt: &'static str) -> PanelAction {
    a.needs_value = Some(prompt);
    a
}

const fn confirms(mut a: PanelAction, word: &'static str) -> PanelAction {
    a.confirm = Some(word);
    a
}

/// Every action, in the panel's order.
pub const ACTIONS: [PanelAction; 23] = [
    asks(
        action(
            "blacklist",
            "Blacklist IP / username",
            "🚫",
            "Auto-ban anyone matching an address or name",
        ),
        "Address or username",
    ),
    action(
        "viewbl",
        "View blacklist",
        "📋",
        "Every blacklisted address, name and account",
    ),
    asks(
        action(
            "alts",
            "View alt accounts",
            "🕵️",
            "Accounts sharing a confirmed address",
        ),
        "Player name",
    ),
    asks(
        action(
            "clearip",
            "Clear a specific address",
            "🧹",
            "Un-flag one address",
        ),
        "Address",
    ),
    confirms(
        action(
            "clearnames",
            "Clear flagged usernames",
            "🧹",
            "Stop all username auto-bans",
        ),
        "CLEAR",
    ),
    confirms(
        action(
            "clearall",
            "Wipe ALL address data",
            "💥",
            "Registry and every flag (irreversible)",
        ),
        "WIPE",
    ),
    asks(
        action(
            "neverban",
            "Never auto-ban a player",
            "🛡️",
            "Stop the bot banning them, and lift it now",
        ),
        "Player name",
    ),
    asks(
        action(
            "allowban",
            "Remove never-ban",
            "↩️",
            "Let the bot auto-ban them again",
        ),
        "Player name",
    ),
    action(
        "neverbanlist",
        "List never-ban players",
        "📋",
        "Who the bot may never auto-ban",
    ),
    asks(
        action(
            "baradd",
            "Bar a Discord user",
            "⛔",
            "Block a Discord user from ALL commands",
        ),
        "Discord user id",
    ),
    asks(
        action(
            "barremove",
            "Un-bar a Discord user",
            "✅",
            "Restore a Discord user's access",
        ),
        "Discord user id",
    ),
    action(
        "barlist",
        "List barred Discord users",
        "📋",
        "Who is blocked from commands",
    ),
    asks(
        action(
            "ignoreadd",
            "Ignore a username",
            "🙈",
            "Stop recording a player's addresses",
        ),
        "Player name",
    ),
    asks(
        action(
            "ignoreremove",
            "Un-ignore a username",
            "👁️",
            "Resume recording",
        ),
        "Player name",
    ),
    action(
        "ignorelist",
        "List ignored usernames",
        "📋",
        "Who is not being tracked",
    ),
    action(
        "savewl",
        "Save whitelists",
        "💾",
        "Snapshot every faction roster file",
    ),
    confirms(
        action(
            "loadwl",
            "Load whitelists",
            "♻️",
            "Restore every roster file from the snapshot (overwrites current)",
        ),
        "RESTORE",
    ),
    confirms(
        action(
            "cleartempbans",
            "Clear temporary bans",
            "⏳",
            "Lift every temp ban, keep permanent ones",
        ),
        "CLEAR",
    ),
    confirms(
        action(
            "clearallbans",
            "Clear ALL ban records",
            "🔓",
            "Empty the bot's ban list (irreversible)",
        ),
        "WIPE",
    ),
    confirms(
        action(
            "stripmenuall",
            "Strip every menu grant",
            "🎫",
            "Revoke all RCON menu access",
        ),
        "STRIP",
    ),
    confirms(
        action(
            "clearflags",
            "Clear every flag",
            "🧹",
            "All address, account and name flags",
        ),
        "CLEAR",
    ),
    confirms(
        action(
            "wipemoney",
            "Wipe ALL money",
            "💰",
            "Delete every player ledger (irreversible)",
        ),
        "WIPE",
    ),
    confirms(
        action(
            "wipeplayers",
            "Wipe ALL player data",
            "🧨",
            "Registry, playtime, last-seen (irreversible)",
        ),
        "WIPE",
    ),
];

/// The action a key names.
#[must_use]
pub fn find(key: Option<&str>) -> Option<&'static PanelAction> {
    ACTIONS.iter().find(|a| Some(a.key) == key)
}

/// Whether the typed confirmation matches the action's word (case-insensitive, trimmed); an
/// action with no word always passes.
#[must_use]
pub fn confirmed(action: &PanelAction, typed: Option<&str>) -> bool {
    action
        .confirm
        .is_none_or(|word| typed.is_some_and(|t| t.trim().eq_ignore_ascii_case(word)))
}

fn id(parts: &[&str]) -> String {
    ComponentId::encode(CONFIG_PANEL, parts).unwrap_or_else(|_| CONFIG_PANEL.to_owned())
}

fn trim(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

/// The panel and its controls.
pub struct ConfigPanel {
    pub actions: Arc<OwnerActions>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
}

impl std::fmt::Debug for ConfigPanel {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ConfigPanel").finish_non_exhaustive()
    }
}

/// The panel message.
#[must_use]
pub fn panel() -> Reply {
    let options = ACTIONS
        .iter()
        .map(|a| SelectOption {
            label: a.label.into(),
            value: a.key.into(),
            description: Some(a.description.into()),
            default: false,
            emoji: Some(a.emoji.into()),
        })
        .collect();
    Reply::embed(
        // The select menu below lists every action with its description; the embed only
        // titles it.
        theme::notice("Owner Control Panel", None).brand(Some("Owner only")),
    )
    .with_components(vec![ActionRow::Select(SelectMenu {
        custom_id: id(&["menu"]),
        placeholder: Some("Choose an owner action...".into()),
        options,
        min_values: 1,
        max_values: 1,
    })])
}

/// The result as the owner sees it; a long list shows its first page and says there is more.
#[must_use]
pub fn result_embed(action: &PanelAction, result: &OwnerActionResult) -> Embed {
    let title = format!("{} {}", action.emoji, action.label);
    if !result.ok {
        return theme::failure(action.label, Some(&result.detail)).brand(None);
    }
    if result.lines.is_empty() {
        return theme::notice(&title, Some(&result.detail)).brand(None);
    }
    let pages = theme::paginate(
        result.lines.iter().map(String::as_str),
        theme::MAX_DESCRIPTION,
    );
    let footer = (pages.len() > 1)
        .then(|| format!("Page 1 of {} — narrow it down to see the rest", pages.len()));
    theme::notice(
        &title,
        Some(&format!(
            "{}\n\n{}",
            result.detail,
            pages.first().map_or("", String::as_str)
        )),
    )
    .brand(footer.as_deref())
}

impl ConfigPanel {
    async fn execute(&self, key: &str, value: &str) -> OwnerActionResult {
        let a = &self.actions;
        match key {
            "blacklist" => a.blacklist(value).await,
            "viewbl" => a.view_blacklist(),
            "alts" => a.alts(value),
            "clearip" => a.clear_address(value).await,
            "clearnames" => a.clear_flagged_names().await,
            "clearall" => a.wipe_all_ip_data().await,
            "neverban" => a.protect_player(value).await,
            "allowban" => a.unprotect_player(value).await,
            "neverbanlist" => a.protected_players(),
            "baradd" => a.bar_user(value).await,
            "barremove" => a.unbar_user(value).await,
            "barlist" => a.barred_users(),
            "ignoreadd" => a.ignore(value).await,
            "ignoreremove" => a.unignore(value).await,
            "ignorelist" => a.ignored_names(),
            "savewl" => a.save_whitelists().await,
            "loadwl" => a.load_whitelists().await,
            "cleartempbans" => a.clear_temp_bans().await,
            "clearallbans" => a.clear_all_bans().await,
            "stripmenuall" => a.strip_all_menus().await,
            "clearflags" => a.clear_all_flags().await,
            "wipemoney" => a.wipe_money(),
            "wipeplayers" => a.wipe_player_data().await,
            _ => OwnerActionResult::refused("That action is not implemented."),
        }
    }

    async fn run(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
        action: &PanelAction,
        value: &str,
    ) -> Result<(), DiscordError> {
        let result = self.execute(action.key, value).await;
        if action.confirm.is_some() && result.ok {
            let detail = Sanitize::message(Some(&result.detail));
            if let Err(error) = self
                .audit
                .record(
                    &format!("configure-{}", action.key),
                    &invocation.username,
                    "(server-wide)",
                    Some(&detail),
                )
                .await
            {
                tracing::error!(%error, action = action.key, "could not audit a destructive owner action");
            }
            tracing::warn!(action = action.key, by = %invocation.username, "DESTRUCTIVE");
        }
        responder
            .followup(Reply::embed(result_embed(action, &result)), true)
            .await
    }

    async fn dispatch(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) -> Result<(), DiscordError> {
        let say = |embed: Embed| responder.respond(Reply::embed(embed.brand(None)), true);
        let caller = &invocation.caller;
        if !self.access.allows(RequiredAccess::Owner, caller) {
            return say(theme::denied(
                "Not allowed",
                Some(&self.access.explain_refusal(caller, RequiredAccess::Owner)),
            ))
            .await;
        }
        let parsed = ComponentId::parse(&invocation.custom_id);
        match parsed.argument(0) {
            Some("menu") => {
                let Some(action) = find(invocation.values.first().map(String::as_str)) else {
                    return say(theme::failure(
                        "Unknown action",
                        Some("That option is no longer supported."),
                    ))
                    .await;
                };
                if action.needs_value.is_some() || action.confirm.is_some() {
                    let mut inputs = Vec::new();
                    if let Some(prompt) = action.needs_value {
                        inputs.push(TextInput {
                            custom_id: "value".into(),
                            label: trim(prompt, MODAL_TEXT),
                            paragraph: false,
                            required: true,
                            placeholder: None,
                            value: None,
                            min_length: None,
                            max_length: Some(100),
                        });
                    }
                    if let Some(word) = action.confirm {
                        inputs.push(TextInput {
                            custom_id: "confirm".into(),
                            label: format!("Type {word} to confirm"),
                            paragraph: false,
                            required: true,
                            placeholder: Some(word.into()),
                            value: None,
                            min_length: None,
                            max_length: Some(32),
                        });
                    }
                    return responder
                        .show_modal(Modal {
                            custom_id: id(&["submit", action.key]),
                            title: trim(action.label, MODAL_TEXT),
                            inputs,
                        })
                        .await;
                }
                responder.defer_reply(true).await?;
                self.run(invocation, responder, action, "").await
            }
            Some("submit") => {
                let Some(action) = find(parsed.argument(1)) else {
                    return say(theme::failure(
                        "Unknown action",
                        Some("That control is no longer supported."),
                    ))
                    .await;
                };
                if let Some(word) = action
                    .confirm
                    .filter(|_| !confirmed(action, invocation.field("confirm")))
                {
                    return say(theme::warning(
                        "Cancelled",
                        Some(&format!(
                            "Type **{word}** exactly to confirm. Nothing was changed."
                        )),
                    ))
                    .await;
                }
                responder.defer_reply(true).await?;
                self.run(
                    invocation,
                    responder,
                    action,
                    invocation.field("value").unwrap_or(""),
                )
                .await
            }
            _ => {
                say(theme::failure(
                    "Unknown action",
                    Some("That control is no longer supported."),
                ))
                .await
            }
        }
    }
}

impl SlashCommand for ConfigPanel {
    fn name(&self) -> &str {
        "configure"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "configure",
            "Owner - The control panel: blacklists, access, tracking, wipes",
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            if !self
                .access
                .allows(RequiredAccess::Owner, &context.invocation.caller)
            {
                return context
                    .reply(Reply::embed(
                        theme::denied("Not allowed", Some(&refusal(RequiredAccess::Owner)))
                            .brand(None),
                    ))
                    .await;
            }
            context.reply(panel()).await
        })
    }
}

impl ComponentHandler for ConfigPanel {
    fn prefix(&self) -> &str {
        CONFIG_PANEL
    }

    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            if let Err(error) = self.dispatch(invocation, responder).await {
                tracing::warn!(%error, "could not answer a control-panel interaction");
            }
        })
    }
}
