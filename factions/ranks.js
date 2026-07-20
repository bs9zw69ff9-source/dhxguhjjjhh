/* ---------------- factions/ranks: per-RP rank registries + spawn maps (pure data) ----------------
   Two RP profiles ship in one codebase, selected by the RP_PROFILE env var so the
   same code can run as different bots:

     RP_PROFILE=rp1  (default)  normal RP - mafia + police whitelists
     RP_PROFILE=rp2             Fallout RP - the classic faction roster

   Each profile carries its faction rank registry (`order` low -> high, `default`
   is the lowest rank, every rank has a badge entry + a globally-unique .txt rank
   file) and its faction -> spawn-file map. No logic and no bot state - just data
   the faction helpers in index.js read. */

const RP1_RANKS = {
  "Gambino": {
    order:   ["Associate", "Soldier", "Capo", "Consigliere", "Underboss", "Boss"],
    default: "Associate",
    badges:  { "Associate": "", "Soldier": "", "Capo": "", "Consigliere": "", "Underboss": "", "Boss": "" },
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
    badges:  { "Associate": "", "Soldier": "", "Capo": "", "Consigliere": "", "Underboss": "", "Boss": "" },
    rankFiles: {
      "Associate":   "colomboassociate.txt",
      "Soldier":     "colombosoldier.txt",
      "Capo":        "colombocapo.txt",
      "Consigliere": "colomboconsigliere.txt",
      "Underboss":   "colombounderboss.txt",
      "Boss":        "colomboboss.txt",
    },
  },
  "Police Department": {
    order:   ["Patrolman", "Corporal", "Sergeant", "Lieutenant", "Captain", "Deputy Chief", "Chief of Police"],
    default: "Patrolman",
    badges:  { "Patrolman": "", "Corporal": "", "Sergeant": "", "Lieutenant": "", "Captain": "", "Deputy Chief": "", "Chief of Police": "" },
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

const RP1_SPAWN_FILE_MAP = {
  "Gambino":           "gambinospawn.txt",
  "Colombo":           "colombospawn.txt",
  "Police Department": "policespawn.txt",
};

/* Fallout roster - Khans and Super Mutants intentionally dropped from the classic set. */
const RP2_RANKS = {
  "NCR": {
    order:   ["Private", "Corporal", "Sergeant", "Medic", "Heavy", "Power Armor", "MP", "Ranger", "Lieutenant", "Officer"],
    default: "Private",
    badges:  { "Private": "", "Corporal": "", "Sergeant": "", "Medic": "", "Heavy": "", "Power Armor": "", "MP": "", "Ranger": "", "Lieutenant": "", "Officer": "" },
    rankFiles: {
      "Private":     "ncrprivate.txt",
      "Corporal":    "ncrcorporal.txt",
      "Sergeant":    "ncrsergeant.txt",
      "Medic":       "ncrmedix.txt",
      "Heavy":       "ncrheavy.txt",
      "Power Armor": "ncrpowerarmor.txt",
      "MP":          "ncrmp.txt",
      "Ranger":      "ncrranger.txt",
      "Lieutenant":  "ncrlieutenant.txt",
      "Officer":     "ncrofficer.txt",
    },
  },
  "Legion": {
    order:   ["Recruit", "Legionnaire", "Explorer", "Slavemaster", "Prime Legionary", "Veteran Legionnaire", "Vexalarius", "Centurion", "Assassin", "Praetorian", "Legate"],
    default: "Recruit",
    badges:  { "Recruit": "", "Legionnaire": "", "Explorer": "", "Slavemaster": "", "Prime Legionary": "", "Veteran Legionnaire": "", "Vexalarius": "", "Centurion": "", "Assassin": "", "Praetorian": "", "Legate": "" },
    rankFiles: {
      "Recruit":             "legionrecruit.txt",
      "Legionnaire":         "legionlegionnaire.txt",
      "Explorer":            "legionexplorer.txt",
      "Slavemaster":         "legionslavemaster.txt",
      "Prime Legionary":     "legionprimelegionary.txt",
      "Veteran Legionnaire": "legionveteranlegionnaire.txt",
      "Vexalarius":          "legionvexalarius.txt",
      "Centurion":           "legioncenturion.txt",
      "Assassin":            "legionassasin.txt",
      "Praetorian":          "legionpraetorian.txt",
      "Legate":              "legionlegate.txt",
    },
  },
  "Enclave": {
    order:   ["Brigadeless", "Truth and Justice", "Research", "56th Rifle Brigade", "Recon", "Demolition", "Mechanized", "Hellfire", "Officer"],
    default: "Brigadeless",
    badges:  { "Brigadeless": "", "Truth and Justice": "", "Research": "", "56th Rifle Brigade": "", "Recon": "", "Demolition": "", "Mechanized": "", "Hellfire": "", "Officer": "" },
    rankFiles: {
      "Brigadeless":        "enclavebrigadeless.txt",
      "Truth and Justice":  "enclavetruthandjustice.txt",
      "Research":           "enclaveresearch.txt",
      "56th Rifle Brigade": "enclave56thriflebrigade.txt",
      "Recon":              "enclaverecon.txt",
      "Demolition":         "enclavedemolition.txt",
      "Mechanized":         "enclavemechanized.txt",
      "Hellfire":           "enclavehellfire.txt",
      "Officer":            "enclaveofficer.txt",
    },
  },
  "Brotherhood of Steel": {
    order:   ["Initiate", "Knight", "Paladin", "Elder"],
    default: "Initiate",
    badges:  { "Initiate": "", "Knight": "", "Paladin": "", "Elder": "" },
    rankFiles: {
      "Initiate": "bosinitiate.txt",
      "Knight":   "bosknight.txt",
      "Paladin":  "bospaladin.txt",
      "Elder":    "boselder.txt",
    },
  },
  "Kings": {
    order:   ["Prospect", "Silver Ace", "Guard", "High Roller", "Crown", "The King"],
    default: "Prospect",
    badges:  { "Prospect": "", "Silver Ace": "", "Guard": "", "High Roller": "", "Crown": "", "The King": "" },
    rankFiles: {
      "Prospect":    "kingsprospect.txt",
      "Silver Ace":  "kingssilverace.txt",
      "Guard":       "kingsguard.txt",
      "High Roller": "kingshighroller.txt",
      "Crown":       "kingscrown.txt",
      "The King":    "kingstheking.txt",
    },
  },
  "Followers": {
    order:   ["Follower", "Guard", "Recruit", "Scholar", "Director"],
    default: "Follower",
    badges:  { "Follower": "", "Guard": "", "Recruit": "", "Scholar": "", "Director": "" },
    rankFiles: {
      "Follower": "followersfollower.txt",
      "Guard":    "followersguard.txt",
      "Recruit":  "followersrecruit.txt",
      "Scholar":  "followersscholar.txt",
      "Director": "followersdirector.txt",
    },
  },
};

const RP2_SPAWN_FILE_MAP = {
  "NCR":                  "ncrspawn.txt",
  "Legion":               "legionspawn.txt",
  "Enclave":              "enclavespawn.txt",
  "Brotherhood of Steel": "bosspawn.txt",
  "Kings":                "kingsspawn.txt",
  "Followers":            "followersspawn.txt",
};

const PROFILES = {
  rp1: { FACTION_RANKS: RP1_RANKS, SPAWN_FILE_MAP: RP1_SPAWN_FILE_MAP },
  rp2: { FACTION_RANKS: RP2_RANKS, SPAWN_FILE_MAP: RP2_SPAWN_FILE_MAP },
};

const RP_PROFILE = (process.env.RP_PROFILE || "rp1").toLowerCase();
const active = PROFILES[RP_PROFILE] || PROFILES.rp1;

const FACTION_RANKS  = active.FACTION_RANKS;
const SPAWN_FILE_MAP = active.SPAWN_FILE_MAP;
// spawn-file basename (no .txt) -> faction name, used by the log tailer.
const FACTION_SPAWN_MAP = Object.fromEntries(
  Object.entries(SPAWN_FILE_MAP).map(([name, file]) => [file.replace(/\.txt$/, ""), name])
);
const ALL_FACTIONS = Object.keys(SPAWN_FILE_MAP);
// RP2 turns on the extra command surface (stats/kd, rank caps + /whitelist rank,
// the playtime leaderboard). RP1 stays lean.
const IS_RP2 = RP_PROFILE === "rp2";

module.exports = {
  RP_PROFILE, IS_RP2, PROFILES,
  FACTION_RANKS, SPAWN_FILE_MAP, FACTION_SPAWN_MAP, ALL_FACTIONS,
};
