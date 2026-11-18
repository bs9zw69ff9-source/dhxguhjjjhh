//! `/testmode` and `/tester` (C# `TestModeCommand.cs`, `TesterCommand.cs`): Pavlov's own
//! whitelist, and locking a server to it.
//!
//! - `/testmode` REFUSES TO LOCK A SERVER TO AN EMPTY WHITELIST: that turns away everyone, the
//!   caller included, and the way back is a command against a server nobody can reach.
//! - It RESTARTS the server, because Game.ini is only read at start; a config edit left on a
//!   running server would look like it had worked. Players get the same warning and grace as
//!   `/serverswitch`.
//! - `/tester` works PER INSTALL, because a whitelist edited on two servers out of three looks,
//!   from the player's side, exactly like the third server being broken.

use std::path::Path;
use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_core::embed::Embed;
use pavlov_core::provisioning::text::set_game_ini_value;
use pavlov_core::servers::installs::{game_ini_path, whitelist_path};
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_core::text::case::eq_ignore_case;
use pavlov_host::moderation::AuditLog;
use pavlov_host::notice::{GRACE, NoticeResult, PlayerNotice};
use pavlov_host::service_control::{UnitAction, UnitControl, UnitResult};
use pavlov_storage::atomic_file::write_text_async;
use pavlov_storage::game_files::GameFileGuard;
use pavlov_storage::whitelist::{WhitelistFile, WhitelistResult, entry};

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// The most of systemd's answer shown.
const DETAIL_LIMIT: usize = 400;
/// Whitelist entries listed per install.
const LIST_SHOWN: usize = 40;

fn refuse(access: &Access, caller: &Caller, required: RequiredAccess) -> Option<Embed> {
    (!access.allows(required, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, required)),
        )
    })
}

fn install_name(install: &str) -> String {
    Path::new(install)
        .file_name()
        .map_or_else(|| install.to_owned(), |n| n.to_string_lossy().into_owned())
}

fn truncate(text: &str) -> String {
    if text.chars().count() <= DETAIL_LIMIT {
        text.to_owned()
    } else {
        format!("{}\n…", text.chars().take(DETAIL_LIMIT).collect::<String>())
    }
}

// ---- /testmode ----

pub struct TestModeCommand {
    pub services: Arc<dyn UnitControl>,
    pub notice: Arc<PlayerNotice>,
    pub whitelist: Arc<WhitelistFile>,
    pub guard: Arc<GameFileGuard>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
    /// The install roots, in server order.
    pub installs: Vec<String>,
    /// The pause between the warning and the restart; [`GRACE`] in production.
    pub grace: std::time::Duration,
}

impl std::fmt::Debug for TestModeCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TestModeCommand").finish_non_exhaustive()
    }
}

/// The outcome.
#[must_use]
#[allow(clippy::too_many_arguments)]
pub fn test_mode_report(
    number: i32,
    unit: &str,
    enabled: bool,
    listed: usize,
    whitelist: &str,
    warned: &NoticeResult,
    result: &UnitResult,
    advice: Option<&str>,
) -> Embed {
    let state = if enabled { "on" } else { "off" };
    let mut embed = if result.ok {
        theme::success(
            &format!("Test mode {state} — server {number}"),
            Some(&if enabled {
                format!(
                    "`{}` is back up and will only admit the **{listed}** name(s) on its whitelist.",
                    Sanitize::code(Some(unit))
                )
            } else {
                format!(
                    "`{}` is back up and open to everyone again.",
                    Sanitize::code(Some(unit))
                )
            }),
        )
    } else {
        theme::failure(
            &format!("Test mode {state} — server {number} did not restart"),
            Some(&format!(
                "`bWhitelist={enabled}` IS written, but `{}` did not come back - so the change has NOT taken effect yet.",
                Sanitize::code(Some(unit))
            )),
        )
    };
    if enabled {
        embed = embed.field(
            "Who may join",
            format!(
                "The names in `{}` — add more with `/tester add`.",
                Sanitize::code(Some(whitelist))
            ),
            false,
        );
    }
    let said = if warned.delivered {
        format!("Yes, {}s beforehand", GRACE.as_secs())
    } else {
        format!("No — {}", warned.detail)
    };
    embed = embed.field("Players warned", said, true);
    if !result.ok {
        embed = embed.field(
            "systemd said",
            format!(
                "```\n{}\n```",
                Sanitize::code(Some(&truncate(&result.detail)))
            ),
            false,
        );
    }
    if let Some(advice) = advice {
        embed = embed.field(
            format!("{} How to fix this", theme::WARN),
            advice.to_owned(),
            false,
        );
    }
    embed
}

