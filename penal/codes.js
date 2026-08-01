/* ---------------- penal/codes: the penal code (pure data + helpers) ----------------
   Every charge: { code, name, cls, min, bail, special }.
     - min:  jail time in minutes. 0 for infractions with no jail, and for a
             charge whose sentence ranges with the crime it attaches to.
     - bail: bail in dollars, or null when the sheet gives no figure.
     - special: "nobail"     -> NOT BAILABLE. The sentence is served; money does
                                not shorten it. This is what "N/A" means in the
                                bail column, and it is the opposite of "a price
                                nobody has chosen yet" - these are the charges you
                                cannot buy your way out of.
                "associated" -> bail is set by the crime this charge attaches to.
                "ranges"     -> the SENTENCE ranges with the associated crime.
                                Carries "nobail" as well; see PC 707.
   Charges group into sections by their hundreds series (PC 104 -> 100,
   VC 604 -> 600). PC = penal code, VC = vehicle code.

   KEPT IN STEP WITH dotnet/src/PavlovBot.Core/Penal/PenalCode.cs. The two bots share
   one database, so a table that differs between them re-prices arrests the moment
   either one is the process that happens to be running. */

const SECTION_TITLES = {
  "100": "Public Order Offenses",
  "200": "Crimes Against Persons",
  "300": "Property Crimes",
  "400": "Weapons Offenses",
  "500": "Crimes Against Government",
  "600": "Vehicle Code",
  "700": "Organized Crime",
  "800": "Controlled Substances",
};

const CLS = { I: "Infraction", M: "Misdemeanor", F: "Felony", "M/F": "Misdemeanor / Felony" };

