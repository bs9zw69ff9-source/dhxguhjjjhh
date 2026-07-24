/* Runtime flow test for /promotion, /demotion and /subclass.
   Drives the real commands/factions.js handlers against a stubbed ctx backed by
   an in-memory model (membership, single rank per member, sub-class sets) using
   the real NYPD rank ladder + sub-classes. */
const { test } = require("node:test");
const assert = require("node:assert");
const { disableValidators, EmbedBuilder } = require("discord.js");
disableValidators();
const { FACTION_RANKS } = require("../factions/ranks");

function makeStore() {
  return {
    member: { NYPD: new Set(), Gambino: new Set() },   // faction -> Set(idLower)
    rank:   { NYPD: {}, Gambino: {} },                 // faction -> idLower -> rank
    sub:    { NYPD: {}, Gambino: {} },                 // faction -> idLower -> Set(subclass)
  };
}

function makeCtx(store, over = {}) {
  const calls = [];
  const key = (p) => String(p).toLowerCase();
  const ctx = {
    EmbedBuilder, MessageFlags: { Ephemeral: 64 }, NV: { GOLD: 1, IRRAD_GREEN: 2, AMBER: 3, NCR_TAN: 4, RUST_RED: 5 },
    brand: (e) => e, logAction: async () => {}, logger: { info() {}, warn() {}, error() {} },
    rankBadge: (f, r) => r ?? "",
    sanitizeId: (x) => String(x ?? "").trim(),
    emptyIdEmbed: () => new EmbedBuilder().setTitle("need id"),
    warningEmbed: (t, d) => new EmbedBuilder().setTitle(`warn ${t}`).setDescription(String(d)),
    errorEmbed: (t, d) => new EmbedBuilder().setTitle(`err ${t}`).setDescription(String(d)),
    factionLeaderOnlyEmbed: () => new EmbedBuilder().setTitle("leaders only"),
    hasModRole: () => false, hasWhitelistManageRole: () => true,
    writeFactionAudit: (...a) => calls.push(["audit", ...a]), writeModLog: (...a) => calls.push(["modlog", ...a]),
    getPlayerFactions: (pid) => Object.entries(store.member).filter(([, s]) => s.has(key(pid))).map(([f]) => f),
    getFactionRankOrder: (f) => FACTION_RANKS[f]?.order ?? [],
    getFactionRank: (f, pid) => store.rank[f]?.[key(pid)] ?? FACTION_RANKS[f]?.default,
    removePlayerFromAllRankFiles: (f, pid) => { delete store.rank[f][key(pid)]; },
    addPlayerToRankFile: (f, pid, r) => { store.rank[f][key(pid)] = r; return true; },
    setFactionRank: async (f, pid, r) => { store.rank[f][key(pid)] = r; },
    getFactionSubclasses: (f) => FACTION_RANKS[f]?.subclasses ?? {},
    getPlayerSubclasses: (f, pid) => [...(store.sub[f]?.[key(pid)] ?? [])],
    addPlayerToSubclassFile: (f, pid, sc) => { (store.sub[f][key(pid)] ??= new Set()).add(sc); return true; },
    removePlayerFromSubclassFile: (f, pid, sc) => { store.sub[f]?.[key(pid)]?.delete(sc); return true; },
    // unused-by-these-handlers ctx the module also destructures:
    ALL_FACTIONS: Object.keys(FACTION_RANKS), SPAWN_FILE_MAP: {}, getFactionRankConfig: (f) => FACTION_RANKS[f],
    getFactionDefaultRank: (f) => FACTION_RANKS[f]?.default, getFactionMembers: () => null, getFactionRankBadge: () => "",
    getFactionCap: () => 50, countFactionRank: () => 0, getPlayerRanks: () => [], readFactionFile: () => [],
    writeFactionFile: () => true, removeFactionRank: async () => {}, removePlayerFromAllSubclassFiles: () => {},
    isOwner: () => false, loadPlaytime: () => ({}), paginate: async () => {}, confirmDialog: async () => true,
    wipeFaction: async () => ({}), successEmbed: (t, d) => new EmbedBuilder().setTitle(t).setDescription(String(d)),
    ownerOnlyEmbed: () => new EmbedBuilder().setTitle("owner"), bar: () => "", meter: () => "", rankLabel: () => "",
    formatPlaytime: () => "", path: require("path"), spawn: () => {}, update: async () => {},
    DIVIDER: "", FACTION_BAK_DIR: "/tmp", addPlayerToRankFile2: null,
    ...over,
  };
  return { ctx, calls };
}

