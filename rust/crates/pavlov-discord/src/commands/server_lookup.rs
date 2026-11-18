//! `/server lookup` and its browser (C# `ServerLookupCommand.cs`): page through the public
//! Pavlov server list, pick one, see everything about it.
//!
//! - An option's VALUE is `ip:port`, never the name: names are operator-controlled, can pass
//!   Discord's 100-character value limit, and are not unique.
//! - Only whoever opened a list may drive it: two people paging one message fight over it.
//! - A search re-filters a FRESH snapshot, so a second search widens as well as narrows.
//! - A stale list says so in the footer: the master server being down is not "no servers".

use std::collections::HashMap;
use std::sync::{Arc, Mutex, PoisonError};
use std::time::Duration;

use chrono::{DateTime, Utc};
use futures_util::future::BoxFuture;
use pavlov_core::component_id::ComponentId;
use pavlov_core::embed::Embed;
use pavlov_core::servers::listing::{ServerListing, matching, ordered};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_core::time::{Clock, EasternTime};
use pavlov_host::master_list::{MasterServerList, Snapshot};

use crate::command::{CommandSpec, OptionKind, OptionSpec};
use crate::component::{
    ActionRow, Button, ButtonStyle, Modal, SelectMenu, SelectOption, TextInput,
};
use crate::interaction::{ComponentInvocation, ComponentResponder, DiscordError, Reply, Responder};
use crate::paged::ComponentHandler;
use crate::prefixes::SERVER_LOOKUP;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Servers per page: Discord's cap on a select's options.
pub const PER_PAGE: usize = 25;
/// Lists held open at once; the oldest are dropped past it.
const MAX_OPEN: usize = 50;
const EXPIRED: &str = "That server list has expired. Run `/server lookup` again.";

#[derive(Debug, Clone)]
struct Session {
    all: Vec<ServerListing>,
    query: Option<String>,
    page: usize,
    selected: Option<String>,
    age: Option<Duration>,
    stale: bool,
    owner: u64,
    at: DateTime<Utc>,
}