impl TestModeCommand {
    async fn run(&self, context: &CommandContext<'_>) -> Result<Embed, CommandError> {
        let invocation = context.invocation;
        let number = invocation
            .integer("server")
            .and_then(|n| i32::try_from(n).ok())
            .unwrap_or(0);
        let enabled = invocation.boolean("enabled") == Some(true);
        let Some(unit) = self.services.unit_for(number).map(str::to_owned) else {
            return Ok(theme::failure(
                "No such server",
                Some(&format!(
                    "Only {} unit(s) are configured. Set `PAVLOV_UNITS` if that is wrong.",
                    self.services.units().len()
                )),
            ));
        };
        let Some(install) = usize::try_from(number - 1)
            .ok()
            .and_then(|i| self.installs.get(i))
        else {
            return Ok(theme::failure(
                "No install for that server",
                Some(&format!(
                    "Server {number} has a unit but no install path. Set `PAVLOV_BASES` so the two line up."
                )),
            ));
        };
        let game_ini = game_ini_path(install);
        let whitelist = whitelist_path(install);
        // IGNORE_PATHS holds here as anywhere: a second bot must not edit another's install.
        if let Some(refused) = self.guard.problem(Some(&game_ini)) {
            return Ok(theme::failure("Refused", Some(&refused)));
        }
        let listed = self.whitelist.read(&whitelist).len();
        if enabled && listed == 0 {
            return Ok(theme::failure(
                "The whitelist is empty",
                Some(&format!(
                    "Turning test mode on now would lock out **everyone**, yourself included.\nAdd names first - \
                     `/tester add` writes to this file:\n`{}`",
                    Sanitize::code(Some(&whitelist))
                )),
            ));
        }
        let existing = match tokio::fs::read_to_string(&game_ini).await {
            Ok(text) => text,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(theme::failure(
                    "No Game.ini",
                    Some(&format!(
                        "`{}` does not exist, so there is nothing to change. Start the server once so it writes its \
                         config, or provision it with `/provisionserver`.",
                        Sanitize::code(Some(&game_ini))
                    )),
                ));
            }
            Err(error) => {
                tracing::error!(path = %game_ini, %error, "could not read Game.ini");
                return Ok(theme::failure(
                    "Could not write Game.ini",
                    Some(&Sanitize::code(Some(&error.to_string()))),
                ));
            }
        };
        let updated = set_game_ini_value(
            &existing,
            "bWhitelist",
            if enabled { "true" } else { "false" },
        );
        if existing == updated {
            return Ok(theme::notice(
                &format!("Already {}", if enabled { "on" } else { "off" }),
                Some(&format!(
                    "`bWhitelist` on server {number} is already `{enabled}`. Nothing was changed and the server was not \
                     restarted."
                )),
            ));
        }
        if let Err(error) = write_text_async(game_ini.clone().into(), updated).await {
            tracing::error!(path = %game_ini, %error, "could not update Game.ini");
            return Ok(theme::failure(
                "Could not write Game.ini",
                Some(&Sanitize::code(Some(&error.to_string()))),
            ));
        }
        let who = &invocation.username;
        self.audit
            .record(
                "testmode",
                who,
                &unit,
                Some(if enabled { "on" } else { "off" }),
            )
            .await?;
        tracing::warn!(state = if enabled { "ON" } else { "OFF" }, user = %who, number, unit = %unit, listed, "TESTMODE");
        // THE RESTART IS THE POINT: Game.ini is read at start.
        let message = if enabled {
            "Server going whitelist-only..."
        } else {
            "Server restarting..."
        };
        let warned = self.notice.warn(number, message).await;
        context
            .reply(Reply::embed(
                theme::notice(
                    &format!("{} Test mode {} for server {number}", theme::WARN, if enabled { "on" } else { "off" }),
                    Some(&format!(
                        "`bWhitelist={enabled}` written to `{}`.\nRestarting `{unit}` in {}s so the game reads it…",
                        Sanitize::code(Some(&game_ini)),
                        GRACE.as_secs()
                    )),
                )
                .brand(None),
            ))
            .await?;
        tokio::time::sleep(self.grace).await;
        let result = self.services.run(&unit, UnitAction::Restart).await;
        let advice = self.services.advice(std::slice::from_ref(&result));
        Ok(test_mode_report(
            number,
            &unit,
            enabled,
            listed,
            &whitelist,
            &warned,
            &result,
            advice.as_deref(),
        ))
    }
}

