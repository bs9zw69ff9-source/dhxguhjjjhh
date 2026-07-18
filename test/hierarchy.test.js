const { test } = require("node:test");
const assert = require("node:assert");
const { TIER, commandTier, commandTierName, canOverride } = require("../moderation/hierarchy");

// Mirror index.js: the super owner id is also an owner id.
const SUPER = "1014251293159731310";
const OWNER = "678362059905171471";
const preds = {
  isSuperOwner: (id) => id === SUPER,
  isOwner: (id) => id === SUPER || id === OWNER,
  hasAdminRole: (m) => !!m && m.roles === "admin",
  hasModRole: (m) => !!m && (m.roles === "admin" || m.roles === "mod"),
};
const tier = (m) => commandTier(m, preds);

test("commandTier resolves each rank, highest-first", () => {
  assert.equal(tier({ id: SUPER }), TIER.SUPER_OWNER);
  assert.equal(tier({ id: OWNER }), TIER.OWNER);
  assert.equal(tier({ id: "x", roles: "admin" }), TIER.ADMIN);
  assert.equal(tier({ id: "y", roles: "mod" }), TIER.MOD);
  assert.equal(tier({ id: "z", roles: null }), TIER.NONE);
  assert.equal(tier(null), TIER.NONE);
});

test("the configured super owner id is the top tier", () => {
  assert.equal(tier({ id: SUPER }), 4);
  assert.ok(tier({ id: SUPER }) > tier({ id: OWNER }));
});

test("tier names", () => {
  assert.equal(commandTierName(4), "Super Owner");
  assert.equal(commandTierName(3), "Owner");
  assert.equal(commandTierName(2), "Admin");
  assert.equal(commandTierName(1), "Mod");
  assert.equal(commandTierName(0), "Staff");
});

test("override rule matches the required hierarchy", () => {
  const S = 4, O = 3, A = 2, M = 1;
  // Super owner overrides anything.
  for (const r of [S, O, A, M, 0]) assert.ok(canOverride(S, r), `super owner overrides ${r}`);
  // Owner cannot override a super owner; can override its own tier and below.
  assert.ok(!canOverride(O, S), "owner cannot override super owner");
  for (const r of [O, A, M, 0]) assert.ok(canOverride(O, r), `owner overrides ${r}`);
  // Admin cannot override super owner or owner.
  assert.ok(!canOverride(A, S), "admin cannot override super owner");
  assert.ok(!canOverride(A, O), "admin cannot override owner");
  for (const r of [A, M, 0]) assert.ok(canOverride(A, r), `admin overrides ${r}`);
  // Mod cannot override super owner, owner, or admin.
  assert.ok(!canOverride(M, S), "mod cannot override super owner");
  assert.ok(!canOverride(M, O), "mod cannot override owner");
  assert.ok(!canOverride(M, A), "mod cannot override admin");
  assert.ok(canOverride(M, M), "mod can override a mod");
});

test("records predating the hierarchy (no tier) are overridable by anyone", () => {
  assert.ok(canOverride(1, undefined));
  assert.ok(canOverride(1, null));
  assert.ok(canOverride(0, 0));
});
