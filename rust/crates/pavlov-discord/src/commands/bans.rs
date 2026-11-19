//! `/tempban`, `/permban`, `/unban` and `/checkban` (C# `BanCommands.cs`).
//!
//! - RECORD FIRST, THEN ENFORCE: a failed enforce leaves a record the sweep retries; the other
//!   way round leaves somebody kicked with nothing saying why and nothing to lift.
//! - NO RECORD, NO BAN: a refused write (an unreadable list is never written over) enforces
//!   nothing, or a temp ban would become a native ban nothing lifts.
//! - REPLACING IS LIFTING: a ban above the caller's tier cannot be swapped for a short one, and
//!   a master owner's pardon stands against everyone below them.
//! - The ACCOUNT ID IS FLAGGED ONLY FOR A PERMANENT BAN: an id flag never expires, so on a temp
//!   ban it keeps catching them after they served it.
//! - No record is NOT "not banned": the server reads its own ban file, so it is READ and the
//!   answer says which; `/unban` lifts a file-only entry too.

use std::sync::Arc;

use chrono::TimeDelta;
use futures_util::future::BoxFuture;
use pavlov_core::data::SerializedStore;
use pavlov_core::embed::Embed;
use pavlov_core::moderation::{BanRecord, BanRules, StaffHierarchy, StaffTier};
use pavlov_core::skin::{police, theme};
use pavlov_core::text::Sanitize;
use pavlov_core::time::{BanDurations, Clock, EasternTime};
use pavlov_host::logs::ip_tracking::IpTrackingService;
use pavlov_host::moderation::bans::BanService;
use pavlov_host::moderation::master_names::StoredMasterNames;
use pavlov_host::moderation::{
    AuditLog, BanFileLookup, BanFileStatus, EnforcementResult, ServerBanFile,
};
use pavlov_storage::datasets::TEMP_BANS;
use pavlov_storage::ledger_store::SaveBanWrite;

use crate::access::{Access, RequiredAccess};
use crate::caller::Caller;
use crate::command::{Choice, CommandSpec, OptionKind, OptionSpec};
use crate::commands::check_ban::ban_history;
use crate::interaction::Reply;
use crate::slash::{CommandContext, CommandError, SlashCommand};

/// What the server's own ban file adds to a NEGATIVE answer, or `None` when nothing worth
/// saying: terse on the clean case, said only where it changes the answer.
#[must_use]
pub fn describe_ban_file(lookup: &BanFileLookup) -> Option<String> {
    match lookup.status {
        BanFileStatus::Read => lookup.entry.as_ref().map(|entry| {
            format!(
                "{} The server's ban file still lists them, so they're banned in game: {} — {}",
                theme::DENY,
                Sanitize::code(Some(&Sanitize::redact_private(Some(&entry.reason)))),
                Sanitize::code(Some(&entry.unban))
            )
        }),
        BanFileStatus::Missing => Some(format!(
            "{} Couldn't check the server's ban file (missing) - set `BLACKLIST_PATH`.",
            theme::WARN
        )),
        BanFileStatus::Unreadable => Some(format!(
            "{} Couldn't read the server's ban file - check its permissions.",
            theme::WARN
        )),
        BanFileStatus::Disabled => None,
    }
}

fn not_banned(player: &str, note: Option<&str>) -> String {
    match note {
        Some(note) => format!(
            "**{}** is not banned.\n\n{note}",
            Sanitize::code(Some(player))
        ),
        None => format!("**{}** is not banned.", Sanitize::code(Some(player))),
    }
}

/// What every ban command is wired to.
pub struct BanParts {
    pub bans: Arc<BanService>,
    pub tracking: Arc<IpTrackingService>,
    pub store: Arc<SerializedStore>,
    pub access: Arc<Access>,
    pub audit: Arc<AuditLog>,
    pub masters: Arc<StoredMasterNames>,
    pub ban_file: Arc<ServerBanFile>,
    pub clock: Arc<dyn Clock>,
}

impl std::fmt::Debug for BanParts {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BanParts").finish_non_exhaustive()
    }
}

fn refuse(access: &Access, caller: &Caller, required: RequiredAccess) -> Option<Embed> {
    (!access.allows(required, caller)).then(|| {
        theme::denied(
            "Not allowed",
            Some(&access.explain_refusal(caller, required)),
        )
    })
}

