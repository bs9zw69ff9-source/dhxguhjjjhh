/* ---------------- economy/moneyLog: balance-change watcher ----------------
   Pavlov's economy mod keeps each player's money in its own ModSave text file
   (`<PlayerName>.txt`, containing a bare integer). Nothing announces a change - the
   game just rewrites the file. This watches them and reports every movement:

     +400 to player1
     -345 to player2

   Design notes:

   SEEDS SILENTLY. The first scan records balances WITHOUT reporting them, otherwise
   every restart would announce one line per player as if everyone had just been paid.

   USES mtime. Re-reading every ledger on each tick is pointless disk work when almost
   none have changed. Files whose mtime is unchanged are skipped entirely, so the poll
   stays cheap with hundreds of players.

   COLLAPSES BULK CHANGES. A wipe or a mass payout would otherwise emit one line per
   player - hundreds of messages, rate-limited into uselessness, burying anything real.
   Past a threshold it reports a summary instead.

   The scan/diff/format helpers are pure and exported, so the interesting logic tests
   without a filesystem or a webhook. */
module.exports = function(ctx) {
  const {
  fs, path, logger, getModsavePath, MODSAVE_SYNC_SKIP, postMoneyLog,
  } = ctx;

  /* Beyond this many changes in one tick, report a count rather than a list. A real
     round produces a handful of movements; hundreds means a wipe or a bulk operation. */
  const BULK_CHANGE_LIMIT = 12;

  /** Is this directory entry one of the per-player ledger files? */
  function isLedgerFile(name, isFile) {
    if (!isFile) return false;                                  // FactionRoles, RCON Plus, ...
    const lower = String(name).toLowerCase();
    if (!lower.endsWith(".txt")) return false;
    if (lower === "banlist.txt") return false;                  // ban-message file, not a ledger
    if (MODSAVE_SYNC_SKIP && MODSAVE_SYNC_SKIP.test(name)) return false;   // menu-access files
    return true;
  }

  /**
   * Read every ledger into a Map of player -> { balance, mtimeMs }.
   * `previous` lets unchanged files be skipped: same mtime means same contents, so the
   * cached balance is reused rather than re-parsed.
   */
  function scanLedgers(previous = new Map()) {
    const base = getModsavePath();
    if (!base) return null;                                     // economy not configured
    let entries;
    try { entries = fs.readdirSync(base, { withFileTypes: true }); }
    catch (e) { logger.warn("MoneyLog", `cannot read ${base}: ${e.code || e.message}`); return null; }

    const out = new Map();
    for (const ent of entries) {
      if (!isLedgerFile(ent.name, ent.isFile())) continue;
      const player = ent.name.slice(0, -4);                     // strip ".txt"
      const fp = path.join(base, ent.name);

      let mtimeMs;
      try { mtimeMs = fs.statSync(fp).mtimeMs; }
      catch { continue; }                                       // vanished mid-scan - next tick will see it

      const cached = previous.get(player);
      if (cached && cached.mtimeMs === mtimeMs) { out.set(player, cached); continue; }

      let balance = NaN;
      try { balance = parseInt(fs.readFileSync(fp, "utf8").trim(), 10); }
      catch { /* handled below, same as an unparseable read */ }

      /* An unparseable ledger - a mid-write truncation, a corrupt file - must not be read
         as 0, which would report a huge fabricated DEBIT for a player whose money is fine.
         It must also not simply drop the player, because losing their last known balance
         makes the next valid read look like a brand-new account and reports the whole
         amount as a fabricated CREDIT. Carry the last good entry forward with its OLD
         mtime, so the next tick re-reads and the comparison stays anchored to real data. */
      if (!Number.isFinite(balance)) {
        if (cached) out.set(player, cached);
        continue;
      }

      out.set(player, { balance, mtimeMs });
    }
    return out;
  }

  /**
   * Compare two scans. Returns [{ player, before, after, delta }], biggest movement
   * first. A player appearing for the first time counts as a credit from zero; one
   * disappearing is NOT reported, because a deleted ledger is an admin action
   * (/wipe, a manual clear) rather than a player losing money.
   */
  function diffScans(before, after) {
    const changes = [];
    for (const [player, now] of after) {
      const was = before.get(player);
      const previous = was ? was.balance : 0;
      if (was && was.balance === now.balance) continue;
      if (!was && now.balance === 0) continue;                  // new empty ledger is not a movement
      changes.push({ player, before: previous, after: now.balance, delta: now.balance - previous });
    }
    changes.sort((a, b) => Math.abs(b.delta) - Math.abs(a.delta));
    return changes;
  }

  /** "+400 to player1" / "-345 to player2", one per line. */
  function formatChanges(changes) {
    return changes
      .map(c => `${c.delta > 0 ? "+" : "-"}${Math.abs(c.delta).toLocaleString("en-US")} to ${c.player}`)
      .join("\n");
  }

  // ---- the watcher ----
  let cache = new Map();
  let seeded = false;

  /** One pass. Returns the changes it reported, for tests and diagnostics. */
  function tick() {
    const scan = scanLedgers(cache);
    if (!scan) return [];

    if (!seeded) {
      cache = scan;
      seeded = true;
      logger.info("MoneyLog", `watching ${scan.size} player ledger(s) - baseline recorded, not reported`);
      return [];
    }

    const changes = diffScans(cache, scan);
    cache = scan;
    if (!changes.length) return [];

    if (changes.length > BULK_CHANGE_LIMIT) {
      /* A wipe or bulk payout. One line per player would be hundreds of messages, so
         report the shape of it and leave the detail to the logs. */
      const up = changes.filter(c => c.delta > 0).length;
      const net = changes.reduce((s, c) => s + c.delta, 0);
      logger.warn("MoneyLog", `${changes.length} balances changed at once - reporting a summary`);
      postMoneyLog(`${changes.length} balances changed at once (${up} up, ${changes.length - up} down, net ${net >= 0 ? "+" : "-"}${Math.abs(net).toLocaleString("en-US")})`);
      return changes;
    }

    postMoneyLog(formatChanges(changes));
    return changes;
  }

  return { tick, scanLedgers, diffScans, formatChanges, isLedgerFile, BULK_CHANGE_LIMIT,
           _state: () => ({ seeded, tracked: cache.size }) };
};