impl SlashCommand for TestModeCommand {
    fn name(&self) -> &str {
        "testmode"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let server = self.services.units().iter().enumerate().fold(
            OptionSpec::new(OptionKind::Integer, "server", "Which server").required(),
            |option, (i, unit)| {
                let n = i64::try_from(i + 1).unwrap_or(i64::MAX);
                option.choice(Choice::integer(format!("Server {n} ({unit})"), n))
            },
        );
        CommandSpec::new(
            "testmode",
            "Admin - Whitelist-only test mode: only names in whitelist.txt may join",
        )
        .option(server)
        .option(
            OptionSpec::new(
                OptionKind::Boolean,
                "enabled",
                "True locks the server to the whitelist, False reopens it",
            )
            .required(),
        )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = match refuse(
                &self.access,
                &context.invocation.caller,
                RequiredAccess::Admin,
            ) {
                Some(refusal) => refusal,
                None => self.run(&context).await?,
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /tester ----

pub struct TesterCommand {
    pub whitelist: Arc<WhitelistFile>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
    pub installs: Vec<String>,
}

impl std::fmt::Debug for TesterCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TesterCommand").finish_non_exhaustive()
    }
}

fn by_hand(entry: &str) -> String {
    format!(
        "Append this line to each failed whitelist:\n```\n{}\n```",
        Sanitize::code(Some(entry))
    )
}

impl TesterCommand {
    fn list(&self) -> Embed {
        let sections: Vec<String> = self
            .installs
            .iter()
            .map(|install| {
                let entries = self.whitelist.read(&whitelist_path(install));
                let body = if entries.is_empty() {
                    "*empty*".to_owned()
                } else {
                    let shown: Vec<String> = entries
                        .iter()
                        .take(LIST_SHOWN)
                        .map(|e| format!("`{}`", Sanitize::code(Some(e))))
                        .collect();
                    let more = entries.len().saturating_sub(LIST_SHOWN);
                    let more = if more > 0 {
                        format!(" *and {more} more*")
                    } else {
                        String::new()
                    };
                    format!("{}{more}", shown.join(" "))
                };
                let noun = if entries.len() == 1 {
                    "entry"
                } else {
                    "entries"
                };
                format!(
                    "**{}** — {} {noun}\n{body}",
                    install_name(install),
                    entries.len()
                )
            })
            .collect();
        theme::notice("Whitelist", Some(&sections.join("\n\n")))
    }

    fn check(&self, entry: &str) -> Embed {
        let listed: Vec<(String, String, bool)> = self
            .installs
            .iter()
            .map(|i| {
                let path = whitelist_path(i);
                let has = self
                    .whitelist
                    .read(&path)
                    .iter()
                    .any(|e| eq_ignore_case(e, entry));
                (install_name(i), path, has)
            })
            .collect();
        let missing = listed.iter().filter(|(_, _, has)| !has).count();
        let code = Sanitize::code(Some(entry));
        let mut embed = if missing == 0 {
            let lines: Vec<String> = listed
                .iter()
                .map(|(i, _, _)| format!("{} `{i}` — whitelisted", theme::OK))
                .collect();
            theme::success(
                &format!("{code} is whitelisted everywhere"),
                Some(&lines.join("\n")),
            )
        } else {
            let lines: Vec<String> = listed
                .iter()
                .map(|(i, _, has)| {
                    let (mark, word) = if *has {
                        (theme::OK, "whitelisted")
                    } else {
                        (theme::BAD, "NOT whitelisted")
                    };
                    format!("{mark} `{i}` — {word}")
                })
                .collect();
            theme::warning(
                &format!("{code} is missing from {missing} server(s)"),
                Some(&lines.join("\n")),
            )
        };
        if missing > 0 {
            embed = embed.field(
                "Add them by hand",
                format!(
                    "The bot does not write files the game server owns. Append this line to each whitelist below \
                     that is missing it:\n```\n{code}\n```"
                ),
                false,
            );
            for (install, path, _) in listed.iter().filter(|(_, _, has)| !has) {
                embed = embed.field(install.clone(), format!("`{path}`"), false);
            }
        }
        tracing::info!(entry, missing, total = self.installs.len(), "tester check");
        embed
    }