function makeInteraction(opts = {}) {
  const out = { replies: [] };
  const interaction = {
    user: { id: "u1", tag: "Cap#1", username: "Cap" }, member: { id: "u1" }, guild: null,
    options: { getString: (k) => opts.strings?.[k] ?? null, getBoolean: (k) => opts.booleans?.[k] ?? null, getSubcommand: () => opts.sub ?? null },
    reply: async (p) => { out.replies.push(p); }, editReply: async (p) => { out.replies.push(p); }, deferReply: async () => {},
  };
  return { interaction, out };
}
const text = (out) => out.replies.map(p => (p.embeds ?? []).map(e => `${e.data.title ?? ""} ${e.data.description ?? ""}`).join(" ")).join(" ");

test("/promotion and /demotion walk the NYPD rank ladder", async () => {
  const store = makeStore(); store.member.NYPD.add("tony");   // starts at Patrolman (default)
  const { ctx } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);

  const promote = async () => { const { interaction, out } = makeInteraction({ strings: { playerid: "Tony" } }); await h.promotion(interaction); return text(out); };
  const demote  = async () => { const { interaction, out } = makeInteraction({ strings: { playerid: "Tony" } }); await h.demotion(interaction); return text(out); };

  assert.match(await promote(), /Patrolman.*Corporal/);
  assert.equal(store.rank.NYPD["tony"], "Corporal");
  assert.match(await promote(), /Corporal.*Sergeant/);
  assert.equal(store.rank.NYPD["tony"], "Sergeant");
  assert.match(await demote(), /Sergeant.*Corporal/);
  assert.equal(store.rank.NYPD["tony"], "Corporal");
});

test("/demotion at the lowest rank is refused", async () => {
  const store = makeStore(); store.member.NYPD.add("rook");   // Patrolman
  const { ctx } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);
  const { interaction, out } = makeInteraction({ strings: { playerid: "Rook" } });
  await h.demotion(interaction);
  assert.match(text(out), /Lowest Rank/);
  assert.equal(store.rank.NYPD["rook"] ?? "Patrolman", "Patrolman");
});

test("/promotion at the highest rank is refused", async () => {
  const store = makeStore(); store.member.NYPD.add("chief"); store.rank.NYPD["chief"] = "Chief of Police";
  const { ctx } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);
  const { interaction, out } = makeInteraction({ strings: { playerid: "Chief" } });
  await h.promotion(interaction);
  assert.match(text(out), /Highest Rank/);
  assert.equal(store.rank.NYPD["chief"], "Chief of Police");
});

test("/promotion on a non-member is refused", async () => {
  const { ctx } = makeCtx(makeStore());
  const h = require("../commands/factions.js")(ctx);
  const { interaction, out } = makeInteraction({ strings: { playerid: "Ghost" } });
  await h.promotion(interaction);
  assert.match(text(out), /Not Whitelisted/);
});

test("/subclass assigns and removes, and rejects duplicates / bad sub-classes", async () => {
  const store = makeStore(); store.member.NYPD.add("nick");
  const { ctx } = makeCtx(store);
  const h = require("../commands/factions.js")(ctx);
  // assign Detective
  {
    const { interaction, out } = makeInteraction({ strings: { playerid: "Nick", subclass: "Detective" } });
    await h.subclass(interaction);
    assert.match(text(out), /Sub-class Assigned/);
    assert.deepEqual([...store.sub.NYPD["nick"]], ["Detective"]);
  }
  // assign again -> already
  {
    const { interaction, out } = makeInteraction({ strings: { playerid: "Nick", subclass: "Detective" } });
    await h.subclass(interaction);
    assert.match(text(out), /Already Assigned/);
  }
  // remove
  {
    const { interaction, out } = makeInteraction({ strings: { playerid: "Nick", subclass: "Detective" }, booleans: { remove: true } });
    await h.subclass(interaction);
    assert.match(text(out), /Sub-class Removed/);
    assert.equal(store.sub.NYPD["nick"].size, 0);
  }
  // a faction without that sub-class (Gambino) is rejected
  {
    const s2 = makeStore(); s2.member.Gambino.add("sal");
    const { ctx: c2 } = makeCtx(s2);
    const h2 = require("../commands/factions.js")(c2);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Sal", subclass: "Detective" } });
    await h2.subclass(interaction);
    assert.match(text(out), /No Such Sub-class/);
  }
});

test("/subclass and /promotion require manage permission", async () => {
  const store = makeStore(); store.member.NYPD.add("tony");
  const { ctx } = makeCtx(store, { hasModRole: () => false, hasWhitelistManageRole: () => false });
  const h = require("../commands/factions.js")(ctx);
  {
    const { interaction, out } = makeInteraction({ strings: { playerid: "Tony" } });
    await h.promotion(interaction);
    assert.match(text(out), /leaders only/);
  }
  {
    const { interaction, out } = makeInteraction({ strings: { playerid: "Tony", subclass: "Detective" } });
    await h.subclass(interaction);
    assert.match(text(out), /leaders only/);
  }
});
