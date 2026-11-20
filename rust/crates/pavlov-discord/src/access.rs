//! Who may do what (C# `Discord/Access.cs`): owners by id, everyone else by the roles in the
//! `roles` dataset.
//!
//! - OWNERS PASS EVERY GATE, checked first and by user id alone. Every other tier needs a
//!   guild member to read roles from, and when one cannot be resolved (a DM, an uncached guild,
//!   a user-installed app) the role checks all answer false. An owner refused a Mod command
//!   while the Owner commands worked was the bug.
//! - Discord's Administrator permission counts ONLY in the staff (home) guild. Counting it
//!   anywhere let anybody invite the bot to their own server and become its admin.
//! - A member with NO roles may be a hollow one (a user-installed app is handed the caller
//!   with an empty role set even where the bot is a member), so the staff guild's view of the
//!   same person is preferred when it has roles.
//! - A refusal says what the caller HOLDS and why, never who does hold the tier.

use std::collections::HashSet;
use std::fmt;
use std::sync::{Arc, PoisonError, RwLock};

use pavlov_core::data::SerializedStore;
use pavlov_core::factions::{FactionRegistry, FactionSet};
use pavlov_core::moderation::{StaffHierarchy, StaffTier};
use pavlov_core::roles::RoleMap;
use pavlov_core::security::{OwnerGuard, OwnerGuardError};
use pavlov_core::text::case::eq_ignore_case;
use pavlov_storage::datasets::ROLES;

use crate::caller::{Caller, MemberView};

/// The tier a command needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum RequiredAccess {
    Public,
    Police,
    FactionLeader,
    Mod,
    Admin,
    Owner,
}

impl fmt::Display for RequiredAccess {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::Public => "Public",
            Self::Police => "Police",
            Self::FactionLeader => "FactionLeader",
            Self::Mod => "Mod",
            Self::Admin => "Admin",
            Self::Owner => "Owner",
        })
    }
}

/// Whether a caller holds one configured role.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RoleStanding {
    /// No role is configured for this.
    Unset,
    /// No guild member could be read.
    NoMember,
    NotHeld,
    Held,
}

/// Looks a user up in the staff guild.
pub type HomeLookup = Arc<dyn Fn(u64) -> Option<MemberView> + Send + Sync>;
/// The staff guild's id, when it is known.
pub type HomeGuildId = Arc<dyn Fn() -> Option<u64> + Send + Sync>;

#[derive(Clone)]
struct Home {
    lookup: HomeLookup,
    guild_id: Option<HomeGuildId>,
}

/// The access rules.
pub struct Access {
    store: Arc<SerializedStore>,
    owners: HashSet<u64>,
    super_owners: HashSet<u64>,
    configured_owner_count: usize,
    factions: FactionSet,
    home: RwLock<Option<Home>>,
}

impl fmt::Debug for Access {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        // Never the owner ids: a debug line is not a place to publish who to target.
        f.debug_struct("Access")
            .field("owners", &self.owners.len())
            .field("super_owners", &self.super_owners.len())
            .finish_non_exhaustive()
    }
}

impl Access {
    /// The rules over `store`'s `roles`, with these owners and super owners. The built-in
    /// super owner is added and the RESULT asserted, so it cannot be filtered out downstream.
    ///
    /// # Errors
    /// When the built-in owner identity fails its check.
    pub fn new(
        store: Arc<SerializedStore>,
        owners: impl IntoIterator<Item = u64>,
        super_owners: impl IntoIterator<Item = u64>,
    ) -> Result<Self, OwnerGuardError> {
        let owners: HashSet<u64> = owners.into_iter().collect();
        let configured_super: HashSet<u64> = super_owners.into_iter().collect();
        let configured_owner_count = owners.len() + configured_super.len();
        Ok(Self {
            store,
            owners,
            super_owners: OwnerGuard::with_built_in_owners(configured_super)?,
            configured_owner_count,
            factions: FactionRegistry::default_set().clone(),
            home: RwLock::new(None),
        })
    }

    #[must_use]
    pub fn with_factions(mut self, factions: FactionSet) -> Self {
        self.factions = factions;
        self
    }

