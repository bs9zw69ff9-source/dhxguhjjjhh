/* ---------------- moderation/bans: native RCON ban/kick enforcement, reconcile, unban ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./moderation/bans")(ctx). */
module.exports = function(ctx) {
  const {
  ACTIVE_SERVERS, FILES, _sameId, blacklistAdd, blacklistRemove, easternStamp,
  getOnlinePlayers, ipBans, isAutobanExempt, isMasterName, loadVpnChecks,
  loadBans, logger, preserveBalanceAcrossKick, removeAutobanExempt, safeRead, safeWrite,
  sanitizeBanName, sanitizeId, sendRcon, update,
  } = ctx;

const { findEvasion, banNetworkSnapshot } = require("./evasion");
const uniqIps = (a) => [...new Set(a.filter(Boolean).map(s => String(s).trim()))];

/* Correlate a joining player against every active ban's stored network snapshot plus
   the standing ipBans flags. Pure matching lives in ./evasion - this only gathers the
   state. Returns the evasion verdict, or null when nothing is known. */
function checkEvasion({ name, eosId, ip } = {}) {
  try {
    const now = Date.now();
    const bans = loadBans().filter(b => b.permanent || (b.expires && b.expires > now));
    let rec = null, vpnRec = null;
    try { rec = ipBans.getRecord(name) ?? null; } catch {}
    try { vpnRec = ip ? (loadVpnChecks?.() ?? {})[ip] ?? null : null; } catch {}
    const bl = (() => { try { return ipBans.getBlacklist(); } catch { return { ips: [], names: [], ids: [] }; } })();
    return findEvasion({
      name, eosId: eosId || rec?.id || null, ip,
      asn: vpnRec?.asn ?? null, provider: vpnRec?.provider ?? null,
      hosting: vpnRec?.hosting ?? null, vpn: vpnRec?.vpn ?? null,
      altNames: rec?.alts ?? [],
    }, bans, {
      flaggedIds:   new Set((bl.ids   ?? []).map(x => String(x).toLowerCase())),
      flaggedIps:   new Set(bl.ips    ?? []),
      flaggedNames: new Set((bl.names ?? []).map(x => String(x).toLowerCase())),
    });
  } catch (e) { logger.warn("Evasion", `check failed for ${name}: ${e.message}`); return null; }
}

async function banWithIp(playerId, server = "both", opts = {}) {
  const name = sanitizeBanName(playerId);
  // Master names are never banned - no matter which path asks (command or auto-ban).
  if (isMasterName(name)) {
    logger.warn("Bans", `Refused to ban master name "${name}"`);
    return { ids: [], ips: [], alts: [], field: null, blacklist: { name, servers: 0 }, ok: false, master: true };
  }
  removeAutobanExempt(name).catch(() => {});   // a deliberate ban clears any unban exemption
  // Native RCON Ban + Kick by USERNAME on every server. NO blacklist.txt.
  // NOTE: the OS firewall (ufw) is deliberately NOT touched here - ufw deny/allow
  // is a manual owner action via /firewall, never applied automatically on a ban.
  // Resolve what we know about the account up front so the audit line can name the
  // EOS id / IPs even when enforcement or flagging fails.
  let known = null;
  try { known = ipBans.getRecord(name); } catch (e) { logger.warn("Bans", `registry lookup failed for "${name}": ${e.message}`); }
  const kind = opts.permanent === true ? "permanent" : "temporary";

  let enforced = { servers: 0, target: null };
  try { enforced = await hardEnforce(name, { uniqueId: known?.id || null }); }
  catch (err) { logger.error("Bans", `RCON ban FAILED for "${name}": ${err.message}`); }
  scheduleBanRecheck(name);                     // 30s: blacklist.txt backup + re-enforce
  // Flag their EXACT confirmed IP(s) + EOS id so an alt/reconnect re-triggers the ban.
  let enf;
  // EOS-id flags are PERMANENT-ban only (ipBans contract): a temp ban flags the
  // IPs but must not brand the account id forever.
  try { enf = ipBans.blacklistPlayer(name, { flagId: opts.permanent === true }); }
  catch (err) { logger.error("IPBan", `IP/EOS flag FAILED for "${name}": ${err.message} - the ban record stands but alts will NOT be caught`); enf = { ids: [], ips: [], alts: [], field: null }; }

  /* Single structured audit line per ban: who, which identifiers, which server(s),
     what was flagged, and whether it actually landed. This is the line to grep when
     asking "why did this ban not stick?". */
  const okAll = enforced.servers === ACTIVE_SERVERS.length;
  logger[enforced.servers ? (okAll ? "info" : "warn") : "error"]("BanAudit",
    `${kind} ban | player="${name}" | target="${enforced.target ?? "none"}"` +
    ` | eos=${enf.ids?.length ? enf.ids.join(",") : (known?.id || "unknown")}` +
    ` | ip=${opts.ip || (known?.cips?.length ? known.cips.join(",") : "none-confirmed")}` +
    ` | server=${server}` +
    ` | flagged: ips=${enf.ips?.length ?? 0} eosIds=${opts.permanent === true ? (enf.ids?.length ?? 0) : 0}${opts.permanent === true ? "" : " (temp ban - EOS id deliberately not flagged)"}` +
    ` | alts=${enf.alts?.length ? enf.alts.join(",") : "none"}` +
    ` | rcon=${enforced.servers}/${ACTIVE_SERVERS.length} accepted` +
    ` | result=${enforced.servers ? (okAll ? "ENFORCED" : "PARTIAL") : "NOT ENFORCED"}` +
    ` | at=${easternStamp()}`);
  if (!enf.ips?.length && !(opts.permanent === true && enf.ids?.length)) {
    logger.warn("BanAudit", `"${name}" has NO confirmed IP and no EOS flag yet - they were likely never seen disconnecting. Their IP will be flagged automatically the moment their disconnect confirms it (pending flag, persisted across restarts).`);
  }
  /* Snapshot the network intelligence ALONGSIDE the ban so a future join can be
     correlated against it later - after the VPN cache entry has expired, and even if
     the player never reconnects from the same address. Returned to the caller, which
     writes it onto the ban record. */
  let network = null;
  try {
    const ips = uniqIps([opts.ip, ...(known?.cips ?? []), ...(enf.ips ?? [])]);
    const vpnRecord = (() => {
      try { const all = loadVpnChecks?.() ?? {}; return ips.map(i => all[i]).find(Boolean) ?? null; } catch { return null; }
    })();
    network = banNetworkSnapshot({
      eosIds: [known?.id, ...(known?.ids ?? []), ...(enf.ids ?? [])],
      ips, names: [name, ...(known?.alts ?? [])], vpnRecord,
    });
    logger.info("BanAudit", `network snapshot | player="${name}" | asn=${network.asn ?? "unknown"}` +
      ` | provider=${network.provider ?? "none"} | hosting=${network.hosting ?? "unknown"}` +
      ` | ips=${network.ips.length} | names=${network.names.length} | eosIds=${network.eosIds.length}`);
  } catch (e) { logger.warn("Bans", `could not snapshot network intel for "${name}": ${e.message}`); }

  return { ...enf, network, blacklist: { name, servers: enforced.servers }, ok: enforced.servers > 0, firewall: null };
}



/* Force-ban + remove a player by USERNAME on every server - native RCON `Ban` + `Kick`,
   NO blacklist.txt - logging the server's response to each so `pm2 logs` shows exactly
   what landed. Returns { servers } = how many servers accepted a command. */
async function hardEnforce(name, { banToo = true, kick = true, uniqueId = null } = {}) {
  /* Pavlov's `Ban`/`Kick` take the player's UniqueId (on Shack that's the Oculus
     username). Prefer an explicit UniqueId from RefreshList when the caller has one -
     a DISPLAY name can differ from it (and can contain spaces, which sanitizeId
     strips, producing a target the server has never heard of). */
  const target = sanitizeId(uniqueId || name);
  if (!target) {
    logger.error("Bans", `ENFORCE FAILED - no usable RCON target for "${name}" (uniqueId=${uniqueId ?? "none"}): the identifier sanitized to empty`);
    return { servers: 0, target: null };
  }
  // Loud diagnostic: the identifier we send differs from the real one, so a silent
  // no-op ban is explainable instead of looking like the server ignored us.
  const source = uniqueId || name;
  if (String(source) !== target) {
    logger.warn("Bans", `ENFORCE TARGET REWRITTEN for "${source}" -> "${target}" (sanitizer stripped characters). If the ban does not land, this mismatch is why - the server knows them as "${source}".`);
  }
  preserveBalanceAcrossKick(name);             // don't let the kick wipe their caps (ledger is keyed on the display name)
  let ok = 0;
  const perServer = async (srv) => {
    const verbs = [];
    if (banToo) verbs.push(`Ban ${target}`);
    if (kick)   verbs.push(`Kick ${target}`);
    if (!verbs.length) return;
    let served = false;
    for (const cmd of verbs) {
      try {
        const r = await sendRcon(cmd, srv, 2500, 1);
        served = true;
        logger.info("IPGuard", `${srv} < ${cmd}  ->  ${String(r ?? "").replace(/\s+/g, " ").trim().slice(0, 140) || "(no response)"}`);
      } catch (e) { logger.warn("IPGuard", `${srv} < ${cmd}  FAILED: ${e.message}`); }
    }
    if (served) ok++;
  };
  await Promise.all(ACTIVE_SERVERS.map(perServer));
  if (!ok) logger.error("Bans", `ENFORCE FAILED - "${target}" was accepted by 0/${ACTIVE_SERVERS.length} server(s) (every RCON call errored; the player is NOT removed)`);
  return { servers: ok, target };
}

/* 30s after a ban, back it up: write the name into blacklist.txt (reconnect block that
   doesn't depend on the RCON ban sticking) and re-run enforcement in case they slipped
   back in. Requested belt-and-suspenders on top of the native ban. */
function scheduleBanRecheck(name) {
  setTimeout(async () => {
    // Skip if they were unbanned (or their temp ban expired) in the 30s window.
    const stillBanned = loadBans().some(b => _sameId(b.playerId, name) && (b.permanent || (b.expires && b.expires > Date.now())));
    if (!stillBanned) return;
    try { blacklistAdd(name); } catch (e) { logger.warn("Bans", `30s blacklist.txt backup failed for ${name}: ${e.message}`); }
    try { await hardEnforce(name); } catch {}
    logger.info("Bans", `30s recheck for ${name}: blacklist.txt backup written + re-enforced`);
  }, 30_000);
}

/* Safety-net enforcement: RefreshList is the AUTHORITATIVE list of who is actually in
   the game. Every sweep, cross-reference it against the active ban list + IP/EOS flags
   and force-remove anyone banned who's still online - a ban whose join-time kick missed,
   or a player who was already in the game when the ban landed. This is what actually
   gets a stuck banned player OUT; the per-command RCON responses are logged so we can
   see whether the removal takes. */
let _sweepBusy = false;
async function enforceBansSweep() {
  if (_sweepBusy) return;                       // don't overlap a slow sweep
  _sweepBusy = true;
  try {
    const now = Date.now();
    const banned = new Set(loadBans()
      .filter(b => b.permanent || (b.expires && b.expires > now))
      .map(b => String(b.playerId).toLowerCase()));
    /* hardEnforce already issues Ban+Kick on EVERY server, so a player sitting on
       several servers only needs enforcing ONCE per sweep. Without this, an N-server
       setup fired N enforcements x N servers = N^2 RCON connections per offender. */
    const handled = new Set();
    for (const srv of ACTIVE_SERVERS) {
      let players;
      try { players = await getOnlinePlayers(srv); } catch (e) { logger.warn("BanSweep", `RefreshList failed on ${srv}: ${e.message} - cannot sweep that server this pass`); continue; }
      for (const p of players) {
        const nm = p.name;
        if (!nm || isMasterName(nm) || isAutobanExempt(nm)) continue;   // never sweep an unban-exempt player
        if (handled.has(nm.toLowerCase())) continue;                    // already enforced across all servers this pass
        const nameBanned = banned.has(nm.toLowerCase());
        let flaggedHit = false, why = nameBanned ? "ban record" : null;
        if (!nameBanned) {
          // Flagged IP / EOS id - resolve once so the log can say WHICH signal hit.
          try {
            const rec = ipBans.getRecord(nm);
            flaggedHit = !!rec?.flagged;
            if (flaggedHit) why = `flagged ${rec?.ids?.length ? `EOS/IP (eos=${rec.id})` : "IP"}`;
          } catch (e) { logger.warn("BanSweep", `flag lookup failed for ${nm}: ${e.message}`); }
        }
        if (nameBanned || flaggedHit) {
          logger.warn("BanSweep", `ENFORCING - "${nm}" [uid=${p.id || "unknown"}] is online on ${srv} but matches a ${why} - removing`);
          // Enforce against the UniqueId RefreshList gave us (what Ban/Kick expect),
          // falling back to the display name when the server didn't report one.
          handled.add(nm.toLowerCase());
          const r = await hardEnforce(nm, { uniqueId: p.id || null });
          logger.info("BanSweep", `RESULT - "${nm}" [uid=${p.id || "unknown"}] target="${r.target ?? "none"}" reason="${why}" accepted=${r.servers}/${ACTIVE_SERVERS.length} server(s) -> ${r.servers ? "removed" : "FAILED"}`);
          // NOTE: the sweep does NOT create a ban RECORD - the IP/EOS flag already
          // persists the auto-ban and re-catches them on join. Recording here made a
          // flagged/shared IP mass-create ban entries wrongly attributed to a person.
          continue;
        }
        // NOTE: mutes are applied once on /mute and re-applied once on join
        // (applyMuteOnJoin) - NOT repeated here. Gag <name> is a bare toggle with
        // no explicit True/False, so calling it again on every 60s sweep would flip
        // an already-muted player's state back off instead of safely re-affirming it.
      }
    }
  } finally { _sweepBusy = false; }
}

/* One-time repair: rewrite legacy auto-ban records (moderator "IP-Guard" or a reason
   starting "Auto-ban") so /checkban shows the REAL punishment - the original ban's
   offense + moderator where we can find it, else a clean "Ban evasion". */
async function fixAutoBanReasons() {
  await update(FILES.TEMPBAN, [], (bans) => {
    let n = 0;
    for (const b of bans) {
      if (b.moderator === "IP-Guard" || /^auto-ban/i.test(String(b.reason || ""))) {
        let rec = null; try { rec = ipBans.getRecord(b.playerId); } catch {}
        const src = sourceBanFor(b.playerId, rec?.id);
        b.reason = src?.reason || "Ban evasion";
        b.moderator = src?.moderator || "Ban evasion (auto)";
        n++;
      }
    }
    if (n) logger.info("Bans", `Rewrote ${n} legacy auto-ban record(s) to show the real punishment (was "IP-Guard")`);
    return bans;
  });
}

/* Ban-list reconciliation: the bot's ban DB (tempbans.json) is the source of truth.
   Re-apply every ACTIVE ban to each server (native RCON Ban) and to blacklist.txt so
   the server's ban list is rebuilt and stays complete - even after a SERVER restart
   wipes its native bans. Re-banning an already-banned player is a harmless no-op, so
   this makes bans self-healing: a banned player can't slip in through a lost ban list.

   Every RCON call opens a brand-new TCP connection (see sendRconRaw), so a big ban
   list means a real burst of connections to the game server. That's fine when it's
   actually needed (the game server itself restarted and lost its native bans), but
   this used to run 15s after every BOT restart too - which doesn't touch the game
   server's own ban list at all, so on a large ban list it was hammering RCON with
   dozens of fresh connections for no reason on every single bot deploy. Gate it to
   run at most once per BAN_RECONCILE_MIN_INTERVAL_MS regardless of how often the
   bot restarts. */
const BAN_RECONCILE_MIN_INTERVAL_MS = 30 * 60 * 1000;
let _reconcileBusy = false;
async function reconcileBans() {
  if (_reconcileBusy) return;
  const state = safeRead(FILES.BAN_RECONCILE_STATE, {});
  if (state.lastRun && Date.now() - state.lastRun < BAN_RECONCILE_MIN_INTERVAL_MS) {
    logger.info("Bans", `Skipped ban reconcile - last ran ${Math.round((Date.now() - state.lastRun) / 60000)}m ago (min interval ${BAN_RECONCILE_MIN_INTERVAL_MS / 60000}m). Native bans persist on the game server across bot restarts on their own.`);
    return;
  }
  _reconcileBusy = true;
  try {
    const now = Date.now();
    const active = loadBans().filter(b => b.permanent || (b.expires && b.expires > now));
    let n = 0;
    for (const b of active) {
      const name = sanitizeId(b.playerId);
      if (!name || isMasterName(name) || isAutobanExempt(name)) continue;
      // Re-verify at ISSUE time: an /unban during this pass removes the DB entry, and
      // re-Banning from the stale snapshot would natively re-ban them with nothing left
      // to lift it (their Unban already went out).
      const still = loadBans().some(x => _sameId(x.playerId, b.playerId) && (x.permanent || (x.expires && x.expires > Date.now())));
      if (!still) continue;
      try { blacklistAdd(b.playerId); } catch {}                     // blacklist.txt reconnect-block backup
      for (const srv of ACTIVE_SERVERS) { try { await sendRcon(`Ban ${name}`, srv, 2500, 0); } catch {} }
      n++;
      await new Promise(r => setTimeout(r, 400));                    // gentle pacing so a big list doesn't flood RCON with fresh connections
    }
    safeWrite(FILES.BAN_RECONCILE_STATE, { lastRun: Date.now() });
    if (n) logger.info("Bans", `Reconciled ${n} active ban(s) to native RCON + blacklist.txt across ${ACTIVE_SERVERS.length} server(s)`);
  } finally { _reconcileBusy = false; }
}

/* The original ban whose flag caught this player - so /checkban and the ban record
   show the REAL offense, not "IP-Guard" / "Ban evasion". A real ban is one whose reason
   isn't itself an auto-ban and whose moderator isn't the auto-ban marker. Matched by:
   1) the SAME EOS account (evader kept the account, changed name),
   2) the same name, or a shared-confirmed-IP alt. */
function isRealBan(b) {
  // A per-player punishment - NOT an auto-ban and NOT a broad /configure IP/name
  // blacklist (those aren't a punishment on one person, and copying their moderator
  // makes every match look like that admin banned it).
  return b && b.reason && !/^(auto-ban|ban evasion)/i.test(b.reason)
    && !/blacklisted via/i.test(b.reason)
    && b.moderator !== "IP-Guard" && !/evasion/i.test(String(b.moderator || ""));
}
function sourceBanFor(name, uniqueId) {
  const real = loadBans().filter(isRealBan);
  if (uniqueId) {   // same account (EOS id) under a possibly different name
    const byEos = real.find(b => { try { return ipBans.getRecord(b.playerId)?.id === uniqueId; } catch { return false; } });
    if (byEos) return byEos;
  }
  let related = [name];
  try { related = [name, ...(ipBans.getAltNamesOf(name) || [])]; } catch {}
  const relSet = new Set(related.map(r => String(r).toLowerCase()));
  return real.find(b => relSet.has(String(b.playerId).toLowerCase())) || null;
}
/* What should the IP-Guard do with a join that matched a flagged IP/name/id?
   - "block": an UNEXPIRED temp ban covers this name - the blacklist already bounced
     them; log it, never escalate.
   - "lift":  the covering temp ban has EXPIRED but the entry/flags are still around
     (the 60s lift sweep hasn't run yet, or a stale flag survived). A served temp ban
     must never turn permanent - lift it right now instead.
   - "ban":   nothing covers them (evasion alt / fresh blacklist match) - auto-ban. */
function autoBanDecision(existing, reason, now = Date.now()) {
  if (existing && !existing.permanent && existing.expires) {
    if (existing.expires > now) return "block";
    // Served temp ban: lift on rejoin instead of escalating. IP and EOS-account flags
    // both come from the ban itself (stale after expiry). A "username" match is only
    // ever a deliberate manual flag (/configure), so that still escalates.
    if (/blacklisted (ip|account)/i.test(String(reason || ""))) return "lift";
  }
  return "ban";
}

// Lift a ban: remove the name from blacklist.txt on both installs + clear IP flags.
function unbanEverywhere(playerId) {
  const name = sanitizeBanName(playerId);
  let known = null;
  try { known = ipBans.getRecord(name); } catch {}
  let bl = { name, removed: 0 };
  try { bl = blacklistRemove(name); } catch (err) { logger.error("Bans", `blacklist.txt remove FAILED for "${name}": ${err.message}`); }
  let cleared = null;
  // NOTE: the OS firewall (ufw) is NOT touched on unban - any ufw deny is an
  // owner-managed manual rule, removed only via /firewall unblock.
  try { cleared = ipBans.unblacklistPlayer(name); }
  catch (err) { logger.error("IPBan", `IP/EOS unflag FAILED for "${name}": ${err.message} - stale flags may re-catch them`); }
  // Lift the native RCON ban too, against the UniqueId when we know it.
  const _uname = sanitizeId(known?.id || name);
  const c = cleared?.cleared ?? { ips: 0, names: 0, ids: 0 };
  logger.info("BanAudit",
    `unban | player="${name}" | target="${_uname || "none"}" | eos=${known?.id || "unknown"}` +
    ` | cleared: ips=${c.ips ?? 0} names=${c.names ?? 0} eosIds=${c.ids ?? 0} blacklistTxt=${bl.removed ?? 0}` +
    ` | at=${easternStamp()}`);
  if (!_uname) logger.error("BanAudit", `unban for "${name}" has NO usable RCON target - the native server ban was NOT lifted`);
  // Fire-and-forget so the reply isn't blocked, but report per-server failures
  // instead of swallowing them (a silent failure leaves them natively banned).
  if (_uname) (async () => {
    for (const srv of ACTIVE_SERVERS) {
      try { await sendRcon(`Unban ${_uname}`, srv, 2500, 1); logger.info("BanAudit", `unban RCON accepted | "${_uname}" | ${srv}`); }
      catch (e) { logger.error("BanAudit", `unban RCON FAILED | "${_uname}" | ${srv} | ${e.message} - they may still be natively banned on that server`); }
    }
  })().catch(() => {});
  return { blacklist: bl, cleared: c };
}

function parseRcon(raw) {
  if (!raw) return null;
  try {
    const start = raw.indexOf("{");
    const end   = raw.lastIndexOf("}");
    if (start === -1 || end === -1) return null;
    return JSON.parse(raw.slice(start, end + 1));
  } catch { return null; }
}


  return { BAN_RECONCILE_MIN_INTERVAL_MS, checkEvasion, _reconcileBusy, _sweepBusy, autoBanDecision, banWithIp, enforceBansSweep, fixAutoBanReasons, hardEnforce, isRealBan, parseRcon, reconcileBans, scheduleBanRecheck, sourceBanFor, unbanEverywhere };
};
