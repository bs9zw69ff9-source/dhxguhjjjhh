/* Ban-evasion correlation. Pure module - no Discord, filesystem or network.
   The property that matters most: a weak signal (shared ASN) must never accuse a
   player on its own, while a definitive one (same EOS account) always reports. */
const { test } = require("node:test");
const assert = require("node:assert");
const { findEvasion, banNetworkSnapshot, MATCH_THRESHOLD } = require("../moderation/evasion");

const ban = (over = {}) => ({
  playerId: "BannedGuy", reason: "Hard R", moderator: "Admin#1", permanent: true,
  network: { eosIds: ["0002aaa"], ips: ["45.83.91.14"], names: ["BannedGuy"],
             asn: "AS9009", organization: "M247", provider: "NordVPN", vpn: true, hosting: true },
  ...over,
});

test("the same EOS account under a new name is a certain match", () => {
  const r = findEvasion({ name: "TotallyNewGuy", eosId: "0002aaa", ip: "8.8.8.8" }, [ban()]);
  assert.equal(r.evasion, true);
  assert.equal(r.certain, true);
  assert.equal(r.matches[0].playerId, "BannedGuy");
  assert.equal(r.matches[0].reasons[0].kind, "eos");
});

test("connecting from a banned player's IP is reported", () => {
  const r = findEvasion({ name: "Fresh", eosId: "0002zzz", ip: "45.83.91.14" }, [ban()]);
  assert.equal(r.evasion, true);
  assert.ok(r.matches[0].reasons.some(x => x.kind === "ip"));
  assert.ok(r.score >= MATCH_THRESHOLD);
});

test("a shared ASN ALONE never accuses anybody", () => {
  // Whole consumer ISPs share one ASN - matching on it alone would flag neighbours.
  const r = findEvasion(
    { name: "Innocent", eosId: "0002new", ip: "1.2.3.4", asn: "AS9009", vpn: false, hosting: false },
    [ban({ network: { ...ban().network, ips: [], names: [], eosIds: [], vpn: false, hosting: false } })]);
  assert.equal(r.evasion, false, "a lone ASN match must not report");
});

test("sharing a VPN brand and its network is NOT enough to accuse on its own", () => {
  /* Millions of people use NordVPN and exit through M247. Provider (25) + anonymising
     ASN (20) = 45, deliberately under the 50 threshold, so two unrelated subscribers
     are never reported as each other's alt. */
  const r = findEvasion(
    { name: "Sneaky", eosId: "0002new", ip: "1.2.3.4", asn: "AS9009", provider: "NordVPN", vpn: true },
    [ban({ network: { ...ban().network, ips: [], names: [], eosIds: [] } })]);
  assert.equal(r.evasion, false, "weak network signals must not accuse alone");
});

test("weak network signals DO add supporting context to a real match", () => {
  // Same person: their IP is on record AND they are back on the same VPN brand.
  const r = findEvasion(
    { name: "Sneaky", eosId: "0002new", ip: "45.83.91.14", asn: "AS9009", provider: "NordVPN", vpn: true },
    [ban()]);
  assert.equal(r.evasion, true);
  const kinds = r.matches[0].reasons.map(x => x.kind);
  assert.ok(kinds.includes("ip"), "the IP hit is what triggers it");
  assert.ok(kinds.includes("provider"), "the VPN brand is reported as supporting context");
  assert.ok(kinds.includes("asn+vpn"), "so is the shared anonymising network");
  assert.ok(r.matches[0].score > 70, "context raises confidence above the bare IP hit");
});

test("a known shared-IP alias of a banned player is caught", () => {
  const r = findEvasion(
    { name: "BrandNew", eosId: "0002new", ip: "9.9.9.9", altNames: ["BannedGuy"] }, [ban()]);
  assert.equal(r.evasion, true);
  assert.ok(r.matches[0].reasons.some(x => x.kind === "username"));
});

test("registry flags report even with no matching ban record", () => {
  const r = findEvasion({ name: "Ghost", eosId: "0002aaa", ip: "1.1.1.1" }, [], {
    flaggedIds: new Set(["0002aaa"]),
  });
  assert.equal(r.evasion, true);
  assert.equal(r.certain, true, "a flagged EOS id is definitive");
  assert.equal(r.flags[0].kind, "eos");
});

test("an unrelated player matches nothing", () => {
  const r = findEvasion({ name: "Normal", eosId: "0002xyz", ip: "20.20.20.20", asn: "AS7922" }, [ban()]);
  assert.equal(r.evasion, false);
  assert.deepEqual(r.matches, []);
});

test("corrupt ban rows are skipped, not crashed on", () => {
  const rows = [null, "nope", { reason: "no playerId" }, { playerId: 42 }, ban()];
  const r = findEvasion({ name: "BannedGuy", eosId: "0002aaa", ip: "45.83.91.14" }, rows);
  assert.equal(r.evasion, true);
  assert.equal(r.matches.length, 1, "only the one valid row matched");
});

test("matches are ranked strongest first", () => {
  // Reportable but weaker: a username hit (50) with no EOS/IP evidence.
  const weak = ban({ playerId: "X", network: { names: ["X"], ips: [], eosIds: [] } });
  const strong = ban({ playerId: "StrongMatch" });
  const r = findEvasion({ name: "X", eosId: "0002aaa", ip: "45.83.91.14", asn: "AS9009", provider: "NordVPN", vpn: true },
                        [weak, strong]);
  assert.equal(r.matches[0].playerId, "StrongMatch", "the definitive EOS match ranks first");
  assert.equal(r.matches.length, 2, "both reportable matches are returned");
  assert.ok(r.matches[0].score > r.matches[1].score);
});

test("banNetworkSnapshot captures the network intel to store with a ban", () => {
  const snap = banNetworkSnapshot({
    eosIds: ["0002aaa", "0002aaa"], ips: ["1.1.1.1", "2.2.2.2"], names: ["Guy"],
    vpnRecord: { asn: "AS9009", organization: "M247", provider: "NordVPN", vpn: true,
                 proxy: true, tor: false, hosting: true, threatScore: 92,
                 country: "Sweden", lastChecked: "2026-07-27T00:00:00.000Z" },
  });
  assert.deepEqual(snap.eosIds, ["0002aaa"], "de-duplicated");
  assert.deepEqual(snap.ips, ["1.1.1.1", "2.2.2.2"]);
  assert.equal(snap.provider, "NordVPN");
  assert.equal(snap.asn, "AS9009");
  assert.equal(snap.threatScore, 92);
  assert.equal(snap.detectedAt, "2026-07-27T00:00:00.000Z");
});

test("a snapshot with no VPN data still produces a usable record", () => {
  const snap = banNetworkSnapshot({ ips: ["1.1.1.1"] });
  assert.equal(snap.asn, null);
  assert.equal(snap.provider, null);
  assert.deepEqual(snap.ips, ["1.1.1.1"]);
  assert.ok(snap.detectedAt, "always stamped");
});
