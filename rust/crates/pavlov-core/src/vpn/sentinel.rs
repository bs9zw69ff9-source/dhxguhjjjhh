//! Reading Sentinel's (sntlhq.com) reply (C# `Core/Vpn/SentinelParser.cs`).
//!
//! It has its own file because it produced three production bugs that all presented as
//! "responded but gave no verdict", each encoded as a rule here:
//!
//! - TWO BODY SHAPES, BOTH USING `network` FOR DIFFERENT THINGS. The lookup style puts the
//!   verdict in `signals` and ASN/org in `network`; the evaluate style puts the verdict IN
//!   `network`. Which shape it is is detected by looking for boolean members, not assumed.
//! - BOOLEANS ARE ENCODED LOOSELY: true, 1, "true", "yes" all appear.
//! - AN ADDRESS WITH NO REPUTATION DATA STILL HAS A VERDICT: `known: false` comes with null
//!   signals and an explicit allow/review/block. The verdict is taken; the per-category fields
//!   are not inferred from it.
//!
//! Returns `None` ONLY when nothing recognisable is present. Never a guess.

use serde_json::{Map, Value};

use super::merge::AsnValue;
use super::{DetectorReading, VpnMerge};
use crate::text::number::fixed;

/// Coerce a loosely encoded boolean. `None` means ABSENT OR UNRECOGNISED, never false - JSON
/// `null` included, which is UNKNOWN.
#[must_use]
pub fn boolish(value: Option<&Value>) -> Option<bool> {
    match value? {
        Value::Bool(b) => Some(*b),
        Value::Number(n) => n.as_f64().map(|n| n != 0.0),
        Value::String(s) => match s.trim().to_lowercase().as_str() {
            "true" | "yes" | "y" | "1" => Some(true),
            "false" | "no" | "n" | "0" => Some(false),
            _ => None,
        },
        _ => None,
    }
}

fn member<'a>(parent: Option<&'a Map<String, Value>>, name: &str) -> Option<&'a Value> {
    parent?.get(name)
}

fn first_bool(candidates: &[Option<&Value>]) -> Option<bool> {
    candidates.iter().find_map(|c| boolish(*c))
}

fn text(parent: Option<&Map<String, Value>>, name: &str) -> Option<String> {
    let value = match member(parent, name)? {
        Value::String(s) => s.clone(),
        Value::Number(n) => n.to_string(),
        _ => return None,
    };
    let value = value.trim();
    (!value.is_empty()).then(|| value.to_owned())
}

fn number(parent: Option<&Map<String, Value>>, names: &[&str]) -> Option<f64> {
    names.iter().find_map(|name| match member(parent, name)? {
        Value::Number(n) => n.as_f64(),
        Value::String(s) => s.trim().parse::<f64>().ok().filter(|v| v.is_finite()),
        _ => None,
    })
}

fn asn(parent: Option<&Map<String, Value>>) -> Option<String> {
    let raw = text(parent, "asn")?;
    VpnMerge::normalise_asn(Some(AsnValue::Text(&raw)))
}

fn country_code(country: Option<&str>) -> Option<String> {
    country
        .filter(|c| c.chars().count() == 2)
        .map(str::to_uppercase)
}

fn sample(value: Option<&Value>) -> String {
    match value {
        None => "absent".to_owned(),
        Some(v) => v.to_string().chars().take(200).collect(),
    }
}

/// These keys appearing as booleans in `network` mean it is the EVALUATE shape.
const VERDICT_KEYS: [&str; 6] = [
    "vpn",
    "proxy",
    "datacenter",
    "tor",
    "anonymous",
    "residential",
];

