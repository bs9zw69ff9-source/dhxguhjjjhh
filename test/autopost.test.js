/* The auto-post loop behind every board (leaderboard, arrests, player list, dashboard).

   The property that matters: a board must be EDITED IN PLACE, never re-posted. Two
   overlapping calls - the startup refresh and the 30s interval landing together - both
   used to find no tracked message, both send, and leave an orphan that the next channel
   purge deleted. From Discord that looks exactly like the board being wiped and reset
   instead of quietly updating. */
const { test } = require("node:test");
const assert = require("node:assert");
const path = require("path");

/* The module needs a large ctx; only the autopost path is exercised here, so the rest
   is stubbed. state/sent capture what the "channel" actually received. */
function makeBoards({ existingId = null, sendDelayMs = 20 } = {}) {
  const state = existingId ? { leaderboard: existingId } : {};
  const sent = [];
  const edits = [];
  const channel = {
    messages: {
      fetch: async (id) => {
        if (!sent.includes(id)) throw new Error("Unknown Message");
        return { edit: async () => { edits.push(id); } };
      },
    },
    send: async () => {
      await new Promise(r => setTimeout(r, sendDelayMs));   // a real send is not instant
      const id = `msg${sent.length + 1}`; sent.push(id); return { id };
    },
  };
  process.env.LEADERBOARD_CHANNEL = "chan1";
  delete require.cache[require.resolve("../leaderboards/index.js")];
  const boards = require("../leaderboards/index.js")({
    ACTIVE_SERVERS: ["server1"], ARREST_LEADERBOARD_CHANNEL: null, DASHBOARD_CHANNEL: null,
    DASHBOARD_INTERVAL_MS: 30000, DIVIDER: "", EmbedBuilder: class { setColor() { return this; } setTitle() { return this; } setDescription() { return this; } addFields() { return this; } },
    FILES: { AUTOPOST_STATE: "autopost", PLAYTIME: "pt", ARRESTS: "ar" },
    GLYPH: {}, LEADERBOARD_TOP_N: 10, NV: {}, allCachedPlayers: () => [],
    bar: () => "", brand: (e) => e, cell: () => "", client: { channels: { fetch: async () => channel } },
    loadMenuGrants: () => ({}), fs: require("fs"), getModsavePath: () => null,
    hero: (t) => t, logger: { info() {}, warn() {}, debug() {}, error() {} }, meter: () => "",
    parseRcon: () => ({}), path, refreshPlayerCache: async () => {},
    safeRead: (f, d) => (f === "autopost" ? { ...state } : d),
    safeWrite: (f, v) => { if (f === "autopost") Object.assign(state, v); return true; },
    sendRcon: async () => "{}", serverLabel: (s) => s,
    buildLeaderboardData: () => [], rankLabel: () => "",
  });
  // A trivial always-truthy embed keeps the test on the post/edit path.
  return { boards, state, sent, edits, post: () => boards.postLeaderboard() };
}

test("two overlapping posts send ONE message, not a duplicate", async () => {
  // This is the regression: the second call must wait, see the stored id, and edit.
  const { post, sent, state } = makeBoards();
  await Promise.all([post(), post()]);
  assert.equal(sent.length, 1, `expected one message, got ${sent.length} - the extra one gets purged and looks like a reset`);
  assert.equal(state.leaderboard, sent[0], "the tracked id must be the message that exists");
});

test("four simultaneous posts still send only one", async () => {
  const { post, sent } = makeBoards();
  await Promise.all([post(), post(), post(), post()]);
  assert.equal(sent.length, 1);
});

test("an existing tracked message is edited in place, never re-sent", async () => {
  const { boards, state, sent, edits } = makeBoards();
  await boards.postLeaderboard();          // first paint
  const firstId = state.leaderboard;
  await boards.postLeaderboard();          // subsequent refresh
  await boards.postLeaderboard();
  assert.equal(sent.length, 1, "only the first paint sends");
  assert.equal(edits.length, 2, "every later refresh is an edit");
  assert.equal(state.leaderboard, firstId, "the tracked id never changes");
});

test("a tracked message that no longer exists is replaced exactly once", async () => {
  // Someone deleted the board by hand: recover by posting one fresh message.
  const { boards, state, sent } = makeBoards({ existingId: "ghost" });
  await Promise.all([boards.postLeaderboard(), boards.postLeaderboard()]);
  assert.equal(sent.length, 1, "recovery must not itself create a duplicate");
  assert.equal(state.leaderboard, sent[0]);
});
