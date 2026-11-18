//! Port of `VpnTests.cs`: VpnConsensusTests, CachedVerdictTests, VpnMergeTests, IpAddressTests
//! and SentinelParserTests.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::vpn::{TierOutcome, VpnThresholds};

fn tier(hits: u32, answered: u32) -> TierOutcome {
    TierOutcome::counts(hits, answered, 0)
}

fn thresholds(screen_min: u32, confirm_min: u32, ban_min: u32) -> VpnThresholds {
    VpnThresholds {
        screen_min,
        confirm_min,
        ban_min,
    }
}

mod vpn_consensus_tests {
    use pavlov_core::vpn::{TierOutcome, VpnConsensus, VpnThresholds};

    use super::{thresholds, tier};

    fn decide(
        screen: Option<&TierOutcome>,
        confirm: Option<&TierOutcome>,
        confirmers: u32,
    ) -> pavlov_core::vpn::VpnDecision {
        VpnConsensus::decide(screen, confirm, confirmers, VpnThresholds::default())
    }

    #[test]
    fn detection_disabled_is_never_actionable() {
        let decision = decide(None, None, 0);
        assert!(!decision.flagged);
        assert!(!decision.actionable);
        assert_eq!(decision.label(), "CLEAN");
    }

    #[test]
    fn a_total_outage_is_not_clean_it_is_no_verdict() {
        /* The caller treats this as "do not cache, retry next connection": caching it as clean
        would let a provider blip whitelist every address that connected during it. */
        let decision = decide(Some(&TierOutcome::counts(0, 0, 5)), None, 1);
        assert!(!decision.actionable);
        assert!(decision.reason.contains("every regular check failed"));
    }

    #[test]
    fn a_clean_screen_ends_the_check() {
        let decision = decide(Some(&tier(0, 4)), None, 1);
        assert!(!decision.flagged);
        assert!(decision.confirmed.is_none());
        assert!(!decision.actionable);
    }

    #[test]
    fn confirmation_agreeing_makes_it_actionable() {
        let decision = decide(Some(&tier(2, 4)), Some(&tier(1, 1)), 1);
        assert!(decision.flagged);
        assert_eq!(decision.confirmed, Some(true));
        assert!(decision.actionable);
        assert_eq!(decision.label(), "CONFIRMED");
    }

    #[test]
    fn confirmation_disputing_it_is_recorded_but_no_longer_a_veto() {
        /* THE CHANGE: the confirmer's veto silently became "auto-ban is off" whenever a 35-a-day
        quota ran out. Three screeners agreeing is now enough; the dissent stays on the card. */
        let decision = decide(Some(&tier(3, 4)), Some(&tier(0, 1)), 1);
        assert!(decision.flagged);
        assert_eq!(decision.confirmed, Some(false));
        assert!(decision.actionable);
        assert_eq!(decision.label(), "DISPUTED");
        assert!(decision.reason.contains("confirmation disputed it"));
    }

    #[test]
    fn a_disputed_confirmation_reports_how_many_actually_flagged_it() {
        /* THE COUNT WAS HARDCODED TO ZERO: with VPN_CONFIRM_MIN=2, a confirmer that DID flag
        the address read as "disputed it (0/2)" - understating the evidence behind a ban. */
        let decision =
            VpnConsensus::decide(Some(&tier(3, 4)), Some(&tier(1, 2)), 2, thresholds(1, 2, 2));

        assert_eq!(decision.confirmed, Some(false));
        assert!(decision.reason.contains("1/2"));
        assert!(!decision.reason.contains("(0/2"));
    }

    #[test]
    fn a_disputed_single_hit_still_does_not_ban() {
        // One screener for, the confirmer against: one vote, not two.
        let decision = decide(Some(&tier(1, 4)), Some(&tier(0, 1)), 1);
        assert!(decision.flagged);
        assert!(!decision.actionable);
    }

