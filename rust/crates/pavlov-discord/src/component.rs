//! Buttons, select menus and modals as plain data. Their custom ids are built with
//! [`pavlov_core::component_id::ComponentId`], which refuses one Discord would truncate.

/// A button's look.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ButtonStyle {
    Primary,
    Secondary,
    Success,
    Danger,
    /// Opens `url`; has no custom id.
    Link,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Button {
    pub style: ButtonStyle,
    pub label: String,
    /// The custom id, or for a link button the URL.
    pub target: String,
    pub emoji: Option<String>,
    pub disabled: bool,
}

impl Button {
    #[must_use]
    pub fn new(style: ButtonStyle, label: impl Into<String>, custom_id: impl Into<String>) -> Self {
        Self {
            style,
            label: label.into(),
            target: custom_id.into(),
            emoji: None,
            disabled: false,
        }
    }

    #[must_use]
    pub fn disabled(mut self) -> Self {
        self.disabled = true;
        self
    }
}

/// One choice in a select menu.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelectOption {
    pub label: String,
    pub value: String,
    pub description: Option<String>,
    pub default: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SelectMenu {
    pub custom_id: String,
    pub placeholder: Option<String>,
    pub options: Vec<SelectOption>,
    pub min_values: u8,
    pub max_values: u8,
}

/// One row of a message's components.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ActionRow {
    Buttons(Vec<Button>),
    Select(SelectMenu),
}

/// A text field in a modal.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TextInput {
    pub custom_id: String,
    pub label: String,
    pub paragraph: bool,
    pub required: bool,
    pub placeholder: Option<String>,
    pub value: Option<String>,
    pub min_length: Option<u16>,
    pub max_length: Option<u16>,
}

/// A modal dialog.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Modal {
    pub custom_id: String,
    pub title: String,
    pub inputs: Vec<TextInput>,
}