// [code, name, class, jailMinutes, bail, special?]
const _RAW = [
  // 100 - Public Order Offenses
  ["PC 100", "Disorderly Conduct", "M", 2, 10],
  ["PC 101", "Disturbing the Peace", "I", 1, 15],
  ["PC 102", "Public Intoxication", "I", 1, 15],
  ["PC 103", "Loitering", "I", 1, 30],
  ["PC 104", "Trespassing", "M", 2, 50],
  ["PC 105", "Unlawful Assembly", "M", 3, 50],
  ["PC 106", "Non-Peaceful Riot", "F", 7, null, "nobail"],
  ["PC 107", "Failure to Obey a Lawful Order", "M", 3, 75],
  // 200 - Crimes Against Persons
  ["PC 200", "Assault", "M", 4, 75],
  ["PC 201", "Aggravated Assault", "F", 5, 250],
  ["PC 202", "Battery", "M", 4, 100],
  ["PC 203", "Aggravated Battery", "F", 5, 250],
  ["PC 204", "Menacing", "M", 4, 150],
  ["PC 205", "Aggravated Menacing", "F", 6, 500],
  ["PC 206", "Brandishing a Weapon", "M", 3, 150],
  ["PC 207", "Kidnapping", "F", 5, 700],
  ["PC 208", "Fraud", "M", 3, null, "nobail"],
  ["PC 209", "Attempted Murder", "F", 7, 75],
  ["PC 210", "Homicide", "F", 8, 250],
  ["PC 211", "Mass Homicide", "F", 10, null, "nobail"],
  // 300 - Property Crimes
  ["PC 300", "Petty Theft (<$500)", "M", 3, 400],
  ["PC 301", "Grand Larceny (>$500)", "F", 5, 500],
  ["PC 302", "Burglary", "F", 6, 100],
  ["PC 303", "Robbery", "F", 5, 75],
  ["PC 304", "Armed Robbery", "F", 7, 75],
  ["PC 305", "Possession of Stolen Property", "M", 4, 150],
  ["PC 306", "Criminal Mischief", "M", 3, 450],
  ["PC 307", "Vandalism", "M", 3, 300],
  ["PC 308", "Attempted Burglary", "M", 4, 150],
  ["PC 309", "Extortion", "F", 6, 475],
  ["PC 310", "Grand Theft Auto", "F", 7, null, "nobail"],
  // 400 - Weapons Offenses
  ["PC 400", "Unlawful Discharge of a Firearm", "M", 4, 450],
  ["PC 401", "Carrying a Concealed Weapon Without Permit", "M", 5, 150],
  ["PC 402", "Possession of Illegal Automatic Weapons", "F", 6, 100],
  ["PC 403", "Possession of Explosives", "F", 8, 500],
  ["PC 404", "Illegal Sale of Firearms", "F", 6, 75],
  ["PC 405", "Brandishing of a Firearm", "M", 3, null, "nobail"],
  // 500 - Crimes Against Government
  ["PC 500", "Resisting or Evading Arrest", "M", 3, 300],
  ["PC 501", "Obstruction of Justice", "M", 4, 250],
  ["PC 502", "Escape from Custody", "F", 5, 75],
  ["PC 503", "False Report", "M", 3, 10],
  ["PC 504", "Impersonating a Police Officer", "F", 6, 100],
  ["PC 505", "Bribery of a Public Official", "F", 5, 150],
  ["PC 506", "Perjury", "F", 6, 325],
  ["PC 507", "Contempt of Court", "M", 3, 10],
  ["PC 508", "Malfeasance", "F", 5, null, "nobail"],
  // 600 - Vehicle Code
  ["VC 600", "Speeding", "I", 0, 50],
  ["VC 601", "Reckless Driving", "M", 3, 5],
  ["VC 602", "Driving While Intoxicated", "M", 4, 350],
  ["VC 603", "Hit and Run", "F", 6, 550],
  ["VC 604", "Failure to Yield", "I", 0, 75],
  ["VC 605", "Evading and Eluding Police", "F", 4, 200],
  ["VC 606", "Driving Without a License", "M", 2, 600],
  ["VC 607", "Illegal Parking", "I", 0, 400],
  ["VC 608", "Vehicular Assault", "F", 5, 400],
  ["VC 609", "Vehicular Manslaughter", "F", 8, 150],
  ["VC 610", "Following an Emergency Vehicle", "M", 3, null, "nobail"],
  ["VC 611", "Failure to Maintain Lane", "I", 0, null, "nobail"],
  // 700 - Organized Crime
  ["PC 700", "Illegal Gambling", "M", 3, null, "associated"],
  ["PC 701", "Bootlegging", "F", 5, null, "nobail"],
  ["PC 702", "Racketeering", "F", 7, null, "nobail"],
  ["PC 703", "Loan Sharking", "F", 6, null, "nobail"],
  ["PC 704", "Smuggling", "F", 6, null, "nobail"],
  ["PC 705", "Operating an Illegal Business", "M", 4, null, "nobail"],
  ["PC 706", "Criminal Conspiracy", "F", 6, null, "nobail"],
  ["PC 707", "Aiding and Abetting", "M/F", 0, null, "ranges"],
  // 800 - Controlled Substances (no bail anywhere in this section)
  ["PC 801", "Possession of Controlled Substance", "M", 3, null, "nobail"],
  ["PC 802", "Possession of Narcotics with Intent to Distribute", "F", 5, null, "nobail"],
  ["PC 803", "Sale of Illegal Narcotics", "F", 6, null, "nobail"],
  ["PC 804", "Manufacturing Illegal Narcotics", "F", 8, null, "nobail"],
  ["PC 805", "Illegal Distribution of Prescription Drugs", "F", 3, null, "nobail"],
  ["PC 806", "Possession of Drug Paraphernalia", "M", 4, null, "nobail"],
];

const CHARGES = _RAW.map(([code, name, c, min, bail, special]) => ({
  code, name, cls: CLS[c] || c, min, bail: bail ?? null, special: special || null,
}));

const BY_CODE = new Map(CHARGES.map(ch => [ch.code, ch]));

