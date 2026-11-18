//! A Discord embed as data, independent of any Discord library, so the feeds (posted to
//! webhooks over plain HTTP) and the gateway layer build the same thing the same way.

use chrono::{DateTime, SecondsFormat, Utc};
use serde_json::{Value, json};

/// The police skin's palette (C# `Skin.Police.Colors`), as 24-bit RGB.
pub mod palette {
    pub const GREEN: u32 = 0x4A_DE_80;
    pub const AMBER: u32 = 0xFB_BF_24;
    pub const GOLD: u32 = 0xFA_CC_15;
    pub const BLUE: u32 = 0x60_A5_FA;
    pub const SKY: u32 = 0x38_BD_F8;
    pub const BAN_RED: u32 = 0xF4_3E_5E;
    pub const ERROR_RED: u32 = 0xFB_71_85;
    pub const GREY: u32 = 0x94_A3_B8;
    pub const PURPLE: u32 = 0xA7_8B_FA;
    pub const PINK: u32 = 0xF4_72_B6;
    pub const TEAL: u32 = 0x2D_D4_BF;
    pub const ORANGE: u32 = 0xFB_92_3C;
    pub const SERVER_UP: u32 = 0x3F_A2_5F;
    pub const SERVER_DOWN: u32 = 0x9E_3B_2F;
}

/// One field.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EmbedField {
    pub name: String,
    pub value: String,
    pub inline: bool,
}

/// An embed.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Embed {
    pub title: Option<String>,
    pub description: Option<String>,
    pub color: Option<u32>,
    pub author: Option<String>,
    /// The author line's icon URL (a caller's avatar).
    pub author_icon: Option<String>,
    pub fields: Vec<EmbedField>,
    pub footer: Option<String>,
    pub timestamp: Option<DateTime<Utc>>,
}

impl Embed {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    #[must_use]
    pub fn title(mut self, title: impl Into<String>) -> Self {
        self.title = Some(title.into());
        self
    }

    #[must_use]
    pub fn description(mut self, description: impl Into<String>) -> Self {
        self.description = Some(description.into());
        self
    }

    #[must_use]
    pub fn color(mut self, color: u32) -> Self {
        self.color = Some(color);
        self
    }

    #[must_use]
    pub fn author(mut self, author: impl Into<String>) -> Self {
        self.author = Some(author.into());
        self
    }

    #[must_use]
    pub fn author_icon(mut self, url: impl Into<String>) -> Self {
        self.author_icon = Some(url.into());
        self
    }

    #[must_use]
    pub fn field(
        mut self,
        name: impl Into<String>,
        value: impl Into<String>,
        inline: bool,
    ) -> Self {
        self.fields.push(EmbedField {
            name: name.into(),
            value: value.into(),
            inline,
        });
        self
    }

    #[must_use]
    pub fn footer(mut self, footer: impl Into<String>) -> Self {
        self.footer = Some(footer.into());
        self
    }

    #[must_use]
    pub fn timestamp(mut self, at: DateTime<Utc>) -> Self {
        self.timestamp = Some(at);
        self
    }

    /// The field with this name, for tests and for renderers.
    /// Discord's counted length: title, description, author, footer and every field's name
    /// and value, in characters. Over 6000 and Discord rejects the whole message.
    #[must_use]
    pub fn length(&self) -> usize {
        let opt = |s: &Option<String>| s.as_deref().map_or(0, |t| t.chars().count());
        opt(&self.title)
            + opt(&self.description)
            + opt(&self.author)
            + opt(&self.footer)
            + self
                .fields
                .iter()
                .map(|f| f.name.chars().count() + f.value.chars().count())
                .sum::<usize>()
    }

    #[must_use]
    pub fn field_named(&self, name: &str) -> Option<&EmbedField> {
        self.fields.iter().find(|f| f.name == name)
    }

    /// Discord's JSON shape.
    #[must_use]
    pub fn to_json(&self) -> Value {
        let mut out = serde_json::Map::new();
        if let Some(title) = &self.title {
            out.insert("title".into(), json!(title));
        }
        if let Some(description) = &self.description {
            out.insert("description".into(), json!(description));
        }
        if let Some(color) = self.color {
            out.insert("color".into(), json!(color));
        }
        if let Some(author) = &self.author {
            let mut entry = json!({ "name": author });
            if let Some(icon) = &self.author_icon {
                entry["icon_url"] = json!(icon);
            }
            out.insert("author".into(), entry);
        }
        if !self.fields.is_empty() {
            out.insert(
                "fields".into(),
                Value::Array(
                    self.fields
                        .iter()
                        .map(|f| json!({ "name": f.name, "value": f.value, "inline": f.inline }))
                        .collect(),
                ),
            );
        }
        if let Some(footer) = &self.footer {
            out.insert("footer".into(), json!({ "text": footer }));
        }
        if let Some(at) = self.timestamp {
            out.insert(
                "timestamp".into(),
                json!(at.to_rfc3339_opts(SecondsFormat::Millis, true)),
            );
        }
        Value::Object(out)
    }
}
