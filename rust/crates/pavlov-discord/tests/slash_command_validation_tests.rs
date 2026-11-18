//! Port of `SlashCommandValidationTests.cs` (the validator and the partition). The three
//! command-specific tests are ported with those commands.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

use pavlov_discord::command::{CommandSpec, OptionKind, OptionSpec, partition, problems};

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
