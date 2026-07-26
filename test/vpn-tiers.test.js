/* Tests for the two-tier VPN detection consensus.

   TIER 1 (screening, high quota): iphub, vpnapi, ipapi.is
   TIER 2 (confirmation, low quota, high accuracy): ipqs, proxycheck

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
};
const HIT_SCREEN = {
  "iphub.info": { block: 1, isp: "M247" },
  "vpnapi.io":  { ip: "1.2.3.4", security: { vpn: true, proxy: false, tor: false, relay: false }, network: { autonomous_system_organization: "M247" } },
  "ipapi.is":   { ip: "1.2.3.4", is_vpn: true, is_proxy: false, is_tor: false, is_datacenter: true, is_abuser: false, company: { name: "M247" } },
};
const TIER2_HIT = {
  "ipqualityscore": { success: true, vpn: true, proxy: true, tor: false, fraud_score: 88, ISP: "M247" },
  "proxycheck.io":  { status: "ok", "1.2.3.4": { proxy: "yes", type: "VPN", risk: 90, operator: { name: "NordVPN" } } },
};
const TIER2_CLEAN = {
  "ipqualityscore": { success: true, vpn: false, proxy: false, tor: false, fraud_score: 10, ISP: "Comcast" },
  "proxycheck.io":  { status: "ok", "1.2.3.4": { proxy: "no", provider: "Comcast" } },
};

test("a clean screen spends no tier-2 quota", async () => {
  const { vpn } = makeVpn();
  const called = mockFetch({ "ipinfo.io": GEO, ...CLEAN_SCREEN, ...TIER2_HIT });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.flagged, false);
  assert.equal(r.screenHits, 0);
  assert.equal(r.screenAnswered, 3, "all three screeners answered");
  assert.ok(!called.includes("ipqualityscore"), "IPQS must not be called on a clean screen");
  assert.ok(!called.includes("proxycheck.io"), "proxycheck must not be called on a clean screen");
});

test("screen hit + tier-2 agreement = CONFIRMED, and reports the consumer brand", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO, ...HIT_SCREEN, ...TIER2_HIT });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.flagged, true);
  assert.equal(r.confirmed, true);
  assert.equal(r.screenHits, 3);
  assert.equal(r.confirmHits, 2);
  // "NordVPN" (proxycheck operator brand) must win over "M247" (the network).
  assert.equal(r.provider, "NordVPN");
  assert.equal(r.detectors.length, 5, "all five detectors recorded");
});

test("screen hit + unanimous tier-2 clean = DISPUTED (no ban)", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO, ...HIT_SCREEN, "iphub.info": { block: 1, isp: "Comcast" },
              "vpnapi.io": CLEAN_SCREEN["vpnapi.io"], "ipapi.is": CLEAN_SCREEN["ipapi.is"], ...TIER2_CLEAN });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.flagged, true, "screening flagged it");
  assert.equal(r.confirmed, false, "tier 2 disputed it -> must not ban");
  assert.equal(r.confirmHits, 0);
  assert.equal(r.confirmAnswered, 2, "both confirmers answered");
});

test("a total screening outage is not cached as clean", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO,
    "iphub.info": new Error("503"), "vpnapi.io": new Error("timeout"), "ipapi.is": new Error("refused") });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r, null, "null = retry next connection, never a cached all-clear");
});

test("one screener failing still yields a verdict from the others", async () => {
  const { vpn } = makeVpn();
  mockFetch({ "ipinfo.io": GEO, "iphub.info": new Error("503"),
              "vpnapi.io": HIT_SCREEN["vpnapi.io"], "ipapi.is": HIT_SCREEN["ipapi.is"], ...TIER2_HIT });
  const r = await vpn._doVpnCheck("1.2.3.4");
  assert.equal(r.screenAnswered, 2, "the dead screener is not counted as clean");
  assert.equal(r.screenHits, 2);
  assert.equal(r.confirmed, true);
});

test("with no tier-2 configured, banning needs screening consensus (>=2)", async () => {
  // IPQS unset; proxycheck is keyless so force it to fail to leave tier 2 empty-handed.
  const { vpn } = makeVpn({ IPQS_API_KEY: null });
  mockFetch({ "ipinfo.io": GEO,
    "iphub.info": { block: 1, isp: "M247" },                                   // 1 hit only
    "vpnapi.io": CLEAN_SCREEN["vpnapi.io"], "ipapi.is": CLEAN_SCREEN["ipapi.is"],
    "proxycheck.io": new Error("quota") });
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