    #[test]
    fn a_confirmer_counts_towards_the_two() {
        // The confirmer is a voice in the count, not a separate gate.
        let decision = decide(Some(&tier(1, 4)), Some(&tier(1, 1)), 1);
        assert!(decision.actionable);
        assert_eq!(decision.confirmed, Some(true));
    }

    #[test]
    fn two_detectors_agreeing_is_enough_to_ban() {
        for (screen, confirm) in [
            (tier(2, 4), tier(0, 0)), // two screeners, no confirmer available
            (tier(2, 4), tier(0, 1)), // two screeners, confirmer disagreed
            (tier(1, 4), tier(1, 1)), // one screener plus the confirmer
        ] {
            assert!(decide(Some(&screen), Some(&confirm), 1).actionable);
        }
        // ...and one alone never is, however it is reached.
        assert!(!decide(Some(&tier(1, 4)), Some(&tier(0, 0)), 1).actionable);
    }

    #[test]
    fn one_screen_hit_alone_never_bans_when_there_is_no_confirmer() {
        /* The regression the separation was built for: as one tri-state, `null` read as
        permission to ban and one screening hit banned people. */
        let decision = decide(Some(&tier(1, 4)), Some(&tier(0, 0)), 0);
        assert!(decision.flagged);
        assert!(decision.confirmed.is_none());
        assert!(!decision.actionable);
        assert_eq!(decision.label(), "FLAGGED (inconclusive)");
    }

    #[test]
    fn real_screen_consensus_bans_when_there_is_no_confirmer() {
        let decision = decide(Some(&tier(2, 4)), Some(&tier(0, 0)), 0);
        assert!(decision.actionable);
        assert!(decision.confirmed.is_none()); // still inconclusive as a VERDICT
    }

    #[test]
    fn the_threshold_is_not_lowered_just_because_few_detectors_are_configured() {
        let decision =
            VpnConsensus::decide(Some(&tier(2, 2)), Some(&tier(0, 0)), 0, thresholds(1, 1, 3));
        assert!(!decision.actionable);
        assert!(decision.reason.contains("3 required"));
    }

    #[test]
    fn all_confirmers_failing_is_reported_differently_from_none_configured() {
        assert!(
            decide(Some(&tier(2, 4)), Some(&TierOutcome::counts(0, 0, 1)), 1)
                .reason
                .contains("all confirmers failed")
        );
        assert!(
            decide(Some(&tier(2, 4)), Some(&tier(0, 0)), 0)
                .reason
                .contains("none configured")
        );
    }

    #[test]
    fn a_threshold_of_zero_is_clamped_to_one() {
        // Zero would flag every address on the internet.
        let decision = VpnConsensus::decide(Some(&tier(0, 4)), None, 0, thresholds(0, 1, 2));
        assert!(!decision.flagged);
    }

    #[test]
    fn a_configuration_that_can_never_ban_is_detectable() {
        assert!(!VpnConsensus::can_ever_auto_ban(2, 0, thresholds(1, 1, 3)));
        assert!(VpnConsensus::can_ever_auto_ban(3, 0, thresholds(1, 1, 3)));
        // Both tiers vote, so a screener plus a confirmer reaches two.
        assert!(VpnConsensus::can_ever_auto_ban(
            1,
            1,
            VpnThresholds::default()
        ));
        assert!(!VpnConsensus::can_ever_auto_ban(
            1,
            0,
            VpnThresholds::default()
        ));
    }
}

mod cached_verdict_tests {
    //! A changed ban threshold applies to addresses screened under the old one: records are
    //! cached for thirty days, and without re-deciding on read a lowered threshold would look
    //! like it had done nothing for a month.

    use chrono::Utc;
    use pavlov_core::vpn::{TierOutcome, VpnConsensus, VpnDecision, VpnRecord, VpnThresholds};

    use super::thresholds;