fn trim(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn id(step: &str) -> String {
    ComponentId::encode(SERVER_LOOKUP, &[step]).unwrap_or_else(|_| SERVER_LOOKUP.to_owned())
}

/// Which of the public servers are this bot's own.
#[derive(Debug, Clone, Default)]
pub struct OwnServers {
    /// The RCON hosts configured.
    pub hosts: Vec<String>,
    /// The RCON server names configured.
    pub names: Vec<String>,
}

impl OwnServers {
    fn contains(&self, server: &ServerListing) -> bool {
        self.hosts.iter().any(|h| eq_ignore_case(h, &server.ip))
            || self.names.iter().any(|n| eq_ignore_case(n, &server.name))
    }
}

impl Session {
    fn new(snapshot: &Snapshot, query: Option<String>, owner: u64, now: DateTime<Utc>) -> Self {
        let all = ordered(
            matching(snapshot.servers.iter(), query.as_deref())
                .into_iter()
                .cloned(),
        );
        Self {
            all,
            query,
            page: 0,
            selected: None,
            age: snapshot.age,
            stale: snapshot.failed,
            owner,
            at: now,
        }
    }

    fn page_count(&self) -> usize {
        self.all.len().div_ceil(PER_PAGE).max(1)
    }

    fn page_of(&self) -> &[ServerListing] {
        let start = (self.page * PER_PAGE).min(self.all.len());
        &self.all[start..(start + PER_PAGE).min(self.all.len())]
    }

    fn freshness(&self) -> String {
        let age = match self.age.map(|a| a.as_secs()) {
            Some(s) if s >= 5 => format!("{s}s ago"),
            _ => "just now".to_owned(),
        };
        if self.stale {
            format!("Master server unreachable — showing the last list, fetched {age}")
        } else {
            format!("Fetched {age}")
        }
    }

    fn render(&self, own: &OwnServers) -> Embed {
        // A picked server replaces the summary: it is what the person came for.
        if let Some(chosen) = self
            .selected
            .as_ref()
            .and_then(|k| self.all.iter().find(|s| &s.key() == k))
        {
            return card(chosen, own.contains(chosen)).brand(Some(&self.freshness()));
        }
        let players: i64 = self.all.iter().map(|s| i64::from(s.players)).sum();
        let populated = self.all.iter().filter(|s| s.players > 0).count();
        let matched = self
            .query
            .as_deref()
            .filter(|q| !q.is_empty())
            .map(|q| format!(" matching `{}`", Sanitize::code(Some(q))))
            .unwrap_or_default();
        let rows: Vec<String> = self.page_of().iter().map(summary).collect();
        let body = format!(
            "**{}** server(s){matched}, **{populated}** with players on, **{players}** playing in total.\n\nPick one \
             from the menu for the full details.\n\n{}",
            self.all.len(),
            rows.join("\n")
        );
        theme::notice(
            &format!(
                "Pavlov servers — page {}/{}",
                self.page + 1,
                self.page_count()
            ),
            Some(&body),
        )
        .brand(Some(&self.freshness()))
    }

    fn controls(&self) -> Vec<ActionRow> {
        let mut options: Vec<SelectOption> = self
            .page_of()
            .iter()
            .map(|s| SelectOption {
                label: trim(&format!("{}/{} {}", s.players, s.max_players, s.name), 100),
                value: s.key(),
                description: Some(trim(
                    &format!("{} • {}", s.game_mode_label, s.map_label),
                    100,
                )),
                default: false,
            })
            .collect();
        if options.is_empty() {
            options.push(SelectOption {
                label: "(no servers on this page)".into(),
                value: "-none-".into(),
                description: None,
                default: false,
            });
        }
        let placeholder = match &self.selected {
            None => format!("Choose a server ({} listed)...", self.all.len()),
            Some(key) => trim(&format!("Showing: {key}"), 150),
        };
        let last = self.page_count() - 1;
        let mut prev = Button::new(ButtonStyle::Secondary, "◀", id("prev"));
        if self.page == 0 {
            prev = prev.disabled();
        }
        let mut next = Button::new(ButtonStyle::Secondary, "▶", id("next"));
        if self.page >= last {
            next = next.disabled();
        }
        vec![
            ActionRow::Select(SelectMenu {
                custom_id: id("pick"),
                placeholder: Some(placeholder),
                options,
                min_values: 1,
                max_values: 1,
            }),
            ActionRow::Buttons(vec![
                prev,
                Button::new(
                    ButtonStyle::Secondary,
                    format!("{}/{}", self.page + 1, last + 1),
                    id("noop"),
                )
                .disabled(),
                next,
                Button::new(ButtonStyle::Primary, "Search", id("find")),
                Button::new(ButtonStyle::Secondary, "Refresh", id("reload")),
            ]),
        ]
    }

    fn reply(&self, own: &OwnServers) -> Reply {
        Reply::embed(self.render(own)).with_components(self.controls())
    }
}

fn summary(server: &ServerListing) -> String {
    let count = format!("{}/{}", server.players, server.max_players);
    let lock = if server.password_protected {
        " 🔒"
    } else {
        ""
    };
    let full = if server.full() { " • FULL" } else { "" };
    format!(
        "`{count:<6}` **{}** — {}{lock}{full}",
        Sanitize::message(Some(&trim(&server.name, 44))),
        Sanitize::message(Some(&server.game_mode_label))
    )
}

fn describe(label: &str, raw: &str) -> String {
    if !raw.is_empty() && !eq_ignore_case(label, raw) {
        format!(
            "{} (`{}`)",
            Sanitize::message(Some(label)),
            Sanitize::code(Some(raw))
        )
    } else {
        Sanitize::message(Some(if label.is_empty() { "*unknown*" } else { label }))
    }
}

/// Everything about one server.
#[must_use]
pub fn card(server: &ServerListing, ours: bool) -> Embed {
    let players = format!("{}/{}", server.players, server.max_players);
    let suffix = if ours { " · yours" } else { "" };
    theme::notice(
        &format!("{}{suffix}", trim(&server.name, 240)),
        Some(&format!(
            "**{players}** playing **{}** on **{}**.",
            Sanitize::message(Some(&server.game_mode_label)),
            Sanitize::message(Some(&server.map_label))
        )),
    )
    .field("Connect", format!("`{}:{}`", server.ip, server.port), true)
    .field(
        "Players",
        if server.full() {
            format!("{players} (full)")
        } else {
            players
        },
        true,
    )
    .field(
        "Game mode",
        describe(&server.game_mode_label, &server.game_mode),
        true,
    )
    .field("Map", Sanitize::message(Some(&server.map_label)), true)
    .field(
        "Map ID",
        if server.map_id.is_empty() {
            "*none*".into()
        } else {
            format!("`{}`", Sanitize::code(Some(&server.map_id)))
        },
        true,
    )
    .field(
        "Workshop",
        if server.is_workshop_map() {
            "Custom map"
        } else {
            "Stock map"
        },
        true,
    )
    .field(
        "Version",
        if server.version.is_empty() {
            "*unknown*".into()
        } else {
            server.version.clone()
        },
        true,
    )
    .field(
        "Password",
        if server.password_protected {
            "🔒 Required"
        } else {
            "Open"
        },
        true,
    )
    .field(
        "Anti-cheat",
        if server.secured {
            "Enforced"
        } else {
            "⚠️ Not enforced"
        },
        true,
    )
    .field(
        "Last seen",
        server.updated.map_or_else(
            || "*unknown*".to_owned(),
            |at| {
                format!(
                    "<t:{}:R> ({})",
                    at.timestamp(),
                    EasternTime::stamp(Some(at))
                )
            },
        ),
        false,
    )
}

/// The browser: the component handler behind `/server lookup`.
pub struct ServerBrowser {
    master: Arc<MasterServerList>,
    own: OwnServers,
    clock: Arc<dyn Clock>,
    open: Mutex<HashMap<u64, Session>>,
}

impl std::fmt::Debug for ServerBrowser {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServerBrowser").finish_non_exhaustive()
    }
}