    /// Read roles from the staff guild for callers the interaction did not resolve (set once
    /// the gateway is up). `guild_id` says which guild's Administrator permission counts.
    pub fn use_home_guild(&self, lookup: HomeLookup, guild_id: Option<HomeGuildId>) {
        *self.home.write().unwrap_or_else(PoisonError::into_inner) =
            Some(Home { lookup, guild_id });
    }

    fn home(&self) -> Option<Home> {
        self.home
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    /// The configured roles. An unreadable dataset reads as none configured.
    #[must_use]
    pub fn roles(&self) -> RoleMap {
        self.store
            .read(ROLES, RoleMap::default())
            .unwrap_or_else(|error| {
                tracing::error!(%error, "cannot read the roles dataset - only owners have access");
                RoleMap::default()
            })
    }

    /// The member to read roles from: the interaction's, unless it carries no roles and the
    /// staff guild sees the same person with some.
    fn member(&self, caller: &Caller) -> Option<MemberView> {
        let home = self.home();
        match &caller.member {
            Some(member) if !member.role_ids.is_empty() => Some(member.clone()),
            Some(member) => home
                .and_then(|h| (h.lookup)(caller.id))
                .filter(|fuller| !fuller.role_ids.is_empty())
                .or_else(|| Some(member.clone())),
            None => home.and_then(|h| (h.lookup)(caller.id)),
        }
    }

    fn has(&self, caller: &Caller, role: Option<u64>) -> bool {
        role.is_some_and(|id| {
            self.member(caller)
                .is_some_and(|m| m.role_ids.contains(&id))
        })
    }

    #[must_use]
    pub fn standing_on(&self, caller: &Caller, role: Option<u64>) -> RoleStanding {
        let Some(id) = role else {
            return RoleStanding::Unset;
        };
        match self.member(caller) {
            None => RoleStanding::NoMember,
            Some(m) if m.role_ids.contains(&id) => RoleStanding::Held,
            Some(_) => RoleStanding::NotHeld,
        }
    }

    /// The roles the bot can see on the caller; `None` when no member could be read.
    #[must_use]
    pub fn visible_roles(&self, caller: &Caller) -> Option<Vec<u64>> {
        self.member(caller).map(|m| m.role_ids)
    }

    #[must_use]
    pub fn is_master_owner(&self, caller: &Caller) -> bool {
        OwnerGuard::is_master_owner(caller.id)
    }

    /// The compiled-in super owner short-circuits before the set is consulted, so emptying the
    /// set does not change the answer either.
    #[must_use]
    pub fn is_super_owner(&self, caller: &Caller) -> bool {
        self.is_master_owner(caller)
            || caller.id == OwnerGuard::SUPER_OWNER_ID
            || self.super_owners.contains(&caller.id)
    }

    #[must_use]
    pub fn is_owner(&self, caller: &Caller) -> bool {
        self.is_owner_id(caller.id)
    }

    #[must_use]
    pub fn is_owner_id(&self, user_id: u64) -> bool {
        OwnerGuard::is_master_owner(user_id)
            || user_id == OwnerGuard::SUPER_OWNER_ID
            || self.super_owners.contains(&user_id)
            || self.owners.contains(&user_id)
    }

    #[must_use]
    pub fn is_admin(&self, caller: &Caller) -> bool {
        self.is_owner(caller)
            || self.has(caller, self.roles().admin_role)
            || self.is_home_administrator(caller)
    }

    /// Administrator in the STAFF guild only, and only when that guild is known.
    fn is_home_administrator(&self, caller: &Caller) -> bool {
        let Some(member) = self.member(caller).filter(|m| m.administrator) else {
            return false;
        };
        self.home()
            .and_then(|h| h.guild_id)
            .and_then(|id| id())
            .is_some_and(|home| member.guild_id == home)
    }

    #[must_use]
    pub fn is_mod(&self, caller: &Caller) -> bool {
        self.is_admin(caller) || self.has(caller, self.roles().mod_role)
    }

    #[must_use]
    pub fn is_faction_leader(&self, caller: &Caller) -> bool {
        self.is_admin(caller) || self.has(caller, self.roles().faction_leader_role)
    }

    #[must_use]
    pub fn is_police(&self, caller: &Caller) -> bool {
        self.is_mod(caller) || self.has(caller, self.roles().police_role)
    }

    /// The factions whose rosters the caller may change: every one for a whitelist leader,
    /// otherwise those whose own role they hold.
    #[must_use]
    pub fn manageable_factions(&self, caller: &Caller) -> Vec<String> {
        if self.is_faction_leader(caller) {
            return self.factions.names().map(str::to_owned).collect();
        }
        let roles = self.roles();
        self.factions
            .names()
            .filter(|name| self.has(caller, roles.role_for(Some(name))))
            .map(str::to_owned)
            .collect()
    }

    #[must_use]
    pub fn can_manage(&self, caller: &Caller, faction: &str) -> bool {
        let name = self
            .factions
            .get(Some(faction))
            .map_or(faction, |f| f.name.as_str());
        self.manageable_factions(caller)
            .iter()
            .any(|f| eq_ignore_case(f, name))
    }

    #[must_use]
    pub fn tier_of(&self, caller: &Caller) -> StaffTier {
        StaffHierarchy::tier_of(
            self.is_super_owner(caller),
            self.is_owner(caller),
            self.is_admin(caller),
            self.is_mod(caller),
            self.is_master_owner(caller),
        )
    }

    #[must_use]
    pub fn describe_access(&self, caller: &Caller) -> &'static str {
        match self.tier_of(caller) {
            StaffTier::MasterOwner => "MASTER OWNER",
            StaffTier::SuperOwner => "SUPER OWNER",
            StaffTier::Owner => "OWNER",
            StaffTier::Admin => "ADMIN",
            StaffTier::Mod => "MODERATOR",
            StaffTier::None if self.is_faction_leader(caller) => "WHITELIST LEADER",
            StaffTier::None if self.is_police(caller) => "POLICE",
            StaffTier::None => "PUBLIC",
        }
    }

