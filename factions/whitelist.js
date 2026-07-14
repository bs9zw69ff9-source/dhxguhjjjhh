/* ---------------- factions/whitelist: snapshot save/load of faction rosters ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./factions/whitelist")(ctx). */
module.exports = function(ctx) {
  const {
  FACTION_ROLES_PATH, FILES, SPAWN_FILE_MAP, backupFactionFile, fs, getFactionRankConfig,
  path, readFactionFile, safeRead, safeWrite, spawn, update,
  writeFactionFile,
  } = ctx;

/* ---------------- faction whitelist snapshot (save/load) ---------------- */
// Snapshot every faction file (spawn + rank rosters) to a JSON we control, so the
// owner can restore the whole whitelist set after accidents/wipes.
function saveFactionBackup() {
  let files;
  try { files = fs.readdirSync(FACTION_ROLES_PATH).filter(f => f.endsWith(".txt")); }
  catch (e) { return { ok: false, error: e.code || e.message }; }
  const data = { savedAt: Date.now(), files: {} };
  for (const f of files) { const lines = readFactionFile(f); if (lines) data.files[f] = lines; }
  if (!safeWrite(FILES.FACTION_BACKUP, data)) return { ok: false, error: "could not write backup" };
  return { ok: true, count: Object.keys(data.files).length };
}
// Restore the last snapshot, writing every saved file back (mirrored to all installs).
function loadFactionBackup() {
  const data = safeRead(FILES.FACTION_BACKUP, null);
  if (!data || !data.files || !Object.keys(data.files).length) return { ok: false, empty: true };
  // Snapshot the live rosters before overwriting, so a mistaken restore can be undone by hand.
  for (const f of Object.keys(data.files)) {
    try { backupFactionFile(f, fs.readFileSync(path.join(FACTION_ROLES_PATH, f), "utf8")); } catch {}
  }
  let restored = 0;
  // allowBulk: a restore legitimately rewrites whole rosters (may remove many entries).
  for (const [f, lines] of Object.entries(data.files)) { if (writeFactionFile(f, lines, { allowBulk: true })) restored++; }
  return { ok: true, restored, savedAt: data.savedAt };
}

/* Reset one faction's whitelist: empties the spawn (membership) roster and every
   rank file, and drops its faction_ranks.json rank overrides. Existing content is
   snapshotted first (same as loadFactionBackup) so a bad wipe is recoverable by hand.
   Returns { ok, count } where count is how many members were on the roster. */
async function wipeFaction(faction) {
  const spawn = SPAWN_FILE_MAP[faction];
  if (!spawn) return { ok: false, error: "unknown faction" };
  const before = readFactionFile(spawn);
  if (before === null) return { ok: false, error: "file unreadable" };
  const cfg   = getFactionRankConfig(faction);
  const files = new Set([spawn]);
  if (cfg) for (const f of Object.values(cfg.rankFiles)) if (f) files.add(f);
  for (const f of files) {
    try { backupFactionFile(f, fs.readFileSync(path.join(FACTION_ROLES_PATH, f), "utf8")); } catch {}
    writeFactionFile(f, [], { allowBulk: true });
  }
  await update(FILES.FACTION_RANKS, {}, (ranks) => { delete ranks[faction]; return ranks; });
  return { ok: true, count: before.length };
}

function memberHasRoleId(member, roleId) {
  if (!roleId || !member) return false;
  const r = member.roles;
  if (r?.cache && typeof r.cache.has === "function") return r.cache.has(roleId);   // GuildMember
  if (Array.isArray(r)) return r.includes(roleId);                                 // raw interaction member
  return false;
}






  return { loadFactionBackup, memberHasRoleId, saveFactionBackup, wipeFaction };
};
