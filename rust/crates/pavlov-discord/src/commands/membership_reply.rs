//! The reply for a membership outcome a command did not describe itself (C#
//! `MembershipReply.cs`): the roster commands share these.

use pavlov_core::embed::Embed;
use pavlov_core::factions::{MembershipDecision, MembershipOutcome};
use pavlov_core::skin::theme;

/// The fallback reply. Its last arm still names the outcome: one reaching it is one a command
/// forgot to describe, and the name is what makes that findable.
#[must_use]
pub fn fallback(decision: &MembershipDecision) -> Embed {
    match decision.outcome {
        MembershipOutcome::RosterUnavailable => theme::failure(
            "Roster unreachable",
            Some(
                "Nothing was changed. `FACTION_ROLES_PATH` must point at an existing roster folder.",
            ),
        ),
        MembershipOutcome::WriteFailed => theme::failure(
            "Roster not written",
            Some("Nothing was changed. Check permissions and disk space; the log has the reason."),
        ),
        MembershipOutcome::UnknownFaction => theme::failure(
            "Unknown faction",
            Some("Anyone recorded against it needs re-adding to a current faction."),
        ),
        other => theme::failure(
            "Refused",
            Some(&format!("The change was refused: `{other:?}`.")),
        ),
    }
}