// section key = the hundreds series: "PC 104" -> "100", "VC 604" -> "600".
const sectionOf = (code) => {
  const n = Number(String(code).match(/\d+/)?.[0] || 0);
  return String(Math.floor(n / 100) * 100);
};

// section number -> [charges], in code order
const SECTIONS = {};
for (const ch of CHARGES) (SECTIONS[sectionOf(ch.code)] ??= []).push(ch);

// [{ num, title, count }] sorted numerically - for the section dropdown
const sectionList = () => Object.keys(SECTIONS)
  .sort((a, b) => Number(a) - Number(b))
  .map(num => ({ num, title: SECTION_TITLES[num] || `Section ${num}`, count: SECTIONS[num].length }));

const getCharge = (code) => BY_CODE.get(String(code)) || null;

// One charge's bail at the current rate, rounded to the nearest dollar. null when
// the charge has no set figure. rate 1 = the base price.
const chargeBail = (c, rate = 1) => (typeof c.bail === "number" ? Math.round(c.bail * rate) : null);

// Can money get this player out early?
const isBailable = (c) => c.special !== "nobail" && c.special !== "ranges";

// Total jail + bail for a set of charge codes, plus the flags the labels need. `rate`
// scales every bail figure (the admin-tunable multiplier), rounded per charge.
//
// ONE NON-BAILABLE CHARGE MAKES THE WHOLE BOOKING NON-BAILABLE. Anything else defeats
// the rule: a player charged with manslaughter and a parking ticket would otherwise pay
// the parking ticket and walk out of the manslaughter.
function bookingTotal(codes, rate = 1) {
  let minutes = 0, bail = 0, bailable = true, associated = false, ranges = false;
  const charges = [];
  for (const code of codes) {
    const ch = getCharge(code);
    if (!ch) continue;
    charges.push(ch);
    minutes += ch.min || 0;
    if (typeof ch.bail === "number") bail += Math.round(ch.bail * rate);
    if (!isBailable(ch))              bailable = false;
    if (ch.special === "associated")  associated = true;
    if (ch.special === "ranges")      ranges = true;
  }
  return { minutes, bail, charges, bailable, associated, ranges };
}

// Jail label: "9 min", "2 min + ranges with the associated charge", "No jail time".
function sentenceLabel(minutes, opts = {}) {
  const { ranges } = opts || {};
  const parts = [];
  if (minutes > 0) parts.push(`${minutes} min`);
  if (ranges)      parts.push("ranges with the associated charge");
  return parts.join(" + ") || "No jail time";
}

// Bail label: "$250", "No bail - the sentence must be served", "Based on ...".
// The non-bailable case is checked FIRST and wins outright: showing a dollar figure the
// player cannot actually pay is worse than showing none, because they turn up at the
// station with the money and nobody can tell them why it will not work.
function bailLabel(bail, opts = {}) {
  const { bailable = true, associated } = opts || {};
  if (!bailable) return "No bail - the sentence must be served";
  const amount = `$${Number(bail || 0).toLocaleString()}`;
  if (!associated) return amount;
  return Number(bail || 0) > 0 ? `${amount} + based on the associated charge` : "Based on the associated charge";
}

// One charge as a short line for menus/summaries: jail + bail (at the given rate).
// "no bail" is printed rather than left blank - a blank is indistinguishable from a
// charge that costs nothing, and that difference is the whole point of the column.
const chargeTime = (c, rate = 1) => {
  const jail = c.special === "ranges" ? "Ranges" : c.min > 0 ? `${c.min} min` : "No jail";
  const price = typeof c.bail === "number" ? `$${chargeBail(c, rate).toLocaleString()}`
    : c.special === "associated" ? "bail by charge"
    : "no bail";
  return `${jail} • ${price}`;
};

module.exports = { SECTION_TITLES, CHARGES, SECTIONS, CLS, sectionList, getCharge, sectionOf, chargeBail, isBailable, bookingTotal, sentenceLabel, bailLabel, chargeTime };
