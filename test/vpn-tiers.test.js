/* Tests for the two-tier VPN detection consensus.

   TIER 1 "regular check"       (every IP):        iphub, vpnapi, ipapi.is, proxycheck
   TIER 2 "final confirmation"  (flagged IPs only): ipqs

   The properties that matter:
     - a clean screen must NOT spend any tier-2 quota
     - tier-2 agreement CONFIRMS (ban); unanimous tier-2 clean DISPUTES (no ban)
     - a total screening outage must never be cached as "clean"
     - the reported provider is the consumer brand, not the underlying network */
const { test } = require("node:test");
const assert = require("node:assert");

const GEO = { ip: "1.2.3.4", city: "Atlanta", region: "Georgia", country: "US",
              loc: "33,-84", org: "AS1 Windstream", postal: "30303", timezone: "America/New_York" };

function makeVpn(env = {}) {
  for (const [k, v] of Object.entries({ IPHUB_API_KEY: "k", VPNAPI_KEY: "k", IPQS_API_KEY: "k", ...env })) {
    if (v === null) delete process.env[k]; else process.env[k] = v;
  }
  delete require.cache[require.resolve("../moderation/vpn.js")];
  const saved = {};
  const vpn = require("../moderation/vpn.js")({
    FILES: { VPN_CHECKS: "vpn" },
    logger: { warn() {}, info() {}, error() {} },
    safeRead: () => ({}), update: async (f, fb, fn) => { fn(saved); },
  });
  return { vpn, saved };
}

// Route fetches by URL fragment and record which upstreams were actually hit.
function mockFetch(routes) {
  const called = [];
  global.fetch = async (url) => {
    const u = String(url);
    for (const [frag, body] of Object.entries(routes)) {
      if (u.includes(frag)) {
        if (!u.includes("ipinfo")) called.push(frag);
        if (body instanceof Error) throw body;
        return { json: async () => body };
      }
    }
    throw new Error("unrouted " + u);
  };
  return called;
}

const CLEAN_SCREEN = {
  "iphub.info": { block: 0, isp: "Windstream" },
  "vpnapi.io":  { ip: "1.2.3.4", security: { vpn: false, proxy: false, tor: false, relay: false } },
  "ipapi.is":   { ip: "1.2.3.4", is_vpn: false, is_proxy: false, is_tor: false, is_datacenter: false, is_abuser: false },
  "proxycheck.io": { status: "ok", "1.2.3.4": { proxy: "no", provider: "Windstream" } },
};
const HIT_SCREEN = {
  "iphub.info": { block: 1, isp: "M247" },
  "vpnapi.io":  { ip: "1.2.3.4", security: { vpn: true, proxy: false, tor: false, relay: false }, network: { autonomous_system_organization: "M247" } },
  "ipapi.is":   { ip: "1.2.3.4", is_vpn: true, is_proxy: false, is_tor: false, is_datacenter: true, is_abuser: false, company: { name: "M247" } },
  "proxycheck.io": { status: "ok", "1.2.3.4": { proxy: "yes", type: "VPN", risk: 90, operator: { name: "NordVPN" } } },
};
const TIER2_HIT   = { "ipqualityscore": { success: true, vpn: true,  proxy: true,  tor: false, fraud_score: 88, ISP: "M247" } };
const TIER2_CLEAN = { "ipqualityscore": { success: true, vpn: false, proxy: false, tor: false, fraud_score: 10, ISP: "Comcast" } };

test("a clean screen spends no tier-2 quota", async () => {
  const { vpn } = makeVpn();
  const called = mockFetch({ "ipinfo.io": GEO, ...CLEAN_SCREEN, ...TIER2_HIT });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.flagged, false);
  assert.equal(r.screenHits, 0);
  assert.equal(r.screenAnswered, 4, "all four regular checks answered");
  assert.ok(called.includes("proxycheck.io"), "proxycheck is a regular check - must run on every IP");
  assert.ok(!called.includes("ipqualityscore"), "final confirmation must not be spent on a clean screen");
});