    fn record(
        screen_hits: u32,
        screen_answered: u32,
        confirm_hits: u32,
        confirm_answered: u32,
        decision: VpnDecision,
    ) -> VpnRecord {
        VpnRecord {
            ip: "203.0.113.9".into(),
            decision,
            screen_hits,
            screen_answered,
            confirm_hits,
            confirm_answered,
            checked_at: Utc::now(),
            ..VpnRecord::default()
        }
    }

    /// What re-deciding does, against the same inputs it uses.
    fn redecide(record: &VpnRecord, limits: VpnThresholds) -> VpnDecision {
        let screen = TierOutcome::counts(record.screen_hits, record.screen_answered, 0);
        let confirm = (record.confirm_answered > 0)
            .then(|| TierOutcome::counts(record.confirm_hits, record.confirm_answered, 0));
        VpnConsensus::decide(Some(&screen), confirm.as_ref(), 1, limits)
    }

    #[test]
    fn a_vetoed_record_becomes_actionable_under_the_new_rule() {
        // The old bug's shape: three screeners flagged it, the confirmer's veto made it unbannable.
        let old = record(
            3,
            4,
            0,
            1,
            VpnDecision::new(true, Some(false), false, "confirmation disputed it"),
        );

        let now = redecide(&old, VpnThresholds::default());

        assert!(now.actionable);
        assert_eq!(now.confirmed, Some(false)); // the disagreement is still on the card
    }

    #[test]
    fn a_raised_threshold_takes_effect_on_cached_addresses_too() {
        let cached = record(
            2,
            4,
            0,
            0,
            VpnDecision::new(true, None, true, "was actionable"),
        );
        assert!(!redecide(&cached, thresholds(1, 1, 3)).actionable);
    }

    #[test]
    fn a_clean_cached_record_stays_clean() {
        let cached = record(
            0,
            4,
            0,
            0,
            VpnDecision::new(false, None, false, "all regular checks clean"),
        );

        let now = redecide(&cached, VpnThresholds::default());

        assert!(!now.flagged);
        assert!(!now.actionable);
    }
}

mod vpn_merge_tests {
    use chrono::DateTime;
    use pavlov_core::vpn::{
        AsnValue, DetectorReading, GeoLocation, TierOutcome, VpnDecision, VpnMerge, VpnRecord,
    };

    fn reading(name: &str) -> DetectorReading {
        DetectorReading {
            name: name.into(),
            tier: 1,
            ..DetectorReading::default()
        }
    }

    fn merge(readings: Vec<DetectorReading>) -> VpnRecord {
        let hits = u32::try_from(readings.iter().filter(|r| r.flagged).count()).unwrap();
        let answered = u32::try_from(readings.len()).unwrap();
        let screen = TierOutcome {
            hits,
            answered,
            failed: 0,
            readings,
        };
        VpnMerge::merge(
            "1.2.3.4",
            VpnDecision::new(true, Some(true), true, "test"),
            Some(&screen),
            None,
            None,
            DateTime::UNIX_EPOCH,
        )
    }

    #[test]
    fn one_provider_knowing_it_is_tor_is_enough() {
        // The others are not contradicting it - they have no data.
        let merged = merge(vec![
            DetectorReading {
                tor: Some(false),
                ..reading("a")
            },
            DetectorReading {
                tor: Some(true),
                ..reading("b")
            },
        ]);
        assert_eq!(merged.tor, Some(true));
    }

    #[test]
    fn nobody_knowing_leaves_it_null_not_false() {
        assert!(merge(vec![reading("a"), reading("b")]).tor.is_none());
    }

    #[test]
    fn a_direct_classifier_outranks_a_coarse_one() {
        // IPHub's block==1 only means "non-residential"; ORing would override IPQS.
        let merged = merge(vec![
            DetectorReading {
                hosting: Some(true),
                residential: Some(false),
                ..reading("iphub")
            },
            DetectorReading {
                hosting: Some(false),
                residential: Some(true),
                tier: 2,
                ..reading("ipqs")
            },
        ]);
        assert_eq!(merged.hosting, Some(false));
        assert_eq!(merged.residential, Some(true));
    }

