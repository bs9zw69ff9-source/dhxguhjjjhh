//! The staff connection card (C# `Discord/ConnectCard.cs`): who just connected, from where,
//! what is known about them, and the VPN verdict with its working.
//!
//! THE ADDRESS AND THE ACCOUNT ID ARE CHARGED FIRST against Discord's 6000-character total:
//! they are why anyone opens the card, so they can never be the fields squeezed out.

use chrono::{DateTime, Utc};

use crate::embed::Embed;
use crate::evasion::AccountRecord;
use crate::skin::{EmbedBudget, police};
use crate::text::Sanitize;
use crate::time::EasternTime;
use crate::vpn::verdict::{self, VpnStance};
use crate::vpn::{DetectorReading, VpnRecord};

/// What the card is built from.
#[derive(Debug, Clone)]
pub struct ConnectCardInput<'a> {
    pub name: &'a str,
    pub account_id: &'a str,
    pub ip: Option<&'a str>,
    /// The address is certain (paired on one line) rather than correlated.
    pub confident_ip: bool,
    pub server: &'a str,
    pub account: Option<&'a AccountRecord>,
    pub alts: &'a [AccountRecord],
    pub vpn: Option<&'a VpnRecord>,
    pub flagged: bool,
    pub master: bool,
    pub at: DateTime<Utc>,
}

fn stamp(at: Option<DateTime<Utc>>) -> String {
    at.map_or_else(|| "unknown".into(), |a| EasternTime::stamp(Some(a)))
}

fn code(value: Option<&str>) -> String {
    value.filter(|v| !v.is_empty()).map_or_else(
        || "unknown".into(),
        |v| format!("`{}`", Sanitize::message(Some(v))),
    )
}

fn plain(value: Option<&str>) -> String {
    value
        .filter(|v| !v.is_empty())
        .map_or_else(|| "unknown".into(), |v| Sanitize::message(Some(v)))
}

/// Yes/No/unknown, with the worrying answer in bold so the card can be skimmed.
fn yes_no(value: Option<bool>, risky_when: bool) -> &'static str {
    match value {
        None => "unknown",
        Some(known) if known == risky_when => {
            if known {
                "**YES**"
            } else {
                "**No**"
            }
        }
        Some(true) => "Yes",
        Some(false) => "No",
    }
}

fn pip(stance: VpnStance) -> &'static str {
    match stance {
        VpnStance::Confirmed => "🚫",
        VpnStance::Disputed | VpnStance::Likely => "⚠️",
        VpnStance::Clean => "✅",
        VpnStance::NotChecked | VpnStance::Local => "▫️",
    }
}

/// THE VERDICT FIRST, on its own line, then a sentence of plain English, then what was done;
/// the detector arithmetic is evidence underneath, never the verdict.
fn verdict_text(vpn: Option<&VpnRecord>) -> String {
    let summary = verdict::of(vpn);
    let mut lines = vec![
        format!("{} **{}**", pip(summary.stance), summary.headline),
        summary.plain.clone(),
    ];
    if summary.concerning() || summary.stance == VpnStance::Clean {
        lines.push(format!("*{}*", summary.action));
    }
    let Some(vpn) = vpn.filter(|v| !v.local) else {
        return lines.join("\n");
    };
    let signals = verdict::signals(Some(vpn));
    if !signals.is_empty() {
        lines.push(format!("Signals: **{}**", signals.join(", ")));
    }
    if vpn.detectors.is_empty() {
        lines.push(
            "*No per-detector breakdown — this result was cached before detectors were recorded.*"
                .into(),
        );
    } else {
        let dot =
            |d: &DetectorReading| format!("{} {}", if d.flagged { "🔴" } else { "🟢" }, d.name);
        let tier = |t: i32| {
            vpn.detectors
                .iter()
                .filter(|d| d.tier == t)
                .map(dot)
                .collect::<Vec<_>>()
        };
        let (tier1, tier2) = (tier(1), tier(2));
        if !tier1.is_empty() {
            lines.push(format!("Checks: {}", tier1.join("  ")));
        }
        if !tier2.is_empty() {
            lines.push(format!("Confirmer: {}", tier2.join("  ")));
        }
    }
    let meta: Vec<String> = [
        vpn.provider
            .as_deref()
            .filter(|p| !p.is_empty())
            .map(|p| format!("provider: **{}**", Sanitize::message(Some(p)))),
        vpn.isp
            .as_deref()
            .filter(|i| !i.is_empty())
            .map(|i| Sanitize::message(Some(i))),
    ]
    .into_iter()
    .flatten()
    .collect();
    if !meta.is_empty() {
        lines.push(meta.join("  —  "));
    }
    lines.join("\n")
}

fn addresses(account: Option<&AccountRecord>) -> String {
    let Some(account) = account else {
        return "```\nno records\n```".into();
    };
    let confirmed = account
        .confirmed_ips
        .iter()
        .rev()
        .take(8)
        .map(|ip| format!("{ip}   confirmed"));
    let guessed = account
        .guessed_ips
        .iter()
        .rev()
        .take(4)
        .map(|ip| format!("{ip}   unconfirmed"));
    let lines: Vec<String> = confirmed.chain(guessed).collect();
    if lines.is_empty() {
        "```\nno records\n```".into()
    } else {
        format!("```\n{}\n```", lines.join("\n"))
    }
}

