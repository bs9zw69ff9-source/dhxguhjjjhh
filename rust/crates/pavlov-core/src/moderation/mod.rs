//! Moderation rules that need no RCON, storage or Discord (`Core/Moderation`).

mod ban_record;
mod menu_link;
mod menu_tier;
mod staff_hierarchy;

pub use ban_record::{AutoBanAction, BanRecord, BanRules};
pub use menu_link::{MenuClaimAction, MenuClaimDecision, MenuLink};
pub use menu_tier::MenuTier;
pub use staff_hierarchy::{StaffHierarchy, StaffTier};
