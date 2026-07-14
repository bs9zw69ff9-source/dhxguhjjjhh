/* ---------------- factions/files: rank/spawn file read/write, membership index ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./factions/files")(ctx). */
module.exports = function(ctx) {
  const {
  FACTION_ROLES_PATH, ensureFile, fs, logger, path, writeGameFile,
  } = ctx;

// ---- faction file helpers ----
function readFactionFile(spawnFile) {
  const fp = path.join(FACTION_ROLES_PATH, spawnFile);
  try {
    return fs.readFileSync(fp, "utf8").split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  } catch (err) { if (err.code === "ENOENT") { ensureFile(fp, ""); return []; } return null; }
}

// A normal whitelist op (add / remove / rank / transfer) changes a roster by at most
// a couple of entries. If a write would DELETE more than this many existing entries it
const FACTION_BULK_DROP_LIMIT = 5;
const FACTION_BAK_DIR = "./faction_file_bak";   // bot-local pre-write snapshots (NOT in the game tree)

// Keep one rolling pre-write copy of a faction file so any bad write is recoverable by hand.
function backupFactionFile(spawnFile, content) {
  try {
    fs.mkdirSync(FACTION_BAK_DIR, { recursive: true });
    fs.writeFileSync(path.join(FACTION_BAK_DIR, spawnFile + ".bak"), content, "utf8");
  } catch { /* best-effort; never block the real write */ }
}

function writeFactionFile(spawnFile, lines, opts = {}) {
  const fp = path.join(FACTION_ROLES_PATH, spawnFile);
  if (!Array.isArray(lines)) {
    logger.error("Faction", `Refused write to ${spawnFile}: payload is not an array`);
    return false;
  }
  // ---- Destruction guard: confirm we are not silently wiping a populated roster. ----
  if (!opts.allowBulk) {
    let raw = null;
    try { raw = fs.readFileSync(fp, "utf8"); }
    catch (e) {
      if (e.code !== "ENOENT") {   // can't read current file to compare -> refuse rather than risk a wipe
        logger.error("Faction", `Refused write to ${spawnFile}: cannot read current contents (${e.code}). Aborting to protect the roster.`);
        return false;
      }
    }
    if (raw !== null) {
      const current = raw.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
      backupFactionFile(spawnFile, raw);   // snapshot BEFORE we change anything
      if (current.length) {
        const keep    = new Set(lines.map(l => String(l).toLowerCase()));
        const dropped = current.filter(l => !keep.has(l.toLowerCase())).length;
        if (dropped > FACTION_BULK_DROP_LIMIT) {
          logger.error("Faction", `REFUSED write to ${spawnFile}: would remove ${dropped} of ${current.length} entries (limit ${FACTION_BULK_DROP_LIMIT}). Suspected corruption — roster left untouched.`);
          return false;
        }
      }
    }
  }
  if (writeGameFile(fp, lines.join("\n") + "\n")) return true;   // mirrored to every install
  logger.error("Faction", `Write failed for ${spawnFile}`);
  return false;
}


  return { FACTION_BAK_DIR, FACTION_BULK_DROP_LIMIT, backupFactionFile, readFactionFile, writeFactionFile };
};