test("screen hit + tier-2 agreement = CONFIRMED, and reports the consumer brand", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO, ...HIT_SCREEN, ...TIER2_HIT });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.flagged, true);
  assert.equal(r.confirmed, true);
  assert.equal(r.screenHits, 4);
  assert.equal(r.confirmHits, 1);
  // "NordVPN" (proxycheck operator brand) must win over "M247" (the network).
  assert.equal(r.provider, "NordVPN");
  assert.equal(r.detectors.length, 5, "all five detectors recorded");
});

test("screen hit + unanimous tier-2 clean = DISPUTED (no ban)", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO, ...CLEAN_SCREEN, "iphub.info": { block: 1, isp: "Comcast" }, ...TIER2_CLEAN });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.flagged, true, "screening flagged it");
  assert.equal(r.confirmed, false, "tier 2 disputed it -> must not ban");
  assert.equal(r.confirmHits, 0);
  assert.equal(r.confirmAnswered, 1, "the confirmer answered");
});

test("a total screening outage is not cached as clean", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO,
    "iphub.info": new Error("503"), "vpnapi.io": new Error("timeout"),
    "ipapi.is": new Error("refused"), "proxycheck.io": new Error("down") });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r, null, "null = retry next connection, never a cached all-clear");
});

test("one screener failing still yields a verdict from the others", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO, ...HIT_SCREEN, "iphub.info": new Error("503"), ...TIER2_HIT });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.screenAnswered, 3, "the dead screener is not counted as clean");
  assert.equal(r.screenHits, 3);
  assert.equal(r.confirmed, true);
});

test("with no tier-2 configured, banning needs screening consensus (>=2)", async () => {
  // IPQS unset -> the final-confirmation tier is empty, so screening must carry it.
  const { vpn } = makeVpn({ IPQS_API_KEY: null });
  mockFetch({ "ipinfo.io": GEO, ...CLEAN_SCREEN,
    "iphub.info": { block: 1, isp: "M247" } });                                // exactly 1 hit
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.flagged, true);
  assert.notEqual(r.confirmed, true, "a lone screening hit must not ban when nothing could confirm it");
});

test("detection is a no-op when no detector is configured", async () => {
  const { vpn } = makeVpn({ IPHUB_API_KEY: null, VPNAPI_KEY: null, IPQS_API_KEY: null });
  // ipapi.is and proxycheck are keyless-capable, so the registry is never truly empty;
  // assert the helper reports enabled and the tier lists are still coherent.
  assert.equal(typeof vpn.vpnDetectionEnabled(), "boolean");
  assert.ok(vpn.tierDetectors(1).every(d => d.tier === 1));
  assert.ok(vpn.tierDetectors(2).every(d => d.tier === 2));
});

/* ---- regression tests for the end-to-end debug ---- */

// A full ctx for checkVpnAndAlert, so the BAN DECISION itself is asserted (not just
// the verdict object - the original bug was in the consumer, which the earlier tests
// never exercised).
function makeAlertable(env = {}, opts = {}) {
  for (const [k, v] of Object.entries({ IPHUB_API_KEY: "k", VPNAPI_KEY: "k", IPQS_API_KEY: "k", ...env })) {
    if (v === null) delete process.env[k]; else process.env[k] = v;
  }
  delete require.cache[require.resolve("../moderation/vpn.js")];
  const bans = [], store = {}, logged = [];
  const vpn = require("../moderation/vpn.js")({
    FILES: { VPN_CHECKS: "v" },
    logger: { warn() {}, info() {}, error() {}, debug() {} },
    safeRead: () => JSON.parse(JSON.stringify(store)),
    update: async (f, fb, fn) => { fn(store); },
    ACTIVE_SERVERS: ["s1"], CLIN: { red: 1 }, NV: { DEAD_GREY: 1 },
    EmbedBuilder: class { setColor() { return this; } setTitle(t) { this.t = t; return this; }
      setDescription() { return this; } addFields() { return this; } setFooter() { return this; } },
    brand: e => e, clinical: e => e, hero: s => s,
    banWithIp: async (n) => { bans.push(n); return { blacklist: { servers: 1 } }; },
    upsertPermBan: async () => {}, writeModLog: () => {},
    logAction: async (e) => { logged.push(e.t); }, logBan: async () => {}, postFeed: () => {},
    isMasterName: (n) => (opts.masters ?? []).includes(n),
    isAutobanExempt: () => false,
  });
  return { vpn, bans, store, logged };
}
const routeVpn = (hits) => async (url) => {
  const u = String(url);
  if (u.includes("ipinfo")) return { json: async () => ({ ip: "45.1.1.1", city: "AMS", country: "NL", loc: "52,4", org: "AS9009 M247" }) };
  if (u.includes("iphub"))      return { json: async () => ({ block: hits.iphub ? 1 : 0, isp: "M247" }) };
  if (u.includes("vpnapi"))     return { json: async () => ({ ip: "45.1.1.1", security: { vpn: !!hits.vpnapi, proxy: false, tor: false, relay: false } }) };
  if (u.includes("ipapi.is"))   return { json: async () => ({ ip: "45.1.1.1", is_vpn: !!hits.ipapi, is_proxy: false, is_tor: false, is_datacenter: true, is_abuser: false, company: { name: "M247" } }) };
  if (u.includes("proxycheck")) return { json: async () => ({ status: "ok", "45.1.1.1": { proxy: hits.proxycheck ? "yes" : "no", type: "VPN", risk: 95, operator: { name: "NordVPN" } } }) };
  if (u.includes("ipqualityscore")) {
    if (hits.ipqsDown) throw new Error("quota exceeded");
    return { json: async () => ({ success: true, vpn: !!hits.ipqs, proxy: false, tor: false, fraud_score: 90, ISP: "M247" }) };
  }
  throw new Error("unrouted " + u);
};

