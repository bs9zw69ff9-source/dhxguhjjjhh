/* ---------------- penal/codes: the penal code (pure data + helpers) ----------------
   Every charge: { code, name, cls: "Misdemeanor"|"Felony", min } where `min` is the
   jail time in minutes. A charge with `untilSober: true` has an indefinite sentence
   ("until sober") and min 0 - it doesn't add to the numeric total. Charges are
   grouped into sections by the number before the dot (1.xx -> section 1). */

const SECTION_TITLES = {
  "1":  "Crimes Against Persons",
  "2":  "Property Crimes",
  "3":  "Public Decency",
  "4":  "Obstruction of Justice",
  "5":  "Public Order",
  "6":  "Controlled Substances",
  "7":  "Vehicular",
  "8":  "Weapons",
  "9":  "Commercial Vehicles",
  "10": "Aircraft",
  "11": "Organized Crime",
  "12": "Terrorism",
  "13": "Narcotics Influence",
};

// [code, name, class("M"|"F"), minutes]  -  "sober" minutes = indefinite (until sober)
const _RAW = [
  ["1.01", "Criminal Threats", "M", 2], ["1.02", "Assault", "M", 3], ["1.03", "Aggravated Assault", "F", 5],
  ["1.04", "Assault with a Deadly Weapon", "F", 6], ["1.05", "Battery", "M", 3], ["1.06", "Aggravated Battery", "F", 5],
  ["1.07", "Attempted Murder of a Peace Officer", "F", 6], ["1.08", "Voluntary Manslaughter", "F", 7],
  ["1.09", "Involuntary Manslaughter", "F", 6], ["1.10", "Vehicular Manslaughter", "F", 6], ["1.12", "Murder", "F", 7],
  ["1.13", "Murder of a Peace Officer", "F", 8], ["1.14", "Attempted Murder of a Peace Officer", "F", 7],
  ["1.15", "Kidnapping", "F", 6], ["1.16", "Hate Crimes", "M", 4],

  ["2.02", "Trespassing (Misdemeanor)", "M", 2], ["2.03", "Trespassing within Restricted Facility", "F", 6],
  ["2.04", "Trespassing with Malicious Intent", "M", 3], ["2.05", "Burglary", "M", 3],
  ["2.06", "Possession of Burglary Tools", "M", 2], ["2.07", "Robbery", "F", 6], ["2.08", "Armed Robbery", "F", 7],
  ["2.09", "Petty Theft (<$999)", "M", 2], ["2.10", "Grand Theft (>$1000)", "F", 6], ["2.11", "Receiving Stolen Property", "F", 3],
  ["2.12", "Sale of Stolen Goods", "M", 3], ["2.13", "Forgery/Fraud", "F", 4], ["2.14", "Bank Robbery", "F", 6],
  ["2.15", "Bribery", "F", 5], ["2.16", "Vandalism", "M", 2], ["2.19", "Destruction of Government Property", "M", 3],

  ["3.01", "Lewd or Dissolute Conduct in Public", "M", 3], ["3.02", "Indecent Exposure", "M", 3],

  ["4.01", "Dissuading a Victim", "F", 5], ["4.02", "Providing False Information to Government Official", "M", 3],
  ["4.03", "Filing a False Police Report", "M", 2], ["4.04", "Failure to Identify to a Peace Officer", "M", 2],
  ["4.05", "Impersonation of a Government Employee", "F", 5], ["4.06", "Obstruction of a Government Employee", "M", 2],
  ["4.07", "Resisting Arrest by a Peace Officer", "M", 2], ["4.08", "Impeding an Investigation", "M", 2],
  ["4.09", "Escape from Custody", "M", 3], ["4.10", "Tampering with Evidence", "M", 2], ["4.11", "Destruction of Evidence", "M", 2],
  ["4.12", "Falsifying Evidence", "M", 2], ["4.13", "Presenting False Evidence", "M", 2], ["4.14", "Failure to Appear in Court", "M", 3],
  ["4.15", "False Arrest", "M", 3], ["4.16", "Conspiracy to Commit a Crime", "F", 5], ["4.17", "Impeding the Court System", "M", 3],
  ["4.18", "Failure to Comply a Lawful Order", "M", 2], ["4.19", "Criminal Mischief", "M", 2], ["4.20", "False Imprisonment", "M", 3],
  ["4.21", "Failure to Pay Fines", "M", 2], ["4.22", "Perjury", "M", 2],

  ["5.01", "Disturbing the Peace", "M", 2], ["5.02", "Disorderly Conduct", "M", 2], ["5.03", "Incitement to Riot", "M", 3],
  ["5.04", "Inducing Panic", "M", 2], ["5.06", "Harassment", "M", 2],

  ["6.01", "Possession of a Controlled Substance", "M", 4], ["6.02", "Possession of a Controlled Substance with Intent to Distribute", "M", 4],
  ["6.03", "Maintaining a Place for the Purpose of Distribution", "F", 5], ["6.04", "Manufacture of a Controlled Substance", "F", 5],
  ["6.05", "Sale of a Controlled Substance", "F", 5], ["6.06", "Public Intoxication", "M", 2],
  ["6.07", "Possession of a Controlled Substance (>4 grams)", "F", 6], ["6.08", "Minor Alcohol Violation", "M", 2],

  ["7.01", "Unlicensed Driver - Impound", "M", 3], ["7.03", "Failure to Present Drivers License to a Peace Officer Upon Demand", "M", 1],
  ["7.04", "Traffic Accident (Death)", "M", 5], ["7.12", "Reckless Endangerment", "F", 5], ["7.14", "Speed Contest", "M", 2],
  ["7.17", "Street Racing", "M", 2], ["7.19", "Reckless Driving", "M", 3], ["7.20", "Driving Under the Influence", "M", 3],
  ["7.21", "Evading and Eluding a Peace Officer", "M", 4], ["7.22", "Evading and Eluding a Peace Officer - Impound", "F", 6],
  ["7.24", "Hit and Run (Property Damage Only)", "M", 2], ["7.25", "Hit and Run (Bodily Injury) - Impound", "M", 3],
  ["7.28", "Grand Theft Auto", "F", 6],

  ["8.01", "Possession of an Illegal Weapon", "M", 4], ["8.02", "Brandishing a Firearm", "M", 3],
  ["8.03", "Unlawful Discharge of a Firearm", "F", 6], ["8.04", "Possession of an Explosive Device", "F", 5],
  ["8.05", "Firing a Weapon from a Moving Vehicle", "F", 5], ["8.06", "Possession of a Firearm While Under the Influence", "M", 3],
  ["8.07", "Possession of Weapons with Intent to Sell", "F", 6],

  ["9.01", "Expired or No License to Operate Commercial Vehicles", "M", 2],
  ["9.02", "Trafficking a Controlled Substance Utilizing a Commercial Vehicle", "M", 4],

  ["10.01", "Failure to Meet Additional Certificate Requirement for Aircraft", "F", 5],
  ["10.02", "Operating Aircraft at Dangerous Altitude", "M", 3], ["10.03", "Operating an Aircraft Under the Influence of Alcohol or Drugs", "M", 4],
  ["10.05", "Reckless Maneuvers in an Aircraft", "M", 5],

  ["11.01", "Racketeering", "F", 6], ["11.02", "Gang Affiliation", "M", 3],
  ["11.03", "Racketeering Influenced and Criminal Organization Law", "F", 6], ["11.04", "Gang Solicitation", "M", 4],
  ["11.05", "Gang Related Activity", "M", 5],

  ["12.01", "Domestic Terrorism", "F", 8],

  ["13.01", "Under the Influence of Narcotics", "M", "sober"], ["13.02", "Possession of Marijuana Over 28.5 Grams", "M", 5],
];

