//! Port of `SlashCommandValidationTests.cs` (the validator and the partition). The three
//! command-specific tests are ported with those commands.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use pavlov_discord::command::{CommandSpec, OptionKind, OptionSpec, partition, problems};
use pavlov_discord::slash::SlashCommand;

fn sub(name: &str) -> OptionSpec {
    OptionSpec::sub(name, "a subcommand")
}

fn command(name: &str, options: Vec<OptionSpec>) -> CommandSpec {
    let mut spec = CommandSpec::new(name, "a command");
    spec.options = options;
    spec
}

#[test]
fn duplicate_subcommand_names_are_rejected() {
    let found = problems(&command(
        "thing",
        vec![sub("player"), sub("staff"), sub("player")],
    ));

    assert_eq!(found.len(), 1);
    assert!(found[0].problem.contains("player"));
    assert!(found[0].problem.to_lowercase().contains("unique"));
}

#[test]
fn duplicates_nested_inside_a_subcommand_are_caught() {
    let nested = sub("group")
        .option(OptionSpec::new(OptionKind::String, "who", "x"))
        .option(OptionSpec::new(OptionKind::String, "who", "y"));
    assert!(!problems(&command("thing", vec![nested])).is_empty());
}

#[test]
fn the_builder_catches_length_and_case_but_not_duplicates() {
    // Here the validator catches all three: there is no builder to throw first.
    let long = CommandSpec::new("thing", "x".repeat(101));
    assert!(!problems(&long).is_empty());
    assert!(!problems(&command("thing", vec![sub("Player")])).is_empty());
    assert!(!problems(&command("thing", vec![sub("player"), sub("player")])).is_empty());
}

#[test]
fn a_well_formed_command_has_no_problems() {
    assert!(problems(&command("thing", vec![sub("one"), sub("two")])).is_empty());
}

#[test]
fn a_bad_command_is_dropped_and_the_rest_survive() {
    let (valid, rejected) = partition(vec![
        command("good", vec![]),
        command("bad", vec![sub("dupe"), sub("dupe")]),
        command("alsogood", vec![]),
    ]);

    assert_eq!(valid.len(), 2);
    assert_eq!(rejected.len(), 1);
    assert_eq!(rejected[0].command, "bad");
}

#[test]
fn two_commands_with_one_name_are_both_dropped() {
    let (valid, rejected) = partition(vec![
        command("stats", vec![]),
        command("stats", vec![]),
        command("other", vec![]),
    ]);

    assert_eq!(valid.len(), 1);
    assert_eq!(valid[0].name, "other");
    assert!(rejected.iter().any(|r| r.command == "stats"));
}

#[test]
fn an_entirely_valid_payload_passes_through_untouched() {
    let (valid, rejected) = partition(vec![
        command("a", vec![]),
        command("b", vec![]),
        command("c", vec![]),
    ]);
    assert_eq!(valid.len(), 3);
    assert!(rejected.is_empty());
}

#[test]
fn the_whitelist_command_registers_cleanly() {
    let store = common::commands::store();
    let built = common::commands::whitelist(&store, common::commands::rosters(None), &[]).build();
    assert!(problems(&built).is_empty(), "{:?}", problems(&built));
}

#[test]
fn the_subclasses_command_registers_cleanly_and_offers_only_factions_with_subclasses() {
    let command = common::commands::subclasses();
    let built = command.build();
    assert!(problems(&built).is_empty());
    let faction = built.options.iter().find(|o| o.name == "faction").unwrap();
    assert!(!faction.choices.is_empty());
    let factions = pavlov_core::factions::testing::ladder();
    for choice in &faction.choices {
        assert!(
            !factions
                .get(Some(&choice.name))
                .unwrap()
                .subclasses
                .is_empty(),
            "{}",
            choice.name
        );
    }
}

#[test]
fn the_subclass_command_takes_a_discord_account() {
    let store = common::commands::store();
    let command = pavlov_discord::commands::subclass::SubclassCommand {
        rosters: common::commands::rosters(None),
        members: std::sync::Arc::new(pavlov_host::factions::FactionMembers::new(
            std::sync::Arc::clone(&store),
        )),
        access: common::commands::access(&store, &[]),
    };
    let built = command.build();
    assert!(problems(&built).is_empty(), "{:?}", problems(&built));

    let member = built.options.iter().find(|o| o.name == "member").unwrap();
    assert_eq!(member.kind, OptionKind::User);
    assert!(!built.options.iter().any(|o| o.name == "playerid"));

    // Asserted on the VALUE and its round trip: the handler parses the bare name back out.
    let choices = &built
        .options
        .iter()
        .find(|o| o.name == "subclass")
        .unwrap()
        .choices;
    assert!(
        choices
            .iter()
            .any(|c| c.value.to_string() == "GWPD:Detective")
    );
    assert_eq!(
        pavlov_discord::commands::subclass::parse_value("GWPD:Detective"),
        (Some("GWPD".to_owned()), "Detective".to_owned())
    );
    assert!(choices.iter().any(|c| c.name == "GWPD - Detective"));
}