    #[test]
    fn sentinel_does_not_win_the_residential_question_it_cannot_answer() {
        let merged = merge(vec![
            DetectorReading {
                residential: Some(false),
                ..reading("sentinel")
            },
            DetectorReading {
                residential: Some(true),
                ..reading("proxycheck")
            },
        ]);
        assert_eq!(merged.residential, Some(true));
    }

    #[test]
    fn a_hosting_address_is_never_reported_residential() {
        let merged = merge(vec![DetectorReading {
            hosting: Some(true),
            tier: 2,
            ..reading("ipqs")
        }]);
        assert_eq!(merged.residential, Some(false));
    }

    #[test]
    fn the_consumer_brand_beats_the_underlying_network_name() {
        let merged = merge(vec![
            DetectorReading {
                provider: Some("M247".into()),
                ..reading("ipapi.is")
            },
            DetectorReading {
                provider: Some("NordVPN".into()),
                ..reading("proxycheck")
            },
        ]);
        assert_eq!(merged.provider.as_deref(), Some("NordVPN"));
    }

    #[test]
    fn the_calibrated_risk_score_wins() {
        let merged = merge(vec![
            DetectorReading {
                threat_score: Some(33.0),
                ..reading("proxycheck")
            },
            DetectorReading {
                threat_score: Some(91.0),
                tier: 2,
                ..reading("ipqs")
            },
        ]);
        assert_eq!(merged.threat_score, Some(91.0));
    }

    #[test]
    fn a_flagged_address_with_no_named_signal_is_still_reported_as_vpn() {
        let screen = TierOutcome {
            hits: 1,
            answered: 1,
            failed: 0,
            readings: vec![DetectorReading {
                flagged: true,
                ..reading("proxycheck")
            }],
        };
        let flagged = VpnMerge::merge(
            "1.2.3.4",
            VpnDecision::new(true, None, false, "x"),
            Some(&screen),
            None,
            None,
            DateTime::UNIX_EPOCH,
        );
        assert_eq!(flagged.vpn, Some(true));
    }

    #[test]
    fn whois_geolocation_beats_a_detectors_country() {
        let geo = GeoLocation {
            city: Some("Toronto".into()),
            country: Some("Canada".into()),
            isp: Some("Bell".into()),
            ..GeoLocation::default()
        };
        let screen = TierOutcome {
            hits: 0,
            answered: 1,
            failed: 0,
            readings: vec![DetectorReading {
                country: Some("United States".into()),
                city: Some("Ashburn".into()),
                ..reading("iphub")
            }],
        };
        let merged = VpnMerge::merge(
            "1.2.3.4",
            VpnDecision::new(false, None, false, "x"),
            Some(&screen),
            None,
            Some(&geo),
            DateTime::UNIX_EPOCH,
        );

        assert_eq!(merged.city.as_deref(), Some("Toronto"));
        assert_eq!(merged.country.as_deref(), Some("Canada"));
    }

    #[test]
    fn asns_are_normalised_with_or_without_the_prefix() {
        for (input, expected) in [
            (Some("AS1234"), Some("AS1234")),
            (Some("1234"), Some("AS1234")),
            (Some("as1234"), Some("AS1234")),
            (Some(""), None),
            (None, None),
        ] {
            assert_eq!(
                VpnMerge::normalise_asn(input.map(AsnValue::Text)).as_deref(),
                expected,
                "{input:?}"
            );
        }
    }

    #[test]
    fn asns_arrive_as_numbers_too() {
        assert_eq!(
            VpnMerge::normalise_asn(Some(AsnValue::Integer(13335))).as_deref(),
            Some("AS13335")
        );
    }

    #[test]
    fn the_schema_is_carried_on_every_record() {
        /* Load-bearing: a cache written by an older merge is never re-screened by the new one.
        Adding Sentinel without bumping it left every known address on a stale verdict. */
        assert_eq!(merge(vec![reading("a")]).schema, VpnMerge::SCHEMA);
    }