impl ServerBrowser {
    #[must_use]
    pub fn new(master: Arc<MasterServerList>, own: OwnServers, clock: Arc<dyn Clock>) -> Self {
        Self {
            master,
            own,
            clock,
            open: Mutex::new(HashMap::new()),
        }
    }

    fn sessions(&self) -> std::sync::MutexGuard<'_, HashMap<u64, Session>> {
        self.open.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn remember(&self, message: u64, session: Session) {
        let mut open = self.sessions();
        open.insert(message, session);
        if open.len() > MAX_OPEN {
            let mut by_age: Vec<(u64, DateTime<Utc>)> =
                open.iter().map(|(k, v)| (*k, v.at)).collect();
            by_age.sort_by_key(|(_, at)| *at);
            for (stale, _) in by_age.into_iter().take(open.len() - MAX_OPEN) {
                open.remove(&stale);
            }
        }
    }

    /// Open a list as the deferred reply.
    ///
    /// # Errors
    /// When Discord refuses the edit.
    pub async fn begin(
        &self,
        responder: &dyn Responder,
        owner: u64,
        search: Option<&str>,
    ) -> Result<(), DiscordError> {
        let snapshot = self.master.get(false).await;
        let session = Session::new(
            &snapshot,
            search.map(str::to_owned),
            owner,
            self.clock.now(),
        );
        if session.all.is_empty() {
            let why = if snapshot.failed {
                "The Pavlov master server could not be reached, and nothing was cached yet. Try again shortly."
            } else {
                "Nothing in the public list matched that. Try a shorter search, or drop it entirely."
            };
            return responder
                .edit_original(Reply::embed(
                    theme::failure("No servers matched", Some(why)).brand(None),
                ))
                .await;
        }
        if let Some(message) = responder
            .edit_original_message(session.reply(&self.own))
            .await?
        {
            self.remember(message, session);
        }
        Ok(())
    }

