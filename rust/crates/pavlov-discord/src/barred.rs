//! Refusing a barred user, in one place for every command, component and autocomplete (C#
//! `DiscordGateway.RefuseIfBarredAsync`). Before the deferral on purpose: the refusal must be
//! private whatever the command would have been, and a deferral has already chosen.

use pavlov_core::skin::theme;
use pavlov_host::metrics::{MetricLabels, MetricsRegistry};
use pavlov_host::moderation::CommandBlacklist;

use crate::interaction::{ComponentResponder, Reply, Responder, Suggester};

/// What is asking: an interaction to answer, or an autocomplete to offer nothing to.
#[derive(Clone, Copy)]
pub enum Asker<'a> {
    Interaction(&'a dyn Responder),
    /// A button, select or modal: a panel in a channel is clickable by anybody who can see
    /// it, so refusing only the slash commands would leave a barred user every control
    /// those commands ever posted.
    Component(&'a dyn ComponentResponder),
    Autocomplete(&'a dyn Suggester),
}

impl std::fmt::Debug for Asker<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(match self {
            Self::Interaction(_) => "Asker::Interaction",
            Self::Component(_) => "Asker::Component",
            Self::Autocomplete(_) => "Asker::Autocomplete",
        })
    }
}

/// The refusal a barred user sees.
#[must_use]
pub fn barred_reply() -> Reply {
    Reply::embed(
        theme::denied("Blacklisted", Some("You are blacklisted from this bot.")).brand(None),
    )
}

/// True when the user is barred and was refused (whether or not the refusal could be said:
/// failing to SAY it is no reason to run the command after all).
pub async fn refuse_if_barred(
    blacklist: &CommandBlacklist,
    user_id: u64,
    username: &str,
    asker: Asker<'_>,
    what: &str,
    metrics: &MetricsRegistry,
) -> bool {
    if !blacklist.is_barred(user_id) {
        return false;
    }
    match asker {
        // One of these per keystroke, so neither logged nor counted.
        Asker::Autocomplete(suggester) => {
            if let Err(error) = suggester.suggest(Vec::new()).await {
                tracing::debug!(%error, "could not answer a barred user's autocomplete");
            }
        }
        Asker::Interaction(_) | Asker::Component(_) => {
            tracing::info!(
                what,
                user = username,
                user_id,
                "refused: barred from bot commands"
            );
            metrics.increment(
                "interactions_barred_total",
                &MetricLabels::none(),
                1.0,
                Some("Interactions refused because the user is barred from bot commands"),
            );
            let said = match asker {
                Asker::Interaction(responder) => responder.respond(barred_reply(), true).await,
                Asker::Component(responder) => responder.respond(barred_reply(), true).await,
                Asker::Autocomplete(_) => Ok(()),
            };
            if let Err(error) = said {
                tracing::warn!(what, %error, "could not tell a barred user they were refused");
            }
        }
    }
    true
}