test("a lone regular-check hit with nothing to confirm it does NOT ban", async () => {
  // The original bug: confirmed===null fell through the `disputed` veto and banned,
  // which made VPN_SCREEN_BAN_MIN (default 2) dead code.
  const { vpn, bans, logged } = makeAlertable({ IPQS_API_KEY: null });
  global.fetch = routeVpn({ iphub: true });                       // 1 of 4 hits
  const r = await vpn._doVpnCheck("45.1.1.1");
  assert.equal(r.flagged, true);
  assert.equal(r.actionable, false, "below the consensus threshold");
  await vpn.checkVpnAndAlert("Innocent", "45.1.1.1");
  assert.deepEqual(bans, [], "must not ban an innocent on one screening hit");
  assert.ok(logged.some(t => /Below Consensus/.test(t)), "logged instead of banned");
});

test("regular-check consensus with nothing to confirm DOES ban", async () => {
  const { vpn, bans } = makeAlertable({ IPQS_API_KEY: null });
  global.fetch = routeVpn({ iphub: true, vpnapi: true, ipapi: true, proxycheck: true });
  const r = await vpn._doVpnCheck("45.1.1.1");
  assert.equal(r.actionable, true);
  await vpn.checkVpnAndAlert("RealVpn", "45.1.1.1");
  assert.deepEqual(bans, ["RealVpn"]);
});

test("a confirmed flag bans; a disputed flag never does", async () => {
  {
    const { vpn, bans } = makeAlertable();
    global.fetch = routeVpn({ iphub: true, vpnapi: true, ipqs: true });
    await vpn.checkVpnAndAlert("Vpn", "45.1.1.1");
    assert.deepEqual(bans, ["Vpn"], "confirmation agreed -> ban");
  }
  {
    const { vpn, bans, logged } = makeAlertable();
    global.fetch = routeVpn({ iphub: true, vpnapi: true, ipqs: false });   // confirmer clears it
    await vpn.checkVpnAndAlert("FalsePositive", "45.1.1.1");
    assert.deepEqual(bans, [], "confirmation disputed -> no ban");
    assert.ok(logged.some(t => /Disputed/.test(t)));
  }
});

