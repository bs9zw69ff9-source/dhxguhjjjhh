//! A slash command's definition, as plain data (C# `SlashCommandProperties`), and the checks it
//! must pass before it is registered (C# `SlashCommandValidation.cs`).
//!
//! The gateway adapter turns a [`CommandSpec`] into Discord's wire form. Validating HERE, not
//! there, matters because Discord's bulk overwrite is ATOMIC: one bad command (a duplicate
//! option name is legal to build and fatal to register) rejects every command at once and
//! empties the whole picker. So bad commands are dropped and named, and the rest register.

use pavlov_core::text::case::{IgnoreCaseMap, fold_case};

/// Discord's limits.
pub const MAX_DESCRIPTION: usize = 100;
pub const MAX_NAME: usize = 32;
pub const MAX_OPTIONS: usize = 25;

/// What an option takes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OptionKind {
    SubCommand,
    SubCommandGroup,
    String,
    Integer,
    Boolean,
    User,
    Channel,
    Role,
    Mentionable,
    Number,
    Attachment,
}

impl OptionKind {
    /// A subcommand or a group of them, rather than a value.
    #[must_use]
    pub fn is_subcommand(self) -> bool {
        matches!(self, Self::SubCommand | Self::SubCommandGroup)
    }
}

/// A fixed choice's value.
#[derive(Debug, Clone, PartialEq)]
pub enum ChoiceValue {
    String(String),
    Integer(i64),
    Number(f64),
}

impl std::fmt::Display for ChoiceValue {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::String(s) => f.write_str(s),
            Self::Integer(i) => write!(f, "{i}"),
            Self::Number(n) => write!(f, "{n}"),
        }
    }
}

/// A fixed choice: the label shown, and what is sent.
#[derive(Debug, Clone, PartialEq)]
pub struct Choice {
    pub name: String,
    pub value: ChoiceValue,
}

impl Choice {
    #[must_use]
    pub fn string(name: impl Into<String>, value: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            value: ChoiceValue::String(value.into()),
        }
    }

    #[must_use]
    pub fn integer(name: impl Into<String>, value: i64) -> Self {
        Self {
            name: name.into(),
            value: ChoiceValue::Integer(value),
        }
    }
}

/// One option, or a subcommand with its own options.
#[derive(Debug, Clone, PartialEq)]
pub struct OptionSpec {
    pub kind: OptionKind,
    pub name: String,
    pub description: String,
    pub required: bool,
    pub autocomplete: bool,
    pub choices: Vec<Choice>,
    pub options: Vec<OptionSpec>,
    pub min_value: Option<f64>,
    pub max_value: Option<f64>,
    pub min_length: Option<u16>,
    pub max_length: Option<u16>,
}

impl OptionSpec {
    #[must_use]
    pub fn new(kind: OptionKind, name: impl Into<String>, description: impl Into<String>) -> Self {
        Self {
            kind,
            name: name.into(),
            description: description.into(),
            required: false,
            autocomplete: false,
            choices: Vec::new(),
            options: Vec::new(),
            min_value: None,
            max_value: None,
            min_length: None,
            max_length: None,
        }
    }

    #[must_use]
    pub fn sub(name: impl Into<String>, description: impl Into<String>) -> Self {
        Self::new(OptionKind::SubCommand, name, description)
    }

    #[must_use]
    pub fn required(mut self) -> Self {
        self.required = true;
        self
    }

    #[must_use]
    pub fn autocomplete(mut self) -> Self {
        self.autocomplete = true;
        self
    }

    #[must_use]
    pub fn choice(mut self, choice: Choice) -> Self {
        self.choices.push(choice);
        self
    }

    #[must_use]
    pub fn option(mut self, option: OptionSpec) -> Self {
        self.options.push(option);
        self
    }

    #[must_use]
    pub fn range(mut self, min: Option<f64>, max: Option<f64>) -> Self {
        self.min_value = min;
        self.max_value = max;
        self
    }

    #[must_use]
    pub fn length(mut self, min: Option<u16>, max: Option<u16>) -> Self {
        self.min_length = min;
        self.max_length = max;
        self
    }
}

/// Where a command may be run from (Discord's interaction contexts).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Contexts {
    pub guild: bool,
    pub bot_dm: bool,
    pub private_channel: bool,
}

/// How a command is installed (Discord's integration types).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Installs {
    pub guild: bool,
    pub user: bool,
}

/// A slash command.
#[derive(Debug, Clone, PartialEq)]
pub struct CommandSpec {
    pub name: String,
    pub description: String,
    pub options: Vec<OptionSpec>,
    /// Discord's default member permissions bitfield, when the command sets one.
    pub default_member_permissions: Option<u64>,
    /// `None` leaves Discord's default.
    pub contexts: Option<Contexts>,
    pub installs: Option<Installs>,
}

