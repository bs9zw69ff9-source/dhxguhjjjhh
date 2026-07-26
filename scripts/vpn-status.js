#!/usr/bin/env node
/* Which VPN detectors are live right now, and (optionally) a real lookup.
   Run from the bot root:
     node scripts/vpn-status.js              -> show configured detectors
     node scripts/vpn-status.js 1.2.3.4      -> also run a real check on that IP
   Reads .env the same way the bot does, so it reflects the live configuration. */
require("dotenv").config();

const vpn = require("../moderation/vpn")({
  FILES: { VPN_CHECKS: "vpn_checks.json" },
  logger: { warn: (t, m) => console.log(`  ! ${m}`), info: () => {}, error: (t, m) => console.log(`  X ${m}`) },
  safeRead: () => ({}), update: async () => {},
});

const KEY_HINT = {
  iphub:      "IPHUB_API_KEY        https://iphub.info/pricing        (free 1,000/day)",
  vpnapi:     "VPNAPI_KEY           https://vpnapi.io/#pricing        (free 1,000/day)",
  "ipapi.is": "IPAPIIS_KEY          https://ipapi.is/                 (works with NO key, 1,000/day)",
  ipqs:       "IPQS_API_KEY         https://ipqualityscore.com/create-account  (free ~35/day)",
  proxycheck: "PROXYCHECK_API_KEY   https://proxycheck.io/register    (works with NO key, 100/day)",
};

console.log("\nVPN DETECTORS");
for (const tier of [1, 2]) {
  console.log(`\n  Tier ${tier} - ${tier === 1 ? "SCREENING (every new IP)" : "CONFIRMATION (only on a screen hit)"}`);
  for (const d of vpn.DETECTORS.filter(x => x.tier === tier)) {
    const on = d.enabled();
    console.log(`    [${on ? "ACTIVE " : "not set"}] ${d.name.padEnd(11)} ${on ? "" : "-> " + KEY_HINT[d.name]}`);
  }
}
const s = vpn.tierDetectors(1).length, c = vpn.tierDetectors(2).length;
console.log(`\n  Active: ${s} screening, ${c} confirmation`);
console.log(`  Thresholds: escalate at >=${vpn.VPN_SCREEN_MIN} screen hit(s), ban at >=${vpn.VPN_CONFIRM_MIN} confirmation hit(s),`);
console.log(`              ban on screening alone at >=${vpn.VPN_SCREEN_BAN_MIN} hit(s) when nothing can confirm`);

if (s === 0) console.log("\n  WARNING: no screening detector configured - VPN detection is effectively OFF.");
else if (c === 0) console.log("\n  NOTE: no confirmation detector configured - bans fall back to screening consensus.");

const ip = process.argv[2];
if (!ip) { console.log("\nPass an IP to run a live check, e.g.  node scripts/vpn-status.js 1.1.1.1\n"); process.exit(0); }

console.log(`\nLIVE CHECK: ${ip}`);
vpn._doVpnCheck(ip).then(r => {
  if (!r) return console.log("  no result (every detector failed, or no geolocation) - would retry on the next connection\n");
  const verdict = !r.flagged ? "CLEAN"
    : r.confirmed === true ? "CONFIRMED VPN/PROXY -> would auto-ban"
    : r.confirmed === false ? "DISPUTED -> logged only, NOT banned"
    : "FLAGGED but inconclusive";
  console.log(`  verdict:  ${verdict}`);
  console.log(`  screen:   ${r.screenHits}/${r.screenAnswered} hit    confirm: ${r.confirmHits}/${r.confirmAnswered} hit`);
  console.log(`  provider: ${r.provider || "unknown"}    isp: ${r.isp || "unknown"}`);
  for (const d of r.detectors) console.log(`    ${d.flagged ? "HIT  " : "clean"} ${d.name.padEnd(11)} ${d.detail ?? ""}`);
  console.log("");
}).catch(e => { console.log("  error:", e.message, "\n"); process.exit(1); });
