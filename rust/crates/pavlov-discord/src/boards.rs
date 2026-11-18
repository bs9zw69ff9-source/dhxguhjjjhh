//! What the auto-posted boards say (C# `Boards.cs`). [`crate::autopost::AutoPost`] owns where a
//! board lives and how it is kept current; this owns only its content.
//!
//! `None` means "skip this cycle and leave what is posted", which is right only where the
//! alternative is a transient failure overwriting real numbers. The cash, most-wanted and
//! warrant boards REPLACED boards the Node bot posts under the same keys, so they always post:
//! skipping would leave the old board frozen in the channel looking current.

use std::path::PathBuf;
use std::sync::Arc;

use chrono::{DateTime, TimeDelta, Utc};
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::police::{Arrest, Warrant};
use pavlov_core::skin::{lore, police, theme};
use pavlov_core::sync::ModSaveFile;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::{IgnoreCaseMap, cmp_ignore_case};
use pavlov_core::text::number::{fixed, group_thousands};
use pavlov_core::time::{Clock, EasternTime};
use pavlov_host::factions::roster::{Membership, RosterService};
use pavlov_host::kill_stats::PlayerKills;
use pavlov_host::moderation::ModAction;
use pavlov_host::registry::RconRegistry;
use pavlov_storage::datasets::{ARRESTS, MOD_LOG, PLAYTIME, WARRANTS};
use serde::{Deserialize, Serialize};

use crate::player_board::{self, BoardPlayer, BoardRoster};

/// Rows on every board but the richest.
pub const ARREST_ROWS: usize = 15;
/// Players the richest board ranks.
pub const TOP_ROWS: usize = 100;
/// The rank column: room for "100" and a gutter.
const RANK_WIDTH: usize = 5;
/// Spaces between the longest name and the money column.
const GUTTER: usize = 3;
/// Longest name the table prints, so one pasted essay cannot set the width for everybody.
const MAX_NAME_WIDTH: usize = 32;
/// How much of the 4096-character description the table may fill. Branding TRUNCATES, and a
/// cut closing fence renders the rest of the message as code.
pub const TABLE_BUDGET: usize = 4060;
/// Rows on the K/D board.
pub const KD_ROWS: usize = 15;
/// Kills needed to rank, so one lucky kill and no deaths cannot top the board.
pub const KD_MINIMUM_KILLS: i64 = 75;
/// The bar under each most-wanted row.
const BAR_WIDTH: usize = 12;

/// Minutes played, accumulated by the playtime ticker. Stored camelCase, the time in epoch
/// milliseconds, as the Node bot reads it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaytimeEntry {
    #[serde(default)]
    pub player: String,
    #[serde(default)]
    pub minutes: i64,
    #[serde(with = "pavlov_core::json::epoch_ms_opt", default)]
    pub last_seen: Option<DateTime<Utc>>,
}

/// The whole economy: every ledger, richest first.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CashStanding {
    pub top: Vec<(String, i64)>,
    /// Across EVERY ledger, not just the rows shown.
    pub total: i64,
    pub ledgers: usize,
}

/// The board builder.
pub struct Boards {
    store: Arc<SerializedStore>,
    rcon: Arc<RconRegistry>,
    ledger_directory: Option<PathBuf>,
    rosters: Option<Arc<RosterService>>,
    clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for Boards {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Boards")
            .field("ledger_directory", &self.ledger_directory)
            .finish_non_exhaustive()
    }
}

fn updated(at: DateTime<Utc>) -> String {
    format!("Updated {} Eastern", EasternTime::stamp(Some(at)))
}

fn medal(index: usize) -> &'static str {
    match index {
        0 => "🥇",
        1 => "🥈",
        2 => "🥉",
        _ => theme::DOT,
    }
}

/// `14h 20m`, `45m`.
fn hours(minutes: i64) -> String {
    if minutes >= 60 {
        format!("{}h {}m", group_thousands(minutes / 60), minutes % 60)
    } else {
        format!("{minutes}m")
    }
}

fn code(text: &str) -> String {
    Sanitize::code(Some(text))
}

/// The display-case name off the entries, falling back to the map key (lowercased).
fn display<'a>(named: &'a str, key: &'a str) -> &'a str {
    if named.is_empty() { key } else { named }
}

