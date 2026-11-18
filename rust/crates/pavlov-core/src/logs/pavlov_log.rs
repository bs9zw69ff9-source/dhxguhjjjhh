//! Parsing one line of `Pavlov.log` (C# `Core/Logs/PavlovLogLine.cs`).
//!
//! Every pattern was validated against real server logs, and the distinctions they draw are
//! the whole basis of the ban-evasion system:
//!
//! - A CONFIRMED PAIRING is an address and a `UniqueId` on the SAME line - a disconnect line,
//!   typically. That pairing is CERTAIN and is the only thing allowed to flag an address or
//!   trigger an auto-ban.
//! - A JOIN-TIME ADDRESS is an "accept" line correlated to a later "login" line. That is a
//!   GUESS ([`super::JoinCorrelator`]), kept for display and never used to flag.
//!
//! Every extractor is field-order independent: Pavlov's field order is not stable across
//! builds, and a positional parse breaks silently on an update.
//!
//! Addresses and timestamps are matched with ASCII digits only. The C# used `\d`, which in
//! .NET also matches other scripts' digits; Pavlov writes ASCII, and a "digit" from another
//! script is not part of an address.

use chrono::{DateTime, NaiveDate, Utc};

use crate::patterns::{static_fancy_regex, static_regex};
use crate::text::case::{contains_ignore_case, eq_ignore_case};

static_regex!(
    TIMESTAMP =
        r"^\[([0-9]{4})\.([0-9]{2})\.([0-9]{2})-([0-9]{2})\.([0-9]{2})\.([0-9]{2}):([0-9]{3})\]"
);
static_regex!(
    ACCEPT = r"(?:NotifyAcceptingConnection accepted from:|NotifyAcceptedConnection:.*?RemoteAddr:|AddClientConnection:.*?RemoteAddr:)\s*((?:[0-9]{1,3}\.){3}[0-9]{1,3})"
);
static_regex!(IP_FIELD = r"(?i)RemoteAddr:\s*((?:[0-9]{1,3}\.){3}[0-9]{1,3})");
static_regex!(UNIQUE_ID_FIELD = r"(?i)UniqueId:\s*([^\s,]+)");
// The value ends at the next URL option, at a following "field: value" token, or at end of
// line. Real lines are `?Name=Bob userId: EOS:...` with no separator before userId, so a lazy
// match to the next `?` or `&` alone would swallow the id.
static_fancy_regex!(
    NAME_OPTION = r"(?i)[?&]Name=([^?&]+?)(?=[?&]|\s+(?:userId|userid|PlayerId|UniqueId|platform|pid)\s*[:=]|\s*$)"
);
static_regex!(LOGIN_ID = r"(?i)(?:userId|PlayerId|UniqueId|userid)\s*[:=]\s*([^\s,?&]+)");
static_regex!(LOGIN_MARKER = r"(?i)Login request:|Join request:");
static_regex!(RCON_BAN = r"(?i)Rcon:\s*BanPlayer\s+(\S+)");
static_regex!(RCON_UNBAN = r"(?i)Rcon:\s*UnbanPlayer\s+(\S+)");
// RCON+ (the menu mod) announces every command it runs as `LogTemp: Warning: Rcon Plus
// Command Executed: ...` or the same at Error level. The level is how the mod prints, not a
// failure, so both count.
static_regex!(RCON_PLUS = r"(?i)Rcon Plus Command Executed:\s*(.+?)\s*$");
// The base RCON channel, anchored on the LogTemp prefix so a chat line or a name containing
// "Rcon:" cannot forge one. The ": " after the bare word keeps it off the RCON+ line.
static_regex!(RCON_BASE = r"(?i)LogTemp:\s*Rcon:\s*(.+?)\s*$");
// The RCON+ pass/fail, on its own untimestamped line after the command.
static_regex!(RCON_RESULT_LINE = r"(?i)^\s*Successful:\s*(true|false)\b");
static_regex!(KILLER = r#"(?i)"Killer":\s*"([^"]*)""#);
static_regex!(KILLED = r#"(?i)"Killed":\s*"([^"]*)""#);
static_regex!(KILLED_BY = r#"(?i)"KilledBy":\s*"([^"]*)""#);
static_regex!(
    STRICT_IPV4 =
        r"^(?:(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])$"
);

/// An address and account seen together on ONE line - a certain pairing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConfirmedPairing {
    pub ip: String,
    pub id: String,
    /// Some close lines carry `?Name=` as well; `None` when this one did not.
    pub name: Option<String>,
}

/// A login or join request. The name may be absent: some lines carry only the id.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LoginRequest {
    pub name: Option<String>,
    pub id: String,
}

/// The fields of a kill record a line carried; the caller assembles split records.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KillEvent {
    /// Empty for a world kill (fall damage, an explosion with no owner).
    pub killer: Option<String>,
    pub killed: Option<String>,
    pub killed_by: Option<String>,
}

