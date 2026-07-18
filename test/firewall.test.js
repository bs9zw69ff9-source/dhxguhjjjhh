const { test } = require("node:test");
const assert = require("node:assert");
const createFirewall = require("../moderation/firewall");

const noLog = { info() {}, warn() {}, error() {} };
const mk = (over = {}) => createFirewall({
  logger: noLog, loadBans: () => [],
  ipBans: { get blacklist() { return over.flagged || []; }, getConfirmedIPsForPlayer: () => over.confirmed || [] },
  masterIps: new Set(over.masters || ["86.166.107.200"]),
});

test("UFW_BLOCK off: every op is a safe no-op", async () => {
  delete process.env.UFW_BLOCK;
  const fw = mk();
  assert.equal(fw.UFW_BLOCK, false);
  assert.deepEqual(await fw.firewallBlockIps(["9.9.9.9"]), { blocked: 0, off: true });
  assert.deepEqual(await fw.firewallUnblockIps(["9.9.9.9"]), { unblocked: 0, off: true });
  assert.deepEqual(await fw.firewallStatus(), { off: true });
});

test("_IPV4_RE: octet-bounded validation", () => {
  const { _IPV4_RE } = mk();
  assert.ok(_IPV4_RE.test("1.2.3.4"));
  assert.ok(_IPV4_RE.test("255.255.255.255"));
  assert.ok(!_IPV4_RE.test("999.1.1.1"));
  assert.ok(!_IPV4_RE.test("1.2.3"));
  assert.ok(!_IPV4_RE.test("1.2.3.4.5"));
  assert.ok(!_IPV4_RE.test("a.b.c.d"));
});

test("master IPs are filtered from blocking even with UFW on", async () => {
  process.env.UFW_BLOCK = "1";
  const warns = [];
  const fw = createFirewall({
    logger: { info() {}, warn: (t, m) => warns.push(m), error() {} },
    loadBans: () => [], ipBans: { get blacklist() { return []; } },
    masterIps: new Set(["86.166.107.200"]),
  });
  // master-only list short-circuits to blocked:0 BEFORE any ufw call runs
  const r = await fw.firewallBlockIps(["86.166.107.200"]);
  assert.equal(r.blocked, 0);
  assert.ok(warns.some(m => /Refused to block protected master/.test(m)));
  delete process.env.UFW_BLOCK;
});

test("firewall exposes only the command-only surface (no auto-apply hooks)", () => {
  const fw = mk();
  // ufw is manual-only via /firewall now: the ban/resync/reconcile auto-appliers
  // and the embed field helper were removed.
  assert.equal(typeof fw.firewallBlockIps, "function");
  assert.equal(typeof fw.firewallUnblockIps, "function");
  assert.equal(typeof fw.firewallStatus, "function");
  assert.equal(fw.firewallResyncAll, undefined);
  assert.equal(fw.firewallReconcile, undefined);
  assert.equal(fw.firewallField, undefined);
});