    async fn dispatch(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
    ) -> Result<(), DiscordError> {
        let parsed = ComponentId::parse(&invocation.custom_id);
        let step = parsed.argument(0).unwrap_or("");
        let Some(session) = self.sessions().get(&invocation.message_id).cloned() else {
            return responder.respond(Reply::text(EXPIRED), true).await;
        };
        if step == "search" {
            return self.on_search(invocation, responder, &session).await;
        }
        if invocation.caller.id != session.owner {
            return responder
                .respond(
                    Reply::text("That list belongs to whoever ran the command. Run `/server lookup` for your own."),
                    true,
                )
                .await;
        }
        let update = |next: Session| async move {
            self.sessions().insert(invocation.message_id, next.clone());
            responder.update(next.reply(&self.own)).await
        };
        match step {
            // A modal opens on an UNACKNOWLEDGED interaction.
            "find" => {
                responder
                    .show_modal(Modal {
                        custom_id: id("search"),
                        title: "Search the server list".into(),
                        inputs: vec![TextInput {
                            custom_id: "query".into(),
                            label: "Name, map, mode or address".into(),
                            paragraph: false,
                            required: false,
                            placeholder: Some("leave empty to clear the search".into()),
                            value: None,
                            min_length: None,
                            max_length: Some(60),
                        }],
                    })
                    .await
            }
            "reload" => {
                responder.acknowledge().await?;
                let snapshot = self.master.get(true).await;
                let mut refreshed = Session::new(
                    &snapshot,
                    session.query.clone(),
                    session.owner,
                    self.clock.now(),
                );
                refreshed.selected = session.selected;
                self.sessions()
                    .insert(invocation.message_id, refreshed.clone());
                responder.edit_original(refreshed.reply(&self.own)).await
            }
            "prev" | "next" => {
                let page = if step == "next" {
                    (session.page + 1).min(session.page_count() - 1)
                } else {
                    session.page.saturating_sub(1)
                };
                // Turning the page clears the card: it belongs to a server no longer shown.
                update(Session {
                    page,
                    selected: None,
                    ..session
                })
                .await
            }
            "pick" => match invocation.values.first().filter(|k| k.as_str() != "-none-") {
                Some(key) => {
                    update(Session {
                        selected: Some(key.clone()),
                        ..session
                    })
                    .await
                }
                None => responder.acknowledge().await,
            },
            _ => responder.acknowledge().await,
        }
    }

    async fn on_search(
        &self,
        invocation: &ComponentInvocation,
        responder: &dyn ComponentResponder,
        session: &Session,
    ) -> Result<(), DiscordError> {
        let typed = invocation.field("query").unwrap_or("").trim().to_owned();
        // From a FRESH snapshot, so a second search widens as well as narrows.
        let snapshot = self.master.get(false).await;
        let query = (!typed.is_empty()).then(|| typed.clone());
        let filtered = Session::new(&snapshot, query, session.owner, self.clock.now());
        if filtered.all.is_empty() {
            return responder
                .respond(
                    Reply::text(format!(
                        "Nothing matched `{}`. The list still shows what it did.",
                        Sanitize::code(Some(&typed))
                    )),
                    true,
                )
                .await;
        }
        self.sessions()
            .insert(invocation.message_id, filtered.clone());
        responder.update(filtered.reply(&self.own)).await
    }
}

impl ComponentHandler for ServerBrowser {
    fn prefix(&self) -> &str {
        SERVER_LOOKUP
    }

    fn handle<'a>(
        &'a self,
        invocation: &'a ComponentInvocation,
        responder: &'a dyn ComponentResponder,
    ) -> BoxFuture<'a, ()> {
        Box::pin(async move {
            if let Err(error) = self.dispatch(invocation, responder).await {
                tracing::warn!(%error, "could not answer a server-browser interaction");
            }
        })
    }
}

/// `/server lookup`.
pub struct ServerLookupCommand {
    pub browser: Arc<ServerBrowser>,
}

impl std::fmt::Debug for ServerLookupCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServerLookupCommand")
            .finish_non_exhaustive()
    }
}

impl SlashCommand for ServerLookupCommand {
    fn name(&self) -> &str {
        "server"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("server", "Browse the public Pavlov server list").option(
            OptionSpec::sub("lookup", "Pick a live server and see everything about it").option(
                OptionSpec::new(
                    OptionKind::String,
                    "search",
                    "Filter by name, map, mode or address",
                ),
            ),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            Ok(self
                .browser
                .begin(
                    context.responder,
                    invocation.caller.id,
                    invocation.string("search"),
                )
                .await?)
        })
    }
}
