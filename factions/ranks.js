/* ---------------- factions/ranks: faction rank registry (pure data) ----------------
   Extracted from index.js. Per-faction rank order, display badges, and the
   Config/*.txt rank-file names. No logic and no bot state - just the data the
   faction rank helpers in index.js read. `order` is low -> high; `default` is the
   lowest rank; every rank has a badge entry and a globally-unique .txt rank file. */
const FACTION_RANKS = {
  "Gambino": {
    order:   ["Associate", "Soldier", "Capo", "Consigliere", "Underboss", "Boss"],
    default: "Associate",
    badges:  {
      "Associate":   "",
      "Soldier":     "",
      "Capo":        "",
      "Consigliere": "",
      "Underboss":   "",
      "Boss":        "",
    },
    rankFiles: {
      "Associate":   "gambinoassociate.txt",
      "Soldier":     "gambinosoldier.txt",
      "Capo":        "gambinocapo.txt",
      "Consigliere": "gambinoconsigliere.txt",
      "Underboss":   "gambinounderboss.txt",
      "Boss":        "gambinoboss.txt",
    },
    // Per-rank member limits. A rank not listed here is uncapped (Consigliere).
    rankCaps: {
      "Associate": 18,
      "Soldier":   12,
      "Capo":       3,
      "Underboss":  1,
      "Boss":       1,
    },
  },
  "Colombo": {
    order:   ["Associate", "Soldier", "Capo", "Consigliere", "Underboss", "Boss"],
    default: "Associate",
    badges:  {
      "Associate":   "",
      "Soldier":     "",
      "Capo":        "",
      "Consigliere": "",
      "Underboss":   "",
      "Boss":        "",
    },
    rankFiles: {
      "Associate":   "colomboassociate.txt",
      "Soldier":     "colombosoldier.txt",
      "Capo":        "colombocapo.txt",
      "Consigliere": "colomboconsigliere.txt",
      "Underboss":   "colombounderboss.txt",
      "Boss":        "colomboboss.txt",
    },
    // Per-rank member limits. A rank not listed here is uncapped (Consigliere).
    rankCaps: {
      "Associate": 18,
      "Soldier":   12,
      "Capo":       3,
      "Underboss":  1,
      "Boss":       1,
    },
  },
  "NYPD": {
    order:   ["Cadet", "Patrolman", "Corporal", "Sergeant", "Lieutenant", "Captain", "Deputy Chief", "Chief of Police"],
    default: "Cadet",
    badges:  {
      "Cadet":           "",
      "Patrolman":       "",
      "Corporal":        "",
      "Sergeant":        "",
      "Lieutenant":      "",
      "Captain":         "",
      "Deputy Chief":    "",
      "Chief of Police": "",
    },
    rankFiles: {
      "Cadet":           "policecadet.txt",
      "Patrolman":       "policepatrolman.txt",
      "Corporal":        "policecorporal.txt",
      "Sergeant":        "policesergeant.txt",
      "Lieutenant":      "policelieutenant.txt",
      "Captain":         "policecaptain.txt",
      "Deputy Chief":    "policedeputychief.txt",
      "Chief of Police": "policechief.txt",
    },
    // Per-rank member limits. Every NYPD rank is capped.
    rankCaps: {
      "Cadet":           50,
      "Patrolman":       20,
      "Corporal":        15,
      "Sergeant":        20,
      "Lieutenant":       8,
      "Captain":          4,
      "Deputy Chief":     1,
      "Chief of Police":  1,
    },
    // Sub-classes are NOT ranks - a member keeps their rank and can also be
    // assigned one of these (its own FactionRoles .txt whitelist), via /subclass.
    subclasses: {
      "Vice Officer":           "policevice.txt",
      "Detective":              "policedetective.txt",
      "Tactical Response Unit": "policetacticalresponse.txt",
    },
  },
};

module.exports = { FACTION_RANKS };
