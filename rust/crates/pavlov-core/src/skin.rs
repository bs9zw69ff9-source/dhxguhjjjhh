//! The bot's voice and look (C# `Discord/Skin.cs`, `Lore.cs`, `Theme.cs`, `EmbedBudget.cs`):
//! the police vocabulary, the palette, the glyphs, the brand on every embed, and Discord's
//! size limits. Pure data, so the feeds and the gateway layer speak with one voice.
//!
//! POLICE ONLY: the fallout skin was removed (owner, 2026-11-17), and `THEME=fallout` is
//! refused by name rather than silently read as police.

use std::sync::LazyLock;

use crate::embed::{Embed, palette};
use crate::text::Currency;
use crate::text::number::group_thousands;

/// The words a skin speaks with.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Vocabulary {
    // ---- glyphs ----
    pub money_glyph: &'static str,
    pub mark: &'static str,
    pub warrant_glyph: &'static str,
    pub jail_glyph: &'static str,
    pub staff_glyph: &'static str,
    // ---- nouns ----
    pub world: &'static str,
    pub money: Currency,
    pub money_label: &'static str,
    pub person: &'static str,
    pub people: &'static str,
    pub roaming_one: &'static str,
    pub roaming_many: &'static str,
    pub whereabouts: &'static str,
    pub whereabouts_title: &'static str,
    pub empty_server: &'static str,
    pub warrant: &'static str,
    pub warrants: &'static str,
    // ---- boards ----
    pub richest_title: &'static str,
    /// `{0}` is the row count.
    pub richest_heading: &'static str,
    pub ledgers_unreadable: &'static str,
    pub no_ledgers: &'static str,
    pub most_wanted_title: &'static str,
    pub no_arrests: &'static str,
    pub warrant_board_title: &'static str,
    pub no_warrants_title: &'static str,
    pub nobody_wanted: &'static str,
    pub staff_board_title: &'static str,
    // ---- moderation ----
    pub warrant_issued: &'static str,
    pub banned_for_good: &'static str,
    pub banned_for_a_while: &'static str,
    pub banned: &'static str,
    pub banned_by_the_server: &'static str,
    pub ban_served: &'static str,
    pub ban_lifted: &'static str,
    pub may_return: &'static str,
    pub kicked: &'static str,
    // ---- everything else ----
    pub money_moved: &'static str,
    pub give_money_command: &'static str,
    pub adjust_money_command: &'static str,
    pub help_title: &'static str,
    pub connect_title: &'static str,
    pub arrived: &'static str,
    pub connect_log: &'static str,
    pub verify_title: &'static str,
    pub verify_review: &'static str,
    pub verify_alts: &'static str,
    pub verified: &'static str,
}

impl Vocabulary {
    /// Every phrase, by field name, so a test can sweep them all for a stray word.
    #[must_use]
    pub fn phrases(&self) -> Vec<(&'static str, &'static str)> {
        vec![
            ("money_glyph", self.money_glyph),
            ("mark", self.mark),
            ("warrant_glyph", self.warrant_glyph),
            ("jail_glyph", self.jail_glyph),
            ("staff_glyph", self.staff_glyph),
            ("world", self.world),
            ("money_label", self.money_label),
            ("person", self.person),
            ("people", self.people),
            ("roaming_one", self.roaming_one),
            ("roaming_many", self.roaming_many),
            ("whereabouts", self.whereabouts),
            ("whereabouts_title", self.whereabouts_title),
            ("empty_server", self.empty_server),
            ("warrant", self.warrant),
            ("warrants", self.warrants),
            ("richest_title", self.richest_title),
            ("richest_heading", self.richest_heading),
            ("ledgers_unreadable", self.ledgers_unreadable),
            ("no_ledgers", self.no_ledgers),
            ("most_wanted_title", self.most_wanted_title),
            ("no_arrests", self.no_arrests),
            ("warrant_board_title", self.warrant_board_title),
            ("no_warrants_title", self.no_warrants_title),
            ("nobody_wanted", self.nobody_wanted),
            ("staff_board_title", self.staff_board_title),
            ("warrant_issued", self.warrant_issued),
            ("banned_for_good", self.banned_for_good),
            ("banned_for_a_while", self.banned_for_a_while),
            ("banned", self.banned),
            ("banned_by_the_server", self.banned_by_the_server),
            ("ban_served", self.ban_served),
            ("ban_lifted", self.ban_lifted),
            ("may_return", self.may_return),
            ("kicked", self.kicked),
            ("money_moved", self.money_moved),
            ("give_money_command", self.give_money_command),
            ("adjust_money_command", self.adjust_money_command),
            ("help_title", self.help_title),
            ("connect_title", self.connect_title),
            ("arrived", self.arrived),
            ("connect_log", self.connect_log),
            ("verify_title", self.verify_title),
            ("verify_review", self.verify_review),
            ("verify_alts", self.verify_alts),
            ("verified", self.verified),
        ]
    }
}

