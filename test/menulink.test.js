/* One Discord account <-> one in-game name, permanently.

   The property under test is an anti-alt rule: a member must not be able to move their
   RCON menu onto a second in-game name. The dangerous path is release-then-reclaim, so
   most of these cases run with holdsMenu:false - that is the state an abuser would put
   themselves in first. */
const { test } = require("node:test");
const assert = require("node:assert");
const { menuClaimDecision } = require("../moderation/menulink");

const claim = (o) => menuClaimDecision({ boundName: null, ownerId: null, selfId: "u1", holdsMenu: false, ...o });

test("an unbound member claiming a free name is granted it", () => {
  assert.equal(claim({ requestedName: "Butter Life" }).action, "grant");
});

test("a bound member cannot claim a different name", () => {
  const r = claim({ boundName: "Butter Life", requestedName: "Butter Life Alt" });
  assert.equal(r.action, "locked");
  assert.equal(r.boundName, "Butter Life");
});

test("releasing the menu does NOT free the name - the alt loophole stays shut", () => {
  // Exactly the bypass: strip your own menu (holdsMenu now false), then try an alt.
  // The binding has to outlive the grant or the whole rule is decorative.
  const r = claim({ boundName: "Butter Life", requestedName: "Nova", holdsMenu: false });
  assert.equal(r.action, "locked");
});

test("a bound member re-entering their own name releases the menu", () => {
  assert.equal(claim({ boundName: "Butter Life", requestedName: "Butter Life", holdsMenu: true }).action, "release");
});

test("re-claiming your own name after releasing is allowed", () => {
  assert.equal(claim({ boundName: "Butter Life", requestedName: "Butter Life", holdsMenu: false }).action, "grant");
});

test("name matching ignores case and surrounding whitespace", () => {
  assert.equal(claim({ boundName: "Butter Life", requestedName: "  butter life  ", holdsMenu: true }).action, "release");
  assert.equal(claim({ boundName: "Butter Life", requestedName: "BUTTER LIFE" }).action, "grant");
});

test("a second Discord account cannot bind to someone else's name", () => {
  const r = claim({ requestedName: "Butter Life", ownerId: "u2", selfId: "u1" });
  assert.equal(r.action, "taken");
  assert.equal(r.ownerId, "u2");
});

test("seeing your own id as the name's owner is not a conflict", () => {
  assert.equal(claim({ boundName: "Butter Life", requestedName: "Butter Life", ownerId: "u1", selfId: "u1" }).action, "grant");
});

test("the lock outranks the ownership check when both apply", () => {
  // Bound to A, requesting B, and B belongs to someone else. Either refusal is correct,
  // but "locked" names the member's own situation, so it is the more useful message.
  assert.equal(claim({ boundName: "A", requestedName: "B", ownerId: "u2", selfId: "u1" }).action, "locked");
});

test("an empty or whitespace-only binding is treated as unbound", () => {
  assert.equal(claim({ boundName: "   ", requestedName: "Anything" }).action, "grant");
  assert.equal(claim({ boundName: null, requestedName: "Anything" }).action, "grant");
});