/// One RCON action the game logged: a base RCON verb or an RCON+ menu command.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RconAction {
    /// True for an RCON+ menu command, false for a base RCON verb.
    pub plus: bool,
    /// The command word. A base verb Pavlov logs under an internal name (`BanPlayer`) is
    /// mapped back to the verb that was sent (`Ban`).
    pub verb: String,
    /// The player who ran it, for RCON+ (the menu logs the acting player first). `None` for
    /// base RCON, which records no issuer.
    pub instigator: Option<String>,
    /// The rest of the command after the verb and the instigator.
    pub argument: Option<String>,
    /// Whether RCON+ reported it took effect, filled in by the ingest layer from the line
    /// after; always `None` for base RCON.
    pub successful: Option<bool>,
}

impl RconAction {
    /// The command as it reads: the verb and its argument, without the instigator.
    #[must_use]
    pub fn command(&self) -> String {
        match self.argument.as_deref() {
            Some(a) if !a.is_empty() => format!("{} {a}", self.verb),
            _ => self.verb.clone(),
        }
    }

    /// The whole command as executed - verb, instigator and argument - for matching a log
    /// line against a command the bot sent.
    #[must_use]
    pub fn full(&self) -> String {
        [
            Some(self.verb.as_str()),
            self.instigator.as_deref(),
            self.argument.as_deref(),
        ]
        .into_iter()
        .flatten()
        .filter(|p| !p.is_empty())
        .collect::<Vec<_>>()
        .join(" ")
    }
}

/// RCON+ verbs that act on the server, not a player, so their first argument is not an
/// instigator: `CleanUp Items` is a cleanup of items, not something "Items" did.
const RCON_PLUS_SERVER_VERBS: [&str; 1] = ["CleanUp"];

/// Verbs that are not audit-worthy: the RCON server narrating its own socket, and the
/// read-only polls the bot and the menu issue constantly. Without this the feed is a line
/// every ~70 seconds saying nothing changed, burying the ban that did.
const RCON_NOISE: [&str; 11] = [
    "User",
    "Connection",
    "RefreshList",
    "ServerInfo",
    "ItemList",
    "MapList",
    "Banlist",
    "ModeratorList",
    "UGCModList",
    "InspectList",
    "InspectAll",
];

/// Base-RCON verbs Pavlov echoes under the internal handler's name. BASE RCON ONLY: an RCON+
/// command genuinely named `BanPlayer` is a different command and keeps its name.
const BASE_RCON_VERB_ALIASES: [(&str, &str); 3] = [
    ("BanPlayer", "Ban"),
    ("KickPlayer", "Kick"),
    ("UnbanPlayer", "Unban"),
];

fn group<'a>(captures: &regex::Captures<'a>, i: usize) -> Option<&'a str> {
    captures.get(i).map(|m| m.as_str())
}

