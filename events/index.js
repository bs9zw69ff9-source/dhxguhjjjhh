/* ---------------- events: clientReady handler + ipBans join/leave/kill/auto-ban callbacks ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./events")(ctx). */
module.exports = function(ctx) {
  const {
  ACTIVE_SERVERS, ActivityType, BOT_VERSION, CLIN, EmbedBuilder, IPHUB_API_KEY, vpnDetectionEnabled,
  PAVLOV_BASES, REST, Routes, UFW_BLOCK, _sameId, addAutobanExempt,
  autoBanDecision, banWithIp, checkEvasion, checkVpn, checkVpnAndAlert, client,
  clinical, commands, enforceBansSweep, ensureFactionFiles, pruneObsoleteFactionFiles, ensureMenuPanel, ensureVerifyPanel, ensureUnverifiedSetup, feedHook,
  fixAutoBanReasons, formatFullLocation, grantMasterMenu, hardEnforce,
  easternStamp, healTreeOwnership, hero, importBlacklistToBans, importModsaveBanlist, ipBans,
  isAutobanExempt, isMasterName, loadBans, log, logBan, logger,
  mainCommands, path, postFeed, postUpdateLogIfChanged,
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

  /* Ban-evasion correlation, run on JOIN so a returning banned player is flagged while
     they are still in the server. The EOS id and username come straight off the login
     line and are always reliable; the join IP is a timing correlation, so it is only
     fed in when ipBans reports the pairing as `confident` - otherwise a mis-correlated
     address could accuse the wrong person. Deduped per account so a reconnect loop
     cannot spam moderators. Reports only; it never bans on its own. */
  const _evasionSeen = new Map();   // nameKey -> ts
  const EVASION_DEBOUNCE_MS = 10 * 60 * 1000;
  async function alertEvasion({ name, ip, server, confident }) {
    if (!name || isMasterName(name)) return;
    const key = String(name).toLowerCase();
    if (Date.now() - (_evasionSeen.get(key) ?? 0) < EVASION_DEBOUNCE_MS) return;
    let rec = null;
    try { rec = ipBans.getRecord(name); } catch {}
    const safeIp = confident ? (ip || null) : null;      // never correlate on a guessed IP
    let evasion = null;
    try { evasion = checkEvasion({ name, eosId: rec?.id ?? null, ip: safeIp }); }
    catch (e) { logger.warn("Evasion", `join check failed for ${name}: ${e.message}`); return; }
    if (!evasion?.evasion) return;
    _evasionSeen.set(key, Date.now());
    if (_evasionSeen.size > 500) { const cut = Date.now() - EVASION_DEBOUNCE_MS; for (const [k, t] of _evasionSeen) if (t < cut) _evasionSeen.delete(k); }

    const srvName = serverNameByLabel.get(String(server)) || "Server 1";
    const lines = evasion.matches.slice(0, 3).map(m =>
      `**${m.playerId}** (${m.permanent ? "permanent" : "temp"} - ${m.reason}, by ${m.moderator})  -  confidence **${m.score}**\n` +
      m.reasons.map(r => `• ${r.detail}`).join("\n"));
    for (const f of evasion.flags) lines.push(`• ${f.detail}`);
    const alert = clinical(new EmbedBuilder().setColor(evasion.certain ? CLIN.red : CLIN.grey)
      .setTitle(evasion.certain ? "Ban Evasion Detected" : "Possible Ban Evasion")
      .setDescription(`**${name}** just joined ${srvName} and matches ${evasion.matches.length || evasion.flags.length} banned record(s).`)
      .addFields(
        { name: "Player",  value: `\`${name}\``, inline: true },
        { name: "EOS ID",  value: rec?.id ? `\`${rec.id}\`` : "unknown", inline: true },
        { name: "IP",      value: safeIp ? `\`${safeIp}\`` : (ip ? `\`${ip}\` (unconfirmed - not matched on)` : "unknown"), inline: true },
        { name: "Matches", value: (lines.join("\n\n") || "*registry flag only*").slice(0, 1024), inline: false },
      ), evasion.certain ? "Ban evasion - review immediately" : "Circumstantial match - review before acting");
    logger.warn("Evasion", `${name} [${rec?.id ?? "?"}] @ ${safeIp ?? "unconfirmed"} matched ${evasion.matches.length} ban(s), ` +
      `score ${evasion.score}${evasion.certain ? " (CERTAIN)" : ""}: ` +
      evasion.matches.map(m => `${m.playerId}(${m.reasons.map(r => r.kind).join("+")})`).join(", "));
    await logBan(alert).catch(() => {});
    postFeed(alert);
  }

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
      // Evasion check on JOIN - flagged while they are still in the server.
      alertEvasion({ name, ip, server, confident }).catch(e => logger.warn("Evasion", e.message));
      // VPN check at JOIN so a VPN/proxy user is banned AND kicked while still online —
      // not just blocked on their next rejoin. Only act on a `confident` (unambiguous)
      // join IP so a mis-correlated IP can't kick the wrong player; checkVpnAndAlert
      // caches per IP and re-checks nothing. The disconnect (onConfirm) check remains a
      // backstop for ambiguous joins. Masters already returned above.
      if (ip && confident && vpnDetectionEnabled() && !isAutobanExempt(name)) {
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
      const fmt = (ms) => (ms ? easternStamp(ms) : "unknown");   // Eastern time, not UTC

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


      /* VPN/proxy verdict. Spells out the consensus explicitly: when every regular
         check agrees the IP is clean it says so (and no IPQS lookup was spent); when
         any of them flags it, the IP is escalated to IPQS for final confirmation and
         the outcome of that escalation is shown. */
      const vpnLines = (() => {
        if (!vpnDetectionEnabled()) return ["Not configured (set a detector API key)"];
        if (!vpnResult) return ["Not checked"];
        if (vpnResult.local) return ["Skipped - private/LAN address (nothing to check)"];
        const dets   = vpnResult.detectors ?? [];
        const t1     = dets.filter(d => d.tier === 1);
        const t2     = dets.filter(d => d.tier === 2);
        const hits   = vpnResult.screenHits ?? 0;
        const of     = vpnResult.screenAnswered ?? t1.length;
        const pip    = (d) => `${d.flagged ? "🔴" : "🟢"} ${d.name}`;
        const out    = [];

        /* Entries cached before the multi-detector upgrade have no per-detector data.
           Render them from the legacy fields instead of printing a bogus "0/0". */
        if (!dets.length) {
          const legacy = !vpnResult.flagged ? "**Clean**"
            : vpnResult.confirmed === true  ? "🚫 **VPN/Proxy confirmed**"
            : vpnResult.confirmed === false ? "⚠️ **Disputed** - flagged, but the cross-check cleared it (not banned)"
            :                                 "⚠️ **Flagged** - unconfirmed";
          const meta = [
            vpnResult.provider ? `provider: **${vpnResult.provider}**` : null,
            vpnResult.isp || null,
            vpnResult.ipqs ? `vpn:${vpnResult.ipqs.vpn} proxy:${vpnResult.ipqs.proxy} tor:${vpnResult.ipqs.tor} fraud:${vpnResult.ipqs.fraudScore}` : null,
          ].filter(Boolean);
          return [legacy, "*Cached before the multi-detector upgrade - no per-detector breakdown.*", ...(meta.length ? [meta.join("  -  ")] : [])];
        }

        if (!vpnResult.flagged) {
          // Every regular check answered clean - state it, and that nothing escalated.
          out.push(of ? `**Clean** - all ${of} regular check${of !== 1 ? "s" : ""} confirm no VPN` : "**Clean**");
          if (t1.length) out.push(t1.map(pip).join("  "));
          if (t2.length === 0 && of) out.push("*Not escalated to IPQS - no regular check flagged it.*");
        } else if (vpnResult.confirmed === true && t2.length) {
          out.push(`🚫 **VPN/Proxy CONFIRMED** - ${hits}/${of} regular check${of !== 1 ? "s" : ""} flagged it, IPQS confirmed (${vpnResult.confirmHits}/${vpnResult.confirmAnswered})`);
          if (t1.length) out.push(t1.map(pip).join("  "));
          out.push(`→ final: ${t2.map(pip).join("  ")}`);
        } else if (vpnResult.confirmed === false) {
          out.push(`⚠️ **Disputed** - ${hits}/${of} regular check${of !== 1 ? "s" : ""} flagged it, but IPQS cleared it (not banned)`);
          if (t1.length) out.push(t1.map(pip).join("  "));
          out.push(`→ final: ${t2.map(pip).join("  ")}`);
        } else {
          // Flagged by screening, but nothing could confirm (IPQS unset or unreachable).
          out.push(`⚠️ **Flagged by ${hits}/${of} regular check${of !== 1 ? "s" : ""}** - IPQS ${t2.length ? "gave no verdict" : "not configured"}, so unconfirmed`);
          if (t1.length) out.push(t1.map(pip).join("  "));
        }
        const meta = [
          vpnResult.provider ? `provider: **${vpnResult.provider}**` : null,
          vpnResult.isp || null,
          vpnResult.ipqs ? `vpn:${vpnResult.ipqs.vpn} proxy:${vpnResult.ipqs.proxy} tor:${vpnResult.ipqs.tor} fraud:${vpnResult.ipqs.fraudScore}` : null,
        ].filter(Boolean);
        if (meta.length) out.push(meta.join("  -  "));
        return out;
      })();
      const vpnField = vpnLines.join("\n");

      const embed = clinical(new EmbedBuilder().setColor(CLIN.green)
        .setTitle(`Player Information: ${name}`)
        .setDescription(`${name} just connected on ${srvName}.`)
        .addFields(
          // IP first, then EOS ID - both in inline code so they're one-tap copyable.
          { name: "IP Address",      value: ip ? `\`${ip}\`` : "unknown",         inline: false },
          { name: "EOS ID",          value: rec.id ? `\`${rec.id}\`` : "unknown", inline: false },
          { name: "First Seen",      value: fmt(rec.firstSeen),                  inline: true },
          { name: "Last Seen",       value: fmt(rec.lastSeen),                   inline: true },
          { name: "Login Count",     value: String(rec.logins ?? 0),             inline: true },
          { name: "Possible Alts",   value: (rec.alts && rec.alts.length ? rec.alts.join(", ") : "None").slice(0, 1024), inline: false },
          { name: "Bypass Auto-Ban", value: rec.bypass ? "Yes" : "No",           inline: true },
          { name: "Server",          value: srvName,                             inline: true },
          { name: "Log Scan Results", value: rec.flagged ? "Flagged - matches the blacklist (auto-banned)"
            : "No matches", inline: false },
          { name: "Network",         value: [
              `ASN: ${vpnResult?.asn ? `\`${vpnResult.asn}\`` : "unknown"}`,
              `Organization: ${vpnResult?.organization || vpnResult?.isp || "unknown"}`,
              `Country: ${vpnResult?.country || rec.country || "unknown"}`,
            ].join("\n"), inline: false },
          { name: "Detection",       value: [
              // Explicit yes/no per category, with the risky ones called out.
              `VPN: ${vpnResult?.vpn === true ? "**YES**" : vpnResult?.vpn === false ? "No" : "unknown"}`,
              `Proxy: ${vpnResult?.proxy === true ? "**YES**" : vpnResult?.proxy === false ? "No" : "unknown"}`,
              `Tor: ${vpnResult?.tor === true ? "**YES**" : vpnResult?.tor === false ? "No" : "unknown"}`,
              `Hosting/Datacenter: ${vpnResult?.hosting === true ? "**YES**" : vpnResult?.hosting === false ? "No" : "unknown"}`,
              `Residential: ${vpnResult?.residential === true ? "Yes" : vpnResult?.residential === false ? "**No**" : "unknown"}`,
              vpnResult?.threatScore != null ? `Threat score: **${vpnResult.threatScore}**/100` : null,
            ].filter(Boolean).join("\n"), inline: false },
          { name: "VPN / Proxy",     value: vpnField.slice(0, 1024),             inline: false },
          { name: "Location",        value: (formatFullLocation(vpnResult?.geo) || (ip ? "unknown" : "no IP")).slice(0, 1024), inline: false },
          { name: "Last Activity",   value: fmt(lastActivity),                   inline: false },
          { name: "Recent Connections", value: "```\n" + (connLines.length ? connLines.join("\n") : "no records").slice(0, 1000) + "\n```", inline: false },
        ), "Connection log - the bot");
      postFeed(embed);
    },
    // Fired on every live PvP kill (ipBans already filters out suicides/environmental
    // deaths - killer is always distinct from and present alongside the victim).
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
      try { await upsertPermBan({ playerId: name, reason: banReason, moderator: banMod, network: res?.network ?? null }); } catch {}   // show in /banlist with the real punishment
      writeModLog({ action: "auto-ipban", playerId: name, reason: `${banReason} (evasion via ${reason || "match"}${ip ? ` ${ip}` : ""})`, by: banMod });
      logger.warn("IPGuard", `Auto-banned ${name} - ${banReason} (evasion via ${reason || "match"}${ip ? ` ${ip}` : ""}), id [${uniqueId || "?"}]`);
      const banEmbed = clinical(new EmbedBuilder().setColor(CLIN.red)
        .setTitle("Auto-Ban - Ban Evasion")
        .setDescription(`**${name}** was auto-banned for ${banReason} - caught by ${reason || "match"}${ip ? ` from \`${ip}\`` : ""}${uniqueId ? ` (id \`${uniqueId}\`)` : ""}. Banned and kicked on ${res?.blacklist?.servers ?? 0}/${ACTIVE_SERVERS.length} server(s).`),
        "Auto-ban - native RCON ban - all servers");
      await logBan(banEmbed);   // dedicated ban-log channel (falls back to mod-log)
      postFeed(banEmbed);       // also surface it in the connection feed
    },
  });
  for (const s of ACTIVE_SERVERS) refreshPlayerCache(s);
  try { healTreeOwnership(); } catch (e) { logger.warn("Init", `ownership heal failed: ${e.message}`); }
  try { const r = syncAllModSave(); if (r.installs > 1 && !r.off) logger.info("Sync", `ModSave sync on startup - ${r.synced} file(s) propagated across ${r.installs} installs`); } catch (e) { logger.warn("Sync", `ModSave sync failed: ${e.message}`); }
  try { ensureFactionFiles(); } catch (e) { logger.warn("Init", `faction file build failed: ${e.message}`); }
  try { pruneObsoleteFactionFiles(); } catch (e) { logger.warn("Init", `obsolete faction prune failed: ${e.message}`); }
  try { reconcileBlacklists(); } catch (e) { logger.warn("Blacklist", `reconcile failed: ${e.message}`); }
  try { await importBlacklistToBans(); } catch (e) { logger.warn("Bans", `blacklist import failed: ${e.message}`); }
  try { await importModsaveBanlist(); } catch (e) { logger.warn("Bans", `modsave banlist import failed: ${e.message}`); }   // pull in-game-menu bans into the DB
  try { syncModsaveBanlist(); } catch {}   // then (re)build the custom ban-message file
  setTimeout(rconHealthCheck, 5_000);
  try { await fixAutoBanReasons(); } catch (e) { logger.warn("Bans", `auto-ban reason repair failed: ${e.message}`); }   // /checkban shows real punishment
  setTimeout(enforceBansSweep, 10_000);   // clear any banned players already online at startup
  setTimeout(reconcileBans,    15_000);   // rebuild the server ban list from the DB on startup
  ensureMenuPanel();
  try { await ensureUnverifiedSetup(); } catch (e) { logger.warn("Verify", `unverified setup failed: ${e.message}`); }
  try { await ensureVerifyPanel(); } catch (e) { logger.warn("Verify", `verify panel failed: ${e.message}`); }
  // Wipe stale leaderboard/player-list messages from the previous run, then post fresh.
  try { await refreshLeaderboardChannels(); } catch (e) { logger.warn("Purge", `leaderboard refresh failed: ${e.message}`); }
});


  return {  };
};