impl Boards {
    #[must_use]
    pub fn new(
        store: Arc<SerializedStore>,
        rcon: Arc<RconRegistry>,
        ledger_directory: Option<PathBuf>,
        rosters: Option<Arc<RosterService>>,
        clock: Arc<dyn Clock>,
    ) -> Self {
        Self {
            store,
            rcon,
            ledger_directory,
            rosters,
            clock,
        }
    }

    /// Every balance, straight from the files the game writes. `banlist.txt` shares the
    /// directory and is not a ledger; a ledger mid-write parses as nothing and is skipped for
    /// a cycle rather than read as zero. `None` when the directory is unset or unreadable.
    #[must_use]
    pub fn read_cash(&self) -> Option<CashStanding> {
        let directory = self.ledger_directory.as_ref()?;
        let entries = std::fs::read_dir(directory).ok()?;
        let mut balances: Vec<(String, i64)> = Vec::new();
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("txt") {
                continue;
            }
            let Some(player) = path.file_stem().and_then(|s| s.to_str()) else {
                continue;
            };
            if player.eq_ignore_ascii_case("banlist") {
                continue;
            }
            let Ok(content) = std::fs::read_to_string(&path) else {
                continue;
            };
            if let Some(balance) = ModSaveFile::parse(Some(&content)).and_then(|f| f.money()) {
                balances.push((player.to_owned(), balance));
            }
        }
        // Stable, by balance; ties in name order so the board does not shuffle between cycles.
        balances.sort_by(|a, b| b.1.cmp(&a.1).then_with(|| a.0.cmp(&b.0)));
        let total = balances.iter().map(|b| b.1).sum();
        let ledgers = balances.len();
        balances.truncate(TOP_ROWS);
        Some(CashStanding {
            top: balances,
            total,
            ledgers,
        })
    }

    /// Players by balance: what `LEADERBOARD_CHANNEL` is for.
    #[must_use]
    pub fn build_cash_board(&self) -> Embed {
        let now = self.clock.now();
        let w = &police().words;
        let title = format!("{} {}", w.money_glyph, w.richest_title);
        let Some(standing) = self.read_cash() else {
            // A configuration problem the owner has to see, not a board that never updates.
            return theme::failure(
                &title,
                Some(&format!(
                    "{} `MODSAVE_PATH` is not set, or the bot cannot read it.",
                    w.ledgers_unreadable
                )),
            )
            .brand(Some(&updated(now)));
        };
        if standing.ledgers == 0 {
            return theme::notice(&title, Some(w.no_ledgers)).brand(Some(&updated(now)));
        }
        let (table, shown) = cash_table(&standing.top, TABLE_BUDGET);
        let mut footer = format!(
            "{} in circulation across {} ledger(s)",
            lore::amount(standing.total),
            standing.ledgers
        );
        if shown < standing.top.len() {
            footer.push_str(&format!(" - showing {shown}, the rest would not fit"));
        }
        footer.push_str(&format!(
            " - updated {} Eastern",
            EasternTime::stamp(Some(now))
        ));
        theme::notice(
            &title,
            Some(&format!(
                "**{}**\n\n{table}",
                lore::richest_heading(TOP_ROWS)
            )),
        )
        .brand(Some(&footer))
    }

    /// The playtime dataset, keyed the way every name lookup is.
    #[must_use]
    pub fn playtime(&self) -> IgnoreCaseMap<PlaytimeEntry> {
        self.store
            .read(PLAYTIME, IgnoreCaseMap::new())
            .unwrap_or_default()
    }

    /// Add `elapsed` to everyone CONFIRMED online: a server that stopped answering must not
    /// keep accruing. There is no playtime board (it was removed before the port); this
    /// feeds `/whitelist playtime` and the Node bot's reader of the same dataset.
    ///
    /// # Errors
    /// When the dataset cannot be written.
    pub async fn tick_playtime(
        &self,
        elapsed: TimeDelta,
    ) -> Result<(), pavlov_core::data::StoreError> {
        let online = self.rcon.confirmed_online_players();
        #[allow(clippy::cast_precision_loss, clippy::cast_possible_truncation)]
        let minutes = (elapsed.num_milliseconds() as f64 / 60_000.0).round() as i64;
        if online.is_empty() || minutes <= 0 {
            return Ok(());
        }
        let now = self.clock.now();
        self.store
            .update(
                PLAYTIME,
                IgnoreCaseMap::new,
                |mut playtime: IgnoreCaseMap<PlaytimeEntry>| {
                    for player in &online {
                        let before = playtime.get(player).map_or(0, |e| e.minutes);
                        playtime.insert(
                            player.clone(),
                            PlaytimeEntry {
                                player: player.clone(),
                                minutes: before + minutes,
                                last_seen: Some(now),
                            },
                        );
                    }
                    Some(playtime)
                },
            )
            .await
            .map(|_| ())
    }

    /// Who is online per server, with their faction, off the roster cache the RCON sweep
    /// already filled (no round trip, and it cannot disagree with `/players`). The faction
    /// comes from the ROSTER FILES, the source of truth the game reads.
    #[must_use]
    pub fn build_player_board(&self) -> Option<Embed> {
        let affiliation: IgnoreCaseMap<Membership> = self
            .rosters
            .as_ref()
            .map(|r| r.affiliations())
            .unwrap_or_default();
        let now = self.clock.now();
        let servers: Vec<BoardRoster> = self
            .rcon
            .servers()
            .into_iter()
            .map(|server| {
                let snapshot = self.rcon.roster(&server);
                let mut names: Vec<String> = snapshot
                    .players
                    .iter()
                    .map(|p| p.name.clone())
                    .filter(|n| !n.trim().is_empty())
                    .collect();
                names.sort_by(|a, b| cmp_ignore_case(a, b));
                let players = names
                    .into_iter()
                    .map(|n| {
                        let faction = affiliation.get(&n).map(|m| m.faction.clone());
                        BoardPlayer { name: n, faction }
                    })
                    .collect();
                // A roster never fetched is NOT an empty server.
                let age = snapshot.taken_at.map(|at| now - at);
                BoardRoster {
                    stale: age.is_some() && !self.rcon.roster_is_fresh(&server),
                    problem: self.rcon.roster_problem(&server),
                    server,
                    players,
                    age,
                }
            })
            .collect();
        player_board::build(&servers, now)
    }

    /// Players by jail time served, arrest count breaking ties: one long sentence outranks
    /// several short ones, which is what "most wanted" means.
    #[must_use]
    pub fn build_arrest_board(&self) -> Embed {
        let now = self.clock.now();
        let w = &police().words;
        let title = format!("{} {}", w.jail_glyph, w.most_wanted_title);
        let arrests: IgnoreCaseMap<Vec<Arrest>> = self
            .store
            .read(ARRESTS, IgnoreCaseMap::new())
            .unwrap_or_default();
        let mut rows: Vec<(String, i64, usize)> = arrests
            .iter()
            .filter_map(|(key, list)| {
                let last = list.last()?;
                let minutes = list.iter().map(|a| i64::from(a.jail_minutes)).sum();
                Some((display(&last.player, key).to_owned(), minutes, list.len()))
            })
            .collect();
        rows.sort_by(|a, b| b.1.cmp(&a.1).then(b.2.cmp(&a.2)));
        rows.truncate(ARREST_ROWS);
        if rows.is_empty() {
            return theme::notice(&title, Some(w.no_arrests)).brand(Some(&updated(now)));
        }
        let top = rows[0].1;
        #[allow(clippy::cast_precision_loss)]
        let lines: Vec<String> = rows
            .iter()
            .enumerate()
            .map(|(i, (player, minutes, count))| {
                format!(
                    "`{:>2}.` {} **{}** — {minutes} min over {count} arrest(s)\n{}",
                    i + 1,
                    medal(i),
                    code(player),
                    theme::bar(*minutes as f64, top as f64, BAR_WIDTH)
                )
            })
            .collect();
        theme::punishment(&title, Some(&lines.join("\n"))).brand(Some(&updated(now)))
    }

    /// Everyone with an outstanding warrant, OLDEST FIRST: a work queue, not a leaderboard.
    /// The latest reason is shown, since a list of bare names makes every officer ask why.
    #[must_use]
    pub fn build_warrant_board(&self) -> Embed {
        let now = self.clock.now();
        let w = &police().words;
        let warrants: IgnoreCaseMap<Vec<Warrant>> = self
            .store
            .read(WARRANTS, IgnoreCaseMap::new())
            .unwrap_or_default();
        let wanted: Vec<(&str, &Vec<Warrant>)> =
            warrants.iter().filter(|(_, l)| !l.is_empty()).collect();
        let mut rows: Vec<(String, usize, DateTime<Utc>, &Warrant)> = wanted
            .iter()
            .filter_map(|(key, list)| {
                let latest = list.last()?;
                let oldest = list.iter().map(|w| w.at).min()?;
                Some((
                    display(&latest.player, key).to_owned(),
                    list.len(),
                    oldest,
                    latest,
                ))
            })
            .collect();
        rows.sort_by_key(|r| r.2);
        rows.truncate(ARREST_ROWS);
        if rows.is_empty() {
            return theme::success(
                &format!("{} {}", w.warrant_glyph, w.no_warrants_title),
                Some(w.nobody_wanted),
            )
            .brand(Some(&updated(now)));
        }
        let lines: Vec<String> = rows
            .iter()
            .map(|(player, count, oldest, latest)| {
                let times = if *count > 1 {
                    format!(" ×{count}")
                } else {
                    String::new()
                };
                format!(
                    "{} **{}**{times}\n　{} — issued {} by {}",
                    theme::DENY,
                    code(player),
                    code(&latest.reason),
                    theme::relative(*oldest),
                    code(&latest.issued_by)
                )
            })
            .collect();
        let total: usize = wanted.iter().map(|(_, l)| l.len()).sum();
        let mut embed = theme::punishment(
            &format!("{} {}", w.warrant_glyph, w.warrant_board_title),
            Some(&lines.join("\n")),
        )
        .field("Wanted", rows.len().to_string(), true)
        .field(lore::warrants_heading(), total.to_string(), true);
        if wanted.len() > ARREST_ROWS {
            embed = embed.field(
                "Not shown",
                format!("{} more wanted.", wanted.len() - ARREST_ROWS),
                false,
            );
        }
        embed.brand(Some(&format!(
            "Oldest first · Updated {} Eastern",
            EasternTime::stamp(Some(now))
        )))
    }

    /// Staff by moderation actions taken. `None` when there is none: nothing else has ever
    /// occupied this board's channel, so skipping hides nothing.
    #[must_use]
    pub fn build_staff_board(&self) -> Option<Embed> {
        let log: Vec<ModAction> = self.store.read(MOD_LOG, Vec::new()).unwrap_or_default();
        if log.is_empty() {
            return None;
        }
        let mut groups: IgnoreCaseMap<(String, usize, DateTime<Utc>)> = IgnoreCaseMap::new();
        for action in &log {
            let entry = groups.get(&action.moderator).cloned().map_or_else(
                || (action.moderator.clone(), 1, action.at),
                |(name, count, last)| (name, count + 1, last.max(action.at)),
            );
            groups.insert(action.moderator.clone(), entry);
        }
        let mut rows: Vec<(String, usize, DateTime<Utc>)> = groups.values().cloned().collect();
        rows.sort_by_key(|r| std::cmp::Reverse(r.1));
        rows.truncate(ARREST_ROWS);
        let lines: Vec<String> = rows
            .iter()
            .enumerate()
            .map(|(i, (staff, count, last))| {
                format!(
                    "`{:>2}.` **{}** — {count} action(s), last {}",
                    i + 1,
                    code(staff),
                    theme::relative(*last)
                )
            })
            .collect();
        let w = &police().words;
        Some(
            theme::notice(
                &format!("{} {}", w.staff_glyph, w.staff_board_title),
                Some(&lines.join("\n")),
            )
            .brand(Some(&format!("{} recorded action(s)", log.len()))),
        )
    }
}