test("an exempt player seen first does not give the IP a free pass", async () => {
  // Was: the gate keyed on checkedAt, so a master joining first cached the lookup and
  // every other player on that VPN IP was skipped for the whole TTL.
  const { vpn, bans, store } = makeAlertable({}, { masters: ["AdminGuy"] });
  global.fetch = routeVpn({ iphub: true, vpnapi: true, ipapi: true, proxycheck: true, ipqs: true });
  await vpn.checkVpnAndAlert("AdminGuy", "45.1.1.1");
  assert.deepEqual(bans, [], "the master is not banned");
  assert.ok(!store["45.1.1.1"]?.actionedAt, "no action stamp, so the gate stays open");
  await vpn.checkVpnAndAlert("Evader", "45.1.1.1");
  assert.deepEqual(bans, ["Evader"], "the next player on that IP is still caught");
  assert.ok(store["45.1.1.1"]?.actionedAt, "action stamped after the real ban");
  await vpn.checkVpnAndAlert("Evader2", "45.1.1.1");
  assert.deepEqual(bans, ["Evader"], "not re-banned inside the TTL - the ipBans IP flag covers recurrence");
});

test("private/unroutable addresses skip every detector and are cached", async () => {
  const { vpn } = makeAlertable();
  let calls = 0;
  global.fetch = async () => { calls++; return { json: async () => ({ bogon: true }) }; };
  for (const ip of ["192.168.1.50", "10.0.0.7", "127.0.0.1", "172.20.1.1", "169.254.5.5", "::1", "fe80::1", "0.0.0.0"]) {
    const r = await vpn._doVpnCheck(ip);
    assert.equal(r.local, true, `${ip} should short-circuit`);
    assert.equal(r.flagged, false);
    assert.equal(r.actionable, false);
  }
  assert.equal(calls, 0, "no API calls for unroutable addresses");
});

test("a pre-upgrade cache entry is only actioned on an outright confirmation", async () => {
  const { vpn, bans, store } = makeAlertable();
  // Legacy shape: no `detectors`, no `actionable`.
  store["45.1.1.1"] = { ip: "45.1.1.1", flagged: true, confirmed: null, checkedAt: Date.now() - 1000,
                        geo: { city: "AMS", isp: "M247" }, isp: "M247" };
  global.fetch = routeVpn({ iphub: true });
  await vpn.checkVpnAndAlert("LegacyUnconfirmed", "45.1.1.1");
  assert.deepEqual(bans, [], "a legacy unconfirmed entry must not ban");
});

test("a single-source flag never bans, even when it is the only detector available", async () => {
  /* ipapi.is genuinely reports Cloudflare's 1.1.1.1 as "vpn+abuser" - a real false
     positive. If only one regular check is reachable, auto-ban must stay OFF rather
     than trusting that one opinion (previously min(BAN_MIN, answered) degraded the
     threshold to 1 and would have banned). */
  const { vpn, bans } = makeAlertable({ IPHUB_API_KEY: null, VPNAPI_KEY: null, IPQS_API_KEY: null });
  global.fetch = async (url) => {
    const u = String(url);
    if (u.includes("ipinfo")) return { json: async () => ({ ip: "1.1.1.1", city: "X", country: "US", loc: "1,1", org: "AS13335 Cloudflare" }) };
    if (u.includes("ipapi.is")) return { json: async () => ({ ip: "1.1.1.1", is_vpn: true, is_abuser: true, is_proxy: false, is_tor: false, is_datacenter: true, company: { name: "Cloudflare" } }) };
    if (u.includes("proxycheck")) throw new Error("quota");        // only one screener answers
    throw new Error("unrouted " + u);
  };
  const r = await vpn._doVpnCheck("1.1.1.1");
  assert.equal(r.screenAnswered, 1);
  assert.equal(r.flagged, true);
  assert.equal(r.actionable, false, "one unconfirmed source must never ban");
  await vpn.checkVpnAndAlert("Victim", "1.1.1.1");
  assert.deepEqual(bans, []);
});

