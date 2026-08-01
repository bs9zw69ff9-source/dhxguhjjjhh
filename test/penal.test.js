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
  assert.equal(P.CHARGES.length, 72);
  assert.equal(P.SECTIONS["100"].every(c => c.code.startsWith("PC 1")), true);
  assert.equal(P.SECTIONS["600"].every(c => c.code.startsWith("VC 6")), true);
  assert.deepEqual(P.sectionList().map(s => s.num), ["100", "200", "300", "400", "500", "600", "700", "800"]);
});

test("no section overflows Discord's 25-option charge picker", () => {
  // A 26th charge would silently vanish from the menu, and only from the menu.
  for (const s of P.sectionList()) assert.ok(s.count <= 25, `section ${s.num} has ${s.count}`);
});

test("getCharge resolves a known code and rejects junk", () => {
  assert.equal(P.getCharge("PC 200").name, "Assault");
  assert.equal(P.getCharge("PC 200").min, 4);
  assert.equal(P.getCharge("PC 200").bail, 75);
  assert.equal(P.getCharge("PC 200").cls, "Misdemeanor");
  assert.equal(P.getCharge("PC 999"), null);
});

test("the table matches the C# one, so a rollback does not re-price arrests", () => {
  // Spot-checked against dotnet/src/PavlovBot.Core/Penal/PenalCode.cs.
  const rows = [
    ["PC 100", "Disorderly Conduct", 2, 10],
    ["PC 107", "Failure to Obey a Lawful Order", 3, 75],
    ["PC 210", "Homicide", 8, 250],
    ["PC 300", "Petty Theft (<$500)", 3, 400],
    ["PC 403", "Possession of Explosives", 8, 500],
    ["VC 606", "Driving Without a License", 2, 600],
  ];
  for (const [code, name, min, bail] of rows) {
    const ch = P.getCharge(code);
    assert.equal(ch.name, name, code);
    assert.equal(ch.min, min, code);
    assert.equal(ch.bail, bail, code);
  }
});

test("bookingTotal sums the jail minutes and bail", () => {
  const t = P.bookingTotal(["PC 100", "PC 104"]);   // 2 + 2 min, $10 + $50
  assert.equal(t.minutes, 4);
  assert.equal(t.bail, 60);
  assert.equal(t.bailable, true);
  assert.equal(t.ranges, false);
  assert.equal(t.charges.length, 2);
});

test("a no-bail charge cannot be paid off", () => {
  /* "N/A" in the bail column means NOT BAILABLE - the sentence is served. Reading it
     as "no price has been chosen yet" would let somebody buy their way out of
     manslaughter for whatever number an officer typed. */
  const ch = P.getCharge("PC 211");
  assert.equal(ch.special, "nobail");
  assert.equal(ch.min, 10);
  assert.equal(ch.bail, null);
  const t = P.bookingTotal(["PC 211"]);
  assert.equal(t.bailable, false);
  assert.equal(P.bailLabel(t.bail, t), "No bail - the sentence must be served");
  assert.equal(P.sentenceLabel(t.minutes, t), "10 min");
});

test("one non-bailable charge makes the whole booking non-bailable", () => {
  // Otherwise the parking ticket gets paid and the manslaughter walks out with it.
  const t = P.bookingTotal(["VC 607", "PC 801"]);
  assert.equal(t.bailable, false);
  assert.equal(P.bailLabel(t.bail, t), "No bail - the sentence must be served");
});

test("illegal gambling takes its price from the associated crime", () => {
  const t = P.bookingTotal(["PC 700"]);
  assert.equal(t.associated, true);
  assert.equal(t.minutes, 3);                                   // the sentence is still fixed
  assert.equal(P.bailLabel(t.bail, t), "Based on the associated charge");
  assert.equal(P.bailLabel(...(() => { const x = P.bookingTotal(["PC 100", "PC 700"]); return [x.bail, x]; })()),
    "$10 + based on the associated charge");
});

test("aiding and abetting ranges with the associated crime, and carries no bail", () => {
  const ch = P.getCharge("PC 707");
  assert.equal(ch.special, "ranges");
  assert.equal(ch.cls, "Misdemeanor / Felony");
  const t = P.bookingTotal(["PC 707"]);
  assert.equal(t.ranges, true);
  assert.equal(t.bailable, false);
  assert.equal(P.sentenceLabel(t.minutes, t), "ranges with the associated charge");
});

test("infractions carry bail but no jail time", () => {
  const t = P.bookingTotal(["VC 600"]);   // Speeding: no jail, $50
  assert.equal(t.minutes, 0);
  assert.equal(t.bail, 50);
  assert.equal(P.sentenceLabel(t.minutes, t), "No jail time");
  assert.equal(P.bailLabel(t.bail, t), "$50");
});

test("a bail rate scales prices and rounds each charge to the nearest dollar", () => {
  // PC 205 base bail $500 -> +21% = $605.
  assert.equal(P.chargeBail(P.getCharge("PC 205"), 1.21), 605);
  assert.equal(P.chargeBail(P.getCharge("PC 205"), 1), 500);          // rate 1 = base
  assert.equal(P.chargeBail(P.getCharge("PC 801"), 1.5), null);       // no bail: no figure
  // Total rounds per charge, then sums: $10*1.1=11, $50*1.1=55 -> 66
  const t = P.bookingTotal(["PC 100", "PC 104"], 1.1);
  assert.equal(t.bail, 66);
  assert.equal(t.minutes, 4);   // jail time is unaffected by the bail rate
});

test("a charge line names the price, or says there is none", () => {
  // A blank price is indistinguishable from a charge that costs nothing.
  assert.equal(P.chargeTime(P.getCharge("PC 100")), "2 min • $10");
  assert.equal(P.chargeTime(P.getCharge("PC 801")), "3 min • no bail");
  assert.equal(P.chargeTime(P.getCharge("VC 600")), "No jail • $50");
  assert.equal(P.chargeTime(P.getCharge("PC 700")), "3 min • bail by charge");
});

test("sentenceLabel and bailLabel format the plain cases", () => {
  assert.equal(P.sentenceLabel(9, {}), "9 min");
  assert.equal(P.sentenceLabel(0, {}), "No jail time");
  assert.equal(P.bailLabel(250, {}), "$250");
  assert.equal(P.bailLabel(0, {}), "$0");
});
