//! The "Recent errors" field `/health` shows (C# `HealthCommand.Errors`). Pure, so the
//! Discord layer only has to place it.

use super::ServiceStatus;

/// Discord's limit for one embed field.
pub const MAX_FIELD_LENGTH: usize = 1024;
/// Room kept for the "and N more" line.
const TAIL_ROOM: usize = 40;
const MAX_MESSAGE: usize = 160;

/// Every service's remembered failures, newest first across services (two failing together is
/// usually the whole diagnosis), one line each, stopping BEFORE the field limit rather than
/// truncating: Discord rejects an over-long field, which would lose the whole health reply.
/// Empty when nothing has failed.
#[must_use]
pub fn recent_errors_field(status: &[ServiceStatus]) -> String {
    let mut failures: Vec<(&str, i64, &str)> = status
        .iter()
        .flat_map(|s| {
            s.errors
                .iter()
                .map(move |e| (s.name.as_str(), e.at.timestamp(), e.message.as_str()))
        })
        .collect();
    if failures.is_empty() {
        return String::new();
    }
    failures.sort_by_key(|(_, at, _)| std::cmp::Reverse(*at));

    let mut body = String::new();
    let mut length = 0;
    let mut shown = 0;
    for (name, at, message) in &failures {
        // Relative: "how long ago" says whether it is still happening.
        let line = format!("<t:{at}:R> `{name}` — {}\n", flatten(message, MAX_MESSAGE));
        let line_length = line.chars().count();
        if length + line_length > MAX_FIELD_LENGTH - TAIL_ROOM {
            break;
        }
        body.push_str(&line);
        length += line_length;
        shown += 1;
    }
    if shown < failures.len() {
        body.push_str(&format!("*and {} more*", failures.len() - shown));
    }
    body
}

/// One line: a stack trace would break the layout, and its first line is all that fits.
fn flatten(text: &str, max: usize) -> String {
    let flat = text.split(['\r', '\n']).collect::<Vec<_>>().join(" ");
    let flat = flat.trim();
    if flat.chars().count() <= max {
        flat.to_owned()
    } else {
        let cut: String = flat.chars().take(max).collect();
        format!("{cut}…")
    }
}
