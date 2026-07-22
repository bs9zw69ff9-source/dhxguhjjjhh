const { test } = require("node:test");
const assert = require("node:assert");
const { verificationConflict } = require("../verify/rules");

const store = {
  "111": { name: "Butter", ips: ["1.2.3.4"], at: 1, by: "staff" },
  "222": { name: "Nick",   ips: ["5.6.7.8", "9.9.9.9"], at: 2, by: "staff" },
};

test("a fresh name + fresh IP verifies cleanly", () => {
  assert.equal(verificationConflict(store, "333", "Fresh", ["10.0.0.1"]), null);
});

test("a name already verified to someone else is rejected", () => {
  const c = verificationConflict(store, "333", "butter", ["10.0.0.1"]);
  assert.equal(c?.code, "name");
  assert.equal(c.otherId, "111");
});

test("a shared confirmed IP is rejected as an alt", () => {
  const c = verificationConflict(store, "333", "Someone", ["9.9.9.9"]);
  assert.equal(c?.code, "alt");
  assert.equal(c.otherId, "222");
});

test("a user already verified can't verify again", () => {
  const c = verificationConflict(store, "111", "Whatever", ["10.0.0.1"]);
  assert.equal(c?.code, "self");
});

test("re-verifying your OWN name/IP still trips the self guard first", () => {
  // self is checked before name/ip, so an already-verified user is told they're verified
  const c = verificationConflict(store, "222", "Nick", ["5.6.7.8"]);
  assert.equal(c?.code, "self");
});

test("no IPs means the alt check can't false-positive", () => {
  assert.equal(verificationConflict(store, "444", "Newbie", []), null);
});
