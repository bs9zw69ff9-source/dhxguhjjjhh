//! `/serverinfo` (C# `ServerInfoCommand.cs`): live map, mode and roster for every server.
//!
//! - THE COUNT AND THE NAMES COME FROM THE SAME REPLY: the Node bot counted one fetch and named
//!   from a cache, so a player who joined between the two showed in one and not the other.
//! - AN UNREACHABLE SERVER IS REPORTED, NOT OMITTED: dropped, a down server looks like one
//!   that does not exist.
//! - Servers are fetched in parallel, so one slow host delays the reply by its own latency.

use std::sync::Arc;

use futures_util::future::{BoxFuture, join, join_all};
use pavlov_core::embed::{Embed, palette};
use pavlov_host::registry::RconRegistry;
use pavlov_rcon::reply;

use crate::command::CommandSpec;
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Names listed per server before "and N more".
pub const MAX_NAMES_SHOWN: usize = 15;

/// What one server reported, or that it could not be reached.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerView {
    pub server: String,
    pub online: bool,
    pub display_name: String,
    pub map: String,
    pub mode: String,
    pub player_count: usize,
    pub max_players: Option<i32>,
    pub roster: Vec<String>,
}

impl ServerView {
    #[must_use]
    pub fn unreachable(server: &str) -> Self {
        Self {
            server: server.to_owned(),
            online: false,
            display_name: server.to_owned(),
            map: "*Unreachable*".into(),
            mode: "*Unreachable*".into(),
            player_count: 0,
            max_players: None,
            roster: Vec::new(),
        }
    }

    /// The view from the two raw replies; `None` for a reply that did not arrive.
    #[must_use]
    pub fn from_replies(server: &str, list: Option<&str>, info: Option<&str>) -> Self {
        let list = reply::try_parse(list);
        let info = reply::try_parse(info)
            .map(|i| reply::server_info(&i))
            .unwrap_or_default();
        let mut roster = list.as_ref().map(reply::names).unwrap_or_default();
        roster.sort_by_key(|n| n.to_lowercase());
        // Tri-state, kept that way: only an explicit false is down.
        let online = list
            .as_ref()
            .is_some_and(|l| reply::successful(l) != Some(false));
        Self {
            server: server.to_owned(),
            online,
            display_name: info.server_name.unwrap_or_else(|| server.to_owned()),
            map: info.map_label.unwrap_or_else(|| "*Unknown*".into()),
            mode: info.game_mode.unwrap_or_else(|| "*Unknown*".into()),
            player_count: roster.len(),
            max_players: info.max_players,
            roster,
        }
    }
}

/// A player name inside a code span: names are attacker-controlled, and a backtick would
/// break out of the span.
fn escape(name: &str) -> String {
    name.replace('`', "'")
}

/// One server's embed.
#[must_use]
pub fn view_embed(view: &ServerView) -> Embed {
    let embed = Embed::new()
        .title(view.display_name.clone())
        .color(if view.online {
            palette::SERVER_UP
        } else {
            palette::SERVER_DOWN
        });
    if !view.online {
        return embed.description(format!(
            "**{}** looks offline right now.",
            view.display_name
        ));
    }
    let capacity = view.max_players.map_or_else(
        || view.player_count.to_string(),
        |max| format!("{}/{max}", view.player_count),
    );
    let mut lines = vec![format!(
        "**{}** is online with **{capacity}** players, playing **{}** on **{}**.",
        view.display_name, view.mode, view.map
    )];
    if view.roster.is_empty() {
        lines.push("Nobody is on right now.".into());
    } else {
        let shown: Vec<String> = view
            .roster
            .iter()
            .take(MAX_NAMES_SHOWN)
            .map(|n| format!("`{}`", escape(n)))
            .collect();
        let extra = view
            .roster
            .len()
            .checked_sub(MAX_NAMES_SHOWN)
            .filter(|n| *n > 0)
            .map(|n| format!(" and {n} more"))
            .unwrap_or_default();
        lines.push(format!("Online: {}{extra}", shown.join(" ")));
    }
    embed.description(lines.join("\n"))
}

/// Every server's embed, the first carrying the network total.
#[must_use]
pub fn embeds(views: &[ServerView]) -> Vec<Embed> {
    let online: usize = views
        .iter()
        .filter(|v| v.online)
        .map(|v| v.player_count)
        .sum();
    let capacity: i64 = views
        .iter()
        .map(|v| i64::from(v.max_players.unwrap_or(0)))
        .sum();
    let total = if capacity > 0 {
        format!(
            "**{online}/{capacity}** online across {} server(s).",
            views.len()
        )
    } else {
        format!("**{online}** online across {} server(s).", views.len())
    };
    let mut embeds: Vec<Embed> = views.iter().map(view_embed).collect();
    match embeds.first_mut() {
        Some(first) => *first = first.clone().field("All servers", total, false),
        None => embeds.push(Embed::new().title("No servers").description(total)),
    }
    embeds
}

/// What the command is wired to.
pub struct ServerInfoCommand {
    pub rcon: Arc<RconRegistry>,
}

impl std::fmt::Debug for ServerInfoCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServerInfoCommand").finish_non_exhaustive()
    }
}

impl ServerInfoCommand {
    async fn fetch(&self, server: String) -> ServerView {
        // Both at once: coalescing one layer down lets a concurrent sweep share the round trip.
        let (list, info) = join(
            self.rcon.send(&server, "RefreshList"),
            self.rcon.send(&server, "ServerInfo"),
        )
        .await;
        match (list, info) {
            (Ok(list), Ok(info)) => ServerView::from_replies(&server, Some(&list), Some(&info)),
            _ => ServerView::unreachable(&server),
        }
    }
}

impl SlashCommand for ServerInfoCommand {
    fn name(&self) -> &str {
        "serverinfo"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new(
            "serverinfo",
            "Live server info - map, mode and who is online.",
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let views = join_all(self.rcon.servers().into_iter().map(|s| self.fetch(s))).await;
            context
                .reply(Reply {
                    embeds: embeds(&views),
                    ..Reply::default()
                })
                .await
        })
    }
}
