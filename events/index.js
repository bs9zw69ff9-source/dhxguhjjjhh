/* ---------------- events: clientReady handler + ipBans join/leave/kill/auto-ban callbacks ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./events")(ctx). */
module.exports = function(ctx) {
  const {
  ACTIVE_SERVERS, ActivityType, BOT_VERSION, CLIN, EmbedBuilder, IPHUB_API_KEY,
  PAVLOV_BASES, REST, Routes, UFW_BLOCK, _sameId, addAutobanExempt,
  autoBanDecision, banWithIp, checkVpn, checkVpnAndAlert, client,
  clinical, commands, enforceBansSweep, ensureFactionFiles, ensureMenuPanel, feedHook,
  fixAutoBanReasons, formatFullLocation, grantMasterMenu, hardEnforce,
  healTreeOwnership, hero, importBlacklistToBans, importModsaveBanlist, ipBans,
  isAutobanExempt, isMasterName, loadBans, log, logBan, logger,
  mainCommands, path, postFeed, postKillFeed, postUpdateLogIfChanged, randomQuote,
  rconHealthCheck, reconcileBans, reconcileBlacklists, refreshLeaderboardChannels, refreshPlayerCache, removeBans,
  scheduleMenuRegrant, seedKnownPlayers, sourceBanFor, syncAllModSave, syncModsaveBanlist, syncPlayerLedger,
  unbanEverywhere, upsertPermBan, writeModLog,
  MASTER_NAMES,
  } = ctx;

// ---- ready ----
client.once("clientReady", async () => {   // "ready" is deprecated in discord.js 14.22+
  logger.info("Bot", `${client.user.tag} online - v${BOT_VERSION}`);
  try {
    client.user.setPresence({
      activities: [{ name: "over the server  -  /help", type: ActivityType.Watching }],
      status: "online",
    });
  } catch (err) {
    logger.warn("Bot", `Could not set presence: ${err.message}`);
  }
  const rest = new REST({ version: "10" }).setToken(process.env.DISCORD_TOKEN);
  try {
    const result = await rest.put(Routes.applicationCommands(process.env.CLIENT_ID), { body: mainCommands });
    logger.info("Bot", `${result.length} slash commands registered`);
  } catch (err) {
    logger.error("Bot", `Command registration failed: ${err.message}`);
  }
  seedKnownPlayers();   // backfill the offline-autocomplete registry from existing data
  postUpdateLogIfChanged().catch(err => logger.warn("UpdateLog", err.message));   // announce this deploy, if it's a new one
  // Re-apply ufw blocks for every currently-active ban's IP at position 1 (self-heals any
  // previously-appended rules). Delayed so it doesn't slow boot; off unless UFW_BLOCK.
  // (ufw is manual-only via /firewall - no automatic resync of ban IPs on startup)
  // Watch EVERY install's Pavlov.log (server 1, 2, ...) - derived from the discovered
  // installs, unioned with any explicit PAVLOV_LOGS, so server 2 is never missed.
  const ipLogFiles = [...new Set([
    ...String(process.env.PAVLOV_LOGS ?? "").split(/[,:]/).map(s => s.trim()).filter(Boolean),
    ...PAVLOV_BASES.map(b => path.join(b, "Pavlov/Saved/Logs/Pavlov.log")),
  ])];
  logger.info("IPBans", `Watching ${ipLogFiles.length} server log(s): ${ipLogFiles.join(", ")}`);
  // Map each watched log's label (the install dir name ipBans tags lines with) to a
  // friendly "Server N" by INSTALL ORDER - robust regardless of folder naming, so
  // server 2's feed isn't mislabeled as server 1.
  const labelOfLog = (f) => { const m = String(f).match(/([^/\\]+)[/\\]Pavlov[/\\]/i); return m ? m[1] : path.basename(path.dirname(f)); };
  const serverNameByLabel = new Map();
  PAVLOV_BASES.forEach((b, i) => serverNameByLabel.set(path.basename(b), `Server ${i + 1}`));   // install order = Server 1, 2, ...
  let _extra = PAVLOV_BASES.length;
  ipLogFiles.forEach((f) => { const l = labelOfLog(f); if (!serverNameByLabel.has(l)) serverNameByLabel.set(l, `Server ${++_extra}`); });
  logger.info("IPBans", `Server labels: ${[...serverNameByLabel].map(([l, n]) => `${l}=${n}`).join(", ")}`);
  ipBans.init({
    logFiles: ipLogFiles,
    masters: [...MASTER_NAMES],   // never IP-logged / fed / auto-banned
    // Fired on every LIVE join - re-grant recorded RCON menus (the server drops a
    // player's menu on disconnect, so a rejoin needs the grant re-applied).
    onConnect: async ({ name, ip, confident }) => {
      // Pull the newest caps ledger onto every install BEFORE this server reads it,
      // so a hop from another server carries their money over.
      try { syncPlayerLedger(name); } catch (e) { logger.warn("Sync", `join ledger sync failed for ${name}: ${e.message}`); }
      // Re-apply an active gag on join, or lift an expired one - for everyone.
      // Master names get a menu handed to them on every join (no bit code).
      if (isMasterName(name)) { try { grantMasterMenu(name); } catch (e) { logger.warn("Menus", `master menu failed: ${e.message}`); } return; }
      try { scheduleMenuRegrant(name); } catch (e) { logger.warn("Menus", `re-grant schedule failed: ${e.message}`); }
      // VPN check at JOIN so a VPN/proxy user is banned AND kicked while still online —
      // not just blocked on their next rejoin. Only act on a `confident` (unambiguous)
      // join IP so a mis-correlated IP can't kick the wrong player; checkVpnAndAlert
      // caches per IP and re-checks nothing. The disconnect (onConfirm) check remains a
      // backstop for ambiguous joins. Masters already returned above.
      if (ip && confident && IPHUB_API_KEY && !isAutobanExempt(name)) {
        checkVpnAndAlert(name, ip).catch(err => logger.warn("VPN", `join check failed for ${name}: ${err.message}`));
      }
    },
    // Fired once a player's IP is CONFIRMED (the same-line disconnect pairing) -
    // posts an accurate name, ID, IP entry to the connection-feed webhook.
    onConfirm: async ({ name, ip, server, record }) => {
      // The economy mod saves the balance at disconnect - propagate it to the other
      // installs now (and once more a few seconds later for a slow save write), so
      // the caps are already there if they hop to another server.
      try { syncPlayerLedger(name); } catch {}
      setTimeout(() => { try { syncPlayerLedger(name); } catch {} }, 5_000);
      let vpnResult = null;
      try { vpnResult = await checkVpnAndAlert(name, ip); }   // runs the check + auto-ban; returns the verdict for the feed
      catch (err) { logger.warn("VPN", `check failed for ${name}: ${err.message}`); }
      // Even with the VPN keys unset, still fetch geolocation for the feed.
      if (!vpnResult && ip) { try { vpnResult = await checkVpn(ip); } catch {} }
      if (!feedHook) return;   // IP connection feed only runs when CONNECT_WEBHOOK_URL is set
      const srvName = serverNameByLabel.get(String(server)) || "Server 1";
      // everything Pavlov.log knows about this player (resolved by id inside ipBans)
      const rec = record || { ips: [], cips: [], alts: [], firstSeen: null, lastSeen: null, recent: [], logins: 0, bypass: false, flagged: false };
      const fmt = (ms) => { if (!ms) return "unknown"; try { return new Date(ms).toISOString().replace("T", " ").slice(0, 19); } catch { return "unknown"; } };

      // recent connections - newest first; fold in this live join, dedupe near-duplicates
      const conns = [];
      if (ip) conns.push({ ts: Date.now(), ip });
      for (const c of (rec.recent || [])) conns.push(c);
      const seen = new Set(); const connLines = [];
      for (const c of conns) {
        const k = `${Math.floor((c.ts || 0) / 1000)}|${c.ip}`;
        if (seen.has(k)) continue; seen.add(k);
        connLines.push(`${fmt(c.ts)}   ${c.ip ?? "unknown"}`);
        if (connLines.length >= 8) break;
      }
      const lastActivity = Math.max(rec.lastSeen || 0, conns[0]?.ts || 0) || null;

      // VPN/proxy verdict for the IP they connected from (from the check above).
      const vpnField = !IPHUB_API_KEY               ? "Not configured (set IPHUB_API_KEY)"
        : !vpnResult                                ? "Not checked"
        : !vpnResult.flagged                        ? "Clean"
        : vpnResult.confirmed === false             ? "IPHub flagged - IPQS disputes (likely false positive)"
        : vpnResult.confirmed === true              ? "VPN/Proxy - IPHub + IPQS agree"
        :                                             "Flagged by IPHub";
      const vpnIsp    = vpnResult?.isp  ? ` - ${vpnResult.isp}` : "";
      const vpnDetail = vpnResult?.ipqs ? ` - vpn:${vpnResult.ipqs.vpn} proxy:${vpnResult.ipqs.proxy} tor:${vpnResult.ipqs.tor} fraud:${vpnResult.ipqs.fraudScore}` : "";

      const embed = clinical(new EmbedBuilder().setColor(CLIN.green)
        .setTitle(`Player Information: ${name}`)
        .setDescription(`${name} just connected on ${srvName}.`)
        .addFields(
          { name: "EOS ID",          value: rec.id ? `\`${rec.id}\`` : "unknown", inline: false },
          { name: "First Seen",      value: fmt(rec.firstSeen),                  inline: true },
          { name: "Last Seen",       value: fmt(rec.lastSeen),                   inline: true },
          { name: "Login Count",     value: String(rec.logins ?? 0),             inline: true },
          { name: "Possible Alts",   value: (rec.alts && rec.alts.length ? rec.alts.join(", ") : "None").slice(0, 1024), inline: false },
          { name: "Bypass Auto-Ban", value: rec.bypass ? "Yes" : "No",           inline: true },
          { name: "Server",          value: srvName,                             inline: true },
          { name: "Log Scan Results", value: rec.flagged ? "Flagged - matches the blacklist (auto-banned)"
            : "No matches", inline: false },
          { name: "VPN / Proxy",     value: (vpnField + vpnIsp + vpnDetail).slice(0, 1024), inline: false },
          { name: "Location",        value: (formatFullLocation(vpnResult?.geo) || (ip ? "unknown" : "no IP")).slice(0, 1024), inline: false },
          { name: "Last Activity",   value: fmt(lastActivity),                   inline: false },
          { name: "Recent Connections", value: "```\n" + (connLines.length ? connLines.join("\n") : "no records").slice(0, 1000) + "\n```", inline: false },
        ), "Connection log - the bot");
      postFeed(embed);
    },
    // Fired on every live PvP kill (ipBans already filters out suicides/environmental
    // deaths - killer is always distinct from and present alongside the victim).
    onKill: async ({ killer, killed }) => { postKillFeed(killer, killed); },
    // Fired when someone CONNECTS (live log) matching a blacklisted username/IP:
    // ban that username on both servers (Shack bans by name, not hex id).
    onAutoBan: async ({ name, uniqueId, ip, reason }) => {
      // Only MASTER names are exempt from auto-ban. Staff/donators are exempt from /flush
      // only, not from ban evasion enforcement.
      if (isMasterName(name)) { logger.info("IPGuard", `Skipped auto-ban for master name ${name}`); return; }
      // An explicitly-unbanned player is never auto-banned again (a lingering shared flag
      // must not re-catch them) until they're deliberately re-banned.
      if (isAutobanExempt(name)) { logger.info("IPGuard", `Skipped auto-ban for unban-exempt player ${name}`); return; }
      // A TEMP-banned player bouncing off their ban still shows up as a join attempt.
      const existing = loadBans().find(b => _sameId(b.playerId, name));
      const decision = autoBanDecision(existing, reason);
      if (decision === "block") {
        // Still actively (temp-)banned - force them off (their ban already stands).
        try { await hardEnforce(name, { banToo: false }); } catch {}
        logger.info("IPGuard", `${name} tried to join while banned - re-removed, no escalation`);
        return;
      }
      if (decision === "lift") {
        try { unbanEverywhere(existing.playerId); } catch {}
        try { await removeBans(existing.playerId); } catch {}
        try { await addAutobanExempt(existing.playerId, "sentence served"); } catch {}   // served ban never re-catches them
        logger.info("IPGuard", `${name} rejoined after temp-ban expiry - lifted now (no escalation)`);
        return;
      }
      // Show the REAL offense in /checkban, not "IP-Guard": inherit the reason + mod from
      // the original ban whose flag caught them (fall back to "Ban evasion").
      const src       = sourceBanFor(name, uniqueId);
      const banReason = src?.reason || "Ban evasion";
      const banMod    = "Ban evasion (auto)";   // never attribute an auto-ban to a person
      // Enforce with a native RCON Ban + Kick by username (banWithIp) + flag the exact
      // IP/EOS. Per-command server responses are logged so we can see what lands.
      const res = await banWithIp(name, "both", { permanent: true, ip });
      try { await upsertPermBan({ playerId: name, reason: banReason, moderator: banMod }); } catch {}   // show in /banlist with the real punishment
      writeModLog({ action: "auto-ipban", playerId: name, reason: `${banReason} (evasion via ${reason || "match"}${ip ? ` ${ip}` : ""})`, by: banMod });
      logger.warn("IPGuard", `Auto-banned ${name} - ${banReason} (evasion via ${reason || "match"}${ip ? ` ${ip}` : ""}), id [${uniqueId || "?"}]`);
      const banEmbed = clinical(new EmbedBuilder().setColor(CLIN.red)
        .setTitle("Auto-Ban - Ban Evasion")
        .setDescription(`${hero(randomQuote("autoban"))}\n\n**${name}** was auto-banned for ${banReason} - caught by ${reason || "match"}${ip ? ` from \`${ip}\`` : ""}${uniqueId ? ` (id \`${uniqueId}\`)` : ""}. Banned and kicked on ${res?.blacklist?.servers ?? 0}/${ACTIVE_SERVERS.length} server(s).`),
        "Auto-ban - native RCON ban - all servers");
      await logBan(banEmbed);   // dedicated ban-log channel (falls back to mod-log)
      postFeed(banEmbed);       // also surface it in the connection feed
    },
  });
  for (const s of ACTIVE_SERVERS) refreshPlayerCache(s);
  try { healTreeOwnership(); } catch (e) { logger.warn("Init", `ownership heal failed: ${e.message}`); }
  try { const r = syncAllModSave(); if (r.installs > 1 && !r.off) logger.info("Sync", `ModSave sync on startup - ${r.synced} file(s) propagated across ${r.installs} installs`); } catch (e) { logger.warn("Sync", `ModSave sync failed: ${e.message}`); }
  try { ensureFactionFiles(); } catch (e) { logger.warn("Init", `faction file build failed: ${e.message}`); }
  try { reconcileBlacklists(); } catch (e) { logger.warn("Blacklist", `reconcile failed: ${e.message}`); }
  try { await importBlacklistToBans(); } catch (e) { logger.warn("Bans", `blacklist import failed: ${e.message}`); }
  try { await importModsaveBanlist(); } catch (e) { logger.warn("Bans", `modsave banlist import failed: ${e.message}`); }   // pull in-game-menu bans into the DB
  try { syncModsaveBanlist(); } catch {}   // then (re)build the custom ban-message file
  setTimeout(rconHealthCheck, 5_000);
  try { await fixAutoBanReasons(); } catch (e) { logger.warn("Bans", `auto-ban reason repair failed: ${e.message}`); }   // /checkban shows real punishment
  setTimeout(enforceBansSweep, 10_000);   // clear any banned players already online at startup
  setTimeout(reconcileBans,    15_000);   // rebuild the server ban list from the DB on startup
  ensureMenuPanel();
  // Wipe stale leaderboard/player-list messages from the previous run, then post fresh.
  try { await refreshLeaderboardChannels(); } catch (e) { logger.warn("Purge", `leaderboard refresh failed: ${e.message}`); }
});


  return {  };
};
