//! Port of `WhitelistListTests.cs`: `/whitelist list` and `/whitelist subclasses`.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_core::factions::FactionDefinition;
use pavlov_core::text::case::IgnoreCaseMap;
use pavlov_discord::commands::whitelist::{roster_lines, subclass_lines};
use pavlov_host::factions::roster::Membership;

fn gwpd() -> &'static FactionDefinition {
    pavlov_core::factions::testing::ladder()
        .get(Some("GWPD"))
        .unwrap()
}

fn at(player: &str, rank: &str) -> Membership {
    Membership {
        faction: gwpd().name.clone(),
        player: player.into(),
        rank: rank.into(),
    }
}

fn held(pairs: &[(&str, &str)]) -> IgnoreCaseMap<String> {
    pairs
        .iter()
        .map(|(p, s)| ((*p).to_owned(), (*s).to_owned()))
        .collect()
}

#[test]
fn the_roster_is_highest_rank_first_with_one_member_per_line() {
    let low = &gwpd().order[0];
    let high = gwpd().order.last().unwrap();
    let roster = [at("Zed", low), at("Amy", low), at("Boss", high)];

    let lines = roster_lines(gwpd(), &roster, &held(&[("Amy", "Detective")]));

    assert_eq!(lines[0], format!("**{high}** · 1"));
    assert!(lines[1].contains("`Boss`"));
    let low_header = lines
        .iter()
        .position(|l| *l == format!("**{low}** · 2"))
        .unwrap();
    assert!(low_header > 1);
    assert_eq!(lines[low_header + 1], "• `Amy` · *Detective*");
    assert_eq!(lines[low_header + 2], "• `Zed`");
}

#[test]
fn an_empty_roster_says_so() {
    assert_eq!(
        roster_lines(gwpd(), &[], &held(&[])),
        ["*Nobody is on this roster.*"]
    );
}

#[test]
fn every_subclass_is_listed_with_its_holders_and_their_ranks() {
    let low = &gwpd().order[0];
    let high = gwpd().order.last().unwrap();
    let roster = [at("Amy", low), at("Boss", high)];

    let lines = subclass_lines(
        gwpd(),
        &roster,
        &held(&[("Amy", "Detective"), ("Boss", "Detective")]),
    );

    let detective = lines.iter().position(|l| l == "**Detective** · 2").unwrap();
    assert_eq!(lines[detective + 1], format!("• `Boss` · {high}")); // senior holder first
    assert_eq!(lines[detective + 2], format!("• `Amy` · {low}"));
    for other in gwpd().subclasses.keys().filter(|k| *k != "Detective") {
        assert!(lines.contains(&format!("**{other}** · 0")), "{other}");
    }
}

#[test]
fn a_faction_without_subclasses_says_so() {
    // Every faction in the police set has sub-classes, so a ladderless one stands in.
    let mut plain = gwpd().clone();
    plain.subclasses.clear();
    let lines = subclass_lines(&plain, &[], &held(&[]));
    assert_eq!(lines.len(), 1);
    assert!(lines[0].contains("has no sub-classes"));
}