fn player_option(description: &str) -> OptionSpec {
    OptionSpec::new(OptionKind::String, "playerid", description)
        .required()
        .autocomplete()
}

impl BanParts {
    /// Write a ban and enforce it. `duration` `None` is permanent.
    ///
    /// # Errors
    /// When the store or the audit log fails outright.
    pub async fn issue(
        &self,
        caller: &Caller,
        username: &str,
        player: &str,
        reason: &str,
        duration: Option<TimeDelta>,
    ) -> Result<Embed, CommandError> {
        let name = Sanitize::id(Some(player));
        if name.is_empty() {
            return Ok(theme::failure(
                "That name has nothing usable in it",
                Some("After sanitising there was no identifier left to ban."),
            ));
        }
        let account = self.tracking.account_by_name(&name);
        let tier = self.access.tier_of(caller);
        let now = self.clock.now();
        let permanent = duration.is_none();
        let master = StaffHierarchy::name(StaffTier::MasterOwner);
        if tier < StaffTier::MasterOwner && self.masters.is_pardoned(&name) {
            return Ok(theme::denied(
                "Above your authority",
                Some(&format!(
                    "A **{master}** unbanned **{}**. Only a {master} can ban them again.",
                    Sanitize::code(Some(&name))
                )),
            ));
        }
        let expires = duration.map(|d| now + d);
        let record = BanRecord {
            // What the ban is ENFORCED against, so the lift names the same thing.
            unique_id: account.as_ref().map(|a| a.id.clone()),
            reason: Some(Sanitize::message(Some(reason))),
            moderator: Some(username.to_owned()),
            at: Some(now),
            expires,
            permanent,
            duration_label: Some(expires.map_or_else(
                || "Permanent".to_owned(),
                |e| EasternTime::time_left(e, now),
            )),
            tier: Some(tier),
            ..BanRecord::for_player(name.clone())
        };
        let mut outranked: Option<BanRecord> = None;
        let saved = self
            .store
            .update(TEMP_BANS, Vec::new, |mut bans: Vec<BanRecord>| {
                if let Some(standing) =
                    BanRules::protected_from_replacement(&bans, &name, tier, now)
                {
                    outranked = Some(standing.clone());
                    return None;
                }
                bans.retain(|b| !BanRules::same_player(Some(&b.player_id), Some(&name)));
                bans.push(record.clone());
                Some(bans)
            })
            .await?;
        if let Some(standing) = outranked {
            return Ok(theme::denied(
                "Above your authority",
                Some(&format!(
                    "**{}** is already banned by a **{}**, and a new ban would replace it. You are **{}**.",
                    Sanitize::code(Some(&name)),
                    StaffHierarchy::name(standing.tier.unwrap_or(StaffTier::None)),
                    StaffHierarchy::name(tier)
                )),
            ));
        }
        if !saved.ok {
            let error = saved.error.unwrap_or_else(|| "unknown error".into());
            tracing::error!(player = %name, %error, "ban NOT issued: the record could not be saved");
            return Ok(theme::failure(
                "Ban not issued",
                Some(&format!(
                    "The ban record could not be saved, so nothing was enforced. ({})",
                    Sanitize::code(Some(&error))
                )),
            ));
        }
        // A master owner banning somebody they pardoned ends the pardon.
        if tier == StaffTier::MasterOwner
            && let Err(error) = self.masters.clear_pardon(&name).await
        {
            tracing::error!(player = %name, %error, "could not clear a master pardon");
        }
        let account_id = account.as_ref().map(|a| a.id.as_str());
        let enforcement = self.bans.hard_enforce(&name, account_id, true, true).await;
        let flags = match account_id {
            Some(id) => Some(self.tracking.request_flag(id, permanent).await),
            None => None,
        };
        self.audit
            .record(
                if permanent { "permban" } else { "tempban" },
                username,
                &name,
                record.reason.as_deref(),
            )
            .await?;
        tracing::info!(
            kind = if permanent { "permanent" } else { "temporary" },
            player = %name,
            target = enforcement.target.as_deref().unwrap_or("none"),
            by = username,
            accepted = enforcement.servers,
            total = self.bans.server_count(),
            pending = flags.as_ref().is_some_and(|f| f.pending),
            at = %EasternTime::stamp(Some(now)),
            "ban"
        );
        let words = &police().words;
        let title = if permanent {
            words.banned_for_good
        } else {
            words.banned_for_a_while
        };
        let length = match expires {
            None => "Permanent".to_owned(),
            Some(e) => format!(
                "{} (until {})",
                record.duration_label.as_deref().unwrap_or(""),
                theme::relative(e)
            ),
        };
        let mut embed = theme::punishment(
            &format!("{} {title}", theme::DENY),
            Some(&format!(
                "**{}** — {}",
                Sanitize::code(Some(&name)),
                Sanitize::code(Some(&Sanitize::redact_private(Some(
                    record
                        .reason
                        .as_deref()
                        .filter(|r| !r.is_empty())
                        .unwrap_or("no reason given")
                ))))
            )),
        )
        .field("Length", length, true)
        .field("Issued by", username.to_owned(), true);
        embed = with_enforcement(embed, &enforcement);
        if flags.is_some_and(|f| f.pending) {
            embed = embed.field(
                "Address not yet known",
                "They have never been seen disconnecting, so no address is confirmed. It will be flagged automatically \
                 the moment one is.",
                false,
            );
        }
        Ok(embed)
    }
}

