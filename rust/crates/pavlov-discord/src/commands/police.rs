//! `/warrant`, `/backgroundcheck` and `/bail` (C# `PoliceCommands.cs`). `/arrest` lives with
//! its booking picker.
//!
//! - CHECKING A WARRANT IS PUBLIC: a player being able to see whether they are wanted is the
//!   point of one. Issuing and clearing need Police.
//! - `/bail` changes ONE setting and keeps the other: rebuilding the config would quietly reset
//!   the jail cap when the bail rate changes, or the other way round.

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::penal::PenalCode;
use pavlov_core::police::{Arrest, PoliceConfig, Warrant};
use pavlov_core::skin::{lore, police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_core::time::Clock;
use pavlov_storage::datasets::{ARRESTS, POLICE_CONFIG, WARRANTS};

use crate::access::{Access, RequiredAccess};
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::paged::Paged;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// Arrests listed on a background check: what an officer at the scene needs.
const RECENT_ARRESTS: usize = 5;

type Warrants = IgnoreCaseMap<Vec<Warrant>>;
type Arrests = IgnoreCaseMap<Vec<Arrest>>;

fn player_option(required: bool) -> OptionSpec {
    let option =
        OptionSpec::new(OptionKind::String, "playerid", "Player ID or username").autocomplete();
    if required { option.required() } else { option }
}

// ---- /warrant ----

pub struct WarrantCommand {
    pub store: Arc<SerializedStore>,
    pub access: Arc<Access>,
    pub paged: Arc<Paged>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for WarrantCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("WarrantCommand").finish_non_exhaustive()
    }
}

/// The wanted list, most warrants first.
#[must_use]
pub fn wanted_lines(all: &Warrants) -> Vec<String> {
    let mut wanted: Vec<(&str, usize)> = all
        .iter()
        .filter(|(_, list)| !list.is_empty())
        .map(|(name, list)| (name, list.len()))
        .collect();
    wanted.sort_by_key(|(_, count)| std::cmp::Reverse(*count));
    wanted
        .into_iter()
        .map(|(name, count)| format!("`{}` — {count}", Sanitize::code(Some(name))))
        .collect()
}

impl WarrantCommand {
    fn load(&self) -> Warrants {
        self.store
            .read(WARRANTS, Warrants::new())
            .unwrap_or_default()
    }

    async fn give(
        &self,
        username: &str,
        player: &str,
        raw_reason: Option<&str>,
    ) -> Result<Embed, CommandError> {
        let reason = Sanitize::message(raw_reason);
        let warrant = Warrant {
            player: player.to_owned(),
            reason: reason.clone(),
            issued_by: username.to_owned(),
            at: self.clock.now(),
        };
        self.store
            .update(WARRANTS, Warrants::new, |mut warrants: Warrants| {
                let mut list = warrants.remove(player).unwrap_or_default();
                list.push(warrant);
                warrants.insert(player, list);
                Some(warrants)
            })
            .await?;
        tracing::info!(player, by = username, reason = %reason, "warrant issued");
        let words = &police().words;
        Ok(theme::warning(
            &format!("{} {}", words.warrant_glyph, words.warrant_issued),
            Some(&format!(
                "**{}** — {}",
                Sanitize::code(Some(player)),
                Sanitize::code(Some(&reason))
            )),
        )
        .field("Issued by", username.to_owned(), true))
    }

    async fn remove(&self, player: &str) -> Result<Embed, CommandError> {
        let had = self.load().get(player).map_or(0, Vec::len);
        self.store
            .update(WARRANTS, Warrants::new, |mut warrants: Warrants| {
                warrants.remove(player);
                Some(warrants)
            })
            .await?;
        let words = &police().words;
        Ok(if had > 0 {
            theme::success(
                &format!("{} cleared", lore::warrants_heading()),
                Some(&format!(
                    "Cleared **{had}** {}(s) on **{}**.",
                    words.warrant,
                    Sanitize::code(Some(player))
                )),
            )
        } else {
            theme::notice(
                "Nothing to clear",
                Some(&format!(
                    "**{}** has no {}.",
                    Sanitize::code(Some(player)),
                    words.warrants
                )),
            )
        })
    }

