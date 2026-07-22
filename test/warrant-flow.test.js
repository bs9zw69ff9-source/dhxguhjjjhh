/* Runtime flow test for /warrant give|check|remove with STACKED warrants.
   Drives the real commands/warrant.js handler against a stubbed ctx backed by an
   in-memory warrant store (id -> list). Covers the police-role gate, required
   reason, stacking, numbered check, remove-one-by-number, remove-all, and the
   out-of-range / empty cases. */
const { test } = require("node:test");
const assert = require("node:assert");
const { disableValidators, EmbedBuilder } = require("discord.js");
disableValidators();

function makeCtx(store, over = {}) {
  const calls = [];
  const key = (p) => String(p).toLowerCase();
  const ctx = {
    EmbedBuilder, MessageFlags: { Ephemeral: 64 },
    NV: { RUST_RED: 1, IRRAD_GREEN: 2, AMBER: 3 },
    brand: (e) => e,
    emptyIdEmbed: () => new EmbedBuilder().setTitle("need id"),
    errorEmbed: (t, d) => new EmbedBuilder().setTitle(`err ${t}`).setDescription(String(d)),
    successEmbed: (t, d) => new EmbedBuilder().setTitle(`ok ${t}`).setDescription(String(d)),
    warningEmbed: (t, d) => new EmbedBuilder().setTitle(`warn ${t}`).setDescription(String(d)),
    policeOnlyEmbed: () => new EmbedBuilder().setTitle("Police only"),
    sanitizeId: (x) => String(x ?? "").trim(),
    hasPoliceRole: () => true,
    logAction: async () => {}, logPolice: () => {}, writeModLog: (...a) => calls.push(["writeModLog", ...a]),
    paginate: async (interaction, lines, render) => { await interaction.reply({ embeds: [render(lines)] }); },
    // in-memory stacked warrant store
    loadWarrants: () => ({ ...store }),
    getWarrants: (p) => store[key(p)] ? [...store[key(p)]] : [],
    addWarrant: async (p, reason, by, byId) => {
      const list = store[key(p)] ?? (store[key(p)] = []);
      list.push({ playerId: p, reason, by, byId, at: Date.now() });
      return list.length;
    },
    removeWarrant: async (p, index = null) => {
      const list = store[key(p)] ?? [];
      let removed = [];
      if (index == null) { removed = list.slice(); delete store[key(p)]; return { removed, remaining: 0 }; }
      const i = Number(index) - 1;
      if (i >= 0 && i < list.length) { removed = [list[i]]; list.splice(i, 1); }
      if (!list.length) delete store[key(p)];
      return { removed, remaining: store[key(p)]?.length ?? 0 };
    },
    ...over,
  };
  return { ctx, calls };
}

function makeInteraction(sub, opts = {}) {
  const out = { replies: [] };
  const interaction = {
    user: { id: "u1", tag: "Officer#1", username: "Officer" },
    member: { id: "u1" }, guild: null,
    options: {
      getSubcommand: () => sub,
      getString: (k) => opts.strings?.[k] ?? null,
      getInteger: (k) => opts.integers?.[k] ?? null,
    },
    reply: async (p) => { out.replies.push(p); },
    editReply: async (p) => { out.replies.push(p); },
    deferReply: async () => {},
  };
  return { interaction, out };
}
const text = (out) => out.replies.map(p => (p.embeds ?? []).map(e =>
  `${e.data.title ?? ""} ${e.data.description ?? ""}`).join(" ")).join(" ");

test("/warrant give requires the police role", async () => {
  const store = {};
  const { ctx } = makeCtx(store, { hasPoliceRole: () => false });
  const h = require("../commands/warrant.js")(ctx);
  const { interaction, out } = makeInteraction("give", { strings: { playerid: "Perp", reason: "robbery" } });
  await h.warrant(interaction, "warrant");
  assert.match(text(out), /Police only/);
  assert.deepEqual(store, {}, "no warrant issued without the role");
});

test("/warrant give requires a reason", async () => {
  const store = {};
  const { ctx } = makeCtx(store);
  const h = require("../commands/warrant.js")(ctx);
  const { interaction, out } = makeInteraction("give", { strings: { playerid: "Perp", reason: "   " } });
  await h.warrant(interaction, "warrant");
  assert.match(text(out), /Reason Required/);
  assert.deepEqual(store, {}, "no warrant issued without a reason");
});

test("/warrant warrants stack, then remove one by number and the rest", async () => {
  const store = {};
  const { ctx, calls } = makeCtx(store);
  const h = require("../commands/warrant.js")(ctx);
  // give twice -> stacks to 2
  for (const reason of ["speeding", "assault"]) {
    const { interaction } = makeInteraction("give", { strings: { playerid: "Perp", reason } });
    await h.warrant(interaction, "warrant");
  }
  assert.equal(store["perp"].length, 2, "two stacked warrants");
  assert.ok(calls.filter(c => c[0] === "writeModLog" && c[1].action === "warrant-give").length === 2);

  // check shows both, numbered
  {
    const { interaction, out } = makeInteraction("check", { strings: { playerid: "Perp" } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /Active Warrants - Perp \(2\)/);
    assert.match(text(out), /#1/); assert.match(text(out), /#2/);
    assert.match(text(out), /speeding/); assert.match(text(out), /assault/);
  }
  // remove #1 -> 1 remains (the assault one)
  {
    const { interaction, out } = makeInteraction("remove", { strings: { playerid: "Perp" }, integers: { number: 1 } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /Warrant Cleared - Perp/);
    assert.match(text(out), /\*\*1\*\* warrant left/);
    assert.equal(store["perp"].length, 1);
    assert.equal(store["perp"][0].reason, "assault");
  }
  // remove all -> gone
  {
    const { interaction, out } = makeInteraction("remove", { strings: { playerid: "Perp" } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /\*\*0\*\* warrants left/);
    assert.equal(store["perp"], undefined);
  }
});

test("/warrant remove: out-of-range number is rejected", async () => {
  const store = { perp: [{ playerId: "Perp", reason: "x", by: "o", at: Date.now() }] };
  const { ctx } = makeCtx(store);
  const h = require("../commands/warrant.js")(ctx);
  const { interaction, out } = makeInteraction("remove", { strings: { playerid: "Perp" }, integers: { number: 5 } });
  await h.warrant(interaction, "warrant");
  assert.match(text(out), /No Such Warrant/);
  assert.equal(store["perp"].length, 1, "nothing removed");
});

test("/warrant check on a clean slate", async () => {
  const store = {};
  const { ctx } = makeCtx(store);
  const h = require("../commands/warrant.js")(ctx);
  {
    const { interaction, out } = makeInteraction("check", { strings: { playerid: "Nobody" } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /No Warrant - Nobody/);
  }
  {
    const { interaction, out } = makeInteraction("check", { strings: {} });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /No Active Warrants/);
  }
});
