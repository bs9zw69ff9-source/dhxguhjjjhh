/* ---------------- penal/codes: the penal code (pure data + helpers) ----------------
   Every charge: { code, name, cls, min, bail, special }.
     - min:  jail time in minutes. 0 for infractions with no jail, for an
             execution charge, and for "based on associated crime" charges.
     - bail: bail in dollars, or null when there's no set figure (homicide,
             aiding and abetting).
     - special: "execution" -> death penalty, no jail timer, no bail.
                "variable"   -> jail and bail depend on the associated crime.
   Charges group into sections by their hundreds series (PC 104 -> 100,
   VC 604 -> 600). PC = penal code, VC = vehicle code. */

const SECTION_TITLES = {
  "100": "Public Order Offenses",
  "200": "Crimes Against Persons",
  "300": "Property Crimes",
  "400": "Weapons Offenses",
  "500": "Crimes Against Government",
  "600": "Vehicle Code",
  "700": "Organized Crime",
};

const CLS = { I: "Infraction", M: "Misdemeanor", F: "Felony", "M/F": "Misdemeanor / Felony" };

// [code, name, class, jailMinutes, bail, special?]
const _RAW = [
  // 100 - Public Order Offenses
  ["PC 100", "Disorderly Conduct", "M", 2, 25],
  ["PC 101", "Disturbing the Peace", "I", 1, 10],
  ["PC 102", "Public Intoxication", "I", 1, 15],
  ["PC 103", "Vagrancy", "I", 1, 15],
  ["PC 104", "Trespassing", "M", 2, 30],
  ["PC 105", "Unlawful Assembly", "M", 3, 50],
  ["PC 106", "Failure to Obey a Lawful Order", "M", 3, 50],
  // 200 - Crimes Against Persons
  ["PC 200", "Assault", "M", 4, 75],
  ["PC 201", "Aggravated Assault", "F", 5, 200],
  ["PC 202", "Battery", "M", 4, 75],
  ["PC 203", "Aggravated Battery", "F", 5, 250],
  ["PC 204", "Menacing", "M", 4, 100],
  ["PC 205", "Aggravated Menacing", "F", 6, 250],
  ["PC 206", "Brandishing a Weapon", "M", 3, 150],
  ["PC 207", "Kidnapping", "F", 5, 500],
  ["PC 208", "False Imprisonment", "M", 5, 150],
  ["PC 209", "Attempted Murder", "F", 6, 700],
  ["PC 210", "Homicide", "F", 0, null, "execution"],
  // 300 - Property Crimes
  ["PC 300", "Petty Theft", "M", 3, 75],
  ["PC 301", "Grand Larceny", "F", 5, 250],
  ["PC 302", "Burglary", "F", 6, 350],
  ["PC 303", "Robbery", "F", 6, 400],
  ["PC 304", "Armed Robbery", "F", 8, 500],
  ["PC 305", "Possession of Stolen Property", "M", 4, 100],
  ["PC 306", "Criminal Mischief", "M", 3, 75],
  ["PC 307", "Vandalism", "M", 3, 75],
  ["PC 308", "Attempted Burglary", "M", 4, 150],
  ["PC 309", "Extortion", "F", 8, 450],
  // 400 - Weapons Offenses
  ["PC 400", "Unlawful Discharge of a Firearm", "F", 5, 300],
  ["PC 401", "Carrying a Concealed Weapon Without Permit", "M", 5, 150],
  ["PC 402", "Possession of Illegal Automatic Weapons", "F", 6, 475],
  ["PC 403", "Possession of Explosives", "F", 8, 700],
  ["PC 404", "Illegal Sale of Firearms", "F", 6, 450],
  // 500 - Crimes Against Government
  ["PC 500", "Resisting Arrest", "M", 3, 150],
  ["PC 501", "Obstruction of Justice", "M", 4, 100],
  ["PC 502", "Escape from Custody", "F", 5, 500],
  ["PC 503", "False Report", "M", 3, 75],
  ["PC 504", "Impersonating a Police Officer", "F", 6, 300],
  ["PC 505", "Bribery of a Public Official", "F", 5, 300],
  ["PC 506", "Perjury", "F", 6, 250],
  ["PC 507", "Contempt of Court", "M", 3, 75],
  // 600 - Vehicle Code
  ["VC 600", "Speeding", "I", 0, 10],
  ["VC 601", "Reckless Driving", "M", 3, 100],
  ["VC 602", "Driving While Intoxicated", "M", 4, 150],
  ["VC 603", "Hit and Run", "F", 6, 325],
  ["VC 604", "Failure to Yield", "I", 0, 10],
  ["VC 605", "Evading Police", "F", 4, 300],
  ["VC 606", "Driving Without a License", "M", 2, 50],
  ["VC 607", "Illegal Parking", "I", 0, 5],
  ["VC 608", "Vehicular Assault", "F", 5, 350],
  ["VC 609", "Vehicular Manslaughter", "F", 8, 550],
  // 700 - Organized Crime
  ["PC 700", "Illegal Gambling", "M", 3, 75],
  ["PC 701", "Bootlegging", "F", 5, 200],
  ["PC 702", "Racketeering", "F", 7, 600],
  ["PC 703", "Loan Sharking", "F", 6, 400],
  ["PC 704", "Smuggling", "F", 6, 400],
  ["PC 705", "Operating an Illegal Business", "M", 4, 150],
  ["PC 706", "Criminal Conspiracy", "F", 6, 350],
  ["PC 707", "Aiding and Abetting", "M/F", 0, null, "variable"],
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
// the charge has no set figure (execution / variable). rate 1 = the base price.
const chargeBail = (c, rate = 1) => (typeof c.bail === "number" ? Math.round(c.bail * rate) : null);

// Total jail + bail for a set of charge codes, plus the special-case flags. `rate`
// scales every bail figure (the admin-tunable multiplier), rounded per charge.
function bookingTotal(codes, rate = 1) {
  let minutes = 0, bail = 0, execution = false, variable = false;
  const charges = [];
  for (const code of codes) {
    const ch = getCharge(code);
    if (!ch) continue;
    charges.push(ch);
    minutes += ch.min || 0;
    if (typeof ch.bail === "number") bail += Math.round(ch.bail * rate);
    if (ch.special === "execution") execution = true;
    if (ch.special === "variable")  variable = true;
  }
  return { minutes, bail, charges, execution, variable };
}

// Jail label: "Execution", "9 min", "9 min + based on the charge", "No jail time".
function sentenceLabel(minutes, opts = {}) {
  const { execution, variable } = opts || {};
  if (execution) return "Execution";
  const parts = [];
  if (minutes > 0) parts.push(`${minutes} min`);
  if (variable)    parts.push("based on the associated charge");
  return parts.join(" + ") || "No jail time";
}

// Bail label: "$250", "based on the associated charge", "No bail (execution)".
function bailLabel(bail, opts = {}) {
  const { execution, variable } = opts || {};
  if (execution) return "No bail (execution)";
  if (variable)  return "Based on the associated charge";
  return `$${Number(bail || 0).toLocaleString()}`;
}

// One charge as a short line for menus/summaries: jail + bail (at the given rate).
const chargeTime = (c, rate = 1) =>
  c.special === "execution" ? "Execution"
  : c.special === "variable" ? "Jail/bail by charge"
  : `${c.min > 0 ? `${c.min} min` : "No jail"} • $${chargeBail(c, rate).toLocaleString()}`;

module.exports = { SECTION_TITLES, CHARGES, SECTIONS, CLS, sectionList, getCharge, sectionOf, chargeBail, bookingTotal, sentenceLabel, bailLabel, chargeTime };
