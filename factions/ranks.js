/* ---------------- factions/ranks: faction rank registry (pure data) ----------------
   Extracted from index.js. Per-faction rank order, display badges, and the
   Config/*.txt rank-file names. No logic and no bot state - just the data the
   faction rank helpers in index.js read. */
const FACTION_RANKS = {
  // No factions configured yet. Add each faction here as:
  //   "Faction Name": {
  //     order:   ["Rank1", "Rank2", ...],   // low -> high
  //     default: "Rank1",
  //     badges:  { "Rank1": "", ... },       // optional emoji per rank
  //     rankFiles: { "Rank1": "factionrank1.txt", ... },  // globally-unique .txt names
  //   },
};

module.exports = { FACTION_RANKS };