    /// Whether the caller may use something needing `required`. Owners first, by id.
    #[must_use]
    pub fn allows(&self, required: RequiredAccess, caller: &Caller) -> bool {
        if self.is_owner(caller) {
            return true;
        }
        match required {
            RequiredAccess::Public => true,
            RequiredAccess::Police => self.is_police(caller),
            RequiredAccess::FactionLeader => {
                self.is_faction_leader(caller) || !self.manageable_factions(caller).is_empty()
            }
            RequiredAccess::Mod => self.is_mod(caller),
            RequiredAccess::Admin => self.is_admin(caller),
            RequiredAccess::Owner => false,
        }
    }

    /// What to tell a refused caller: what they need, what they hold, and the likeliest reason.
    #[must_use]
    pub fn explain_refusal(&self, caller: &Caller, required: RequiredAccess) -> String {
        let mut lines = vec![
            refusal(required),
            format!("You hold: **{}**.", self.describe_access(caller)),
        ];
        let roles = self.roles();
        if self.member(caller).is_none() {
            // No member anywhere: distinct from lacking a role, and it used to look identical.
            lines.push(
                "Your roles could not be read here; only owner access was checked. Outside the \
                 server they come from the staff guild."
                    .into(),
            );
        } else if required == RequiredAccess::Mod && roles.mod_role.is_none() {
            lines.push(
                "No moderator role is configured. An owner can set one with `/setroles`.".into(),
            );
        } else if required == RequiredAccess::Admin && roles.admin_role.is_none() {
            lines
                .push("No admin role is configured. An owner can set one with `/setroles`.".into());
        } else if self.visible_roles(caller).is_some_and(|r| r.is_empty()) {
            // SEEN, BUT WITH NO ROLES: after the unconfigured checks, since "run /setroles" is
            // the fix whatever roles the caller has when nothing is mapped.
            lines.push(
                "No roles were read on you. Check the bot is a member of this server and the \
                 **Server Members** intent is on, or set `HOME_GUILD_ID` to the staff server."
                    .into(),
            );
        }
        if required == RequiredAccess::Owner && self.configured_owner_count == 0 {
            lines.push("No owners are configured. Set `OWNER_IDS` in `.env`.".into());
        }
        lines.join("\n")
    }
}

/// The one-line refusal.
#[must_use]
pub fn refusal(required: RequiredAccess) -> String {
    format!("Requires **{required}** access.")
}