// ---- /tempban ----

pub struct TempBanCommand {
    pub parts: Arc<BanParts>,
}

impl std::fmt::Debug for TempBanCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TempBanCommand").finish_non_exhaustive()
    }
}

impl SlashCommand for TempBanCommand {
    fn name(&self) -> &str {
        "tempban"
    }

    fn build(&self) -> CommandSpec {
        // LENGTHS, not dates: a moderator has an opinion about "three days", not a timestamp.
        let duration = BanDurations::all().iter().fold(
            OptionSpec::new(OptionKind::String, "duration", "How long the ban lasts").required(),
            |option, (value, _)| option.choice(Choice::string(*value, *value)),
        );
        CommandSpec::new("tempban", "Mod - Ban a player for a set length of time")
            .option(player_option("Player ID or username"))
            .option(
                OptionSpec::new(OptionKind::String, "reason", "Why they are being banned")
                    .required(),
            )
            .option(duration.choice(Choice::string("Permanent", BanDurations::PERMANENT)))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let p = &self.parts;
            let embed = if let Some(refusal) =
                refuse(&p.access, &invocation.caller, RequiredAccess::Mod)
            {
                refusal
            } else {
                let choice = invocation.string("duration").unwrap_or("");
                let duration = BanDurations::length(Some(choice));
                if duration.is_none() && !choice.eq_ignore_ascii_case(BanDurations::PERMANENT) {
                    theme::failure(
                        "That duration makes no sense",
                        Some(
                            "Use a length like `1d`, `3d 4h` or `1mo`. Calendar dates are not accepted.",
                        ),
                    )
                } else {
                    p.issue(
                        &invocation.caller,
                        &invocation.username,
                        invocation.string("playerid").unwrap_or(""),
                        invocation.string("reason").unwrap_or("No reason given"),
                        duration,
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /permban ----

pub struct PermBanCommand {
    pub parts: Arc<BanParts>,
}

impl std::fmt::Debug for PermBanCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PermBanCommand").finish_non_exhaustive()
    }
}

impl SlashCommand for PermBanCommand {
    fn name(&self) -> &str {
        "permban"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("permban", "Admin - Ban a player permanently")
            .option(player_option("Player ID or username"))
            .option(
                OptionSpec::new(OptionKind::String, "reason", "Why they are being banned")
                    .required(),
            )
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let p = &self.parts;
            let embed = match refuse(&p.access, &invocation.caller, RequiredAccess::Admin) {
                Some(refusal) => refusal,
                None => {
                    p.issue(
                        &invocation.caller,
                        &invocation.username,
                        invocation.string("playerid").unwrap_or(""),
                        invocation.string("reason").unwrap_or("No reason given"),
                        None,
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /unban ----

pub struct UnbanCommand {
    pub parts: Arc<BanParts>,
}

impl std::fmt::Debug for UnbanCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UnbanCommand").finish_non_exhaustive()
    }
}

impl UnbanCommand {
    /// A master owner's unban becomes a PARDON: nobody below them, and no automated path, can
    /// ban the player again until a master owner does.
    async fn pardon_if_master(
        &self,
        caller: &Caller,
        username: &str,
        player: &str,
        embed: Embed,
    ) -> Embed {
        let p = &self.parts;
        if p.access.tier_of(caller) != StaffTier::MasterOwner {
            return embed;
        }
        if matches!(p.masters.pardon(player).await, Ok(true)) {
            tracing::warn!(player, by = username, "MASTER PARDON");
            embed.field(
                "Master Owner pardon",
                "Nobody below a Master Owner can ban them again, and automatic bans will not touch them.",
                false,
            )
        } else {
            tracing::error!(
                player,
                "master pardon could not be saved - unbanned but NOT protected"
            );
            embed.field(
                format!("{} Pardon not saved", theme::WARN),
                "They are unbanned, but the pardon could not be written, so staff can still ban them.",
                false,
            )
        }
    }

    async fn run(
        &self,
        caller: &Caller,
        username: &str,
        raw: Option<&str>,
    ) -> Result<Embed, CommandError> {
        let p = &self.parts;
        let player = Sanitize::id(raw);
        let existing = p
            .bans
            .load_bans()
            .into_iter()
            .find(|b| BanRules::same_player(Some(&b.player_id), Some(&player)));
        let listed = p.ban_file.find(&player).await;
        let Some(existing) = existing else {
            // NO RECORD IS NOT "NOT BANNED": a ban can live only in their save (written in
            // game or by hand), or only in the server's own blacklist.txt.
            if let Some(in_save) = p.bans.save_ban(&player) {
                let account = p.tracking.account_by_name(&player);
                let result = p
                    .bans
                    .lift(&player, account.as_ref().map(|a| a.id.as_str()))
                    .await;
                p.audit
                    .record(
                        "unban",
                        username,
                        &player,
                        Some(&format!("Cleared from their save - {}", in_save.reason)),
                    )
                    .await?;
                let mut embed = theme::success(
                    "Ban cleared from their save",
                    Some(&format!(
                        "**{}** had no record with this bot, but their save held a ban. It is cleared.",
                        Sanitize::code(Some(&player))
                    )),
                )
                .field(
                    "Was",
                    format!(
                        "{} — {}",
                        Sanitize::code(Some(&Sanitize::redact_private(Some(&in_save.reason)))),
                        if in_save.unban_date.is_empty() {
                            "Permanent".to_owned()
                        } else {
                            format!("until {}", Sanitize::code(Some(&in_save.unban_date)))
                        }
                    ),
                    false,
                )
                .field("Lifted by", username.to_owned(), true);
                embed = with_lift_problems(embed, &result, &listed);
                return Ok(self
                    .pardon_if_master(caller, username, &player, embed)
                    .await);
            }
            if listed.listed() {
                return Ok(theme::notice(
                    "Listed in blacklist.txt only",
                    Some(&format!(
                        "**{}** has no ban with this bot or in their save, but the server's own `blacklist.txt` lists them, \
                         and the game refuses them for that. The bot no longer edits that file: remove their entry by hand in \
                         `{}`.",
                        Sanitize::code(Some(&player)),
                        Sanitize::code(Some(listed.path.as_deref().unwrap_or("blacklist.txt")))
                    )),
                ));
            }
            return Ok(theme::notice(
                "No ban to lift",
                Some(&not_banned(&player, describe_ban_file(&listed).as_deref())),
            ));
        };
        // THE HIERARCHY: without it any mod quietly unbans whoever the owner banned.
        let actor = p.access.tier_of(caller);
        if !StaffHierarchy::can_override(actor, existing.tier) {
            return Ok(theme::denied(
                "Above your authority",
                Some(&format!(
                    "That ban was issued by **{}**. You are **{}**.",
                    StaffHierarchy::name(existing.tier.unwrap_or(StaffTier::None)),
                    StaffHierarchy::name(actor)
                )),
            ));
        }
        // THE WHOLE LIFT, through the one path that does all of it.
        let result = p.bans.lift(&player, existing.unique_id.as_deref()).await;
        p.audit
            .record("unban", username, &player, existing.reason.as_deref())
            .await?;
        let words = &police().words;
        let mut embed = theme::success(
            words.ban_lifted,
            Some(&format!(
                "**{}** {}",
                Sanitize::code(Some(&player)),
                words.may_return
            )),
        )
        .field(
            "Was",
            format!(
                "{} — by {}",
                Sanitize::code(Some(&Sanitize::redact_private(Some(
                    existing.reason.as_deref().unwrap_or("no reason recorded")
                )))),
                existing.moderator.as_deref().unwrap_or("unknown")
            ),
            false,
        )
        .field("Lifted by", username.to_owned(), true);
        embed = with_lift_problems(embed, &result, &listed);
        Ok(self
            .pardon_if_master(caller, username, &player, embed)
            .await)
    }
}

/// Why a ban is not (yet) in force in the player's save, when it is not. Every caller that bans
/// shows the same words.
#[must_use]
pub fn enforcement_problem(result: &EnforcementResult) -> Option<(String, String)> {
    match &result.save {
        Some(SaveBanWrite::NoSave) => Some((
            "No save yet".into(),
            "They have never joined, so there is no save to write the ban into. They are kicked whenever they are \
             seen, and the ban is written the moment the game creates their save."
                .into(),
        )),
        Some(SaveBanWrite::Refused(reason)) => Some((
            format!("{} Not written to their save", theme::WARN),
            format!(
                "{}. The record is saved and they were kicked; the sweep retries every 30 seconds.",
                Sanitize::code(Some(reason))
            ),
        )),
        None => Some((
            format!("{} Not enforced", theme::WARN),
            "MODSAVE_PATH is not set, so there is no save to write the ban into. It is recorded, and they are \
             kicked whenever they are seen."
                .into(),
        )),
        Some(_) => None,
    }
}

fn with_enforcement(embed: Embed, result: &EnforcementResult) -> Embed {
    match enforcement_problem(result) {
        Some((title, body)) => embed.field(title, body, false),
        None => embed,
    }
}

/// What could still keep a lifted player out: a save the lift could not clear, or an entry in
/// the server's `blacklist.txt`, which the bot no longer edits.
fn with_lift_problems(
    mut embed: Embed,
    result: &EnforcementResult,
    listed: &BanFileLookup,
) -> Embed {
    match &result.save {
        Some(SaveBanWrite::Refused(reason)) => {
            embed = embed.field(
                format!("{} Not cleared from their save", theme::WARN),
                format!(
                    "The record is removed, but their save still holds the ban: {}. The game will keep refusing them \
                     until it is cleared.",
                    Sanitize::code(Some(reason))
                ),
                false,
            );
        }
        None => {
            embed = embed.field(
                format!("{} No save written", theme::WARN),
                "MODSAVE_PATH is not set, so there is no save to clear. Only the record was removed.",
                false,
            );
        }
        Some(_) => {}
    }
    if listed.listed() {
        embed = embed.field(
            format!("{} Still in blacklist.txt", theme::WARN),
            format!(
                "The server's own `blacklist.txt` lists them, and the game refuses them for that. The bot no longer \
                 edits that file: remove their entry by hand in `{}`.",
                Sanitize::code(Some(listed.path.as_deref().unwrap_or("blacklist.txt")))
            ),
            false,
        );
    }
    embed
}

impl SlashCommand for UnbanCommand {
    fn name(&self) -> &str {
        "unban"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("unban", "Mod - Lift a ban").option(player_option("Player to pardon"))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let invocation = context.invocation;
            let embed = match refuse(&self.parts.access, &invocation.caller, RequiredAccess::Mod) {
                Some(refusal) => refusal,
                None => {
                    self.run(
                        &invocation.caller,
                        &invocation.username,
                        invocation.string("playerid"),
                    )
                    .await?
                }
            };
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}

// ---- /checkban ----

pub struct CheckBanCommand {
    pub parts: Arc<BanParts>,
}

impl std::fmt::Debug for CheckBanCommand {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CheckBanCommand").finish_non_exhaustive()
    }
}

/// What the moderator field means: "in-game" and "auto" are opaque otherwise.
#[must_use]
pub fn ban_origin(moderator: Option<&str>) -> &'static str {
    match moderator {
        Some("in-game") => {
            "Found in their save, set in game or by hand rather than by this bot, and recorded here. It is lifted \
             when its date is reached, and `/unban` clears it from the save."
        }
        Some("auto") => {
            "This bot, automatically - VPN screening or ban evasion. The reason above says which. Nobody typed it."
        }
        _ => "A moderator, using the ban commands.",
    }
}

impl CheckBanCommand {
    fn with_history(&self, embed: Embed, player: &str) -> Embed {
        let p = &self.parts;
        let mut names = p
            .tracking
            .account_by_name(player)
            .map(|a| a.names)
            .unwrap_or_default();
        names.push(player.to_owned());
        match ban_history(&p.audit.all(), &names, player) {
            Some(history) => embed.field("Ban history", history, false),
            None => embed,
        }
    }

    async fn run(&self, raw: Option<&str>) -> Embed {
        let p = &self.parts;
        let player = Sanitize::id(raw);
        let now = p.clock.now();
        let words = &police().words;
        let bans = p.bans.load_bans();
        let Some(record) = bans
            .iter()
            .find(|b| BanRules::same_player(Some(&b.player_id), Some(&player)))
        else {
            let listed = p.ban_file.find(&player).await;
            let body = not_banned(&player, describe_ban_file(&listed).as_deref());
            let embed = if listed.listed() {
                theme::punishment(
                    &format!("{} {}", theme::DENY, words.banned_by_the_server),
                    Some(&body),
                )
                .field("Lift it", "`/unban` removes them from that file.", false)
            } else {
                theme::success("No active ban", Some(&body))
            };
            return self.with_history(embed, &player);
        };
        if !record.is_active_at(now) {
            let when = record
                .expires
                .map_or_else(|| "earlier".to_owned(), theme::relative);
            return self.with_history(
                theme::notice(
                    words.ban_served,
                    Some(&format!(
                        "**{}** served their ban; it expired {when}.",
                        Sanitize::code(Some(&player))
                    )),
                ),
                &player,
            );
        }
        // Redacted: an auto-ban reason embeds the address that triggered it.
        let mut embed = theme::punishment(
            &format!("{} {}", theme::DENY, words.banned),
            Some(&format!("**{}**", Sanitize::code(Some(&player)))),
        )
        .field(
            "Reason",
            Sanitize::code(Some(&Sanitize::redact_private(Some(
                record.reason.as_deref().unwrap_or("none recorded"),
            )))),
            false,
        )
        .field(
            "By",
            record.moderator.clone().unwrap_or_else(|| "unknown".into()),
            true,
        )
        .field(
            "Since",
            record
                .at
                .map_or_else(|| "unknown".to_owned(), theme::relative),
            true,
        )
        .field(
            "Expires",
            if record.permanent {
                "Never".to_owned()
            } else {
                record
                    .expires
                    .map_or_else(|| "unknown".to_owned(), theme::relative)
            },
            true,
        )
        .field(
            "Where this came from",
            ban_origin(record.moderator.as_deref()),
            false,
        );
        // The ORIGINAL offence behind an auto-ban, or an appeal gets "banned because banned".
        if !BanRules::is_real_ban(Some(record)) {
            let account = p.tracking.account_by_name(&player);
            let alts = account
                .as_ref()
                .map(|a| a.names.clone())
                .unwrap_or_default();
            let source = BanRules::source_ban_for(
                &bans,
                &player,
                account.as_ref().map(|a| a.id.as_str()),
                |n| p.tracking.account_by_name(n).map(|a| a.id),
                &alts,
            );
            embed = embed.field(
                "Original offence",
                source.map_or_else(
                    || "Ban evasion (no earlier record found)".to_owned(),
                    |s| {
                        format!(
                            "{} — by {}",
                            Sanitize::code(Some(&Sanitize::redact_private(s.reason.as_deref()))),
                            s.moderator.as_deref().unwrap_or("unknown")
                        )
                    },
                ),
                false,
            );
        }
        self.with_history(embed, &player)
    }
}

impl SlashCommand for CheckBanCommand {
    fn name(&self) -> &str {
        "checkban"
    }

    fn build(&self) -> CommandSpec {
        CommandSpec::new("checkban", "Check whether a player is banned")
            .option(player_option("Player ID or username"))
    }

    fn handle<'a>(
        &'a self,
        context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async move {
            let embed = self.run(context.invocation.string("playerid")).await;
            context.reply(Reply::embed(embed.brand(None))).await
        })
    }
}
