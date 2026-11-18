//! Port of `SubcommandFilterTests.cs`: `COMMANDS_DISABLED` switches off whole commands and
//! single subcommands, and reports a typo rather than ignoring it.

#![allow(clippy::unwrap_used, clippy::expect_used, clippy::panic)]

mod common;

use std::sync::Arc;

use futures_util::future::BoxFuture;
use pavlov_discord::command::{CommandSpec, OptionSpec};
use pavlov_discord::slash::{
    CommandContext, CommandError, DisabledCommandList, SlashCommand, apply,
};

struct FakeCommand {
    name: String,
    subcommands: Vec<String>,
}

impl SlashCommand for FakeCommand {
    fn name(&self) -> &str {
        &self.name
    }

    fn ephemeral(&self) -> bool {
        true
    }

    fn build(&self) -> CommandSpec {
        let mut spec = CommandSpec::new(&self.name, "d");
        for sub in &self.subcommands {
            spec = spec.option(OptionSpec::sub(sub, "d"));
        }
        spec
    }

    fn handle<'a>(
        &'a self,
        _context: CommandContext<'a>,
    ) -> BoxFuture<'a, Result<(), CommandError>> {
        Box::pin(async { Ok(()) })
    }
}

const WHITELIST: [&str; 7] = [
    "add",
    "setrank",
    "remove",
    "list",
    "subclasses",
    "playtime",
    "wipe",
];

fn fake(name: &str, subs: &[&str]) -> Arc<dyn SlashCommand> {
    Arc::new(FakeCommand {
        name: name.into(),
        subcommands: subs.iter().map(|s| (*s).to_owned()).collect(),
    })
}

struct Applied {
    all: Vec<Arc<dyn SlashCommand>>,
    kept: Vec<Arc<dyn SlashCommand>>,
    unknown: Vec<String>,
}

fn applied(entries: &[&str]) -> Applied {
    let all = vec![
        fake("whitelist", &WHITELIST),
        fake("arrest", &[]),
        fake("stats", &[]),
    ];
    let (kept, unknown) = apply(&all, &DisabledCommandList::parse(entries));
    Applied { all, kept, unknown }
}

fn named<'a>(kept: &'a [Arc<dyn SlashCommand>], name: &str) -> &'a Arc<dyn SlashCommand> {
    kept.iter().find(|c| c.name() == name).unwrap()
}

fn registered(command: &Arc<dyn SlashCommand>) -> Vec<String> {
    command
        .build()
        .options
        .into_iter()
        .map(|o| o.name)
        .collect()
}

fn names(kept: &[Arc<dyn SlashCommand>]) -> Vec<&str> {
    kept.iter().map(|c| c.name()).collect()
}

#[test]
fn two_subcommands_go_and_the_rest_of_the_command_stays() {
    let a = applied(&["whitelist playtime", "whitelist setrank"]);

    assert_eq!(
        registered(named(&a.kept, "whitelist")),
        ["add", "remove", "list", "subclasses", "wipe"]
    );
    assert!(a.unknown.is_empty());
}

#[test]
fn a_whole_command_still_goes_the_old_way() {
    let a = applied(&["arrest"]);
    assert!(!names(&a.kept).contains(&"arrest"));
    assert!(names(&a.kept).contains(&"whitelist"));
}

#[test]
fn both_kinds_mix() {
    let a = applied(&["stats", "whitelist playtime", "arrest"]);

    assert_eq!(names(&a.kept), ["whitelist"]);
    assert!(!registered(&a.kept[0]).contains(&"playtime".to_owned()));
    assert!(a.unknown.is_empty());
}

#[test]
fn spelling_and_spacing_do_not_matter() {
    for entry in ["WHITELIST Playtime", "whitelist   playtime"] {
        let a = applied(&[entry]);
        assert!(
            !registered(named(&a.kept, "whitelist")).contains(&"playtime".to_owned()),
            "{entry}"
        );
        assert!(a.unknown.is_empty(), "{entry}");
    }
}

#[test]
fn a_command_with_nothing_left_is_dropped_whole() {
    let entries: Vec<String> = WHITELIST.iter().map(|s| format!("whitelist {s}")).collect();
    let refs: Vec<&str> = entries.iter().map(String::as_str).collect();
    assert!(!names(&applied(&refs).kept).contains(&"whitelist"));
}

#[test]
fn a_typo_is_reported_not_ignored() {
    let mut unknown = applied(&["whitelist playtim", "whitlist add", "arest"]).unknown;
    unknown.sort();
    assert_eq!(unknown, ["arest", "whitelist playtim", "whitlist add"]);
}

#[test]
fn an_untouched_command_is_not_wrapped() {
    let a = applied(&["whitelist playtime"]);
    let original = a.all.iter().find(|c| c.name() == "stats").unwrap();
    assert!(Arc::ptr_eq(named(&a.kept, "stats"), original));
}

#[test]
fn the_wrapper_keeps_the_commands_own_settings() {
    let a = applied(&["whitelist playtime"]);
    let whitelist = named(&a.kept, "whitelist");
    assert_eq!(whitelist.name(), "whitelist");
    assert!(whitelist.ephemeral());
}

#[test]
fn the_real_whitelist_has_the_subcommands_this_is_about() {
    // The .env example switches off "whitelist playtime" and "whitelist setrank"; a rename
    // would turn that line into a typo the bot only warns about.
    let store = common::commands::store();
    let whitelist = common::commands::whitelist(&store, common::commands::rosters(None), &[]);
    let subs = whitelist
        .build()
        .subcommands()
        .into_iter()
        .map(str::to_owned)
        .collect::<Vec<_>>();
    assert!(subs.contains(&"playtime".to_owned()));
    assert!(subs.contains(&"setrank".to_owned()));
}
