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

/* ---------------- VPN / proxy detection ----------------
   IPHub is the free baseline check, run on a given IP exactly ONCE, ever. Only when
   IPHub flags it (block==1: hosting/proxy/non-residential) do we spend an
   IPQualityScore lookup to cross-reference it - IPQS is more accurate but its free
   tier is far more limited than IPHub's, so it's reserved for the flagged subset
   instead of running on every connection. The result is cached per-IP permanently -
   once an IP is checked, it's never re-queried against either API again: a clean
   result means that IP is trusted for good, a confirmed VPN means it's already been
   acted on. The check is keyed on the IP itself, not the account or EOS id - a clean
   IP is clean no matter who connects from it next.
   A confirmed VPN/proxy auto-bans the connecting player (native RCON Ban+Kick, IP+EOS
   flagged same as any other ban). If IPHub flags but IPQS explicitly disputes it
   (checked, came back clean), that's treated as a likely false positive and only
   logged, not banned - the whole point of the cross-check. If IPQS isn't configured,
   an IPHub flag alone is enough to ban.
   Entirely optional - a no-op if IPHUB_API_KEY isn't set. */
const IPHUB_API_KEY  = process.env.IPHUB_API_KEY || "";
const IPQS_API_KEY   = process.env.IPQS_API_KEY  || "";
const IPINFO_TOKEN   = process.env.IPINFO_TOKEN  || "";   // optional - higher ipinfo.io free quota
// proxycheck.io names the actual VPN provider (e.g. "NordVPN"). Optional: works
// keyless (100/day), a free key raises it to 1000/day. Queried only on flagged IPs.
const PROXYCHECK_API_KEY = process.env.PROXYCHECK_API_KEY || "";
// Look up the VPN provider name for an IP. `operator.name` is the consumer brand
// (NordVPN, IVPN, ...); `provider` is the underlying network - prefer the brand.
async function proxycheckLookup(ip) {
  try {
    const key = PROXYCHECK_API_KEY ? `&key=${PROXYCHECK_API_KEY}` : "";
    const res = await fetch(`https://proxycheck.io/v2/${encodeURIComponent(ip)}?vpn=1&risk=1${key}`, { headers: { Accept: "application/json" } });
    const data = await res.json();
    if (!data || (data.status !== "ok" && data.status !== "warning")) return null;
    const rec = data[ip];
    if (!rec) return null;
    return rec.operator?.name || rec.provider || null;
  } catch (err) {
    const msg = PROXYCHECK_API_KEY ? String(err.message ?? err).split(PROXYCHECK_API_KEY).join("***") : String(err.message ?? err);
    logger.warn("VPN", `proxycheck lookup failed for ${ip}: ${msg}`);
    return null;
  }
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
  // Geolocation first - free/keyless, for every IP regardless of the VPN keys.
  const gwho = await geoLookup(ip);

  // VPN detection - optional, only when IPHUB_API_KEY is set.
  let iphub = null, iphubFailed = false, flagged = false, confirmed = null, ipqs = null, provider = null;
  if (IPHUB_API_KEY) {
    try {
      const res = await fetch(`https://v2.api.iphub.info/ip/${encodeURIComponent(ip)}`, { headers: { "X-Key": IPHUB_API_KEY } });
      iphub = await res.json();
    } catch (err) {
      logger.warn("VPN", `IPHub lookup failed for ${ip}: ${err.message}`);
      iphubFailed = true;
    }
    flagged = iphub?.block === 1;
    // On an IPHub-flagged IP, cross-check with IPQS and look up the provider name via
    // proxycheck.io - both reserved for the flagged subset to conserve their free tiers.
    if (flagged) {
      const [ipqsRes, providerRes] = await Promise.all([
        IPQS_API_KEY
          ? fetch(`https://www.ipqualityscore.com/api/json/ip/${IPQS_API_KEY}/${encodeURIComponent(ip)}?strictness=1`)
              .then(r => r.json())
              .catch(err => { logger.warn("VPN", `IPQS lookup failed for ${ip}: ${String(err.message ?? err).split(IPQS_API_KEY).join("***")}`); return null; })
          : Promise.resolve(null),
        proxycheckLookup(ip),
      ]);
      if (ipqsRes?.success) {
        ipqs = { vpn: !!ipqsRes.vpn, proxy: !!ipqsRes.proxy, tor: !!ipqsRes.tor, fraudScore: ipqsRes.fraud_score ?? null };
        confirmed = !!(ipqsRes.vpn || ipqsRes.proxy || ipqsRes.tor);
      }
      provider = providerRes;
    }
  }

  // Prefer the whois geo (city-level); fall back to IPHub's country when whois is down.
  const geo = gwho || (iphub ? {
    city: null, region: null, country: iphub.countryName || null, countryCode: iphub.countryCode || null,
    zip: null, isp: iphub.isp || null, org: null, asn: null, timezone: null, lat: null, lon: null,
  } : null);
  if (!geo) return null;                           // no location at all - don't cache, retry next time
  if (IPHUB_API_KEY && iphubFailed) return null;   // VPN enabled but IPHub down - retry to capture the flag

  const result = { ip, flagged, confirmed, iphubBlock: iphub?.block ?? null, isp: geo.isp || null, provider, ipqs, geo };
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
  if (!ip || !IPHUB_API_KEY) return null;
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

  const disputed = result.confirmed === false;   // IPHub flagged it, IPQS actively said clean
  if (disputed) {
    const embed = brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
      .setTitle("VPN Flag Disputed - No Action")
      .setDescription(`**${name}** connected from an IP IPHub flagged, but IPQS checked it and disagreed.`)
      .addFields(
        { name: "VPN Provider", value: result.provider || "unknown", inline: true },
        { name: "ISP",         value: result.isp || "unknown",        inline: true },
        { name: "IPHub block", value: String(result.iphubBlock ?? "?"), inline: true },
        { name: "IPQS", value: `vpn:${result.ipqs.vpn} - proxy:${result.ipqs.proxy} - tor:${result.ipqs.tor} - fraud:${result.ipqs.fraudScore}`, inline: true },
      ).setFooter({ text: "Likely false positive - not banned" }));
    await logAction(embed);
    return result;
  }

  const label = result.confirmed === true ? "Confirmed VPN/proxy (IPHub + IPQS agree)" : "Flagged by IPHub (IPQS not configured to cross-check)";
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
      { name: "IPHub block", value: String(result.iphubBlock ?? "?"), inline: true },
      ...(result.ipqs ? [{ name: "IPQS", value: `vpn:${result.ipqs.vpn} - proxy:${result.ipqs.proxy} - tor:${result.ipqs.tor} - fraud:${result.ipqs.fraudScore}`, inline: true }] : []),
      { name: "Enforced", value: `RCON Ban+Kick on ${res?.blacklist?.servers ?? 0}/${ACTIVE_SERVERS.length} server(s)`, inline: false },
    ), "Auto-ban - native RCON ban - all servers");
  await logBan(embed);
  postFeed(embed);
  return result;
}


  return { IPHUB_API_KEY, IPINFO_TOKEN, IPQS_API_KEY, PROXYCHECK_API_KEY, _backfillGeo, _doVpnCheck, _regionName, _vpnInFlight, checkVpn, checkVpnAndAlert, formatFullLocation, geoLookup, loadVpnChecks, proxycheckLookup, saveVpnCheck };
};
