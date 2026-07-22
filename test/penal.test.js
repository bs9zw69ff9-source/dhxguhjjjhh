const { test } = require("node:test");
const assert = require("node:assert");
const P = require("../penal/codes");

test("every charge is well-formed and codes are unique", () => {
  const seen = new Set();
  for (const ch of P.CHARGES) {
    assert.match(ch.code, /^\d+\.\d+$/, `bad code ${ch.code}`);
    assert.ok(ch.name && typeof ch.name === "string");
    assert.ok(ch.cls === "Felony" || ch.cls === "Misdemeanor", `${ch.code}: bad class`);
    assert.ok(Number.isInteger(ch.min) && ch.min >= 0, `${ch.code}: bad min`);
    assert.ok(!seen.has(ch.code), `duplicate code ${ch.code}`);
    seen.add(ch.code);
  }
});

test("sections group by the code prefix and cover every charge", () => {
  const total = P.sectionList().reduce((s, x) => s + x.count, 0);
  assert.equal(total, P.CHARGES.length);
  assert.equal(P.SECTIONS["1"].every(c => c.code.startsWith("1.")), true);
});

test("getCharge resolves a known code and rejects junk", () => {
  assert.equal(P.getCharge("1.04").name, "Assault with a Deadly Weapon");
  assert.equal(P.getCharge("1.04").min, 6);
  assert.equal(P.getCharge("9.99"), null);
});

test("bookingTotal sums the minutes (matches the screenshot: 1.02 + 1.04 = 9)", () => {
  const t = P.bookingTotal(["1.02", "1.04"]);
  assert.equal(t.minutes, 9);
  assert.equal(t.untilSober, false);
  assert.equal(t.charges.length, 2);
});

test("until-sober charge is indefinite: 0 numeric minutes but flagged", () => {
  const ch = P.getCharge("13.01");
  assert.equal(ch.untilSober, true);
  assert.equal(ch.min, 0);
  const t = P.bookingTotal(["13.01", "1.01"]);
  assert.equal(t.minutes, 2);            // only the numeric charge counts
  assert.equal(t.untilSober, true);
  assert.equal(P.sentenceLabel(t.minutes, t.untilSober), "2 min + until sober");
});

test("sentenceLabel formats numeric / indefinite / combined", () => {
  assert.equal(P.sentenceLabel(9, false), "9 min");
  assert.equal(P.sentenceLabel(0, true), "until sober");
  assert.equal(P.sentenceLabel(0, false), "0 min");
});
