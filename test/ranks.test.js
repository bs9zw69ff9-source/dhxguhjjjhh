const { test } = require("node:test");
const assert = require("node:assert");
const { FACTION_RANKS } = require("../factions/ranks");

test("every faction has a coherent rank registry", () => {
  const factions = Object.keys(FACTION_RANKS);
  assert.ok(factions.length >= 8);
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