/// The line's own timestamp, in UTC. Pavlov writes UTC as `[YYYY.MM.DD-HH.MM.SS:mmm]`.
///
/// The line's stamp, not the read time, is what makes the correlation window meaningful when
/// the tailer catches up on a backlog. A corrupt timestamp is `None`, never a reason to stop
/// reading the log.
#[must_use]
pub fn timestamp_of(line: &str) -> Option<DateTime<Utc>> {
    let c = TIMESTAMP.captures(line)?;
    let n = |i: usize| group(&c, i).and_then(|v| v.parse::<u32>().ok());
    let date = NaiveDate::from_ymd_opt(i32::try_from(n(1)?).ok()?, n(2)?, n(3)?)?;
    let time = date.and_hms_milli_opt(n(4)?, n(5)?, n(6)?, n(7)?)?;
    Some(time.and_utc())
}

/// An incoming connection's address, before anybody has authenticated.
#[must_use]
pub fn accepted_address(line: &str) -> Option<String> {
    let ip = group(&ACCEPT.captures(line)?, 1)?;
    is_ipv4(Some(ip)).then(|| ip.to_owned())
}

/// The `?Name=` option on a line, trimmed.
fn name_option(line: &str) -> Option<String> {
    let captures = NAME_OPTION.captures(line).ok()??;
    captures.get(1).map(|m| m.as_str().trim().to_owned())
}

/// An address and account on the same line. CERTAIN, and order independent.
#[must_use]
pub fn confirmed(line: &str) -> Option<ConfirmedPairing> {
    let ip = group(&IP_FIELD.captures(line)?, 1)?;
    let id = clean_id(Some(group(&UNIQUE_ID_FIELD.captures(line)?, 1)?));
    if id.is_empty() || !is_ipv4(Some(ip)) {
        return None;
    }
    /* Some close lines carry ?Name= too, and it matters: an account first seen on a
    DISCONNECT - anybody already connected when the bot started - otherwise has no name,
    and a leave line for a nameless account is dropped. */
    Some(ConfirmedPairing {
        ip: ip.to_owned(),
        id,
        name: name_option(line).filter(|n| !n.is_empty()),
    })
}

/// A login or join request: the id, and the name when the line carries one.
#[must_use]
pub fn login(line: &str) -> Option<LoginRequest> {
    if !LOGIN_MARKER.is_match(line) {
        return None;
    }
    let id = clean_id(Some(group(&LOGIN_ID.captures(line)?, 1)?));
    if id.is_empty() {
        return None;
    }
    Some(LoginRequest {
        name: name_option(line),
        id,
    })
}

/// The player in a logged base-RCON `BanPlayer`.
#[must_use]
pub fn banned_by_rcon(line: &str) -> Option<String> {
    group(&RCON_BAN.captures(line)?, 1).map(str::to_owned)
}

/// The player in a logged base-RCON `UnbanPlayer`.
#[must_use]
pub fn unbanned_by_rcon(line: &str) -> Option<String> {
    group(&RCON_UNBAN.captures(line)?, 1).map(str::to_owned)
}

/// The command text of an RCON+ line.
#[must_use]
pub fn rcon_plus_command(line: &str) -> Option<String> {
    group(&RCON_PLUS.captures(line)?, 1).map(str::to_owned)
}

/// Split "BanPlayer Alice" into ("BanPlayer", "Alice"); a lone verb has no argument.
fn split_verb(command: &str) -> (&str, Option<&str>) {
    let text = command.trim();
    match text.split_once(' ') {
        None => (text, None),
        Some((verb, rest)) => {
            let rest = rest.trim();
            (verb, (!rest.is_empty()).then_some(rest))
        }
    }
}

