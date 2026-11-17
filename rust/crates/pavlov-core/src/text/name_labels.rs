/// The parenthesised tags the bot puts after a player's name, in one place
/// (C# `Core/Text/NameLabels.cs`).
///
/// THESE COME BACK. A tag added for display ("Alice (online)" in an autocomplete list) returns
/// from Discord, or is read off the screen and typed back, with the tag still attached. So
/// every tag added must be removable at every point a name is accepted, and the C# once had two
/// lists that drifted and banned "Aliceonline". Here [`Sanitize::id`](super::Sanitize::id)
/// builds its pattern FROM this list, so the drift cannot happen by construction.
#[derive(Debug)]
pub struct NameLabels;

impl NameLabels {
    /// Every tag, without its brackets. "offline" is kept although nothing emits it any more:
    /// names carrying it were written into rosters and ban records by earlier builds.
    pub const ALL: [&'static str; 6] = [
        "online",
        "offline",
        "recent",
        "manual entry",
        "banned",
        "nobody online",
    ];

    /// Append a tag for display. An empty or blank tag leaves the name alone.
    #[must_use]
    pub fn decorate(name: &str, tag: Option<&str>) -> String {
        match tag {
            Some(t) if !t.trim().is_empty() => format!("{name} ({t})"),
            _ => name.to_owned(),
        }
    }
}