    #[test]
    fn the_full_location_reads_like_a_place() {
        let geo = GeoLocation {
            city: Some("Toronto".into()),
            region: Some("Ontario".into()),
            country: Some("Canada".into()),
            country_code: Some("CA".into()),
            zip: Some("M5V".into()),
            isp: Some("Bell Canada".into()),
            timezone: Some("America/Toronto".into()),
            ..GeoLocation::default()
        };
        assert_eq!(
            geo.full_description().as_deref(),
            Some("Toronto, Ontario, Canada M5V  -  Bell Canada  -  TZ America/Toronto")
        );
    }

    #[test]
    fn an_empty_location_has_nothing_to_say() {
        assert!(GeoLocation::default().full_description().is_none());
    }

    #[test]
    fn a_stored_record_keeps_its_shape() {
        // Beyond the C# suite: camelCase, epoch-millisecond timestamps, and the decision's
        // label, as the C# writes every public property.
        let mut record = merge(vec![reading("ipqs")]);
        record.checked_at = DateTime::from_timestamp_millis(1_700_000_000_000).unwrap();
        let json = serde_json::to_value(&record).unwrap();

        assert_eq!(json["checkedAt"], 1_700_000_000_000_i64);
        assert_eq!(json["decision"]["label"], "CONFIRMED");
        assert_eq!(json["screenHits"], 0);
        let back: VpnRecord = serde_json::from_value(json).unwrap();
        assert_eq!(back, record);
    }
}

mod ip_address_tests {
    use chrono::DateTime;
    use pavlov_core::vpn::ip_addresses::{is_unroutable, local_record};

    #[test]
    fn private_and_reserved_addresses_are_unroutable() {
        /* LAN players connect from these. No provider can say anything about them, and asking
        costs a lookup per detector per join. */
        for ip in [
            "10.0.0.5",
            "192.168.1.1",
            "172.16.0.1",
            "172.31.255.255",
            "127.0.0.1",
            "169.254.1.1",
            "0.0.0.0",
            "224.0.0.1",
            "::1",
            "fe80::1",
            "fd00::1",
        ] {
            assert!(is_unroutable(Some(ip)), "{ip}");
        }
    }

    #[test]
    fn public_addresses_are_routable() {
        for ip in [
            "8.8.8.8",
            "1.1.1.1",
            "172.32.0.1",
            "172.15.255.255",
            "2606:4700::1",
        ] {
            assert!(!is_unroutable(Some(ip)), "{ip}");
        }
    }

    #[test]
    fn garbage_is_treated_as_unroutable_rather_than_looked_up() {
        for ip in [
            Some(""),
            None,
            Some("not an address"),
            Some("999.999.999.999"),
        ] {
            assert!(is_unroutable(ip), "{ip:?}");
        }
    }

    #[test]
    fn the_local_record_is_clean_and_not_actionable() {
        let record = local_record("10.0.0.5", DateTime::UNIX_EPOCH);
        assert!(record.local);
        assert!(!record.decision.flagged);
        assert!(!record.decision.actionable);
    }
}

mod sentinel_parser_tests {
    use pavlov_core::vpn::DetectorReading;
    use pavlov_core::vpn::sentinel::{boolish, parse};
    use serde_json::Value;

    fn read(json: &str) -> Result<DetectorReading, String> {
        parse(&serde_json::from_str::<Value>(json).unwrap())
    }

    #[test]
    fn the_lookup_shape_is_read() {
        let reading = read(
            r#"{"known":true,"verdict":"block","risk_score":88,
             "signals":{"vpn":true,"proxied":false,"tor":false,"dch":true,"anon":false},
             "network":{"asn":9009,"org":"M247 Europe","country":"NL","city":"Amsterdam"}}"#,
        )
        .unwrap();