const CHARGES = _RAW.map(([code, name, c, min]) => ({
  code, name,
  cls: c === "F" ? "Felony" : "Misdemeanor",
  min: min === "sober" ? 0 : min,
  untilSober: min === "sober",
}));

const BY_CODE = new Map(CHARGES.map(ch => [ch.code, ch]));
const sectionOf = (code) => String(code).split(".")[0];

// section number -> [charges], in code order
const SECTIONS = {};
for (const ch of CHARGES) (SECTIONS[sectionOf(ch.code)] ??= []).push(ch);

// [{ num, title, count }] sorted numerically - for the section dropdown
const sectionList = () => Object.keys(SECTIONS)
  .sort((a, b) => Number(a) - Number(b))
  .map(num => ({ num, title: SECTION_TITLES[num] || `Section ${num}`, count: SECTIONS[num].length }));

const getCharge = (code) => BY_CODE.get(String(code)) || null;

// Total sentence for a set of charge codes: numeric minutes + whether any is "until sober".
function bookingTotal(codes) {
  let minutes = 0, untilSober = false;
  const charges = [];
  for (const code of codes) {
    const ch = getCharge(code);
    if (!ch) continue;
    charges.push(ch);
    minutes += ch.min;
    if (ch.untilSober) untilSober = true;
  }
  return { minutes, untilSober, charges };
}

// Human label for a sentence: "9 min", "until sober", or "9 min + until sober".
function sentenceLabel(minutes, untilSober) {
  const parts = [];
  if (minutes > 0) parts.push(`${minutes} min`);
  if (untilSober)  parts.push("until sober");
  return parts.join(" + ") || "0 min";
}

module.exports = { SECTION_TITLES, CHARGES, SECTIONS, sectionList, getCharge, sectionOf, bookingTotal, sentenceLabel };