/// The palette. The names keep their meaning in every skin: green is cleared, red a ban,
/// amber a warning - a reader learns the bar before the words.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Palette {
    pub green: u32,
    pub amber: u32,
    pub gold: u32,
    pub blue: u32,
    pub sky: u32,
    pub ban_red: u32,
    pub error_red: u32,
    pub grey: u32,
    pub purple: u32,
    pub pink: u32,
    pub teal: u32,
    pub orange: u32,
    pub server_up: u32,
    pub server_down: u32,
}

impl Palette {
    /// Every colour by name, for `/theme` and the tests.
    #[must_use]
    pub fn entries(&self) -> [(&'static str, u32); 14] {
        [
            ("Green", self.green),
            ("Amber", self.amber),
            ("Gold", self.gold),
            ("Blue", self.blue),
            ("Sky", self.sky),
            ("BanRed", self.ban_red),
            ("ErrorRed", self.error_red),
            ("Grey", self.grey),
            ("Purple", self.purple),
            ("Pink", self.pink),
            ("Teal", self.teal),
            ("Orange", self.orange),
            ("ServerUp", self.server_up),
            ("ServerDown", self.server_down),
        ]
    }
}

/// A skin: a name, a brand, its words and its colours.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Skin {
    pub name: &'static str,
    pub brand: &'static str,
    pub words: Vocabulary,
    pub colors: Palette,
}

/// The `THEME` value that used to select the fallout skin, refused by name.
pub const REMOVED_FALLOUT_NAME: &str = "fallout";

static POLICE: LazyLock<Skin> = LazyLock::new(|| Skin {
    name: "police",
    brand: "GWPD",
    words: Vocabulary {
        money_glyph: "💰",
        mark: "🚔",
        warrant_glyph: "🚨",
        jail_glyph: "🔒",
        staff_glyph: "👮",
        world: "the city",
        money: Currency::dollars(),
        money_label: "Balance",
        person: "player",
        people: "players",
        roaming_one: "player on the streets right now.",
        roaming_many: "players on the streets right now.",
        whereabouts: "online",
        whereabouts_title: "Online",
        empty_server: "*nobody*",
        warrant: "warrant",
        warrants: "warrants",
        richest_title: "Richest players",
        richest_heading: "Top {0} richest players",
        ledgers_unreadable: "Economy records are unreadable.",
        no_ledgers: "No ledgers on file yet. Balances appear here once players have money.",
        most_wanted_title: "Most wanted",
        no_arrests: "No arrests on record yet.",
        warrant_board_title: "Active warrants",
        no_warrants_title: "No active warrants",
        nobody_wanted: "Nobody is currently wanted.",
        staff_board_title: "Staff activity",
        warrant_issued: "Warrant issued",
        banned_for_good: "Permanently banned",
        banned_for_a_while: "Banned",
        banned: "Banned",
        banned_by_the_server: "Banned by the server",
        ban_served: "Ban served",
        ban_lifted: "Ban lifted",
        may_return: "may reconnect.",
        kicked: "Kicked",
        money_moved: "Balance updated",
        give_money_command: "givemoney",
        adjust_money_command: "adjustmoney",
        help_title: "Commands",
        connect_title: "Player information",
        arrived: "just connected on",
        connect_log: "Connection log",
        verify_title: "Verify to unlock the server",
        verify_review: "A staff member reviews each request. Once approved you get access to the rest of the server.",
        verify_alts: "One account per person — alts cannot be verified.",
        verified: "Verified",
    },
    colors: Palette {
        green: palette::GREEN,
        amber: palette::AMBER,
        gold: palette::GOLD,
        blue: palette::BLUE,
        sky: palette::SKY,
        ban_red: palette::BAN_RED,
        error_red: palette::ERROR_RED,
        grey: palette::GREY,
        purple: palette::PURPLE,
        pink: palette::PINK,
        teal: palette::TEAL,
        orange: palette::ORANGE,
        server_up: palette::SERVER_UP,
        server_down: palette::SERVER_DOWN,
    },
});