test("a pre-upgrade cache entry is re-screened once, then stays cached", async () => {
  /* The reported bug: `checkVpn` treated "has geo" as complete, so entries written
     before the multi-tier upgrade were returned forever - the new detectors never ran
     and the feed showed a bare "Clean" with no breakdown. Because the cache is keyed
     by IP, brand-new players on a previously-seen IP hit it too. */
  const { vpn, store } = makeAlertable();
  store["130.44.169.127"] = { ip: "130.44.169.127", flagged: false, confirmed: null, isp: "RCN",
    geo: { city: "Arlington", country: "United States", isp: "RCN" }, checkedAt: Date.now() - 86400000 };
  let calls = 0;
  global.fetch = async (url) => {
    calls++;
    const u = String(url);
    if (u.includes("ipinfo")) return { json: async () => ({ ip: "130.44.169.127", city: "Arlington", country: "US", loc: "42,-71", org: "AS6079 RCN" }) };
    if (u.includes("iphub")) return { json: async () => ({ block: 0, isp: "RCN" }) };
    if (u.includes("vpnapi")) return { json: async () => ({ ip: "130.44.169.127", security: { vpn: false, proxy: false, tor: false, relay: false } }) };
    if (u.includes("ipapi.is")) return { json: async () => ({ ip: "130.44.169.127", is_vpn: false, is_proxy: false, is_tor: false, is_datacenter: false, is_abuser: false }) };
    if (u.includes("proxycheck")) return { json: async () => ({ status: "ok", "130.44.169.127": { proxy: "no", provider: "RCN" } }) };
    throw new Error("unrouted " + u);
  };
  const first = await vpn.checkVpn("130.44.169.127");
  assert.ok(calls > 0, "the legacy entry must trigger a real re-screen");
  assert.equal(first.detectors.length, 4, "detector data is now populated");
  assert.equal(first.screenAnswered, 4);

  calls = 0;
  const second = await vpn.checkVpn("130.44.169.127");
  assert.equal(calls, 0, "healed entry must stay cached - no re-check loop");
  assert.equal(second.detectors.length, 4);
});

test("re-screening a legacy entry keeps its action stamp (no double ban)", async () => {
  const { vpn, store, bans } = makeAlertable();
  const actionedAt = Date.now() - 60_000;
  store["45.1.1.1"] = { ip: "45.1.1.1", flagged: true, confirmed: true, isp: "M247",
    geo: { city: "AMS", isp: "M247" }, checkedAt: actionedAt, actionedAt, actionedOn: "EarlierGuy" };
  global.fetch = routeVpn({ iphub: true, vpnapi: true, ipapi: true, proxycheck: true, ipqs: true });
  const r = await vpn.checkVpn("45.1.1.1");
  assert.equal(r.actionedAt, actionedAt, "the action stamp survives the re-screen");
  await vpn.checkVpnAndAlert("SomeoneElse", "45.1.1.1");
  assert.deepEqual(bans, [], "still inside the TTL - must not re-ban after healing");
});

/* ---- normalised intelligence record + staleness ---- */

test("the stored record carries the full normalised intelligence schema", async () => {
  const { vpn } = makeAlertable();
  global.fetch = async (url) => {
    const u = String(url);
    if (u.includes("ipinfo")) return { json: async () => ({ ip: "45.83.91.14", city: "Stockholm", region: "Stockholm", country: "SE", loc: "59,18", org: "AS9009 M247 Europe SRL", timezone: "Europe/Stockholm" }) };
    if (u.includes("iphub")) return { json: async () => ({ block: 1, isp: "M247", asn: 9009, countryName: "Sweden", countryCode: "SE" }) };
    if (u.includes("vpnapi")) return { json: async () => ({ security: { vpn: true, proxy: false, tor: false, relay: false }, network: { autonomous_system_number: "AS9009", autonomous_system_organization: "M247 Europe SRL" }, location: { country: "Sweden", country_code: "SE", region: "Stockholm", city: "Stockholm" } }) };
    if (u.includes("ipapi.is")) return { json: async () => ({ is_vpn: true, is_proxy: false, is_tor: false, is_datacenter: true, is_abuser: false, company: { name: "M247", type: "hosting" }, asn: { asn: 9009, org: "M247 Europe SRL" }, location: { country: "Sweden", country_code: "SE", state: "Stockholm", city: "Stockholm" } }) };
    if (u.includes("proxycheck")) return { json: async () => ({ status: "ok", "45.83.91.14": { asn: "AS9009", provider: "M247 Europe SRL", organisation: "M247 Ltd", country: "Sweden", isocode: "SE", region: "Stockholm", city: "Stockholm", proxy: "yes", type: "VPN", risk: 73, operator: { name: "NordVPN" } } }) };
    if (u.includes("ipqualityscore")) return { json: async () => ({ success: true, vpn: true, proxy: true, tor: false, fraud_score: 92, ISP: "M247", organization: "M247 Europe SRL", ASN: 9009, country_code: "SE", region: "Stockholm", city: "Stockholm", connection_type: "Data Center", mobile: false, host: "host.m247.com", recent_abuse: true }) };
    throw new Error("unrouted " + u);
  };
  const r = await vpn._doVpnCheck("45.83.91.14");
  for (const k of ["ip", "vpn", "proxy", "tor", "hosting", "datacenter", "residential", "mobile",
                   "threatScore", "asn", "organization", "provider", "country", "countryCode",
                   "region", "city", "lastChecked", "lookupSource", "schema"]) {
    assert.ok(k in r, `record is missing ${k}`);
  }
  assert.equal(r.vpn, true);
  assert.equal(r.hosting, true);
  assert.equal(r.datacenter, true);
  assert.equal(r.residential, false, "a hosting IP can never be residential");
  assert.equal(r.mobile, false, "an explicit provider 'no' must not read as unknown");
  assert.equal(r.threatScore, 92, "IPQS's calibrated score beats proxycheck's risk");
  assert.equal(r.organization, "M247 Europe SRL", "full ASN org, not IPHub's short label");
  assert.equal(r.provider, "NordVPN");
  assert.equal(r.asn, "AS9009");
  assert.equal(r.city, "Stockholm");
  assert.ok(/^\d{4}-\d{2}-\d{2}T/.test(r.lastChecked), "lastChecked is an ISO timestamp");
  assert.ok(r.lookupSource.includes("ipqs"), "lookupSource names the contributing providers");
});