    async fn edit(
        &self,
        username: &str,
        entry: &str,
        removing: bool,
    ) -> Result<Embed, CommandError> {
        let mut results: Vec<WhitelistResult> = Vec::with_capacity(self.installs.len());
        for install in &self.installs {
            let path = whitelist_path(install);
            results.push(if removing {
                self.whitelist.remove(&path, entry).await
            } else {
                self.whitelist.add(&path, entry).await
            });
        }
        let failed: Vec<&WhitelistResult> = results.iter().filter(|r| !r.ok).collect();
        let changed = results.iter().filter(|r| r.changed).count();
        let verb = if removing {
            "removed from"
        } else {
            "whitelisted on"
        };
        let lines: Vec<String> = results
            .iter()
            .map(|r| {
                let said = if !r.ok {
                    format!("FAILED: {}", r.error.as_deref().unwrap_or("unknown error"))
                } else if r.changed {
                    "done".to_owned()
                } else if removing {
                    "was not on it".to_owned()
                } else {
                    "already on it".to_owned()
                };
                format!(
                    "{} `{}` — {said}",
                    if r.ok { theme::OK } else { theme::BAD },
                    r.install()
                )
            })
            .collect();
        let code = Sanitize::code(Some(entry));
        let mut embed = if failed.is_empty() {
            theme::success(
                &format!("{code} {verb} {} server(s)", results.len()),
                Some(&lines.join("\n")),
            )
        } else {
            theme::warning(
                &format!(
                    "{code} — {} of {} server(s) failed",
                    failed.len(),
                    results.len()
                ),
                Some(&lines.join("\n")),
            )
        };
        if !failed.is_empty() {
            embed = embed.field("Finish these by hand", by_hand(entry), false);
            for r in &failed {
                embed = embed.field(r.install(), format!("`{}`", r.path), false);
            }
        }
        self.audit
            .record(
                if removing {
                    "tester-remove"
                } else {
                    "tester-add"
                },
                username,
                entry,
                Some(&format!(
                    "{changed} of {} install(s) changed",
                    results.len()
                )),
            )
            .await?;
        Ok(embed)
    }
}

impl SlashCommand for TesterCommand {
    fn name(&self) -> &str {
        "tester"
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let player = || {
            OptionSpec::new(
                OptionKind::String,
                "playerid",
                "In-game username, exactly as it appears",
            )
            .required()
            .autocomplete()
        };
        CommandSpec::new(
            "tester",
            "Admin - Manage Pavlov's own whitelist across every install",
        )
        .option(OptionSpec::sub("add", "Whitelist a player on every server").option(player()))
        .option(
            OptionSpec::sub("remove", "Take a player off every server's whitelist")
                .option(player()),
        )
        .option(
            OptionSpec::sub(
                "check",
                "Is this player whitelisted, and what line adds them",
            )
            .option(player()),
        )
        .option(OptionSpec::sub("list", "Who is whitelisted, per server"))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            // ADMIN: this grants access to a server somebody is otherwise locked out of.
            let embed = if let Some(refusal) =
                refuse(&self.access, &invocation.caller, RequiredAccess::Admin)
            {
                refusal
            } else {
                let sub = invocation.subcommand().unwrap_or("list");
                let name = entry(invocation.string("playerid"));
                match sub {
                    "list" => self.list(),
                    _ if name.is_empty() => {
                        theme::failure("That name has nothing usable in it", None)
                    }
                    "add" | "remove" => {
                        self.edit(&invocation.username, &name, sub == "remove")
                            .await?
                    }
                    _ => self.check(&name),
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