/// The reading, or `Err` with the detail needed to fix an unrecognised body - the VALUES as
/// well as the keys, because the keys alone never showed the booleans encoded as something else.
///
/// # Errors
/// When the body is not an object, or holds nothing usable.
pub fn parse(root: &Value) -> Result<DetectorReading, String> {
    let Some(root) = root.as_object() else {
        return Err(format!("body is {}, not an object", kind(root)));
    };

    let signals = root.get("signals").and_then(Value::as_object);
    let network = root.get("network").and_then(Value::as_object);

    let network_is_verdict =
        network.is_some_and(|n| VERDICT_KEYS.iter().any(|k| boolish(n.get(*k)).is_some()));
    let verdict_scope = if network_is_verdict { network } else { None };
    let geo_scope = if network_is_verdict { None } else { network };
    let top = Some(root);

    let vpn = first_bool(&[
        member(signals, "vpn"),
        member(verdict_scope, "vpn"),
        member(top, "vpn"),
    ]);
    let proxy = first_bool(&[
        member(signals, "proxied"),
        member(signals, "proxy"),
        member(verdict_scope, "proxy"),
        member(top, "proxy"),
    ]);
    let tor = first_bool(&[
        member(signals, "tor"),
        member(verdict_scope, "tor"),
        member(top, "tor"),
    ]);
    let datacenter = first_bool(&[
        member(signals, "dch"),
        member(signals, "datacenter"),
        member(verdict_scope, "datacenter"),
        member(top, "datacenter"),
        member(top, "hosting"),
    ]);
    let anonymous = first_bool(&[
        member(signals, "anon"),
        member(signals, "anonymous"),
        member(verdict_scope, "anonymous"),
        member(top, "anonymous"),
    ]);

    let no_signals = vpn.is_none()
        && proxy.is_none()
        && tor.is_none()
        && datacenter.is_none()
        && anonymous.is_none();
    let risk = number(top, &["risk_score", "riskScore"]);
    let verdict = text(top, "verdict")
        .or_else(|| text(top, "decision"))
        .map(|v| v.to_lowercase());
    let risk_text =
        |r: Option<f64>| r.map_or_else(String::new, |r| format!(" risk:{}", fixed(r, 0)));

    if no_signals {
        if let Some(verdict) = verdict
            .as_deref()
            .filter(|v| matches!(*v, "allow" | "review" | "block"))
        {
            let known = boolish(member(top, "known"));
            let country = text(network, "country");
            return Ok(DetectorReading {
                name: "sentinel".into(),
                tier: 1,
                flagged: matches!(verdict, "block" | "review"),
                detail: Some(format!(
                    "verdict:{verdict}{}{}",
                    risk_text(risk),
                    if known == Some(false) {
                        " (no reputation data)"
                    } else {
                        ""
                    }
                )),
                /* Every per-category field stays UNKNOWN: the overall call is known, that the
                address is specifically "not a proxy" is not, and asserting it would outvote a
                provider that actually classified the address. */
                threat_score: risk,
                asn: asn(network),
                organization: text(network, "org"),
                isp: text(network, "org"),
                country_code: country_code(country.as_deref()),
                country,
                city: text(network, "city"),
                ..DetectorReading::default()
            });
        }

        let error = text(top, "error");
        return Err(format!(
            "unrecognised body - no verdict. Top-level keys: [{}]; signals={}; network={}{}",
            root.keys()
                .map(String::as_str)
                .collect::<Vec<_>>()
                .join(", "),
            sample(root.get("signals")),
            sample(root.get("network")),
            error.map_or_else(String::new, |e| format!("; error: {e}"))
        ));
    }

    /* Anonymising signals only. Datacenter is REPORTED but is not a hit on its own: honest
    players sit behind carrier and cloud ranges. */
    let hits: Vec<&str> = [
        ("vpn", vpn),
        ("proxy", proxy),
        ("tor", tor),
        ("anon", anonymous),
    ]
    .into_iter()
    .filter(|(_, v)| *v == Some(true))
    .map(|(name, _)| name)
    .collect();
    let country = text(geo_scope, "country");

    Ok(DetectorReading {
        name: "sentinel".into(),
        tier: 1,
        flagged: !hits.is_empty(),
        detail: Some(format!(
            "{}{}{}{}",
            if hits.is_empty() {
                "clean".to_owned()
            } else {
                hits.join("+")
            },
            if datacenter == Some(true) {
                " (datacenter)"
            } else {
                ""
            },
            verdict
                .as_deref()
                .map_or_else(String::new, |v| format!(" verdict:{v}")),
            risk_text(risk)
        )),
        vpn,
        proxy,
        tor,
        hosting: datacenter,
        /* The evaluate-style body states residential outright; otherwise the only inference
        is that a datacenter address is not residential, so this never outvotes a real
        classifier. */
        residential: boolish(member(verdict_scope, "residential"))
            .or_else(|| (datacenter == Some(true)).then_some(false)),
        threat_score: risk,
        provider: text(verdict_scope, "service").or_else(|| text(top, "service")),
        asn: asn(geo_scope),
        organization: text(geo_scope, "org"),
        isp: text(geo_scope, "org"),
        country_code: country_code(country.as_deref()),
        country: country.or_else(|| text(top, "country")),
        city: text(geo_scope, "city"),
        ..DetectorReading::default()
    })
}

/// The JSON kind, as .NET's `JsonValueKind` names it.
fn kind(value: &Value) -> &'static str {
    match value {
        Value::Null => "Null",
        Value::Bool(true) => "True",
        Value::Bool(false) => "False",
        Value::Number(_) => "Number",
        Value::String(_) => "String",
        Value::Array(_) => "Array",
        Value::Object(_) => "Object",
    }
}
