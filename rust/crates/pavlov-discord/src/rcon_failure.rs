//! Why an RCON command did not land, per server (C# `Commands/RconFailure.cs`): the reply a
//! command gives when every server refused. "No server answered" was useless without saying
//! what each one said.

use pavlov_core::embed::Embed;
use pavlov_core::skin::theme;
use pavlov_core::text::Sanitize;
use pavlov_host::registry::RconRegistry;

/// The failure embed. `attempts` is each server tried with its send error, if any; an empty
/// list is "no servers configured", a different fault nobody diagnoses from "servers refused".
#[must_use]
pub fn explain(
    title: &str,
    body: &str,
    rcon: &RconRegistry,
    attempts: &[(String, Option<String>)],
) -> Embed {
    let embed = theme::failure(title, Some(body));
    if attempts.is_empty() {
        return embed.field(
            "Why",
            "No RCON servers configured. Set `RCON_HOST_1`, `RCON_PORT_1` and `RCON_PASSWORD_1`.",
            false,
        );
    }
    let lines: Vec<String> = attempts
        .iter()
        .map(|(server, problem)| {
            // The send error first: it is what just happened. Then the last health probe,
            // which words a down server as a connection rather than a timeout.
            let reason = problem
                .clone()
                .or_else(|| rcon.last_error(server))
                .unwrap_or_else(|| "refused the command without an error".to_owned());
            format!(
                "{} **{}** — {}",
                theme::DOT,
                Sanitize::markdown(Some(server)),
                Sanitize::message(Some(&reason))
            )
        })
        .collect();
    embed.field("Why", lines.join("\n"), false).field(
        "Usually",
        "**Connection refused** — server down, or wrong RCON port.\n\
         **Authentication failed** — `RCON_PASSWORD_n` does not match `RconPassword`.\n\
         **Timed out** — server up but not answering; check `/serverinfo`.",
        false,
    )
}
