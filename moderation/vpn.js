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

     TIER 1 - SCREENING (high daily quotas, run on every not-yet-checked IP)
       • IPHub      1,000/day   block==1 = hosting/proxy/non-residential
       • vpnapi.io  1,000/day   security.{vpn,proxy,tor,relay}
       • ipapi.is   1,000/day   is_vpn / is_proxy / is_tor  (works keyless)

     TIER 2 - CONFIRMATION (small quotas, high accuracy, ONLY on a screening hit)
       • IPQualityScore  ~35/day   vpn/proxy/tor + fraud score
       • proxycheck.io   100/day   proxy=yes + risk + the VPN brand name

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
  { name: "proxycheck", tier: 2, enabled: () => true, async run(ip) {    // keyless-capable
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
async function _doVpnCheck(ip) {
  // Geolocation first - free/keyless, for every IP regardless of the detector keys.
  const gwho = await geoLookup(ip);
  const enabled = vpnDetectionEnabled();

  let flagged = false, confirmed = null, screen = null, conf = null;
  if (enabled) {
    /* TIER 1 - screening. All high-quota detectors in parallel. */
    screen = await runTier(1, ip);
    if (screen.answered === 0) {
      logger.warn("VPN", `every screening detector failed for ${ip} - not caching, will retry on the next connection`);
      return null;                                   // an outage must never be cached as "clean"
    }
    flagged = screen.hits >= VPN_SCREEN_MIN;

    /* TIER 2 - confirmation. Only spent on an IP the screen already flagged. */
    if (flagged) {
      conf = await runTier(2, ip);
      if (conf.answered > 0) {
        confirmed = conf.hits >= VPN_CONFIRM_MIN;     // true = agreed, false = disputed
      } else if (tierDetectors(2).length) {
        confirmed = null;                             // configured but all failed - inconclusive
        logger.warn("VPN", `every confirmation detector failed for ${ip} - falling back to screen consensus (${screen.hits} hit(s))`);
      }
      // No tier-2 detectors configured at all: fall back to screen consensus.
      if (confirmed === null && !tierDetectors(2).length) {
        confirmed = screen.hits >= Math.min(VPN_SCREEN_BAN_MIN, screen.answered) ? true : null;
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
    ip, flagged, confirmed,
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
// Called from ipBans' onConfirm for every freshly-confirmed IP. The cached verdict
// is reused (no repeat API calls), but the ACTION gate re-arms after a TTL - if the
// first check happened while an exempt player was on the IP (so nothing was actioned),
// the next player from that IP can still be actioned once the entry ages out.
const VPN_ACTION_TTL_MS = 7 * 24 * 60 * 60 * 1000;
async function checkVpnAndAlert(name, ip) {
  if (!ip || !vpnDetectionEnabled()) return null;
  const prev = loadVpnChecks()[ip];
  const alreadyChecked = !!prev && (Date.now() - (prev.checkedAt || 0) < VPN_ACTION_TTL_MS);
  const result = await checkVpn(ip).catch(() => null);
  if (!result) return null;
  // Clean, or an IP we've already acted on - return the verdict for the feed, but don't ban.
  if (!result.flagged || alreadyChecked) return result;

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

  const disputed = result.confirmed === false;   // screening flagged it, confirmation tier said clean
  if (disputed) {
    const embed = brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
      .setTitle("VPN Flag Disputed - No Action")
      .setDescription(`**${name}** connected from an IP the screening tier flagged, but every high-accuracy confirmation detector checked it and disagreed.\n${tally}`)
      .addFields(
        { name: "VPN Provider", value: result.provider || "unknown", inline: true },
        { name: "ISP",         value: result.isp || "unknown",        inline: true },
        { name: "Screening (tier 1)",    value: breakdown(1), inline: false },
        { name: "Confirmation (tier 2)", value: breakdown(2), inline: false },
      ).setFooter({ text: "Likely false positive - not banned" }));
    await logAction(embed);
    return result;
  }

  const label = result.confirmed === true
    ? (result.confirmAnswered ? `Confirmed VPN/proxy - ${result.confirmHits}/${result.confirmAnswered} confirmation detector(s) agree`
                              : `Confirmed by screening consensus - ${result.screenHits}/${result.screenAnswered} detectors agree`)
    : `Flagged by ${result.screenHits}/${result.screenAnswered} screening detector(s) (confirmation inconclusive)`;
  let res;
  try { res = await banWithIp(name, "both", { permanent: true, ip }); }
  catch (err) { logger.warn("VPN", `auto-ban failed for ${name}: ${err.message}`); res = { blacklist: { servers: 0 } }; }
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
    VPN_SCREEN_MIN, VPN_CONFIRM_MIN, VPN_SCREEN_BAN_MIN, DETECTORS, tierDetectors, vpnDetectionEnabled, runTier,
    _backfillGeo, _doVpnCheck, _regionName, _vpnInFlight, checkVpn, checkVpnAndAlert, formatFullLocation, geoLookup, loadVpnChecks, proxycheckLookup, saveVpnCheck };
};
