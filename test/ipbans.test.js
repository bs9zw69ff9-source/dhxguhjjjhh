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