/// The police skin: the only one.
#[must_use]
pub fn police() -> &'static Skin {
    &POLICE
}

/// Every skin.
#[must_use]
pub fn all() -> [&'static Skin; 1] {
    [police()]
}

/// The skin of this name (trimmed, ignoring case), or `None`.
#[must_use]
pub fn named(name: Option<&str>) -> Option<&'static Skin> {
    let wanted = name?.trim();
    all()
        .into_iter()
        .find(|s| s.name.eq_ignore_ascii_case(wanted))
}

/// Whether this is the removed fallout skin's name.
#[must_use]
pub fn is_removed(name: Option<&str>) -> bool {
    name.is_some_and(|n| n.trim().eq_ignore_ascii_case(REMOVED_FALLOUT_NAME))
}

/// The words the bot speaks with (C# `Lore`).
pub mod lore {
    use super::{group_thousands, police};

    /// An amount of money: `$1,250`.
    #[must_use]
    pub fn amount(amount: i64) -> String {
        police().words.money.format(amount)
    }

    /// `1 player`, `1,234 players`.
    #[must_use]
    pub fn head_count(count: i64) -> String {
        let w = &police().words;
        if count == 1 {
            format!("1 {}", w.person)
        } else {
            format!("{} {}", group_thousands(count), w.people)
        }
    }

    #[must_use]
    pub fn roaming(count: i64) -> &'static str {
        let w = &police().words;
        if count == 1 {
            w.roaming_one
        } else {
            w.roaming_many
        }
    }

    #[must_use]
    pub fn richest_heading(rows: usize) -> String {
        police()
            .words
            .richest_heading
            .replace("{0}", &rows.to_string())
    }

    /// The plural with its first letter capitalised: `Warrants`.
    #[must_use]
    pub fn warrants_heading() -> String {
        let mut chars = police().words.warrants.chars();
        chars
            .next()
            .map(|c| c.to_uppercase().chain(chars).collect())
            .unwrap_or_default()
    }
}

/// The house style (C# `Theme`).
pub mod theme {
    use super::{Embed, police};

    pub const OK: &str = "✅";
    pub const BAD: &str = "❌";
    pub const WARN: &str = "⚠️";
    pub const DENY: &str = "🚫";
    pub const INFO: &str = "ℹ️";
    pub const DOT: &str = "•";
    pub const UP: &str = "🟢";
    pub const DOWN: &str = "🔴";
    pub const RANK: &str = "🏅";
    pub const DIVIDER: &str = "▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔";

    // Discord's hard limits: exceeding any one rejects the whole message.
    pub const MAX_TITLE: usize = 256;
    pub const MAX_DESCRIPTION: usize = 4096;
    pub const MAX_FIELD_NAME: usize = 256;
    pub const MAX_FIELD_VALUE: usize = 1024;
    pub const MAX_FIELDS: usize = 25;
    pub const MAX_FOOTER: usize = 2048;

    /// `BOT_NAME`, applied once at startup over the skin's own brand. Set-once rather than
    /// mutable: nothing after startup may rename the bot mid-flight.
    static BRAND_NAME: std::sync::OnceLock<String> = std::sync::OnceLock::new();

    /// Apply `BOT_NAME`. The first call wins; a later one is refused and returns `false`.
    pub fn set_brand_name(name: &str) -> bool {
        let name = name.trim();
        !name.is_empty() && BRAND_NAME.set(name.to_owned()).is_ok()
    }