fn location(vpn: Option<&VpnRecord>, ip: Option<&str>) -> String {
    let Some(vpn) = vpn else {
        return if ip.is_some_and(|i| !i.is_empty()) {
            "unknown".into()
        } else {
            "no address".into()
        };
    };
    let place: Vec<String> = [&vpn.city, &vpn.region, &vpn.country]
        .into_iter()
        .filter_map(|p| p.as_deref().filter(|s| !s.is_empty()))
        .map(|p| Sanitize::message(Some(p)))
        .collect();
    if place.is_empty() {
        "unknown".into()
    } else {
        place.join(", ")
    }
}

/// The card.
#[must_use]
pub fn build(input: &ConnectCardInput<'_>) -> Embed {
    let skin = police();
    let who = Sanitize::message(Some(input.name));
    let where_ = Sanitize::message(Some(input.server));
    let title = EmbedBudget::truncate(
        &format!("{} — {who}", skin.words.connect_title),
        EmbedBudget::TITLE_LIMIT,
    );
    let description =
        EmbedBudget::truncate(&format!("{who} {} **{where_}**.", skin.words.arrived), 2048);
    let color = if input.flagged {
        skin.colors.ban_red
    } else if input.vpn.is_some_and(|v| v.decision.flagged) {
        skin.colors.amber
    } else {
        skin.colors.green
    };
    let mut embed = Embed::new()
        .color(color)
        .title(title.clone())
        .description(description.clone());
    let mut budget = EmbedBudget::new(
        title.chars().count() + description.chars().count() + EmbedBudget::FOOTER_ALLOWANCE,
    );
    let vpn = input.vpn;

    // A guessed address is MARKED: acting on a correlation as certain bans the wrong person.
    let ip = input.ip.filter(|i| !i.is_empty()).map_or_else(
        || "unknown".to_owned(),
        |ip| {
            format!(
                "`{ip}`{}",
                if input.confident_ip {
                    ""
                } else {
                    "  ⚠️ *unconfirmed - correlated, not paired*"
                }
            )
        },
    );
    let mut add = |label: &str, value: String, inline: bool| {
        budget.add(&mut embed, label, Some(&value), inline);
    };
    add("IP address", ip, false);
    add(
        "Account ID",
        if input.account_id.is_empty() {
            "unknown".into()
        } else {
            format!("`{}`", input.account_id)
        },
        false,
    );
    add(
        "First seen",
        stamp(input.account.and_then(|a| a.first_seen)),
        true,
    );
    add(
        "Last seen",
        stamp(input.account.and_then(|a| a.last_seen)),
        true,
    );
    add(
        "Known addresses",
        format!(
            "{} confirmed",
            input.account.map_or(0, |a| a.confirmed_ips.len())
        ),
        true,
    );
    add(
        "Possible alts",
        if input.alts.is_empty() {
            "None".into()
        } else {
            input
                .alts
                .iter()
                .map(|a| Sanitize::message(Some(a.name().unwrap_or(&a.id))))
                .collect::<Vec<_>>()
                .join(", ")
        },
        false,
    );
    add(
        "Protected from auto-ban",
        if input.master {
            "Yes — master account".into()
        } else {
            "No".into()
        },
        true,
    );
    add("Server", where_.clone(), true);
    add(
        "Flag match",
        if input.flagged {
            "**Flagged** — matches a standing blacklist entry".into()
        } else {
            "No matches".into()
        },
        true,
    );
    add(
        "Network",
        [
            format!("ASN: {}", code(vpn.and_then(|v| v.asn.as_deref()))),
            format!(
                "Organization: {}",
                plain(vpn.and_then(|v| v.organization.as_deref().or(v.isp.as_deref())))
            ),
            format!("Country: {}", plain(vpn.and_then(|v| v.country.as_deref()))),
        ]
        .join("\n"),
        false,
    );
    // "unknown" is its own answer, never collapsed into "No".
    let mut detection = vec![
        format!("VPN: {}", yes_no(vpn.and_then(|v| v.vpn), true)),
        format!("Proxy: {}", yes_no(vpn.and_then(|v| v.proxy), true)),
        format!("Tor: {}", yes_no(vpn.and_then(|v| v.tor), true)),
        format!(
            "Hosting/datacenter: {}",
            yes_no(vpn.and_then(|v| v.hosting), true)
        ),
        format!(
            "Residential: {}",
            yes_no(vpn.and_then(|v| v.residential), false)
        ),
    ];
    if let Some(score) = vpn.and_then(|v| v.threat_score) {
        detection.push(format!(
            "Threat score: **{}**/100",
            crate::text::number::fixed(score, 0)
        ));
    }
    add("Detection", detection.join("\n"), false);
    add("VPN / proxy", verdict_text(vpn), false);
    add("Location", location(vpn, input.ip), false);
    add("Recent addresses", addresses(input.account), false);
    embed.brand(Some(&format!(
        "{} — {} Eastern",
        skin.words.connect_log,
        EasternTime::stamp(Some(input.at))
    )))
}

/// The card `/feeds test:true` sends to the connect webhook. Built through [`build`] rather
/// than by hand, so a card Discord would reject fails the test too.
#[must_use]
pub fn sample(at: DateTime<Utc>) -> Embed {
    build(&ConnectCardInput {
        name: "feed-test",
        account_id: "0000000000000000",
        ip: Some("203.0.113.1"),
        confident_ip: true,
        server: "test",
        account: None,
        alts: &[],
        vpn: None,
        flagged: false,
        master: false,
        at,
    })
}