    fn check_one(&self, player: &str) -> Embed {
        let words = &police().words;
        let list = self.load().get(player).cloned().unwrap_or_default();
        if list.is_empty() {
            return theme::success(
                &format!("No {}", words.warrants),
                Some(&format!("**{}** is clean.", Sanitize::code(Some(player)))),
            );
        }
        let lines: Vec<String> = list
            .iter()
            .enumerate()
            .map(|(i, w)| {
                format!(
                    "**{}.** {} — {}, {}",
                    i + 1,
                    Sanitize::code(Some(&w.reason)),
                    w.issued_by,
                    theme::relative(w.at)
                )
            })
            .collect();
        theme::warning(
            &format!(
                "{} {} {}(s) on {}",
                words.warrant_glyph,
                list.len(),
                words.warrant,
                Sanitize::code(Some(player))
            ),
            Some(&lines.join("\n")),
        )
    }
}

impl SlashCommand for WarrantCommand {
    fn name(&self) -> &str {
        "warrant"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("warrant", "Police - Manage warrants")
            .option(
                OptionSpec::sub("give", "Issue a warrant")
                    .option(player_option(true))
                    .option(OptionSpec::new(OptionKind::String, "reason", "Why").required()),
            )
            .option(
                OptionSpec::sub("remove", "Clear a player's warrants").option(player_option(true)),
            )
            .option(OptionSpec::sub("check", "Look up warrants").option(player_option(false)))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let sub = invocation.subcommand();
            let player = Sanitize::id(invocation.string("playerid"));
            let caller = &invocation.caller;
            if sub != Some("check") && !self.access.allows(RequiredAccess::Police, caller) {
                let refusal = self.access.explain_refusal(caller, RequiredAccess::Police);
                return context
                    .reply(Reply::embed(
                        theme::denied("Not allowed", Some(&refusal)).brand(None),
                    ))
                    .await;
            }
            let embed = match sub {
                Some("give") => {
                    self.give(&invocation.username, &player, invocation.string("reason"))
                        .await?
                }
                Some("remove") => self.remove(&player).await?,
                _ if !player.is_empty() => self.check_one(&player),
                _ => {
                    let wanted = wanted_lines(&self.load());
                    if wanted.is_empty() {
                        theme::success(
                            &format!("Nobody is wanted in {}", police().words.world),
                            None,
                        )
                    } else {
                        // Every page: this used to show only the first, with no sign of more.
                        let pages = theme::paginate(
                            wanted.iter().map(String::as_str),
                            theme::MAX_DESCRIPTION,
                        );
                        let title = format!("{} wanted player(s)", wanted.len());
                        return Ok(self
                            .paged
                            .send(context.responder, caller.id, &title, pages)
                            .await?);
                    }
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /backgroundcheck ----

pub struct BackgroundCheckCommand {
    pub store: Arc<SerializedStore>,
}

impl std::fmt::Debug for BackgroundCheckCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BackgroundCheckCommand")
            .finish_non_exhaustive()
    }
}

/// A player's record: arrests, jail time and outstanding warrants.
#[must_use]
pub fn record(player: &str, arrests: &[Arrest], warrants: &[Warrant]) -> Embed {
    if arrests.is_empty() && warrants.is_empty() {
        return theme::success(
            "Clean record",
            Some(&format!(
                "**{}** has no arrests or warrants.",
                Sanitize::code(Some(player))
            )),
        );
    }
    let jail: i64 = arrests.iter().map(|a| i64::from(a.jail_minutes)).sum();
    let mut embed = theme::notice(&format!("Record — {}", Sanitize::code(Some(player))), None)
        .field("Arrests", arrests.len().to_string(), true)
        .field("Total jail time", format!("{jail} min"), true)
        .field("Outstanding warrants", warrants.len().to_string(), true);
    if !arrests.is_empty() {
        let mut recent: Vec<&Arrest> = arrests.iter().collect();
        recent.sort_by_key(|a| std::cmp::Reverse(a.at));
        let lines: Vec<String> = recent
            .iter()
            .take(RECENT_ARRESTS)
            .map(|a| {
                format!(
                    "{} — `{}` ({} min) by {}",
                    theme::relative(a.at),
                    a.codes.join(", "),
                    a.jail_minutes,
                    a.arrested_by
                )
            })
            .collect();
        embed = embed.field("Recent arrests", lines.join("\n"), false);
    }
    if !warrants.is_empty() {
        let reasons: Vec<String> = warrants
            .iter()
            .map(|w| Sanitize::code(Some(&w.reason)))
            .collect();
        embed = embed.field(
            format!("{} Wanted for", theme::WARN),
            reasons.join("\n"),
            false,
        );
    }
    embed
}

impl SlashCommand for BackgroundCheckCommand {
    fn name(&self) -> &str {
        "backgroundcheck"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("backgroundcheck", "Police - Pull a player's record")
            .option(player_option(true))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let player = Sanitize::id(context.invocation.string("playerid"));
            let arrests: Arrests = self.store.read(ARRESTS, Arrests::new()).unwrap_or_default();
            let warrants: Warrants = self
                .store
                .read(WARRANTS, Warrants::new())
                .unwrap_or_default();
            let embed = record(
                &player,
                arrests.get(&player).map_or(&[][..], Vec::as_slice),
                warrants.get(&player).map_or(&[][..], Vec::as_slice),
            );
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /bail ----

pub struct BailCommand {
    pub store: Arc<SerializedStore>,
    pub access: Arc<Access>,
}

impl std::fmt::Debug for BailCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BailCommand").finish_non_exhaustive()
    }
}

fn percent(rate: f64) -> String {
    format!("{:.0}", rate * 100.0)
}

/// What `/bail show` says.
#[must_use]
pub fn settings(config: &PoliceConfig) -> Embed {
    let cap = if config.max_jail_minutes > 0 {
        format!("**{} min** per booking", config.max_jail_minutes)
    } else {
        "**Uncapped** — set with `/bail jailcap`".to_owned()
    };
    theme::notice("Police settings", None)
        .field(
            "Bail",
            format!("**{}%** of the base prices", percent(config.bail_rate)),
            true,
        )
        .field("Jail cap", cap, true)
}

impl BailCommand {
    async fn change(
        &self,
        context: &CommandContext<'_>,
        action: &str,
        current: PoliceConfig,
    ) -> Result<Embed, CommandError> {
        let invocation = context.invocation;
        if action == "jailcap" {
            let Some(minutes) = invocation.integer("minutes").filter(|m| *m >= 0) else {
                return Ok(theme::failure(
                    "Invalid minutes",
                    Some("Use a whole number such as `20`, or `0` for no cap."),
                ));
            };
            let minutes = i32::try_from(minutes).unwrap_or(i32::MAX);
            self.store
                .write(
                    POLICE_CONFIG,
                    &PoliceConfig {
                        max_jail_minutes: minutes,
                        ..current
                    },
                )
                .await?;
            return Ok(theme::success(
                "Sentence cap updated",
                Some(&if minutes > 0 {
                    format!(
                        "Jail is capped at **{minutes} min** per booking. Bail is **not** capped."
                    )
                } else {
                    "The cap is off.".to_owned()
                }),
            ));
        }
        let rate = if action == "reset" {
            1.0
        } else {
            let Some(given) = invocation.integer("percent").filter(|p| *p >= 0) else {
                return Ok(theme::failure(
                    "Invalid percentage",
                    Some("Use a whole number such as `150`."),
                ));
            };
            #[allow(clippy::cast_precision_loss)]
            let rate = given as f64 / 100.0;
            rate
        };
        self.store
            .write(
                POLICE_CONFIG,
                &PoliceConfig {
                    bail_rate: rate,
                    ..current
                },
            )
            .await?;
        // The effect on a real charge: "1.5" means nothing until it lands on a price.
        let example = PenalCode::get(Some("PC 100"))
            .map(|c| {
                format!(
                    " — `PC 100` is now ${}.",
                    c.bail_at(rate).map(|b| b.to_string()).unwrap_or_default()
                )
            })
            .unwrap_or_default();
        Ok(theme::success(
            "Bail rate updated",
            Some(&format!(
                "Now **{}%** of the base prices.{example}",
                percent(rate)
            )),
        ))
    }
}

impl SlashCommand for BailCommand {
    fn name(&self) -> &str {
        "bail"
    }

    fn build(&self) -> CommandSpec {
        let action = ["show", "set", "reset", "jailcap"].iter().fold(
            OptionSpec::new(OptionKind::String, "action", "What to do").required(),
            |option, a| option.choice(Choice::string(*a, *a)),
        );
        CommandSpec::new("bail", "Mod - Bail prices and the sentence cap")
            .option(action)
            .option(OptionSpec::new(
                OptionKind::Integer,
                "percent",
                "Percentage of the base price, e.g. 150",
            ))
            .option(OptionSpec::new(
                OptionKind::Integer,
                "minutes",
                "jailcap: the most jail one booking can carry. 0 removes the cap.",
            ))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let action = context.invocation.string("action").unwrap_or("show");
            let current: PoliceConfig = self
                .store
                .read(POLICE_CONFIG, PoliceConfig::default())
                .unwrap_or_default();
            let caller = &context.invocation.caller;
            let embed = if action == "show" {
                settings(&current)
            } else if !self.access.allows(RequiredAccess::Mod, caller) {
                theme::denied(
                    "Not allowed",
                    Some(&self.access.explain_refusal(caller, RequiredAccess::Mod)),
                )
            } else {
                self.change(&context, action, current).await?
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
