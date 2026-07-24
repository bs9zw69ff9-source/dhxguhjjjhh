/* Unit test for the arrests / jail-time "most wanted" board data + embed.
   Drives the real leaderboards module against a stubbed ctx whose safeRead returns
   a fixed arrests map, so we exercise the ranking (jail time, then arrest count),
   the display-case name preference, and the empty case. */
const { test } = require("node:test");
const assert = require("node:assert");
const { disableValidators, EmbedBuilder } = require("discord.js");
disableValidators();

function makeBoards(arrests) {
  const ctx = {
    ACTIVE_SERVERS: ["server1"], ARREST_LEADERBOARD_CHANNEL: "chan", DASHBOARD_CHANNEL: "",
    DASHBOARD_INTERVAL_MS: 1, DIVIDER: "-", EmbedBuilder, FILES: { ARRESTS: "arrests", AUTOPOST_STATE: "auto", SERVER_STATS: "stats" },
    GLYPH: { rank: "R", caps: "$", up: "^", down: "v", dot: "•" }, LEADERBOARD_TOP_N: 30,
    NV: { GOLD: 1, RUST_RED: 2, IRRAD_GREEN: 3 }, PLAYERLIST_CHANNEL: "", allCachedPlayers: () => [],
    bar: () => "====", brand: (e) => e, cell: (s) => String(s), client: {},
    loadMenuGrants: () => ({}), fs: {}, getModsavePath: () => null, hero: (t) => t,
    logger: { info() {}, warn() {}, error() {} }, meter: () => "",
    parseRcon: () => ({}), path: {}, refreshPlayerCache: async () => {},
    safeRead: (file) => (file === "arrests" ? arrests : {}), safeWrite: () => {},
    sendRcon: async () => ({}), serverLabel: (s) => s, playerCache: { server1: [] }, easternClock: () => ({ date: "2026-01-01" }),
  };
  return require("../leaderboards/index.js")(ctx);
}
const embedText = (e) => `${e.data.title ?? ""} ${e.data.description ?? ""}`;

test("ranks by total jail time, then arrest count, and totals every offender", () => {
  const now = Date.now();
  const arrests = {
    // key is lowercased; entries carry the display-case playerId
    ricky:  [{ playerId: "Ricky",  minutes: 4, at: now }, { playerId: "Ricky",  minutes: 6, at: now }],  // 2 arrests, 10 min
    sal:    [{ playerId: "Sal",    minutes: 8, at: now }],                                                 // 1 arrest, 8 min
    trashy: [{ playerId: "Trashy", minutes: 2, at: now }, { playerId: "Trashy", minutes: 2, at: now }, { playerId: "Trashy", minutes: 2, at: now }], // 3 arrests, 6 min
  };
  const b = makeBoards(arrests);
  const rows = b.buildArrestBoardData();
  assert.deepEqual(rows.map(r => r.playerId), ["Ricky", "Sal", "Trashy"]);   // by jail: 10, 8, 6
  assert.deepEqual(rows.map(r => r.arrests), [2, 1, 3]);
  assert.equal(rows.totalArrests, 6);
  assert.equal(rows.totalJail, 24);
  assert.equal(rows.totalPlayers, 3);

  const txt = embedText(b.buildArrestBoardEmbed());
  assert.match(txt, /Most Wanted/);
  assert.match(txt, /Ricky\*\*  -  2 arrests, 10 min/);
  assert.match(txt, /Combined: 6 arrests, 24 min served/);
  assert.match(txt, /across \*\*3\*\* offenders/);
});

test("ties on jail time break by arrest count", () => {
  const arrests = {
    a: [{ playerId: "A", minutes: 10 }],
    b: [{ playerId: "B", minutes: 5 }, { playerId: "B", minutes: 5 }],   // same 10 min, more arrests -> ranks first
  };
  const rows = makeBoards(arrests).buildArrestBoardData();
  assert.deepEqual(rows.map(r => r.playerId), ["B", "A"]);
});

test("falls back to the map key when an entry has no display name", () => {
  const rows = makeBoards({ oldrecord: [{ minutes: 3 }] }).buildArrestBoardData();
  assert.equal(rows[0].playerId, "oldrecord");
});

test("empty registry renders the quiet-night board", () => {
  const b = makeBoards({});
  assert.equal(b.buildArrestBoardData().length, 0);
  assert.match(embedText(b.buildArrestBoardEmbed()), /No arrests have been recorded yet/);
});
