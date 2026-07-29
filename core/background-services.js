/* ---------------- core/background-services: the bot's recurring work, as services ----------------
   Every one of these used to be a bare setInterval in index.js. Registering them here
   instead buys four things, without changing what any of them DO or how often:

     - a throwing tick is caught, counted and reported instead of becoming an unhandled
       rejection that can take the process down
     - a slow tick is never re-entered, so a stalled RCON sweep cannot stack up copies
       of itself until the event loop drowns
     - each one can be stopped, restarted and inspected (/health, /metrics)
     - shutdown stops them deterministically rather than leaving them mid-flight

   Intervals and behaviour are preserved exactly as they were. `runOnStart` is only set
   where the original code had a matching setTimeout kick-off.

   Everything arrives through `deps` - this module reaches for no globals, so it can be
   tested with fakes and holds no import of index.js. */
"use strict";

const MINUTE = 60_000;

/**
 * @param {object} sm    a ServiceManager
 * @param {object} deps  the bot's already-built functions, injected
 */
function registerBackgroundServices(sm, deps) {
  const {
    // ban lifecycle
    processExpiredBans, enforceBansSweep, reconcileBans,
    // police / rank timers
    sentenceSweep, rankSuspensionSweep, processDonatorRestores,
    // discord surfaces
    postLeaderboard, postArrestBoard, postPlayerList, postDashboard,
    // rcon + cache
    rconHealthCheck, refreshPlayerCache, tickPlaytime,
    // persistence / sync
    exportDbToJson, autoBackupFactions, importModsaveBanlist, syncModsaveBanlist, syncAllModSave,
    // config
    ACTIVE_SERVERS, DB_EXPORT_INTERVAL_MS, LEADERBOARD_INTERVAL_MS, PLAYERLIST_INTERVAL_MS,
    DASHBOARD_INTERVAL_MS, RCON_HEALTH_INTERVAL_MS, MODSAVE_SYNC_INTERVAL_MS,
    ARREST_LEADERBOARD_CHANNEL, DASHBOARD_CHANNEL,
    logger,
  } = deps;

  const add = (name, intervalMs, tick, opts = {}) => {
    if (!tick) { logger?.debug?.("Services", `${name} not registered - no implementation supplied`); return; }
    sm.register({ name, intervalMs, tick, ...opts });
  };

  // ---- bans ----
  add("ban-expiry", MINUTE, () => processExpiredBans());
  add("ban-sweep", 30_000, () => enforceBansSweep());
  add("ban-reconcile", 5 * MINUTE, () => reconcileBans());

  // ---- police / ranks / perks ----
  add("sentence-sweep", 30_000, () => sentenceSweep());
  add("rank-suspension-sweep", 30_000, () => rankSuspensionSweep());
  add("donator-restore", MINUTE, () => processDonatorRestores());

  // ---- discord surfaces ----
  /* NO runOnStart on the boards. Services start BEFORE client.login(), so an immediate
     tick fires with no gateway connection, and it also re-fires on every supervisor
     restart. The real first paint comes from refreshLeaderboardChannels() on ready,
     backed by the delayed one-shot in startIntervals - which is what the original code
     did and what keeps the post ordered after the startup purge. */
  add("leaderboard", LEADERBOARD_INTERVAL_MS, () => postLeaderboard());
  if (ARREST_LEADERBOARD_CHANNEL) add("arrest-board", LEADERBOARD_INTERVAL_MS, () => postArrestBoard());
  add("player-list", PLAYERLIST_INTERVAL_MS, () => postPlayerList());
  if (DASHBOARD_CHANNEL) add("dashboard", DASHBOARD_INTERVAL_MS, () => postDashboard());

  // ---- rcon ----
  add("rcon-health", RCON_HEALTH_INTERVAL_MS, () => rconHealthCheck());
  add("player-cache", MINUTE, async () => {
    for (const s of ACTIVE_SERVERS) await refreshPlayerCache(s);
    tickPlaytime();
  });

  // ---- persistence ----
  if (DB_EXPORT_INTERVAL_MS > 0) add("db-export", DB_EXPORT_INTERVAL_MS, () => exportDbToJson());
  add("faction-backup", 24 * 60 * MINUTE, () => autoBackupFactions());
  add("banlist-sync", 5 * MINUTE, async () => {
    // Pull in-game-menu bans first, THEN rebuild the file - order matters, an import
    // after the write would be a whole cycle late.
    try { await importModsaveBanlist(); } catch (e) { logger?.warn?.("Bans", `modsave import failed: ${e.message}`); }
    syncModsaveBanlist();
  });
  add("modsave-sync", MODSAVE_SYNC_INTERVAL_MS, async () => syncAllModSave());

  return sm;
}

module.exports = { registerBackgroundServices };
