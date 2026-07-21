const { test } = require("node:test");
const assert = require("node:assert");
const { FACTION_RANKS } = require("../factions/ranks");

test("every configured faction has a coherent rank registry", () => {
  // No minimum count - factions are added on demand. This validates the SCHEMA of
  // whatever is configured, so a malformed faction added later fails the suite.
  for (const [name, cfg] of Object.entries(FACTION_RANKS)) {
    assert.ok(Array.isArray(cfg.order) && cfg.order.length > 0, `${name}: empty order`);
    assert.ok(cfg.order.includes(cfg.default), `${name}: default '${cfg.default}' not in order`);
    for (const rank of cfg.order) {
      assert.ok(rank in cfg.badges, `${name}: rank '${rank}' missing badge entry`);
      assert.ok(cfg.rankFiles[rank], `${name}: rank '${rank}' missing rank file`);
      assert.match(cfg.rankFiles[rank], /\.txt$/, `${name}/${rank}: rank file must be .txt`);
    }
    // no duplicate ranks and no duplicate rank files within a faction
    assert.equal(new Set(cfg.order).size, cfg.order.length, `${name}: duplicate rank names`);
    const files = Object.values(cfg.rankFiles);
    assert.equal(new Set(files).size, files.length, `${name}: duplicate rank files`);
  }
});

test("rank files are globally unique across factions", () => {
  const all = [];
  for (const cfg of Object.values(FACTION_RANKS)) all.push(...Object.values(cfg.rankFiles));
  assert.equal(new Set(all).size, all.length);
});

test("sub-class files are .txt and don't collide with rank or spawn files", () => {
  const rankAndSpawn = [];
  for (const cfg of Object.values(FACTION_RANKS)) rankAndSpawn.push(...Object.values(cfg.rankFiles));
  const subFiles = [];
  for (const [name, cfg] of Object.entries(FACTION_RANKS)) {
    for (const [sc, file] of Object.entries(cfg.subclasses ?? {})) {
      assert.match(file, /\.txt$/, `${name}/${sc}: sub-class file must be .txt`);
      assert.ok(!cfg.order.includes(sc), `${name}: sub-class '${sc}' must not also be a rank`);
      subFiles.push(file);
    }
  }
  const clash = subFiles.filter(f => rankAndSpawn.includes(f));
  assert.equal(clash.length, 0, `sub-class files clash with rank files: ${clash.join(", ")}`);
  assert.equal(new Set(subFiles).size, subFiles.length, "duplicate sub-class files");
});
