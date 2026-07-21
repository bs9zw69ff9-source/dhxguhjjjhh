const { test } = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");

const noLog = { info() {}, warn() {}, error() {}, debug() {} };

// The database factory anchors bot.db at baseDir but resolves FILES relative to
// CWD — run the whole suite from an isolated temp dir so nothing touches the repo.
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "botdb-"));
const prevCwd = process.cwd();
process.chdir(tmp);
const db = require(path.join(prevCwd, "database"))({ logger: noLog, baseDir: tmp });

test("registry: 31 datasets, defaults seeded", () => {
  assert.equal(Object.keys(db.FILES).length, 31);
  assert.deepEqual(db.safeRead(db.FILES.TEMPBAN, []), []);
  assert.deepEqual(db.safeRead(db.FILES.PLAYTIME, {}), {});
  assert.deepEqual(db.safeRead(db.FILES.WARRANTS, {}), {});
  const roles = db.safeRead(db.FILES.ROLES, {});
  assert.ok("modRoleId" in roles && "adminRoleId" in roles && "policeRoleId" in roles);
});

test("safeWrite/safeRead round-trip returns clones, not references", () => {
  const val = { alice: 42 };
  assert.ok(db.safeWrite(db.FILES.PLAYTIME, val));
  const got = db.safeRead(db.FILES.PLAYTIME, {});
  assert.deepEqual(got, val);
  got.alice = 999;                                   // mutating the clone…
  assert.equal(db.safeRead(db.FILES.PLAYTIME, {}).alice, 42);   // …never leaks back
});

test("update: serialized read-modify-write", async () => {
  await db.safeWrite(db.FILES.KNOWN, {});
  // 20 concurrent increments must all land (per-file queue serializes them)
  await Promise.all(Array.from({ length: 20 }, () =>
    db.update(db.FILES.KNOWN, {}, (m) => { m.count = (m.count || 0) + 1; return m; })));
  assert.equal(db.safeRead(db.FILES.KNOWN, {}).count, 20);
});

test("exportDbToJson writes pretty backups to disk", () => {
  // Only datasets with a seeded default (25 of 31) exist as kv rows on a fresh
  // db — the others appear lazily on first read/write.
  const n = db.exportDbToJson();
  assert.ok(n >= 20, `expected >=20 exported, got ${n}`);
  const onDisk = JSON.parse(fs.readFileSync(path.join(tmp, "playtime.json"), "utf8"));
  assert.equal(onDisk.alice, 42);
});

test("casino config defaults merge", () => {
  assert.equal(db.CASINO_CONFIG_DEFAULTS.enabled, true);
  assert.ok(db.CASINO_CONFIG_DEFAULTS.minBet > 0);
});