fn clamp_name(name: &str) -> String {
    if name.chars().count() <= MAX_NAME_WIDTH {
        name.to_owned()
    } else {
        let mut cut: String = name.chars().take(MAX_NAME_WIDTH - 1).collect();
        cut.push('…');
        cut
    }
}

/// The ranked table as ONE fenced block: fixed width (outside a code block Discord's
/// proportional font lines nothing up), the money right-aligned under its header, the opening
/// fence on its own line (text after a fence reads as a language tag), and BOUNDED: rows are
/// dropped until the block fits `budget`, and the count shown is returned so the caller can
/// say so.
#[must_use]
pub fn cash_table(rows: &[(String, i64)], budget: usize) -> (String, usize) {
    let label = police().words.money_label;
    let cells: Vec<(String, String)> = rows
        .iter()
        // No glyph or unit: an emoji is not one cell wide, and the header names the money.
        .map(|(player, balance)| (clamp_name(&code(player)), group_thousands(*balance)))
        .collect();
    let width = |s: &str| s.chars().count();
    let name_width = cells
        .iter()
        .map(|c| width(&c.0))
        .max()
        .unwrap_or("Username".len());
    let money_width = cells
        .iter()
        .map(|c| width(&c.1))
        .max()
        .unwrap_or(width(label));
    let header = format!(
        "{:<RANK_WIDTH$}{:<name$}{:>money$}",
        "#",
        "Username",
        label,
        name = name_width + GUTTER,
        money = money_width
    );
    let row_width = RANK_WIDTH + name_width + GUTTER + money_width + 1;
    let overhead = "```\n".len() + width(&header) + 1 + "```".len();
    let shown = (budget.saturating_sub(overhead) / row_width).min(cells.len());
    let mut block = format!("```\n{header}\n");
    for (i, (name, money)) in cells.iter().take(shown).enumerate() {
        block.push_str(&format!(
            "{:<RANK_WIDTH$}{name:<nw$}{money:>money_width$}\n",
            i + 1,
            nw = name_width + GUTTER,
        ));
    }
    block.push_str("```");
    (block, shown)
}