    /// The brand: `BOT_NAME` when set, else the skin's.
    #[must_use]
    pub fn brand_name() -> &'static str {
        BRAND_NAME.get().map_or(police().brand, String::as_str)
    }

    /// `text` cut to `limit` characters, ending `...` when cut.
    #[must_use]
    pub fn truncate(text: &str, limit: usize) -> String {
        if text.chars().count() <= limit {
            text.to_owned()
        } else {
            format!(
                "{}...",
                text.chars()
                    .take(limit.saturating_sub(3))
                    .collect::<String>()
            )
        }
    }

    /// What a footer says besides the brand: all of it when unbranded, the part after the
    /// brand when branded already, or `None`.
    #[must_use]
    pub fn footer_after_brand(text: Option<&str>) -> Option<String> {
        footer_after(brand_name(), text)
    }

    /// The footer minus a brand stamp. Either brand counts: the process brand, which every
    /// themed builder stamps first, and the one being applied now.
    fn footer_after(brand: &str, text: Option<&str>) -> Option<String> {
        let text = text.filter(|t| !t.is_empty())?;
        let mark = police().words.mark;
        let Some(rest) = [brand, brand_name()]
            .into_iter()
            // A whole name only: "GWPD" must not eat the front of "GWPD Dispatch".
            .find_map(|b| {
                text.strip_prefix(&format!("{mark} {b}"))
                    .filter(|rest| rest.is_empty() || rest.starts_with(' '))
            })
        else {
            return Some(text.to_owned());
        };
        let rest = rest.trim_start();
        let rest = rest.strip_prefix(DOT).map_or(rest, str::trim_start);
        (!rest.is_empty()).then(|| rest.to_owned())
    }

    impl Embed {
        /// The house style, clamped to Discord's limits: the mark and the brand in the footer
        /// (a footer already set keeps its place after it, and branding twice cannot stack),
        /// no timestamp.
        #[must_use]
        pub fn brand(self, footer: Option<&str>) -> Self {
            self.brand_as(brand_name(), footer)
        }

        /// [`Embed::brand`] under an explicit brand name.
        #[must_use]
        pub fn brand_as(mut self, brand: &str, footer: Option<&str>) -> Self {
            let footer = footer
                .map(str::to_owned)
                .or_else(|| footer_after(brand, self.footer.as_deref()));
            let mark = police().words.mark;
            let stamp = match footer.filter(|f| !f.is_empty()) {
                Some(footer) => format!("{mark} {brand} {DOT} {footer}"),
                None => format!("{mark} {brand}"),
            };
            self.footer = Some(truncate(&stamp, MAX_FOOTER));
            self.timestamp = None;
            self.title = self.title.map(|t| truncate(&t, MAX_TITLE));
            self.description = self.description.map(|d| truncate(&d, MAX_DESCRIPTION));
            self.fields.truncate(MAX_FIELDS);
            for field in &mut self.fields {
                field.name = truncate(&field.name, MAX_FIELD_NAME);
                field.value = truncate(&field.value, MAX_FIELD_VALUE);
            }
            self
        }
    }

    fn base(color: u32, title: String, description: Option<&str>) -> Embed {
        Embed::new()
            .color(color)
            .title(title)
            .description(description.unwrap_or_default())
            .brand(None)
    }

    #[must_use]
    pub fn success(title: &str, description: Option<&str>) -> Embed {
        base(police().colors.green, format!("{OK} {title}"), description)
    }

    #[must_use]
    pub fn failure(title: &str, description: Option<&str>) -> Embed {
        base(
            police().colors.error_red,
            format!("{BAD} {title}"),
            description,
        )
    }

    #[must_use]
    pub fn denied(title: &str, description: Option<&str>) -> Embed {
        base(
            police().colors.ban_red,
            format!("{DENY} {title}"),
            description,
        )
    }

    #[must_use]
    pub fn warning(title: &str, description: Option<&str>) -> Embed {
        base(
            police().colors.amber,
            format!("{WARN} {title}"),
            description,
        )
    }

    #[must_use]
    pub fn notice(title: &str, description: Option<&str>) -> Embed {
        base(police().colors.blue, format!("{INFO} {title}"), description)
    }

    #[must_use]
    pub fn punishment(title: &str, description: Option<&str>) -> Embed {
        base(police().colors.ban_red, title.to_owned(), description)
    }

    /// Lines packed into pages of at most `limit` characters; never zero pages.
    #[must_use]
    pub fn paginate<'a>(lines: impl IntoIterator<Item = &'a str>, limit: usize) -> Vec<String> {
        let mut pages = Vec::new();
        let mut current = String::new();
        let mut current_len = 0;
        for line in lines {
            let piece = if line.chars().count() > limit {
                truncate(line, limit)
            } else {
                line.to_owned()
            };
            let piece_len = piece.chars().count();
            if current_len > 0 && current_len + piece_len + 1 > limit {
                pages.push(std::mem::take(&mut current));
                current_len = 0;
            }
            if current_len > 0 {
                current.push('\n');
                current_len += 1;
            }
            current.push_str(&piece);
            current_len += piece_len;
        }
        if current_len > 0 {
            pages.push(current);
        }
        if pages.is_empty() {
            pages.push(String::new());
        }
        pages
    }

    /// `█████░░░░░░░`: `value` of `max` in `width` cells.
    #[must_use]
    pub fn bar(value: f64, max: f64, width: usize) -> String {
        if max <= 0.0 || width == 0 {
            return "░".repeat(width);
        }
        #[allow(
            clippy::cast_possible_truncation,
            clippy::cast_sign_loss,
            clippy::cast_precision_loss
        )]
        let filled = ((value / max).clamp(0.0, 1.0) * width as f64).round() as usize;
        format!(
            "{}{}",
            "█".repeat(filled),
            "░".repeat(width - filled.min(width))
        )
    }

    /// Discord's relative timestamp: `<t:1700000000:R>`.
    #[must_use]
    pub fn relative(at: chrono::DateTime<chrono::Utc>) -> String {
        format!("<t:{}:R>", at.timestamp())
    }

    /// Discord's full date and time: `<t:1700000000:f>`.
    #[must_use]
    pub fn absolute(at: chrono::DateTime<chrono::Utc>) -> String {
        format!("<t:{}:f>", at.timestamp())
    }
}

