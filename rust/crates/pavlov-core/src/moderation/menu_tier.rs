//! The in-game menu tiers (owner change, 2026-11-18, replacing RCON+ menu grants): five plain
//! whitelists in `ModSave/ServerRoles`, one in-game name per line, read live by the game.
//!
//! - A player holds AT MOST ONE tier: giving a tier moves them, never stacks them.
//! - WHO MAY GIVE WHAT: an owner any tier; an admin `srstaff` and below. The top two tiers
//!   (owner, manager) hand out the most power in game, so only the bot's owners touch them -
//!   granting, moving somebody out of them, or stripping them.

/// One tier, highest first in [`MenuTier::ALL`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum MenuTier {
    JrStaff,
    Staff,
    SrStaff,
    Manager,
    Owner,
}

impl MenuTier {
    /// Highest first: the order the files are listed and checked in.
    pub const ALL: [Self; 5] = [
        Self::Owner,
        Self::Manager,
        Self::SrStaff,
        Self::Staff,
        Self::JrStaff,
    ];

    /// The command choice value (and the file's stem).
    #[must_use]
    pub fn key(self) -> &'static str {
        match self {
            Self::Owner => "owner",
            Self::Manager => "manager",
            Self::SrStaff => "srstaff",
            Self::Staff => "staff",
            Self::JrStaff => "jrstaff",
        }
    }

    /// The file in `ServerRoles`.
    #[must_use]
    pub fn file(self) -> String {
        format!("{}.txt", self.key())
    }

    /// How it reads in a reply.
    #[must_use]
    pub fn label(self) -> &'static str {
        match self {
            Self::Owner => "Owner",
            Self::Manager => "Manager",
            Self::SrStaff => "Sr. Staff",
            Self::Staff => "Staff",
            Self::JrStaff => "Jr. Staff",
        }
    }

    /// The tier a command choice names, ignoring case and spacing.
    #[must_use]
    pub fn parse(choice: Option<&str>) -> Option<Self> {
        let choice = choice?.trim();
        Self::ALL
            .into_iter()
            .find(|t| t.key().eq_ignore_ascii_case(choice))
    }

    /// Whether only a bot owner may give, move or strip this tier.
    #[must_use]
    pub fn owner_only(self) -> bool {
        self >= Self::Manager
    }

    /// Whether a caller may give this tier: owners any, admins `srstaff` and below.
    #[must_use]
    pub fn may_grant(self, is_owner: bool, is_admin: bool) -> bool {
        is_owner || (is_admin && !self.owner_only())
    }
}

#[cfg(test)]
mod tests {
    use super::MenuTier;

    #[test]
    fn the_files_are_the_five_server_roles_lists_highest_first() {
        let files: Vec<String> = MenuTier::ALL.iter().map(|t| t.file()).collect();
        assert_eq!(
            files,
            [
                "owner.txt",
                "manager.txt",
                "srstaff.txt",
                "staff.txt",
                "jrstaff.txt"
            ]
        );
    }

    #[test]
    fn owners_give_any_tier_and_admins_srstaff_and_below() {
        for tier in MenuTier::ALL {
            assert!(tier.may_grant(true, false), "{tier:?}");
            assert!(
                !tier.may_grant(false, false),
                "{tier:?}: a mod gives nothing"
            );
        }
        assert!(!MenuTier::Owner.may_grant(false, true));
        assert!(!MenuTier::Manager.may_grant(false, true));
        assert!(MenuTier::SrStaff.may_grant(false, true));
        assert!(MenuTier::Staff.may_grant(false, true));
        assert!(MenuTier::JrStaff.may_grant(false, true));
    }

    #[test]
    fn a_choice_names_its_tier() {
        assert_eq!(MenuTier::parse(Some(" SrStaff ")), Some(MenuTier::SrStaff));
        assert_eq!(MenuTier::parse(Some("highstaff")), None);
        assert_eq!(MenuTier::parse(None), None);
    }
}
