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
  },
  "NYPD": {
    order:   ["Patrolman", "Corporal", "Sergeant", "Lieutenant", "Captain", "Deputy Chief", "Chief of Police"],
    default: "Patrolman",
    badges:  {
      "Patrolman":       "",
      "Corporal":        "",
      "Sergeant":        "",
      "Lieutenant":      "",
      "Captain":         "",
      "Deputy Chief":    "",
      "Chief of Police": "",
    },
    rankFiles: {
      "Patrolman":       "policepatrolman.txt",
      "Corporal":        "policecorporal.txt",
      "Sergeant":        "policesergeant.txt",
      "Lieutenant":      "policelieutenant.txt",
      "Captain":         "policecaptain.txt",
      "Deputy Chief":    "policedeputychief.txt",
      "Chief of Police": "policechief.txt",
    },
  },
};

module.exports = { FACTION_RANKS };