/// Fields within Discord's 6000-character embed total, spent in the order they are added, so
/// the ones that matter most are charged first and never the ones squeezed out (C#
/// `EmbedBudget`). A field with nothing left to spend is dropped, never a rejected message.
#[derive(Debug, Clone)]
pub struct EmbedBudget {
    used: usize,
}

impl EmbedBudget {
    pub const FIELD_LIMIT: usize = 1024;
    /// Under 6000, leaving room for what is added after the budget.
    pub const TOTAL_BUDGET: usize = 5400;
    pub const TITLE_LIMIT: usize = 200;
    pub const FOOTER_ALLOWANCE: usize = 80;
    const MINIMUM: usize = 8;

    /// Seeded with what counts towards the total but is not a field.
    #[must_use]
    pub fn new(seeded: usize) -> Self {
        Self { used: seeded }
    }

    /// The room left for a field with this label.
    #[must_use]
    pub fn room(&self, label: &str) -> usize {
        Self::FIELD_LIMIT.min(Self::TOTAL_BUDGET.saturating_sub(self.used + label.chars().count()))
    }

    /// Add a field if there is room, cutting the value to fit. `false` when dropped.
    pub fn add(
        &mut self,
        embed: &mut Embed,
        label: &str,
        value: Option<&str>,
        inline: bool,
    ) -> bool {
        self.add_or(embed, label, value, inline, "unknown")
    }

    /// [`add`](Self::add) with a placeholder for a blank value.
    pub fn add_or(
        &mut self,
        embed: &mut Embed,
        label: &str,
        value: Option<&str>,
        inline: bool,
        placeholder: &str,
    ) -> bool {
        let text = value
            .filter(|v| !v.trim().is_empty())
            .unwrap_or(placeholder);
        let room = self.room(label);
        if room < Self::MINIMUM {
            return false;
        }
        let text = if text.chars().count() > room {
            Self::truncate(text, room)
        } else {
            text.to_owned()
        };
        self.used += label.chars().count() + text.chars().count();
        embed.fields.push(crate::embed::EmbedField {
            name: label.to_owned(),
            value: text,
            inline,
        });
        true
    }

    /// `text` cut to `max` characters, ending `…` when cut.
    #[must_use]
    pub fn truncate(text: &str, max: usize) -> String {
        if text.chars().count() <= max {
            text.to_owned()
        } else {
            format!(
                "{}…",
                text.chars().take(max.saturating_sub(1)).collect::<String>()
            )
        }
    }
}
