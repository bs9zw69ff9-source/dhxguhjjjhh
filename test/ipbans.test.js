"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");

// ipBans.js requires only `fs` + `path` (no discord.js, no config validation, no
// network), so it can be required directly. At load it creates a handful of
// gitignored ip_*.json / kd.json / kill_log.json files next to itself — those are
// runtime state, not committed. We clear all flags before and after so the
// assertions don't depend on (or leave behind) real state.
const ipBans = require("../ipBans");

test.before(() => ipBans.clearFlags());
test.after(() => ipBans.clearFlags());

test("getRecord returns null for an unknown player", () => {
  assert.equal(ipBans.getRecord("nobody-here-xyz"), null);
});

test("getKD defaults to a zeroed record for an unseen name", () => {
  assert.deepEqual(ipBans.getKD("unseen-player"), { name: "unseen-player", kills: 0, deaths: 0 });
});

test("getKills returns an empty list for an unseen killer", () => {
  assert.deepEqual(ipBans.getKills("unseen-player"), []);
});

test("clearKD returns a count and empties K/D + kill tallies", () => {
  const n = ipBans.clearKD();
  assert.equal(typeof n, "number");
  assert.ok(n >= 0);
  assert.deepEqual(ipBans.topKD(), []);
  assert.deepEqual(ipBans.getKD("anyone"), { name: "anyone", kills: 0, deaths: 0 });
});

test("flagTarget treats a valid IPv4 as an IP flag", () => {
  const r = ipBans.flagTarget("1.2.3.4");
  assert.equal(r.kind, "IP");
  assert.equal(r.value, "1.2.3.4");
  assert.ok(ipBans.getBlacklist().ips.includes("1.2.3.4"));
});

test("flagTarget rejects an out-of-range octet as NOT an IP (octet validation)", () => {
  // Before the IPV4_RE fix this matched the loose /^(\d{1,3}\.){3}\d{1,3}$/ and was
  // wrongly treated as an IP. It must now fall through to the username branch.
  const r = ipBans.flagTarget("999.1.1.1");
  assert.equal(r.kind, "username");
  assert.ok(!ipBans.getBlacklist().ips.includes("999.1.1.1"));
});

test("clearFlags empties every flag category and reports the count", () => {
  ipBans.flagTarget("5.6.7.8");
  const n = ipBans.clearFlags();
  assert.ok(typeof n === "number" && n >= 1);
  const bl = ipBans.getBlacklist();
  assert.deepEqual(bl, { ips: [], names: [], ids: [] });
});

test("untracked list add/remove round-trips (case-insensitive)", () => {
  ipBans.addUntracked("SomeCourier");
  assert.ok(ipBans.getUntracked().includes("somecourier"));
  assert.equal(ipBans.removeUntracked("somecourier"), true);
  assert.ok(!ipBans.getUntracked().includes("somecourier"));
});

// Two accounts that shared a CONFIRMED IP (same-line disconnect pairing). This is
// exactly the state the connection-feed webhook renders as "Possible Alts".
function seedAltPair() {
  ipBans.clearAll();
  const now = Date.now();
  const id1 = "0002aaaaaaaaaaaaaaaaaaaaaaaaaaa1";
  const id2 = "0002bbbbbbbbbbbbbbbbbbbbbbbbbbb2";
  ipBans.registry[id1] = { name: "MainAcct", ips: ["1.2.3.4"], cips: ["1.2.3.4"], firstSeen: now, lastSeen: now, logins: 3, recent: [] };
  ipBans.registry[id2] = { name: "AltAcct",  ips: ["1.2.3.4"], cips: ["1.2.3.4"], firstSeen: now, lastSeen: now, logins: 1, recent: [] };
  return { id1, id2 };
}

test("known alts surface in getRecord (the webhook feed's Possible Alts)", () => {
  seedAltPair();
  const rec = ipBans.getRecord("MainAcct");
  assert.deepEqual(rec.alts, ["AltAcct"]);
  assert.deepEqual(ipBans.getAltNamesOf("MainAcct"), ["AltAcct"]);
  // alt detection is symmetric
  assert.deepEqual(ipBans.getRecord("AltAcct").alts, ["MainAcct"]);
  ipBans.clearAll();
});

test("a permanent ban flags the EOS id, and it re-enforces on join/sweep", () => {
  const { id1 } = seedAltPair();
  ipBans.blacklistPlayer("MainAcct", { flagId: true });   // /permban path
  assert.ok(ipBans.getBlacklist().ids.includes(id1), "EOS id flagged");
  // getRecord().flagged is what the join check (line 647) and the online sweep
  // (bans.js: getRecord(nm).flagged) both read to re-enforce a name-changed ban.
  assert.equal(ipBans.getRecord("MainAcct").flagged, true);
  // the alt sharing the confirmed IP is caught too
  assert.equal(ipBans.getRecord("AltAcct").flagged, true);
  ipBans.clearAll();
});

test("a temp ban flags IPs but NOT the EOS id (never brands the account forever)", () => {
  const now = Date.now();
  ipBans.clearAll();
  const id = "0002cccccccccccccccccccccccccccc3";
  ipBans.registry[id] = { name: "TempGuy", ips: ["9.9.9.9"], cips: ["9.9.9.9"], firstSeen: now, lastSeen: now, logins: 1, recent: [] };
  ipBans.blacklistPlayer("TempGuy", { flagId: false });   // /tempban path
  assert.ok(!ipBans.getBlacklist().ids.includes(id), "temp ban must not flag the EOS id");
  assert.ok(ipBans.getBlacklist().ips.includes("9.9.9.9"), "but it does flag the confirmed IP");
  ipBans.clearAll();
});