/// The RCON action a line records, or `None` when it is not one worth surfacing.
///
/// RCON+ FIRST: its marker never reaches the base branch. A base line is dropped when its
/// verb is noise; an RCON+ command is always kept, since the menu only logs deliberate
/// actions.
#[must_use]
pub fn rcon(line: &str) -> Option<RconAction> {
    if let Some(plus) = RCON_PLUS.captures(line) {
        let (verb, rest) = split_verb(group(&plus, 1)?);
        if verb.is_empty() {
            return None;
        }
        let action = |instigator: Option<&str>, argument: Option<&str>| RconAction {
            plus: true,
            verb: verb.to_owned(),
            instigator: instigator.map(str::to_owned),
            argument: argument.map(str::to_owned),
            successful: None,
        };
        // A server-wide verb has no acting player, so its argument stays the argument.
        if RCON_PLUS_SERVER_VERBS
            .iter()
            .any(|v| eq_ignore_case(v, verb))
        {
            return Some(action(None, rest));
        }
        let (instigator, argument) = split_verb(rest.unwrap_or_default());
        return Some(action(
            (!instigator.is_empty()).then_some(instigator),
            argument,
        ));
    }

    let base = RCON_BASE.captures(line)?;
    let (verb, argument) = split_verb(group(&base, 1)?);
    if verb.is_empty() || RCON_NOISE.iter().any(|n| eq_ignore_case(n, verb)) {
        return None;
    }
    // `Ban Alice` is echoed as `Rcon: BanPlayer Alice`; show the verb that was sent.
    let verb = BASE_RCON_VERB_ALIASES
        .iter()
        .find(|(alias, _)| eq_ignore_case(alias, verb))
        .map_or(verb, |(_, canonical)| canonical);
    Some(RconAction {
        plus: false,
        verb: verb.to_owned(),
        instigator: None,
        argument: argument.map(str::to_owned),
        successful: None,
    })
}

/// The pass/fail on an RCON+ result line, or `None` when the line is not one.
#[must_use]
pub fn rcon_result(line: &str) -> Option<bool> {
    let value = group(&RCON_RESULT_LINE.captures(line)?, 1)?;
    Some(value.eq_ignore_ascii_case("true"))
}

/// A kill record (needs `bVerboseLogging=true`). Each field is matched on its own, because
/// Pavlov logs the record as one JSON line or split across several.
#[must_use]
pub fn kill(line: &str) -> Option<KillEvent> {
    let field = |re: &regex::Regex| {
        re.captures(line)
            .and_then(|c| group(&c, 1).map(str::to_owned))
    };
    let event = KillEvent {
        killer: field(&KILLER),
        killed: field(&KILLED),
        killed_by: field(&KILLED_BY),
    };
    (event.killer.is_some() || event.killed.is_some() || event.killed_by.is_some()).then_some(event)
}

/// Normalise an account id: "NULL:abc123" becomes "abc123", punctuation removed.
///
/// The strip is not cosmetic: NetworkFailure lines quote the id, and an id with a stray
/// apostrophe does not match the same account seen anywhere else, so an evader's flag would
/// silently miss. (Letters and digits are Unicode's here, as in .NET; the only difference is
/// that .NET drops a few numeric symbols like superscripts that no Pavlov id contains.)
#[must_use]
pub fn clean_id(raw: Option<&str>) -> String {
    let text = raw.unwrap_or_default();
    let tail = text.rsplit_once(':').map_or(text, |(_, after)| after);
    tail.chars().filter(|c| c.is_alphanumeric()).collect()
}

/// Ids that identify nobody: a pre-authentication placeholder or the server's connection to
/// itself. Tracking either attributes real addresses to a phantom account.
#[must_use]
pub fn is_placeholder_id(id: Option<&str>) -> bool {
    match id {
        None | Some("") => true,
        Some(id) => contains_ignore_case(id, "INVALID") || contains_ignore_case(id, "localhost-"),
    }
}

/// Octet-bounded, so "999.1.1.1" is not an address.
#[must_use]
pub fn is_ipv4(value: Option<&str>) -> bool {
    value.is_some_and(|v| STRICT_IPV4.is_match(v))
}
