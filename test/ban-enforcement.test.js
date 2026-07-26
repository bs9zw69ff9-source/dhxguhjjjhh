/* Regression tests for the ban-enforcement layer (moderation/bans.js).
   These cover the bugs found in the ban-system audit:
     1. the sweep enforced against the DISPLAY name, which sanitizeId mangles when it
        contains a space - so a spaced-name player was effectively unbannable. It must
        use the UniqueId RefreshList reports (what Pavlov's Ban/Kick actually take).
     2. a player online on several servers was enforced once PER SERVER, and each
        enforcement already hits every server (N^2 RCON connections).
     3. every ban/unban must emit one structured audit line carrying the identifiers,
        server, flags and success/failure. */
const { test } = require("node:test");
const assert = require("node:assert");

function makeBans(over = {}) {
  const rcon = [], logs = [];
  const ctx = {
    ACTIVE_SERVERS: ["server1", "server2"],
    FILES: { TEMPBAN: "t", BAN_RECONCILE_STATE: "s" },
    _sameId: (a, b) => String(a).toLowerCase() === String(b).toLowerCase(),
    blacklistAdd: () => {}, blacklistRemove: (n) => ({ name: n, removed: 1 }),
    getOnlinePlayers: async () => [{ name: "Butter Life", id: "ButterLife_EOS" }],
    ipBans: {
      getRecord: () => ({ id: "ButterLife_EOS", ids: ["ButterLife_EOS"], cips: ["5.5.5.5"], flagged: true }),
      blacklistPlayer: () => ({ ids: ["ButterLife_EOS"], ips: ["5.5.5.5"], alts: ["AltGuy"], field: null }),
      unblacklistPlayer: () => ({ cleared: { ips: 1, names: 0, ids: 1 } }),
    },
    isAutobanExempt: () => false, isMasterName: () => false, loadBans: () => [],
    logger: {
      info: (t, m) => logs.push({ lvl: "info", t, m }),
      warn: (t, m) => logs.push({ lvl: "warn", t, m }),
      error: (t, m) => logs.push({ lvl: "error", t, m }),
    },
    preserveBalanceAcrossKick: () => {}, removeAutobanExempt: async () => {},
    safeRead: () => ({}), safeWrite: () => {},
    sanitizeBanName: (s) => String(s).trim(),
    sanitizeId: (s) => String(s ?? "").replace(/[^a-zA-Z0-9_\-.]/g, "").slice(0, 64),
    sendRcon: async (cmd, srv) => { rcon.push(`${srv}: ${cmd}`); return "ok"; },
    update: async () => {},
    ...over,
  };
  return { bans: require("../moderation/bans.js")(ctx), rcon, logs };
}
const audit = (logs) => logs.filter(l => l.t === "BanAudit").map(l => l.m).join("\n");

test("the sweep enforces against the UniqueId, not the space-mangled display name", async () => {
  const { bans, rcon } = makeBans();
  await bans.enforceBansSweep();
  assert.ok(rcon.length > 0, "something was enforced");
  // Every command must target the UniqueId; the mangled display name must never appear.
  for (const c of rcon) assert.match(c, /ButterLife_EOS/);
  assert.ok(!rcon.some(c => /\bBan ButterLife\b(?!_)/.test(c)), "must not target the mangled display name");
});

test("a player on several servers is enforced once per sweep, not once per server", async () => {
  const { bans, rcon } = makeBans();
  await bans.enforceBansSweep();
  // 2 verbs (Ban, Kick) x 2 servers = 4. Enforcing per-server would double it to 8.
  assert.equal(rcon.length, 4, `expected 4 RCON calls, got ${rcon.length}`);
});

test("hardEnforce reports a target it had to rewrite, and an all-servers failure", async () => {
  {
    const { bans, logs } = makeBans();
    await bans.hardEnforce("Butter Life");            // no uniqueId -> name gets mangled
    assert.ok(logs.some(l => /TARGET REWRITTEN/.test(l.m)), "rewrite is reported");
  }
  {
    const { bans, logs } = makeBans({ sendRcon: async () => { throw new Error("connection refused"); } });
    const r = await bans.hardEnforce("Tony", { uniqueId: "Tony" });
    assert.equal(r.servers, 0);
    assert.ok(logs.some(l => l.lvl === "error" && /ENFORCE FAILED/.test(l.m)), "total failure is an error");
  }
  {
    const { bans, logs } = makeBans();
    const r = await bans.hardEnforce("!!!", { uniqueId: "!!!" });   // sanitizes to empty
    assert.equal(r.servers, 0);
    assert.ok(logs.some(l => l.lvl === "error" && /no usable RCON target/.test(l.m)));
  }
});

test("a ban emits one audit line with every identifier and the enforcement result", async () => {
  const { bans, logs } = makeBans();
  await bans.banWithIp("Butter Life", "both", { permanent: true, ip: "5.5.5.5" });
  const line = audit(logs);
  for (const part of ["permanent ban", 'player="Butter Life"', "eos=ButterLife_EOS", "ip=5.5.5.5",
                      "server=both", "alts=AltGuy", "rcon=2/2", "result=ENFORCED"]) {
    assert.ok(line.includes(part), `audit line missing ${part}: ${line}`);
  }
});

test("a temp ban records that the EOS id was deliberately not flagged", async () => {
  const { bans, logs } = makeBans();
  await bans.banWithIp("Butter Life", "both", {});   // no permanent flag
  assert.match(audit(logs), /temp ban - EOS id deliberately not flagged/);
});

test("a ban whose RCON is refused is logged as NOT ENFORCED, not silently dropped", async () => {
  const { bans, logs } = makeBans({ sendRcon: async () => { throw new Error("down"); } });
  await bans.banWithIp("Butter Life", "both", { permanent: true });
  assert.match(audit(logs), /result=NOT ENFORCED/);
});

test("an unban audits what it cleared across every store", async () => {
  const { bans, logs } = makeBans();
  bans.unbanEverywhere("Butter Life");
  assert.match(audit(logs), /unban \| player="Butter Life"/);
  assert.match(audit(logs), /cleared: ips=1 names=0 eosIds=1 blacklistTxt=1/);
});
