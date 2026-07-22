const { test } = require("node:test");
const assert = require("node:assert");
const P = require("../penal/codes");

const CLASSES = new Set(["Infraction", "Misdemeanor", "Felony", "Misdemeanor / Felony"]);

test("every charge is well-formed and codes are unique", () => {
  const seen = new Set();
  for (const ch of P.CHARGES) {
    assert.match(ch.code, /^(PC|VC) \d+$/, `bad code ${ch.code}`);
    assert.ok(ch.name && typeof ch.name === "string");
    assert.ok(CLASSES.has(ch.cls), `${ch.code}: bad class ${ch.cls}`);
    assert.ok(Number.isInteger(ch.min) && ch.min >= 0, `${ch.code}: bad min`);
    assert.ok(ch.bail === null || (Number.isInteger(ch.bail) && ch.bail >= 0), `${ch.code}: bad bail`);
    assert.ok(!seen.has(ch.code), `duplicate code ${ch.code}`);
    seen.add(ch.code);
  }
});

test("sections group by the hundreds series and cover every charge", () => {
  const total = P.sectionList().reduce((s, x) => s + x.count, 0);
  assert.equal(total, P.CHARGES.length);
  assert.equal(P.SECTIONS["100"].every(c => c.code.startsWith("PC 1")), true);
  assert.equal(P.SECTIONS["600"].every(c => c.code.startsWith("VC 6")), true);
  assert.deepEqual(P.sectionList().map(s => s.num), ["100", "200", "300", "400", "500", "600", "700"]);
});

test("getCharge resolves a known code and rejects junk", () => {
  assert.equal(P.getCharge("PC 200").name, "Assault");
  assert.equal(P.getCharge("PC 200").min, 4);
  assert.equal(P.getCharge("PC 200").bail, 75);
  assert.equal(P.getCharge("PC 200").cls, "Misdemeanor");
  assert.equal(P.getCharge("PC 999"), null);
});

test("bookingTotal sums the jail minutes and bail", () => {
  const t = P.bookingTotal(["PC 100", "PC 104"]);   // 2 + 2 min, $25 + $30
  assert.equal(t.minutes, 4);
  assert.equal(t.bail, 55);
  assert.equal(t.execution, false);
  assert.equal(t.variable, false);
  assert.equal(t.charges.length, 2);
});

test("homicide is an execution: no timed jail, no bail", () => {
  const ch = P.getCharge("PC 210");
  assert.equal(ch.special, "execution");
  assert.equal(ch.min, 0);
  assert.equal(ch.bail, null);
  const t = P.bookingTotal(["PC 210", "PC 100"]);
  assert.equal(t.execution, true);
  assert.equal(P.sentenceLabel(t.minutes, t), "Execution");
  assert.equal(P.bailLabel(t.bail, t), "No bail (execution)");
});

test("aiding and abetting is variable: jail and bail depend on the associated crime", () => {
  const ch = P.getCharge("PC 707");
  assert.equal(ch.special, "variable");
  assert.equal(ch.cls, "Misdemeanor / Felony");
  const t = P.bookingTotal(["PC 707"]);
  assert.equal(t.variable, true);
  assert.equal(P.sentenceLabel(t.minutes, t), "based on the associated charge");
  assert.equal(P.bailLabel(t.bail, t), "Based on the associated charge");
});

test("infractions carry bail but no jail time", () => {
  const t = P.bookingTotal(["VC 600"]);   // Speeding: no jail, $10
  assert.equal(t.minutes, 0);
  assert.equal(t.bail, 10);
  assert.equal(P.sentenceLabel(t.minutes, t), "No jail time");
  assert.equal(P.bailLabel(t.bail, t), "$10");
});

test("a bail rate scales prices and rounds each charge to the nearest dollar", () => {
  // PC 205 base bail $250 -> +21% = $302.5 -> rounds to $303 (nearest dollar).
  assert.equal(P.chargeBail(P.getCharge("PC 205"), 1.21), 303);
  assert.equal(P.chargeBail(P.getCharge("PC 205"), 1), 250);          // rate 1 = base
  assert.equal(P.chargeBail(P.getCharge("PC 210"), 1.5), null);       // execution: no bail
  // Total rounds per charge, then sums: $25*1.1=27.5->28, $30*1.1=33 -> 61
  const t = P.bookingTotal(["PC 100", "PC 104"], 1.1);
  assert.equal(t.bail, 61);
  assert.equal(t.minutes, 4);   // jail time is unaffected by the bail rate
});

test("sentenceLabel and bailLabel format the plain cases", () => {
  assert.equal(P.sentenceLabel(9, {}), "9 min");
  assert.equal(P.sentenceLabel(0, {}), "No jail time");
  assert.equal(P.bailLabel(250, {}), "$250");
  assert.equal(P.bailLabel(0, {}), "$0");
});
