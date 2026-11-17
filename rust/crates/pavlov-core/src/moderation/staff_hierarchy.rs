//! Who may undo whose moderation actions (C# `Core/Moderation/StaffHierarchy.cs`).
//!
//! A lower tier can NEVER override (lift, undo, replace) an action issued by a higher tier.
//! Equal tiers can, or a mod going inactive would leave their bans permanent. A Master Owner
//! sits above the super owners, so a super owner added through `.env` still cannot undo what
//! the bot's owner decided.

use serde::{Deserialize, Deserializer, Serialize, Serializer};

/// Staff rank, lowest to highest. The numeric order IS the rule, and the numbers are what is
/// stored (`"tier": 3`), as the Node bot stored them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub enum StaffTier {
    /// Not staff, or automation.
    #[default]
    None = 0,
    Mod = 1,
    Admin = 2,
    Owner = 3,
    SuperOwner = 4,
    /// Above everyone: nobody else can lift a Master Owner's ban, replace it, or ban a player a
    /// Master Owner unbanned.
    MasterOwner = 5,
}

impl StaffTier {
    /// Every tier, lowest first.
    pub const ALL: [Self; 6] = [
        Self::None,
        Self::Mod,
        Self::Admin,
        Self::Owner,
        Self::SuperOwner,
        Self::MasterOwner,
    ];

    /// The stored number.
    #[must_use]
    pub fn as_number(self) -> i64 {
        self as i64
    }

    /// A stored number as a tier. Out-of-range values are CLAMPED, never an error: the C# read
    /// any integer into the enum and compared it numerically, so a record carrying 9 was
    /// above everyone; reading it as Master Owner keeps that, and an error would have failed
    /// the whole ban list.
    #[must_use]
    pub fn from_number(n: i64) -> Self {
        match n {
            i64::MIN..=0 => Self::None,
            1 => Self::Mod,
            2 => Self::Admin,
            3 => Self::Owner,
            4 => Self::SuperOwner,
            _ => Self::MasterOwner,
        }
    }
}

impl Serialize for StaffTier {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_i64(self.as_number())
    }
}

impl<'de> Deserialize<'de> for StaffTier {
    /// A number, or a number in quotes (the Node bot compared with `Number(tier)`, so a quoted
    /// tier worked there). Anything else reads as no tier, which is liftable by anyone - the
    /// same as a record that predates the hierarchy.
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let value = serde_json::Value::deserialize(deserializer)?;
        let n = match &value {
            serde_json::Value::Number(n) => n.as_i64().or_else(|| n.as_f64().map(|f| f as i64)),
            serde_json::Value::String(s) => s.trim().parse::<i64>().ok(),
            _ => None,
        };
        Ok(n.map_or(Self::None, Self::from_number))
    }
}

/// Namespace for the hierarchy rules, mirroring the C# static class.
#[derive(Debug)]
pub struct StaffHierarchy;

impl StaffHierarchy {
    /// The tier's display name. `None` reads as "Staff", as in the Node bot.
    #[must_use]
    pub fn name(tier: StaffTier) -> &'static str {
        match tier {
            StaffTier::MasterOwner => "Master Owner",
            StaffTier::SuperOwner => "Super Owner",
            StaffTier::Owner => "Owner",
            StaffTier::Admin => "Admin",
            StaffTier::Mod => "Mod",
            StaffTier::None => "Staff",
        }
    }

    /// Resolve a member to a tier, HIGHEST FIRST, so a super owner who also holds the mod role
    /// does not fall through to the lower answer.
    #[must_use]
    pub fn tier_of(
        is_super_owner: bool,
        is_owner: bool,
        is_admin: bool,
        is_mod: bool,
        is_master_owner: bool,
    ) -> StaffTier {
        if is_master_owner {
            StaffTier::MasterOwner
        } else if is_super_owner {
            StaffTier::SuperOwner
        } else if is_owner {
            StaffTier::Owner
        } else if is_admin {
            StaffTier::Admin
        } else if is_mod {
            StaffTier::Mod
        } else {
            StaffTier::None
        }
    }

    /// Whether `actor` may lift an action issued at `record`. A record with no tier (anything
    /// predating the hierarchy) counts as `None`, so anyone may lift it: locking historical
    /// records retroactively would strand every ban issued before the feature existed.
    #[must_use]
    pub fn can_override(actor: StaffTier, record: Option<StaffTier>) -> bool {
        actor >= record.unwrap_or(StaffTier::None)
    }
}
