/* ---------------- moderation/vpn: IPHub/IPQS proxy detection + geolocation + auto-ban ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./moderation/vpn")(ctx). */
module.exports = function(ctx) {
  const {
  ACTIVE_SERVERS, CLIN, DIVIDER, EmbedBuilder, FILES, NV,
  banWithIp, brand, clinical, hero, isAutobanExempt,
  isMasterName, logAction, logBan, logger, postFeed,
  safeRead, update, upsertPermBan, writeModLog,
  } = ctx;

/* ---------------- VPN / proxy detection (two-tier, consensus based) ----------------
   Detection runs in two stacked tiers so accuracy is high without burning quota:

     TIER 1 - REGULAR CHECK (high daily quotas, run on EVERY not-yet-checked IP)
       • IPHub         1,000/day   block==1 = hosting/proxy/non-residential
       • vpnapi.io     1,000/day   security.{vpn,proxy,tor,relay}
       • ipapi.is      1,000/day   is_vpn / is_proxy / is_tor  (works keyless)
       • proxycheck.io   100/day   proxy=yes + risk + the VPN BRAND name (keyless)

     TIER 2 - FINAL CONFIRMATION (small quota, highest accuracy, ONLY on a screen hit)
       • IPQualityScore  ~35/day   vpn/proxy/tor + fraud score

   A clean screen ends the check immediately, so tier 2's tiny quota is only ever
   spent on IPs that already look suspicious. Verdict:

     confirmHits >= VPN_CONFIRM_MIN                -> CONFIRMED  (auto-ban)
     tier 2 ran and all came back clean            -> DISPUTED   (log only, no ban)
     tier 2 not configured, screenHits >= ban min  -> CONFIRMED by screen consensus
     screenHits < VPN_SCREEN_MIN                   -> CLEAN

   Results are cached per-IP forever (an IP is checked once, ever - keyed on the IP,
   not the account, so a clean IP stays clean whoever connects from it next).
   Every detector is optional; the whole subsystem is a no-op with none configured. */
const IPHUB_API_KEY  = process.env.IPHUB_API_KEY || "";
const IPQS_API_KEY   = process.env.IPQS_API_KEY  || "";
const IPINFO_TOKEN   = process.env.IPINFO_TOKEN  || "";   // optional - higher ipinfo.io free quota
const PROXYCHECK_API_KEY = process.env.PROXYCHECK_API_KEY || "";
const VPNAPI_KEY     = process.env.VPNAPI_KEY   || "";
const IPAPIIS_KEY    = process.env.IPAPIIS_KEY  || "";    // ipapi.is works keyless too

// How many detectors must agree. Defaults are deliberately conservative.
const VPN_SCREEN_MIN     = Math.max(1, Number(process.env.VPN_SCREEN_MIN)     || 1);   // hits needed to escalate to tier 2
const VPN_CONFIRM_MIN    = Math.max(1, Number(process.env.VPN_CONFIRM_MIN)    || 1);   // tier-2 hits needed to ban
const VPN_SCREEN_BAN_MIN = Math.max(1, Number(process.env.VPN_SCREEN_BAN_MIN) || 2);   // screen-only consensus needed to ban when tier 2 is unconfigured

const _scrub = (s, ...keys) => keys.filter(Boolean).reduce((acc, k) => acc.split(k).join("***"), String(s ?? ""));

/* Each detector: { name, tier, enabled, run(ip) -> { flagged, provider?, detail? } }.
   `run` must never throw - it returns null when the lookup itself failed, which is
   tracked separately from "answered clean" so an outage can't be read as innocence. */
const DETECTORS = [
  { name: "iphub", tier: 1, enabled: () => !!IPHUB_API_KEY, async run(ip) {
      const res = await fetch(`https://v2.api.iphub.info/ip/${encodeURIComponent(ip)}`, { headers: { "X-Key": IPHUB_API_KEY } });
      const d = await res.json();
      if (!d || d.block === undefined) return null;
      return { flagged: d.block === 1, detail: `block:${d.block}`, isp: d.isp || null, block: d.block };
    } },
  { name: "vpnapi", tier: 1, enabled: () => !!VPNAPI_KEY, async run(ip) {
      const res = await fetch(`https://vpnapi.io/api/${encodeURIComponent(ip)}?key=${VPNAPI_KEY}`, { headers: { Accept: "application/json" } });
      const d = await res.json();
      const s = d?.security;
      if (!s) return null;
      const hits = ["vpn", "proxy", "tor", "relay"].filter(k => s[k]);
      return { flagged: hits.length > 0, detail: hits.length ? hits.join("+") : "clean",
               isp: d?.network?.autonomous_system_organization || null };
    } },
  { name: "ipapi.is", tier: 1, enabled: () => true, async run(ip) {      // keyless-capable
      const key = IPAPIIS_KEY ? `&key=${IPAPIIS_KEY}` : "";
      const res = await fetch(`https://api.ipapi.is/?q=${encodeURIComponent(ip)}${key}`, { headers: { Accept: "application/json" } });
      const d = await res.json();
      if (!d || d.is_vpn === undefined) return null;
      // Anonymising signals only. is_datacenter alone is NOT a hit - plenty of
      // legitimate players sit behind carrier/cloud ranges - but it is reported.
      const hits = [["vpn", d.is_vpn], ["proxy", d.is_proxy], ["tor", d.is_tor], ["abuser", d.is_abuser]]
        .filter(([, v]) => v).map(([k]) => k);
      return { flagged: hits.length > 0, detail: (hits.length ? hits.join("+") : "clean") + (d.is_datacenter ? " (datacenter)" : ""),
               provider: d.company?.name || null, isp: d.asn?.org || d.company?.name || null };
    } },
  { name: "ipqs", tier: 2, enabled: () => !!IPQS_API_KEY, async run(ip) {
      const res = await fetch(`https://www.ipqualityscore.com/api/json/ip/${IPQS_API_KEY}/${encodeURIComponent(ip)}?strictness=1`);
      const d = await res.json();
      if (!d?.success) return null;
      const flagged = !!(d.vpn || d.proxy || d.tor);
      return { flagged, detail: `vpn:${!!d.vpn} proxy:${!!d.proxy} tor:${!!d.tor} fraud:${d.fraud_score ?? "?"}`,
               isp: d.ISP || null,
               ipqs: { vpn: !!d.vpn, proxy: !!d.proxy, tor: !!d.tor, fraudScore: d.fraud_score ?? null } };
    } },
  /* Tier 1: runs on EVERY IP. It is the only source of the consumer VPN brand name
     ("NordVPN"), so screening every connection means the feed can show the provider
     even for IPs that come back clean. Keyless is 100/day - set PROXYCHECK_API_KEY
     for 1,000/day if the server sees more unique IPs than that per day. */
  { name: "proxycheck", tier: 1, enabled: () => true, async run(ip) {    // keyless-capable
      const key = PROXYCHECK_API_KEY ? `&key=${PROXYCHECK_API_KEY}` : "";
      const res = await fetch(`https://proxycheck.io/v2/${encodeURIComponent(ip)}?vpn=1&risk=1${key}`, { headers: { Accept: "application/json" } });
      const d = await res.json();
      if (!d || (d.status !== "ok" && d.status !== "warning")) return null;
      const rec = d[ip];
      if (!rec) return null;
      return { flagged: String(rec.proxy).toLowerCase() === "yes",
               detail: `proxy:${rec.proxy}${rec.type ? ` type:${rec.type}` : ""}${rec.risk !== undefined ? ` risk:${rec.risk}` : ""}`,
               // operator.name is the consumer brand (NordVPN, IVPN); provider is the network.
               provider: rec.operator?.name || rec.provider || null };
    } },
];
const tierDetectors = (tier) => DETECTORS.filter(d => d.tier === tier && d.enabled());
// True when ANY detector is usable - what gates the whole subsystem.
const vpnDetectionEnabled = () => DETECTORS.some(d => d.enabled());

/* Run one tier in parallel. Returns { hits, answered, failed, results[] } where
   `answered` counts detectors that actually returned a verdict (so a total outage is
   distinguishable from a unanimous all-clear). */
async function runTier(tier, ip) {
  const dets = tierDetectors(tier);
  const settled = await Promise.all(dets.map(async (d) => {
    try {
      const out = await d.run(ip);
      return out ? { name: d.name, tier, ...out } : { name: d.name, tier, error: "no verdict" };
    } catch (err) {
      return { name: d.name, tier, error: _scrub(err.message ?? err, IPQS_API_KEY, PROXYCHECK_API_KEY, VPNAPI_KEY, IPAPIIS_KEY, IPHUB_API_KEY) };
    }
  }));
  for (const r of settled) if (r.error) logger.warn("VPN", `${r.name} lookup failed for ${ip}: ${r.error}`);
  const answered = settled.filter(r => !r.error);
  return {
    hits: answered.filter(r => r.flagged).length,
    answered: answered.length,
    failed: settled.length - answered.length,
    results: settled,
  };
}
/* Human label for a detector's role, used by /vpncheck. */
const TIER_ROLE = { 1: "Regular check (every IP)", 2: "Final confirmation (flagged IPs only)" };

/* Live health probe for /vpncheck: hits EVERY detector (both tiers, ignoring the
   normal tier gating) against one IP and reports per-detector status. Diagnostic
   only - it spends one lookup per configured detector, so it is owner-gated. */
async function probeDetectors(ip) {
  const out = await Promise.all(DETECTORS.map(async (d) => {
    const base = { name: d.name, tier: d.tier, role: TIER_ROLE[d.tier], configured: d.enabled() };
    if (!d.enabled()) return { ...base, status: "not configured" };
    const t0 = Date.now();
    try {
      const r = await d.run(ip);
      const ms = Date.now() - t0;
      if (!r) return { ...base, status: "no verdict", ms };
      return { ...base, status: "up", ms, flagged: !!r.flagged, detail: r.detail ?? null, provider: r.provider ?? null };
    } catch (err) {
      return { ...base, status: "down", ms: Date.now() - t0,
               error: _scrub(err.message ?? err, IPQS_API_KEY, PROXYCHECK_API_KEY, VPNAPI_KEY, IPAPIIS_KEY, IPHUB_API_KEY).slice(0, 120) };
    }
  }));
  return {
    ip,
    detectors: out,
    screenActive:  out.filter(d => d.tier === 1 && d.configured).length,
    screenTotal:   out.filter(d => d.tier === 1).length,
    confirmActive: out.filter(d => d.tier === 2 && d.configured).length,
    confirmTotal:  out.filter(d => d.tier === 2).length,
    up:   out.filter(d => d.status === "up").length,
    down: out.filter(d => d.status === "down" || d.status === "no verdict").length,
    thresholds: { screenMin: VPN_SCREEN_MIN, confirmMin: VPN_CONFIRM_MIN, screenBanMin: VPN_SCREEN_BAN_MIN },
  };
}

// Kept for callers that only want the provider name for a single IP.
async function proxycheckLookup(ip) {
  const d = DETECTORS.find(x => x.name === "proxycheck");
  try { return (await d.run(ip))?.provider ?? null; }
  catch (err) { logger.warn("VPN", `proxycheck lookup failed for ${ip}: ${_scrub(err.message ?? err, PROXYCHECK_API_KEY)}`); return null; }
}
const _regionName    = (() => { try { return new Intl.DisplayNames(["en"], { type: "region" }); } catch { return null; } })();
const loadVpnChecks  = () => safeRead(FILES.VPN_CHECKS, {});
function saveVpnCheck(ip, data) {
  return update(FILES.VPN_CHECKS, {}, (all) => { all[ip] = { ...data, checkedAt: Date.now() }; return all; });
}
// Exactly one VPN check per IP: cached forever once done, and concurrent checks of the
// same not-yet-cached IP share a single in-flight lookup (no double API calls when two
// players connect from the same new IP at once, or /inspect races the connection feed).
const _vpnInFlight = new Map();   // ip -> Promise
async function checkVpn(ip) {
  if (!ip) return null;                            // geolocation runs even without the VPN keys
  const cached = loadVpnChecks()[ip];
  if (cached?.geo) return cached;                  // fully cached (already has geolocation)
  const inflight = _vpnInFlight.get(ip);
  if (inflight) return inflight;                   // a check for this exact IP is already running - reuse it
  // Entry cached before geolocation existed → backfill geo only (keep the VPN verdict);
  // otherwise run the full check. This heals old "unknown"-location cache entries.
  const p = (cached ? _backfillGeo(ip, cached) : _doVpnCheck(ip)).finally(() => _vpnInFlight.delete(ip));
  _vpnInFlight.set(ip, p);
  return p;
}
async function _backfillGeo(ip, prev) {
  const geo = await geoLookup(ip);
  if (!geo) return prev;                           // geo still unavailable - keep the old entry, retry next time
  const result = { ...prev, geo, isp: geo.isp || prev.isp || null };
  await saveVpnCheck(ip, result);
  return { ...result, checkedAt: Date.now() };
}
// IP geolocation via ipinfo.io - full city-level location. Works keyless (rate-limited);
// set IPINFO_TOKEN for the larger free quota. Doesn't touch the IPHub/IPQS quotas.
async function geoLookup(ip) {
  try {
    const url = `https://ipinfo.io/${encodeURIComponent(ip)}/json${IPINFO_TOKEN ? `?token=${IPINFO_TOKEN}` : ""}`;
    const res = await fetch(url, { headers: { Accept: "application/json" } });
    const d = await res.json();
    if (!d || !d.ip || d.error || d.bogon) return null;   // error / private / reserved IP
    const [lat, lon] = String(d.loc || "").split(",");
    // ipinfo's `org` is "AS#### <ISP name>" - split into ASN + ISP.
    let asn = null, isp = d.org || null;
    const m = String(d.org || "").match(/^(AS\d+)\s+(.*)$/);
    if (m) { asn = m[1]; isp = m[2]; }
    // ipinfo returns a 2-letter country code; expand to a full name via built-in Intl.
    let country = d.country || null;
    if (country && _regionName) { try { country = _regionName.of(country) || d.country; } catch {} }
    return {
      city: d.city || null, region: d.region || null,
      country, countryCode: d.country || null,
      zip: d.postal || null, isp, org: isp, asn,
      timezone: d.timezone || null,
      lat: lat ? Number(lat) : null, lon: lon ? Number(lon) : null,
    };
  } catch (err) {
    const msg = IPINFO_TOKEN ? String(err.message).split(IPINFO_TOKEN).join("***") : String(err.message);
    logger.warn("Geo", `ipinfo.io lookup failed for ${ip}: ${msg}`);
    return null;
  }
}
/* Private / loopback / link-local / unroutable addresses. No detector can say anything
   useful about these, and geolocation returns "bogon" - which used to mean the result
   was never cached, so EVERY reconnect from a LAN address re-spent one lookup per
   detector (5 calls a join; proxycheck's keyless tier is only 100/day). Short-circuit
   them instead, and cache the answer so it is asked exactly once. */
function isUnroutableIp(ip) {
  const s = String(ip ?? "").trim();
  if (!s) return true;
  if (s === "::1" || /^f[cde]/i.test(s) || s.toLowerCase().startsWith("fe80:")) return true;   // IPv6 loopback/ULA/link-local
  const m = s.match(/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/);
  if (!m) return false;                                  // not IPv4 - let the detectors decide
  const [a, b] = [Number(m[1]), Number(m[2])];
  return a === 0 || a === 10 || a === 127 || a >= 224     // this-network, private, loopback, multicast/reserved
    || (a === 169 && b === 254)                           // link-local
    || (a === 172 && b >= 16 && b <= 31)                  // private
    || (a === 192 && b === 168);                          // private
}

async function _doVpnCheck(ip) {
  // Unroutable addresses can't be checked by anyone - cache a clean verdict and skip
  // every API call rather than burning quota on every reconnect.
  if (isUnroutableIp(ip)) {
    const local = {
      ip, flagged: false, confirmed: null, actionable: false,
      decision: "private/unroutable address - detection skipped",
      iphubBlock: null, isp: null, provider: null, ipqs: null,
      geo: { city: null, region: null, country: "Local network", countryCode: null, zip: null,
             isp: "private address", org: null, asn: null, timezone: null, lat: null, lon: null },
      detectors: [], screenHits: 0, screenAnswered: 0, confirmHits: 0, confirmAnswered: 0, local: true,
    };
    logger.debug("VPN", `${ip} is a private/unroutable address - skipped all detectors`);
    await saveVpnCheck(ip, local);
    return { ...local, checkedAt: Date.now() };
  }
  // Geolocation first - free/keyless, for every IP regardless of the detector keys.
  const gwho = await geoLookup(ip);
  const enabled = vpnDetectionEnabled();

  /* `confirmed` is the CONFIRMATION VERDICT for display (true agreed / false disputed
     / null inconclusive). `actionable` is the separate, explicit "may we auto-ban"
     decision. They must stay separate: overloading the tri-state meant a null
     (inconclusive, or below the screening threshold) read as permission to ban, which
     banned players on a single screening hit and made VPN_SCREEN_BAN_MIN dead code. */
  let flagged = false, confirmed = null, actionable = false, reason = "detection disabled";
  let screen = null, conf = null;
  if (enabled) {
    /* TIER 1 - regular check. All high-quota detectors in parallel, every IP. */
    screen = await runTier(1, ip);
    if (screen.answered === 0) {
      logger.warn("VPN", `every regular check failed for ${ip} - not caching, will retry on the next connection`);
      return null;                                   // an outage must never be cached as "clean"
    }
    flagged = screen.hits >= VPN_SCREEN_MIN;
    reason  = flagged ? "flagged by regular checks" : "all regular checks clean";

    /* TIER 2 - final confirmation. Only spent on an IP the regular checks flagged. */
    if (flagged) {
      conf = await runTier(2, ip);
      if (conf.answered > 0) {
        confirmed  = conf.hits >= VPN_CONFIRM_MIN;    // authoritative: agreed or disputed
        actionable = confirmed;
        reason     = confirmed ? `confirmation agreed (${conf.hits}/${conf.answered})`
                               : `confirmation disputed it (0/${conf.answered})`;
      } else {
        /* Nothing could confirm - either unconfigured, or every confirmer failed. Fall
           back to REQUIRING REAL CONSENSUS among the regular checks: VPN_SCREEN_BAN_MIN
           agreeing detectors, with no downward adjustment for how few are configured.
           Individual providers DO produce false positives on legitimate infrastructure
           (ipapi.is reports Cloudflare's 1.1.1.1 as "vpn+abuser"), so a single
           unconfirmed source must never be enough to ban somebody. If too few
           detectors are available to ever reach the threshold, auto-ban is off and we
           say so loudly rather than quietly banning on one opinion. */
        const configured = tierDetectors(2).length;
        confirmed  = null;
        actionable = screen.hits >= VPN_SCREEN_BAN_MIN;
        reason = `no confirmation available (${configured ? "all confirmers failed" : "none configured"}); ` +
                 `regular-check consensus ${screen.hits}/${screen.answered} vs required ${VPN_SCREEN_BAN_MIN} -> ${actionable ? "actionable" : "NOT actionable"}`;
        logger[actionable ? "warn" : "info"]("VPN", `${ip}: ${reason}`);
        if (!actionable && screen.answered < VPN_SCREEN_BAN_MIN) {
          logger.warn("VPN", `Only ${screen.answered} regular check(s) answered but ${VPN_SCREEN_BAN_MIN} must agree to ban without a confirmation detector. ` +
            `Auto-ban cannot trigger in this configuration - add another detector (VPNAPI_KEY / IPHUB_API_KEY) or an IPQS key, or set VPN_SCREEN_BAN_MIN=1 to accept single-source bans.`);
        }
      }
    }
  }

  // Merge whatever ISP / provider name the detectors reported.
  const all = [...(screen?.results ?? []), ...(conf?.results ?? [])].filter(r => !r.error);
  /* Provider name: prefer proxycheck's `operator.name` - that's the CONSUMER BRAND
     ("NordVPN") which is what staff actually want to read. Other detectors only know
     the underlying network ("M247", "Datacamp"), so they're the fallback. */
  const provider = all.find(r => r.name === "proxycheck" && r.provider)?.provider
    ?? all.map(r => r.provider).find(Boolean) ?? null;
  const detIsp   = all.map(r => r.isp).find(Boolean) ?? null;
  const iphubRes = all.find(r => r.name === "iphub");
  const ipqsRes  = all.find(r => r.name === "ipqs");

  // Prefer the whois geo (city-level); fall back to a detector's country when whois is down.
  const geo = gwho || (detIsp ? {
    city: null, region: null, country: null, countryCode: null,
    zip: null, isp: detIsp, org: null, asn: null, timezone: null, lat: null, lon: null,
  } : null);
  if (!geo) return null;                             // no location at all - retry next time

  const result = {
    ip, flagged, confirmed, actionable, decision: reason,
    iphubBlock: iphubRes?.block ?? null,             // kept for backwards compatibility
    isp: geo.isp || detIsp || null,
    provider,
    ipqs: ipqsRes?.ipqs ?? null,                     // kept for backwards compatibility
    geo,
    // Full per-detector audit so the feed / /inspect can show exactly who said what.
    detectors: all.map(r => ({ name: r.name, tier: r.tier, flagged: !!r.flagged, detail: r.detail ?? null })),
    screenHits: screen?.hits ?? 0, screenAnswered: screen?.answered ?? 0,
    confirmHits: conf?.hits ?? 0,  confirmAnswered: conf?.answered ?? 0,
  };
  logger.info("VPN", `${ip} -> screen ${result.screenHits}/${result.screenAnswered}` +
    (conf ? ` | confirm ${result.confirmHits}/${result.confirmAnswered}` : "") +
    ` | verdict=${!flagged ? "CLEAN" : confirmed === true ? "CONFIRMED" : confirmed === false ? "DISPUTED" : "FLAGGED (inconclusive)"}` +
    ` | action=${actionable ? "BAN" : "none"} (${reason})` +
    (provider ? ` | provider=${provider}` : "") +
    ` | ${result.detectors.map(d => `${d.name}:${d.flagged ? "HIT" : "clean"}`).join(" ")}`);
  await saveVpnCheck(ip, result);
  return { ...result, checkedAt: Date.now() };
}
// Human-readable full location from a stored geo object (webhook feed / /inspect).
function formatFullLocation(geo) {
  if (!geo) return null;
  const place = [geo.city, geo.region, geo.country].filter(Boolean).join(", ") + (geo.zip ? ` ${geo.zip}` : "");
  const bits  = [place.trim() || geo.country || null, geo.isp || null, geo.timezone ? `TZ ${geo.timezone}` : null].filter(Boolean);
  return bits.length ? bits.join("  -  ") : null;
}
/* Called from ipBans' onConfirm for every freshly-confirmed IP. The cached verdict is
   reused (no repeat API calls), and an IP that was already ACTED on is not re-banned
   within the TTL - recurrence is handled by the ipBans IP flag, not by re-running this.

   The gate deliberately keys on `actionedAt` (when a ban was actually issued), NOT on
   `checkedAt` (when the lookup happened). Keying on checkedAt meant that if the first
   player seen on a VPN IP was a master/exempt account, the lookup was cached, nothing
   was banned, and the IP was never flagged - so every OTHER player on that IP got a
   free pass for the whole TTL. */
const VPN_ACTION_TTL_MS = 7 * 24 * 60 * 60 * 1000;
async function checkVpnAndAlert(name, ip) {
  if (!ip || !vpnDetectionEnabled()) return null;
  const prev = loadVpnChecks()[ip];
  const alreadyActioned = !!prev?.actionedAt && (Date.now() - prev.actionedAt < VPN_ACTION_TTL_MS);
  const result = await checkVpn(ip).catch(() => null);
  if (!result) return null;
  // Clean, or an IP we've already banned someone over - report for the feed, don't re-ban.
  if (!result.flagged) return result;
  if (alreadyActioned) {
    logger.info("VPN", `${ip} already actioned <${Math.round((Date.now() - prev.actionedAt) / 3600e3)}h ago - not re-banning (${name})`);
    return result;
  }

  // Masters and explicitly-unbanned players are never auto-actioned (matches onAutoBan).
  // Only master names bypass ALL enforcement; staff/donators are NOT exempt from this.
  if (isMasterName(name) || isAutobanExempt(name)) {
    logger.info("VPN", `Skipped VPN auto-ban for exempt/master ${name}`);
    return result;
  }

  // One-line-per-detector breakdown, grouped by tier, for the alert embeds.
  const breakdown = (tier) => {
    const rows = (result.detectors ?? []).filter(d => d.tier === tier);
    if (!rows.length) return "*none configured*";
    return rows.map(d => `${d.flagged ? "🔴" : "🟢"} **${d.name}** - ${d.detail ?? (d.flagged ? "flagged" : "clean")}`).join("\n").slice(0, 1024);
  };
  const tally = `Screening **${result.screenHits ?? 0}/${result.screenAnswered ?? 0}** - Confirmation **${result.confirmHits ?? 0}/${result.confirmAnswered ?? 0}**`;

  /* Only an EXPLICITLY actionable verdict bans. A disputed flag, or a flag that never
     reached the required consensus, is logged and left alone - `actionable` is computed
     at detection time. `?? confirmed === true` keeps pre-upgrade cache entries safe:
     they have no `actionable` field, so only an outright confirmation acts on them. */
  const actionable = result.actionable ?? (result.confirmed === true);
  if (!actionable) {
    const disputed = result.confirmed === false;
    const embed = brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
      .setTitle(disputed ? "VPN Flag Disputed - No Action" : "VPN Flag Below Consensus - No Action")
      .setDescription(disputed
        ? `**${name}** connected from an IP the regular checks flagged, but every high-accuracy confirmation detector checked it and disagreed.\n${tally}`
        : `**${name}** connected from a flagged IP, but the flag never reached the confirmation threshold, so no ban was issued.\n${tally}\n\`${result.decision ?? "inconclusive"}\``)
      .addFields(
        { name: "VPN Provider", value: result.provider || "unknown", inline: true },
        { name: "ISP",         value: result.isp || "unknown",        inline: true },
        { name: "Regular checks",     value: breakdown(1), inline: false },
        { name: "Final confirmation", value: breakdown(2), inline: false },
      ).setFooter({ text: disputed ? "Likely false positive - not banned" : "Below the consensus threshold - not banned" }));
    await logAction(embed);
    return result;
  }

  const label = result.confirmAnswered
    ? `Confirmed VPN/proxy - ${result.confirmHits}/${result.confirmAnswered} final confirmation(s) agree`
    : `Confirmed by regular-check consensus - ${result.screenHits}/${result.screenAnswered} detectors agree (no confirmation available)`;
  let res;
  try { res = await banWithIp(name, "both", { permanent: true, ip }); }
  catch (err) { logger.error("VPN", `auto-ban FAILED for ${name}: ${err.message}`); res = { blacklist: { servers: 0 } }; }
  // Stamp the action so the TTL gate counts from the BAN, not from the lookup.
  try { await saveVpnCheck(ip, { ...result, actionedAt: Date.now(), actionedOn: name }); }
  catch (e) { logger.warn("VPN", `could not record the action stamp for ${ip}: ${e.message}`); }
  try { await upsertPermBan({ playerId: name, reason: "VPN/proxy detected", moderator: "VPN detection (auto)" }); } catch {}
  writeModLog({ action: "auto-vpnban", playerId: name, reason: `VPN/proxy detected (${label})`, by: "VPN detection (auto)" });
  logger.warn("VPN", `Auto-banned ${name} - ${label}, ip ${ip}`);
  const embed = clinical(new EmbedBuilder().setColor(CLIN.red)
    .setTitle("Auto-Ban - VPN/Proxy Detected")
    .setDescription(hero(`\`${name}\` was automatically banned: a VPN or proxy connection was detected.`))
    .addFields(
      { name: "Player", value: `\`${name}\``, inline: true },
      { name: "IP",      value: `\`${ip}\``,   inline: true },
      { name: "Status",  value: label,          inline: false },
      { name: "VPN Provider", value: result.provider || "unknown", inline: true },
      { name: "ISP",         value: result.isp || "unknown",        inline: true },
      { name: "Consensus",   value: tally,                          inline: true },
      { name: "Screening (tier 1)",    value: breakdown(1), inline: false },
      { name: "Confirmation (tier 2)", value: breakdown(2), inline: false },
      { name: "Enforced", value: `RCON Ban+Kick on ${res?.blacklist?.servers ?? 0}/${ACTIVE_SERVERS.length} server(s)`, inline: false },
    ), "Auto-ban - native RCON ban - all servers");
  await logBan(embed);
  postFeed(embed);
  return result;
}


  return { IPHUB_API_KEY, IPINFO_TOKEN, IPQS_API_KEY, PROXYCHECK_API_KEY, VPNAPI_KEY, IPAPIIS_KEY,
    VPN_SCREEN_MIN, VPN_CONFIRM_MIN, VPN_SCREEN_BAN_MIN, DETECTORS, TIER_ROLE, tierDetectors, vpnDetectionEnabled, runTier, probeDetectors,
    _backfillGeo, _doVpnCheck, _regionName, _vpnInFlight, checkVpn, checkVpnAndAlert, formatFullLocation, geoLookup, loadVpnChecks, proxycheckLookup, saveVpnCheck };
};
