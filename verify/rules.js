/* ---------------- verify/rules: pure verification conflict logic ----------------
   A Pavlov name is linked to a Discord user via their confirmed IP(s). Rules:
   - one Discord user per Pavlov name (and one name per Discord user)
   - no alts: a confirmed IP already tied to a DIFFERENT verified Discord user
     means this is an alt account and can't be verified.
   No I/O - just decides. `store` is { [discordId]: { name, ips: [...], at, by } }. */
function norm(s) { return String(s ?? "").trim().toLowerCase(); }

// Returns null when OK to verify, else { code, reason, otherId }.
function verificationConflict(store, discordId, name, ips = []) {
  const wantName = norm(name);
  const wantIps  = new Set((ips || []).map(norm).filter(Boolean));
  if (store[discordId]) {
    return { code: "self", reason: `You're already verified as \`${store[discordId].name}\`.`, otherId: discordId };
  }
  for (const [id, rec] of Object.entries(store)) {
    if (id === discordId) continue;
    if (wantName && norm(rec.name) === wantName) {
      return { code: "name", reason: `The name \`${name}\` is already verified to another member.`, otherId: id };
    }
    if ((rec.ips || []).some(ip => wantIps.has(norm(ip)))) {
      return { code: "alt", reason: `This looks like an alt account - a shared IP is already verified to another member.`, otherId: id };
    }
  }
  return null;
}

module.exports = { verificationConflict };