/// Players by kills per death, what `KD_LEADERBOARD_CHANNEL` is for, with each one's faction
/// and playtime. A floor of [`KD_MINIMUM_KILLS`] to rank; ties go to more kills. Always a
/// board, never `None`.
#[must_use]
pub fn build_kd_board(
    kills: &[PlayerKills],
    playtime: &IgnoreCaseMap<PlaytimeEntry>,
    affiliations: &IgnoreCaseMap<Membership>,
    now: DateTime<Utc>,
) -> Embed {
    let title = format!("💀 Deadliest in {}", police().words.world);
    let footer = format!(
        "At least {KD_MINIMUM_KILLS} kills to rank - updated {} Eastern",
        EasternTime::stamp(Some(now))
    );
    let mut ranked: Vec<&PlayerKills> = kills
        .iter()
        .filter(|k| k.kills >= KD_MINIMUM_KILLS)
        .collect();
    ranked.sort_by(|a, b| {
        b.ratio()
            .total_cmp(&a.ratio())
            .then_with(|| b.kills.cmp(&a.kills))
    });
    ranked.truncate(KD_ROWS);
    if ranked.is_empty() {
        return theme::notice(
            &title,
            Some(&format!("Nobody has {KD_MINIMUM_KILLS} kills yet.")),
        )
        .brand(Some(&footer));
    }
    let lines: Vec<String> = ranked
        .iter()
        .enumerate()
        .map(|(i, k)| {
            let faction = affiliations.get(&k.player).map_or_else(
                || "No faction".to_owned(),
                |m| {
                    format!(
                        "{} {}",
                        Sanitize::markdown(Some(&m.faction)),
                        Sanitize::markdown(Some(&m.rank))
                    )
                },
            );
            let played = playtime
                .get(&k.player)
                .map_or_else(|| "no playtime".to_owned(), |e| hours(e.minutes));
            format!(
                "`{:>2}.` {} **{}** · **{}** K/D ({}/{})\n{} {faction} · {played}",
                i + 1,
                medal(i),
                code(&k.player),
                fixed(k.ratio(), 2),
                k.kills,
                k.deaths,
                theme::DOT
            )
        })
        .collect();
    theme::notice(&title, Some(&lines.join("\n"))).brand(Some(&footer))
}