        assert!(reading.flagged);
        assert_eq!(reading.vpn, Some(true));
        assert_eq!(reading.hosting, Some(true));
        assert_eq!(reading.asn.as_deref(), Some("AS9009"));
        assert_eq!(reading.organization.as_deref(), Some("M247 Europe"));
        assert_eq!(reading.country_code.as_deref(), Some("NL"));
        assert_eq!(reading.threat_score, Some(88.0));
    }

    #[test]
    fn the_evaluate_shape_puts_the_verdict_in_network_and_is_detected() {
        /* Both shapes use `network` for different things; reading only one returned nothing
        for every address - the first "responded but gave no verdict". */
        let reading = read(
            r#"{"network":{"vpn":true,"proxy":false,"datacenter":true,"residential":false,"service":"PROTON_VPN"}}"#,
        )
        .unwrap();

        assert_eq!(reading.vpn, Some(true));
        assert_eq!(reading.hosting, Some(true));
        assert_eq!(reading.residential, Some(false));
        assert_eq!(reading.provider.as_deref(), Some("PROTON_VPN"));
        // ASN/org do not exist in this shape and must not be invented from the verdict keys.
        assert!(reading.asn.is_none());
    }

    #[test]
    fn booleans_encoded_loosely_are_still_booleans() {
        // A body full of 0/1 looked like it had no verdict - the second production bug.
        for encoded in ["1", "\"true\"", "\"yes\"", "\"Y\""] {
            let reading = read(&format!(r#"{{"signals":{{"vpn":{encoded}}}}}"#)).unwrap();
            assert_eq!(reading.vpn, Some(true), "{encoded}");
        }
    }

    #[test]
    fn false_encoded_loosely_is_still_false() {
        for encoded in ["0", "\"false\"", "\"no\""] {
            let reading =
                read(&format!(r#"{{"signals":{{"vpn":{encoded},"tor":true}} }}"#)).unwrap();
            assert_eq!(reading.vpn, Some(false), "{encoded}");
        }
    }

    #[test]
    fn null_is_unknown_never_false() {
        // The rule the whole subsystem turns on.
        assert!(boolish(Some(&Value::Null)).is_none());
        assert!(boolish(Some(&Value::String("maybe".into()))).is_none());
        assert!(boolish(None).is_none());
    }

    #[test]
    fn an_address_with_no_reputation_still_has_a_verdict() {
        let reading = read(
            r#"{"known":false,"verdict":"allow","risk_score":3,
             "signals":{"vpn":null,"proxied":null,"tor":null},
             "network":{"asn":15169,"org":"Google","country":"US"}}"#,
        )
        .unwrap();

        assert!(!reading.flagged);
        assert!(reading.vpn.is_none()); // the overall call is known; this is not
        assert!(reading.proxy.is_none());
        assert_eq!(reading.asn.as_deref(), Some("AS15169"));
        assert!(reading.detail.unwrap().contains("no reputation data"));
    }

    #[test]
    fn a_review_verdict_counts_as_flagged() {
        assert!(
            read(r#"{"known":false,"verdict":"review","signals":{}}"#)
                .unwrap()
                .flagged
        );
    }

    #[test]
    fn an_unrecognisable_body_returns_null_and_explains_why() {
        let warning = read(r#"{"lip":"1.2.3.4","latency_ms":42}"#).unwrap_err();
        assert!(warning.contains("lip"));
        assert!(warning.contains("latency_ms"));
    }

    #[test]
    fn a_datacenter_alone_is_not_a_hit() {
        let reading =
            read(r#"{"signals":{"vpn":false,"proxied":false,"tor":false,"dch":true}}"#).unwrap();

        assert!(!reading.flagged);
        assert_eq!(reading.hosting, Some(true));
        assert_eq!(reading.residential, Some(false)); // inferable from datacenter, and only that
        assert!(reading.detail.unwrap().contains("datacenter"));
    }

    #[test]
    fn a_non_object_body_is_rejected_cleanly() {
        assert!(read("[1,2,3]").is_err());
    }
}
