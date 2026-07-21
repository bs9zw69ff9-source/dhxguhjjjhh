/* Runtime flow test for /whitelist add -> list -> remove.
   Builds commands/factions.js against a stubbed ctx backed by an in-memory faux
   FactionRoles filesystem (real rank registry from factions/ranks), then drives
   the real handler with fake interactions and asserts on the resulting roster
   state + replies. Covers the happy path and the guard cases that protect the
   roster (one whitelist per player, duplicate add, empty id, unknown whitelist,
   removing someone who isn't on the list). */
const { test } = require("node:test");
const assert = require("node:assert");
const { disableValidators, EmbedBuilder } = require("discord.js");
disableValidators();
const { FACTION_RANKS } = require("../factions/ranks");

const SPAWN_FILE_MAP = { Gambino: "gambinospawn.txt", Colombo: "colombospawn.txt" };
const FILE_TO_FACTION = { "gambinospawn.txt": "Gambino", "colombospawn.txt": "Colombo" };

// In-memory faux filesystem: spawn[file] = [playerId,...]; rank[faction][idLower] = rank
function makeStore() {
  return { spawn: { "gambinospawn.txt": [], "colombospawn.txt": [] }, rank: { Gambino: {}, Colombo: {} } };
}

function makeCtx(store) {
  const calls = [];
  const factionOf = (file) => FILE_TO_FACTION[file];
  const ctx = {
    EmbedBuilder, MessageFlags: { Ephemeral: 64 },
    NV: { GOLD: 1, IRRAD_GREEN: 2, NCR_TAN: 3, AMBER: 4, RUST_RED: 5 },
    ALL_FACTIONS: Object.keys(SPAWN_FILE_MAP), SPAWN_FILE_MAP, FACTION_BAK_DIR: "/tmp/bak",
    DIVIDER: "", bar: () => "", meter: () => "", path: require("path"), spawn: () => {},
    brand: (e) => e, randomQuote: () => "q", rankLabel: (i) => `#${i}`,
    formatPlaytime: (m) => `${m}m`, loadPlaytime: () => ({}),
    emptyIdEmbed: () => new EmbedBuilder().setTitle("need id"),
    errorEmbed: (t, d) => new EmbedBuilder().setTitle(`err ${t}`).setDescription(String(d)),
    warningEmbed: (t, d) => new EmbedBuilder().setTitle(`warn ${t}`).setDescription(String(d)),
    successEmbed: (t, d) => new EmbedBuilder().setTitle(`ok ${t}`).setDescription(String(d)),
    ownerOnlyEmbed: () => new EmbedBuilder().setTitle("owner only"),
    sanitizeId: (x) => String(x ?? "").trim(),
    isOwner: () => true,
    logger: { info() {}, warn() {}, error() {} },
    logAction: async () => {}, confirmDialog: async () => true,
    // paginate: capture the first page's embed as a reply so we can assert on it
    paginate: async (interaction, lines, render) => { await interaction.reply({ embeds: [render(lines)] }); },
    writeModLog: (...a) => calls.push(["writeModLog", ...a]),
    writeFactionAudit: (...a) => calls.push(["writeFactionAudit", ...a]),
    wipeFaction: async () => ({ ok: true, count: 0 }),

    // ---- rank registry (real data) ----
    getFactionRankConfig: (f) => FACTION_RANKS[f] ?? null,
    getFactionRankOrder: (f) => FACTION_RANKS[f]?.order ?? [],
    getFactionDefaultRank: (f) => FACTION_RANKS[f]?.default ?? "Recruit",
    getFactionRankBadge: (f, r) => FACTION_RANKS[f]?.badges?.[r] ?? "",
    rankBadge: (f, r) => r ?? "",
    getFactionCap: () => 50,

    // ---- faux filesystem ----
    readFactionFile: (file) => (store.spawn[file] ? [...store.spawn[file]] : null),
    writeFactionFile: (file, lines) => { store.spawn[file] = [...lines]; calls.push(["writeFactionFile", file, [...lines]]); return true; },
    getPlayerFactions: (pid) => Object.entries(store.spawn)
      .filter(([, list]) => list.some(l => l.toLowerCase() === pid.toLowerCase()))
      .map(([file]) => factionOf(file)),
    getFactionMembers: (f) => {
      const file = SPAWN_FILE_MAP[f]; if (!store.spawn[file]) return null;
      return store.spawn[file].map(pid => {
        const r = store.rank[f][pid.toLowerCase()] ?? FACTION_RANKS[f].default;
        return { playerId: pid, rank: r, ranks: [r] };
      });
    },
    getFactionRank: (f, pid) => store.rank[f]?.[pid.toLowerCase()] ?? null,
    getPlayerRanks: (f, pid) => { const r = store.rank[f]?.[pid.toLowerCase()]; return r ? [r] : []; },
    addPlayerToRankFile: (f, pid, r) => { store.rank[f][pid.toLowerCase()] = r; calls.push(["addPlayerToRankFile", f, pid, r]); return true; },
    removePlayerFromAllRankFiles: (f, pid) => { delete store.rank[f][pid.toLowerCase()]; calls.push(["removePlayerFromAllRankFiles", f, pid]); },
    removePlayerFromAllSubclassFiles: (f, pid) => { calls.push(["removePlayerFromAllSubclassFiles", f, pid]); },
    setFactionRank: async (f, pid, r) => { store.rank[f][pid.toLowerCase()] = r; },
    removeFactionRank: async (f, pid) => { delete store.rank[f][pid.toLowerCase()]; },
    countFactionRank: (f, r) => Object.values(store.rank[f] || {}).filter(x => x === r).length,
  };
  return { ctx, calls };
}