impl CommandSpec {
    #[must_use]
    pub fn new(name: impl Into<String>, description: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            description: description.into(),
            options: Vec::new(),
            default_member_permissions: None,
            contexts: None,
            installs: None,
        }
    }

    #[must_use]
    pub fn option(mut self, option: OptionSpec) -> Self {
        self.options.push(option);
        self
    }

    /// The names of its subcommands and groups.
    #[must_use]
    pub fn subcommands(&self) -> Vec<&str> {
        self.options
            .iter()
            .filter(|o| o.kind.is_subcommand())
            .map(|o| o.name.as_str())
            .collect()
    }
}

/// One thing wrong with one command.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommandProblem {
    pub command: String,
    pub problem: String,
}

fn is_lowercase(name: &str) -> bool {
    !name.chars().any(char::is_uppercase)
}

fn length(text: &str) -> usize {
    text.chars().count()
}

/// Everything wrong with one command: lengths, case, counts, and duplicate names among
/// siblings at every level (the one the builder never catches).
#[must_use]
pub fn problems(spec: &CommandSpec) -> Vec<CommandProblem> {
    let name = if spec.name.is_empty() {
        "(unnamed)"
    } else {
        spec.name.as_str()
    };
    let mut out = Vec::new();
    let mut problem = |text: String| {
        out.push(CommandProblem {
            command: name.to_owned(),
            problem: text,
        });
    };
    let n = length(&spec.name);
    if n == 0 || n > MAX_NAME {
        problem(format!(
            "the name is {n} characters; the limit is {MAX_NAME}"
        ));
    }
    if !is_lowercase(&spec.name) {
        problem("the name must be lowercase".into());
    }
    let d = length(&spec.description);
    if d > MAX_DESCRIPTION {
        problem(format!(
            "the description is {d} characters; the limit is {MAX_DESCRIPTION}"
        ));
    }
    check(&spec.options, name, &mut problem);
    out
}

fn check(options: &[OptionSpec], path: &str, problem: &mut impl FnMut(String)) {
    if options.len() > MAX_OPTIONS {
        problem(format!(
            "{path} has {} options; the limit is {MAX_OPTIONS}",
            options.len()
        ));
    }
    let mut counts: IgnoreCaseMap<usize> = IgnoreCaseMap::new();
    for option in options {
        let n = counts.get(&option.name).copied().unwrap_or(0);
        counts.insert(option.name.clone(), n + 1);
    }
    for (duplicate, count) in counts.iter().filter(|(_, c)| **c > 1) {
        problem(format!(
            "{path} declares \"{duplicate}\" {count} times - names must be unique among siblings"
        ));
    }
    for option in options {
        let here = format!("{path} {}", option.name);
        let n = length(&option.name);
        if n == 0 || n > MAX_NAME {
            problem(format!(
                "{here}: the name is {n} characters; the limit is {MAX_NAME}"
            ));
        }
        if !is_lowercase(&option.name) {
            problem(format!("{here}: option names must be lowercase"));
        }
        let d = length(&option.description);
        if d > MAX_DESCRIPTION {
            problem(format!(
                "{here}: the description is {d} characters; the limit is {MAX_DESCRIPTION}"
            ));
        }
        if option.choices.len() > MAX_OPTIONS {
            problem(format!(
                "{here}: {} choices; the limit is {MAX_OPTIONS}",
                option.choices.len()
            ));
        }
        if !option.options.is_empty() {
            check(&option.options, &here, problem);
        }
    }
}

/// Split commands into those safe to register and the problems that kept the rest out.
/// Duplicate TOP-LEVEL names are checked across the whole set, since no one command can see
/// the collision; every command claiming the name is dropped.
#[must_use]
pub fn partition(specs: Vec<CommandSpec>) -> (Vec<CommandSpec>, Vec<CommandProblem>) {
    let mut valid = Vec::new();
    let mut rejected = Vec::new();
    for spec in specs {
        let found = problems(&spec);
        if found.is_empty() {
            valid.push(spec);
        } else {
            rejected.extend(found);
        }
    }
    let mut counts: IgnoreCaseMap<usize> = IgnoreCaseMap::new();
    for spec in &valid {
        let n = counts.get(&spec.name).copied().unwrap_or(0);
        counts.insert(spec.name.clone(), n + 1);
    }
    let duplicated: Vec<(String, usize)> = counts
        .iter()
        .filter(|(_, c)| **c > 1)
        .map(|(n, c)| (n.to_owned(), *c))
        .collect();
    for (name, count) in &duplicated {
        rejected.push(CommandProblem {
            command: name.clone(),
            problem: format!("{count} commands are named \"{name}\" - two classes are claiming it"),
        });
        let folded = fold_case(name);
        valid.retain(|s| fold_case(&s.name) != folded);
    }
    (valid, rejected)
}
