/* ---------------- moderation/hierarchy: staff command tiers + override rules ----------------
   The staff hierarchy, highest to lowest:

     Super Owner (4)  →  Owner (3)  →  Admin (2)  →  Mod (1)  →  none (0)

   The rule: a lower tier can NEVER override a moderation action issued by a higher
   tier. "Override" = lift/undo an action (unban a ban, unmute a mute, or clear a ban
   the higher tier issued). Concretely:

     • Super Owner can override anything.
     • Owner cannot override a Super Owner's action.
     • Admin cannot override a Super Owner's or Owner's action.
     • Mod cannot override a Super Owner's, Owner's, or Admin's action.

   Equal tiers can override each other (the rule only protects strictly-higher tiers).
   This module is pure — role predicates are injected — so it unit-tests without any
   Discord or filesystem dependency. */

const TIER = { SUPER_OWNER: 4, OWNER: 3, ADMIN: 2, MOD: 1, NONE: 0 };
const TIER_NAMES = { 4: "Super Owner", 3: "Owner", 2: "Admin", 1: "Mod", 0: "Staff" };

/* Resolve a guild member/user to a numeric tier using the caller's role predicates.
   Checked highest-first so a super owner never falls through to a lower predicate. */
function commandTier(member, { isSuperOwner, isOwner, hasAdminRole, hasModRole }) {
  const id = member?.id ?? member?.user?.id;
  if (isSuperOwner(id)) return TIER.SUPER_OWNER;
  if (isOwner(id))      return TIER.OWNER;
  if (hasAdminRole(member)) return TIER.ADMIN;
  if (hasModRole(member))   return TIER.MOD;
  return TIER.NONE;
}

function commandTierName(tier) { return TIER_NAMES[tier] ?? "Staff"; }

/* True if a staff member at `actorTier` may override a record issued at `recordTier`.
   Records predating the hierarchy carry no tier — treated as 0, so anyone may lift
   them (no retroactive lock-outs). */
function canOverride(actorTier, recordTier) {
  return Number(actorTier) >= Number(recordTier || 0);
}

module.exports = { TIER, TIER_NAMES, commandTier, commandTierName, canOverride };
