//! Which Discord roles map to which RCON menu tier (`/setrconroles`).
//!
//! In the C# this record lives in `Host/Discord/Commands/ConfigCommands.cs`. It is pure, so the
//! port puts it in core; the stored shape (`menu_roles`: `highStaff`, `staff`, `blacklist`, each
//! a role id or `null`) is unchanged.

use serde::{Deserialize, Serialize};

/// A menu tier a member can qualify for. Stored and compared as the strings the C# used.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MenuTier {
    HighStaff,
    Staff,
}

impl MenuTier {
    /// `highstaff` or `staff`, as the menu grants are keyed.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::HighStaff => "highstaff",
            Self::Staff => "staff",
        }
    }
}

/// The role mapping. Every field optional: an unmapped tier is simply not grantable.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MenuRoleMap {
    /// Discord role granted the high-staff menu tier.
    pub high_staff: Option<u64>,
    /// Discord role granted the ordinary staff menu tier.
    pub staff: Option<u64>,
    /// Discord role that revokes menu access outright.
    pub blacklist: Option<u64>,
}

impl MenuRoleMap {
    /// Nothing mapped.
    pub const EMPTY: Self = Self {
        high_staff: None,
        staff: None,
        blacklist: None,
    };

    /// This map over the environment's, TIER BY TIER: what `/setrconroles` stored wins, and
    /// the environment still applies to tiers the command never set.
    #[must_use]
    pub fn over(
        self,
        environment_high_staff: Option<u64>,
        environment_staff: Option<u64>,
        environment_blacklist: Option<u64>,
    ) -> Self {
        Self {
            high_staff: self.high_staff.or(environment_high_staff),
            staff: self.staff.or(environment_staff),
            blacklist: self.blacklist.or(environment_blacklist),
        }
    }

    /// Whether any tier is mapped at all. False means nobody can qualify.
    #[must_use]
    pub fn any(self) -> bool {
        self.high_staff.is_some() || self.staff.is_some()
    }

    /// The tier a member qualifies for, HIGHEST FIRST, or `None`.
    #[must_use]
    pub fn tier_for(self, role_ids: &[u64]) -> Option<MenuTier> {
        if self.high_staff.is_some_and(|high| role_ids.contains(&high)) {
            return Some(MenuTier::HighStaff);
        }
        if self.staff.is_some_and(|staff| role_ids.contains(&staff)) {
            return Some(MenuTier::Staff);
        }
        None
    }
}
