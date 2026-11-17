//! Turning player-supplied text into something safe to print or send (`Core/Text`).

pub mod case;
mod currency;
mod name_labels;
pub mod number;
mod sanitize;

pub use currency::Currency;
pub use name_labels::NameLabels;
pub use sanitize::Sanitize;