test("IPQS's hostname string is never mistaken for a hosting flag", async () => {
  // `host` is the IP's hostname, truthy on most IPs - using it as a datacenter signal
  // would have marked nearly every player as hosting.
  const { vpn } = makeAlertable();
  global.fetch = async (url) => {
    const u = String(url);
    if (u.includes("ipinfo")) return { json: async () => ({ ip: "9.9.9.9", city: "Boston", country: "US", loc: "42,-71", org: "AS1 Comcast" }) };
    if (u.includes("iphub")) return { json: async () => ({ block: 1, isp: "Comcast" }) };
    if (u.includes("vpnapi")) return { json: async () => ({ security: { vpn: false, proxy: false, tor: false, relay: false } }) };
    if (u.includes("ipapi.is")) return { json: async () => ({ is_vpn: false, is_proxy: false, is_tor: false, is_datacenter: false, is_abuser: false, company: { name: "Comcast", type: "isp" } }) };
    if (u.includes("proxycheck")) return { json: async () => ({ status: "ok", "9.9.9.9": { proxy: "no", type: "Residential", risk: 0 } }) };
    if (u.includes("ipqualityscore")) return { json: async () => ({ success: true, vpn: false, proxy: false, tor: false, fraud_score: 5, connection_type: "Residential", mobile: false, host: "c-9-9-9-9.comcast.net" }) };
    throw new Error("unrouted " + u);
  };
  const r = await vpn._doVpnCheck("9.9.9.9");
  assert.equal(r.hosting, false, "a residential IP with a hostname must not be hosting");
  assert.equal(r.residential, true);
});

test("cache entries expire and are refreshed", () => {
  const { vpn } = makeAlertable();
  const geo = { city: "X" }, dets = [{ name: "iphub", tier: 1, flagged: false }], day = 86400000;
  const now = Date.now();
  assert.equal(vpn.isCompleteCheck({ geo, detectors: dets, schema: 2, checkedAt: now - day }), true, "fresh");
  assert.equal(vpn.isCompleteCheck({ geo, detectors: dets, schema: 2, checkedAt: now - 400 * day }), false, "stale");
  assert.equal(vpn.isCompleteCheck({ geo, detectors: dets, schema: 1, checkedAt: now }), false, "old schema");
  assert.equal(vpn.isCompleteCheck({ geo, schema: 2, checkedAt: now }), false, "no detector data");
  assert.equal(vpn.isCompleteCheck({ geo, local: true, checkedAt: now - 999 * day }), true, "private never expires");
});
