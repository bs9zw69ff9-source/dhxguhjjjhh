/* ---------------- moderation/menulink: one Discord account <-> one in-game name ----------------
   Pure decision logic for the self-service RCON menu panel, kept out of index.js so the
   rule can actually be tested. No I/O, no ctx - callers pass the state in.

   The rule exists to stop menu access being cycled onto alt accounts. A member binds to
   exactly one in-game name the first time they claim a menu, and that binding OUTLIVES
   the menu itself: releasing the menu frees the access, never the name. Only an admin
   can break a binding (/unlinkname), for a genuine Pavlov name change. */

const norm = (s) => String(s ?? "").trim().toLowerCase();

/* Decide what a claim attempt should do.

     boundName       the name this Discord account is already bound to (null if none)
     requestedName   the name they just typed
     ownerId         Discord id already bound to requestedName (null if unclaimed)
     selfId          the claiming member's Discord id
     holdsMenu       true when they currently hold a granted menu

   Returns one of:
     { action: "locked",  boundName }   bound to a different name - refuse
     { action: "taken",   ownerId }     that name belongs to someone else - refuse
     { action: "release" }              re-entered their own name while holding a menu - strip it
     { action: "grant" }                proceed with the grant */
function menuClaimDecision({ boundName, requestedName, ownerId, selfId, holdsMenu }) {
  const want = norm(requestedName);
  const bound = norm(boundName);
  // A member bound to another name can do nothing here, menu or not. Checked FIRST:
  // it must hold even when they currently have no menu, which is precisely the state
  // someone would engineer (release, then re-claim as an alt) to slip past it.
  if (bound && want !== bound) return { action: "locked", boundName };
  // Reverse direction - two Discord accounts must not converge on one in-game name.
  if (ownerId && ownerId !== selfId) return { action: "taken", ownerId };
  if (holdsMenu && bound && want === bound) return { action: "release" };
  return { action: "grant" };
}

module.exports = { menuClaimDecision, norm };
