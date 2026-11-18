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
            "The roster files are unreachable",
            Some(
                "Nothing was changed — the bot cannot reach the roster directory.\n\n\
                 Check `FACTION_ROLES_PATH` on the process running this command. It has to point at \
                 the folder holding the faction `.txt` files, and that folder has to exist already.",
            ),
        ),
        MembershipOutcome::WriteFailed => theme::failure(
            "The roster could not be written",
            Some(
                "Nothing was changed. The files read fine but the write failed — permissions, a full \
                 disk, or the guard blocking a change that would have emptied the roster. The log has the reason.",
            ),
        ),
        MembershipOutcome::UnknownFaction => theme::failure(
            "No such faction",
            Some(
                "That faction is not in the registry. Anyone recorded against it needs re-adding to a current one.",
            ),
        ),
        other => theme::failure(
            "Refused",
            Some(&format!("The change was refused: `{other:?}`.")),
        ),
    }
}
