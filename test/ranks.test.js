const { test } = require("node:test");
const assert = require("node:assert");
const { PROFILES } = require("../factions/ranks");

// Validate EVERY RP profile's rank registry (rp1 = mafia/police, rp2 = fallout),
// not just whichever RP_PROFILE happens to be active - a malformed set in either
// one fails the suite.
for (const [profile, { FACTION_RANKS, SPAWN_FILE_MAP }] of Object.entries(PROFILES)) {
  test(`[${profile}] every configured faction has a coherent rank registry`, () => {
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

  test(`[${profile}] rank files are globally unique across factions`, () => {
    const all = [];
    for (const cfg of Object.values(FACTION_RANKS)) all.push(...Object.values(cfg.rankFiles));
    assert.equal(new Set(all).size, all.length);
  });

  test(`[${profile}] every faction has a unique spawn file`, () => {
    for (const name of Object.keys(FACTION_RANKS)) {
      assert.ok(SPAWN_FILE_MAP[name], `${name}: missing spawn file`);
      assert.match(SPAWN_FILE_MAP[name], /\.txt$/, `${name}: spawn file must be .txt`);
    }
    const spawns = Object.values(SPAWN_FILE_MAP);
    assert.equal(new Set(spawns).size, spawns.length, `${profile}: duplicate spawn files`);
  });
}
