//! Whether a joining player looks like a returning banned one (C# `Core/Evasion/EvasionScorer.cs`).
//!
//! Pure: all state is passed in, and nobody is banned here. Every signal is scored on its own
//! and the strongest are reported, so a moderator sees WHY a match fired.

use std::collections::HashSet;

use super::{
    BanNetwork, BanRecord, EvasionMatch, EvasionReason, EvasionResult, FlagHit, JoinContext,
    RegistryFlags, SignalKind,
};

/// Namespace for the scorer, mirroring the C# static class.
#[derive(Debug)]
pub struct EvasionScorer;

fn norm(s: Option<&str>) -> String {
    s.map(|s| s.trim().to_lowercase()).unwrap_or_default()
}

fn norm_set<'a>(sources: impl IntoIterator<Item = &'a str>) -> HashSet<String> {
    sources
        .into_iter()
        .map(|v| norm(Some(v)))
        .filter(|n| !n.is_empty())
        .collect()
}

impl EvasionScorer {
    /// Score at or above which a match is reported to moderators.
    pub const MATCH_THRESHOLD: i32 = 50;

    /// Correlate one join against every ban and the standing registry flags.
    #[must_use]
    pub fn find(
        join: &JoinContext,
        bans: &[BanRecord],
        flags: Option<&RegistryFlags>,
    ) -> EvasionResult {
        let empty_flags = RegistryFlags::default();
        let flags = flags.unwrap_or(&empty_flags);

        let join_name = norm(join.name.as_deref());
        let join_eos = norm(join.eos_id.as_deref());
        let join_ip = join.ip.as_deref().map(str::trim).unwrap_or("").to_owned();
        let join_asn = norm(join.asn.as_deref());
        let join_provider = norm(join.provider.as_deref());
        let join_alts: Vec<String> = join
            .alt_names
            .iter()
            .map(|a| norm(Some(a)))
            .filter(|a| !a.is_empty())
            .collect();

        let empty_net = BanNetwork::default();
        let mut matches = Vec::new();

        for ban in bans {
            if ban.player_id.trim().is_empty() {
                continue; // a corrupt row
            }
            let net = ban.network.as_ref().unwrap_or(&empty_net);
            let mut reasons = Vec::new();
            let mut definitive = false;

            // EOS account: the same account returning under any name.
            if !join_eos.is_empty()
                && norm_set(net.eos_ids.iter().map(String::as_str)).contains(&join_eos)
            {
                definitive = true;
                reasons.push(EvasionReason {
                    kind: SignalKind::Eos,
                    detail: format!(
                        "same EOS account ({})",
                        join.eos_id.as_deref().unwrap_or("")
                    ),
                });
            }

            // IP history: the join address is one of this ban's recorded addresses (exact).
            if !join_ip.is_empty() && net.ips.contains(&join_ip) {
                reasons.push(EvasionReason {
                    kind: SignalKind::Ip,
                    detail: format!("connected from a banned IP ({join_ip})"),
                });
            }

            // Username: exact, or one of their known shared-IP aliases.
            let ban_names = norm_set(
                std::iter::once(ban.player_id.as_str()).chain(net.names.iter().map(String::as_str)),
            );
            if !join_name.is_empty() && ban_names.contains(&join_name) {
                reasons.push(EvasionReason {
                    kind: SignalKind::Username,
                    detail: format!("username matches a banned player ({})", ban.player_id),
                });
            } else if let Some(alias) = join_alts.iter().find(|a| ban_names.contains(*a)) {
                reasons.push(EvasionReason {
                    kind: SignalKind::Username,
                    detail: format!("known alias of a banned player ({alias})"),
                });
            }

            // VPN provider: the same consumer brand.
            let ban_provider = norm(net.provider.as_deref());
            if !join_provider.is_empty()
                && !ban_provider.is_empty()
                && join_provider == ban_provider
            {
                reasons.push(EvasionReason {
                    kind: SignalKind::Provider,
                    detail: format!(
                        "same VPN provider ({})",
                        join.provider.as_deref().unwrap_or("")
                    ),
                });
            }

            /* ASN: near-worthless alone (an ASN can be a whole national ISP), so it only ADDS
            confidence, and more when both sides are on an anonymising network. */
            let ban_asn = norm(net.asn.as_deref());
            if !join_asn.is_empty() && !ban_asn.is_empty() && join_asn == ban_asn {
                let asn = join.asn.as_deref().unwrap_or("");
                reasons.push(if join.is_anonymising() && net.is_anonymising() {
                    let org = net
                        .organization
                        .as_deref()
                        .filter(|o| !o.is_empty())
                        .map(|o| format!(" - {o}"))
                        .unwrap_or_default();
                    EvasionReason {
                        kind: SignalKind::AsnVpn,
                        detail: format!("same VPN/hosting network as a banned player ({asn}{org})"),
                    }
                } else {
                    EvasionReason {
                        kind: SignalKind::Asn,
                        detail: format!("same network/ASN ({asn}) - weak on its own"),
                    }
                });
            }

            if reasons.is_empty() {
                continue;
            }
            let score: i32 = reasons.iter().map(EvasionReason::weight).sum();
            // A lone ASN hit never reports: entire ISPs share one.
            let only_weak_asn = reasons.iter().all(|r| r.kind == SignalKind::Asn);
            if !definitive && (score < Self::MATCH_THRESHOLD || only_weak_asn) {
                continue;
            }

            // Strongest first. Stable, so equal weights keep the order they were found in.
            reasons.sort_by_key(|r| std::cmp::Reverse(r.weight()));
            matches.push(EvasionMatch {
                player_id: ban.player_id.clone(),
                reason: ban.reason.clone(),
                moderator: ban.moderator.clone(),
                permanent: ban.permanent,
                expires: ban.expires,
                score,
                definitive,
                reasons,
            });
        }

        // Standing flags, independent of any ban RECORD: an auto-ban may flag without a row.
        let mut flag_hits = Vec::new();
        if !join_eos.is_empty() && flags.eos_ids.contains(&join_eos) {
            flag_hits.push(FlagHit {
                kind: SignalKind::Eos,
                detail: "EOS account is flagged in the ban registry".to_owned(),
            });
        }
        if !join_ip.is_empty() && flags.ips.contains(&join_ip) {
            flag_hits.push(FlagHit {
                kind: SignalKind::Ip,
                detail: format!("IP {join_ip} is flagged in the ban registry"),
            });
        }
        if !join_name.is_empty() && flags.names.contains(&join_name) {
            flag_hits.push(FlagHit {
                kind: SignalKind::Username,
                detail: "username is flagged in the ban registry".to_owned(),
            });
        }

        // Highest score first. STABLE, where the C# List.Sort was not: equal scores keep ban order.
        matches.sort_by_key(|m| std::cmp::Reverse(m.score));
        EvasionResult {
            matches,
            flags: flag_hits,
        }
    }
}