function makeInteraction(sub, opts = {}) {
  const out = { replies: [] };
  const interaction = {
    user: { id: "u1", tag: "Boss#1", username: "Boss" },
    member: { id: "u1" }, guild: null,
    options: {
      getSubcommand: () => sub,
      getString: (k) => opts.strings?.[k] ?? null,
      getBoolean: (k) => opts.booleans?.[k] ?? null,
      getInteger: (k) => opts.integers?.[k] ?? null,
    },
    reply: async (p) => { out.replies.push(p); },
    editReply: async (p) => { out.replies.push(p); },
    deferReply: async () => {},
  };
  return { interaction, out };
}
const text = (out) => out.replies.map(p => (p.embeds ?? []).map(e =>
  `${e.data.title ?? ""} ${e.data.description ?? ""} ${(e.data.fields ?? []).map(f => `${f.name} ${f.value}`).join(" ")}`).join(" ")).join(" ");

test("/whitelist add -> list -> remove: full round trip", async () => {
  const store = makeStore();
  const { ctx, calls } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);

  // add
  {
    const { interaction, out } = makeInteraction("add", { strings: { playerid: "Tony", whitelist: "Gambino" } });
    await h.whitelist(interaction, "whitelist");
    assert.match(text(out), /Added to Gambino/, "add confirms");
    assert.deepEqual(store.spawn["gambinospawn.txt"], ["Tony"], "Tony is on the spawn file");
    assert.equal(store.rank.Gambino["tony"], "Associate", "default rank applied");
    assert.ok(calls.some(c => c[0] === "writeModLog" && c[1].action === "faction-add"));
  }
  // list shows Tony
  {
    const { interaction, out } = makeInteraction("list", { strings: { whitelist: "Gambino" } });
    await h.whitelist(interaction, "whitelist");
    assert.match(text(out), /Gambino - Roster/);
    assert.match(text(out), /Tony/);
  }
  // remove
  {
    const { interaction, out } = makeInteraction("remove", { strings: { playerid: "Tony", whitelist: "Gambino" } });
    await h.whitelist(interaction, "whitelist");
    assert.match(text(out), /Removed from Gambino/);
    assert.deepEqual(store.spawn["gambinospawn.txt"], [], "spawn file emptied");
    assert.equal(store.rank.Gambino["tony"], undefined, "rank cleared");
    assert.ok(calls.some(c => c[0] === "removePlayerFromAllRankFiles"));
  }
});

test("/whitelist add: one whitelist per player (blocks a second faction)", async () => {
  const store = makeStore();
  store.spawn["gambinospawn.txt"] = ["Tony"]; store.rank.Gambino["tony"] = "Associate";
  const { ctx } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);
  const { interaction, out } = makeInteraction("add", { strings: { playerid: "Tony", whitelist: "Colombo" } });
  await h.whitelist(interaction, "whitelist");
  assert.match(text(out), /Already in a Whitelist/);
  assert.deepEqual(store.spawn["colombospawn.txt"], [], "not added to the second whitelist");
});

test("/whitelist add: duplicate add to the same whitelist is rejected", async () => {
  const store = makeStore();
  store.spawn["gambinospawn.txt"] = ["Tony"]; store.rank.Gambino["tony"] = "Associate";
  const { ctx } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);
  const { interaction, out } = makeInteraction("add", { strings: { playerid: "tony", whitelist: "Gambino" } });
  await h.whitelist(interaction, "whitelist");
  assert.match(text(out), /Already Whitelisted/);
  assert.deepEqual(store.spawn["gambinospawn.txt"], ["Tony"], "no duplicate line written");
});

test("/whitelist add: empty id and unknown whitelist are rejected", async () => {
  const { ctx } = makeCtx(makeStore());
  const h = require("../commands/factions.js")(ctx);
  {
    const { interaction, out } = makeInteraction("add", { strings: { playerid: "   ", whitelist: "Gambino" } });
    await h.whitelist(interaction, "whitelist");
    assert.match(text(out), /need id/i);
  }
  {
    const { interaction, out } = makeInteraction("add", { strings: { playerid: "Tony", whitelist: "Nobody" } });
    await h.whitelist(interaction, "whitelist");
    assert.match(text(out), /Unknown Whitelist/);
  }
});

test("/whitelist remove: removing a non-member is a clean no-op", async () => {
  const store = makeStore();
  const { ctx, calls } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);
  const { interaction, out } = makeInteraction("remove", { strings: { playerid: "Ghost", whitelist: "Gambino" } });
  await h.whitelist(interaction, "whitelist");
  assert.match(text(out), /Not Whitelisted/);
  assert.ok(!calls.some(c => c[0] === "writeFactionFile"), "nothing written for a non-member");
});
