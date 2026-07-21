/* Runtime flow test for /warrant give|check|remove.
   Drives the real commands/warrant.js handler against a stubbed ctx backed by an
   in-memory warrant store, asserting on the store state and the replies. Covers
   the police-role gate, the required reason, give/check/remove, and the
   no-warrant cases. */
const { test } = require("node:test");
const assert = require("node:assert");
const { disableValidators, EmbedBuilder } = require("discord.js");
disableValidators();

function makeCtx(store, over = {}) {
  const calls = [];
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
    logAction: async () => {}, writeModLog: (...a) => calls.push(["writeModLog", ...a]),
    paginate: async (interaction, lines, render) => { await interaction.reply({ embeds: [render(lines)] }); },
    // in-memory warrant store
    loadWarrants: () => ({ ...store }),
    getWarrant: (pid) => store[String(pid).toLowerCase()] ?? null,
    setWarrant: async (pid, reason, by, byId) => { store[String(pid).toLowerCase()] = { playerId: pid, reason, by, byId, at: Date.now() }; },
    removeWarrant: async (pid) => { delete store[String(pid).toLowerCase()]; },
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

test("/warrant give -> check -> remove round trip", async () => {
  const store = {};
  const { ctx, calls } = makeCtx(store);
  const h = require("../commands/warrant.js")(ctx);
  // give
  {
    const { interaction, out } = makeInteraction("give", { strings: { playerid: "Perp", reason: "grand theft auto" } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /Warrant Issued - Perp/);
    assert.equal(store["perp"].reason, "grand theft auto");
    assert.ok(calls.some(c => c[0] === "writeModLog" && c[1].action === "warrant-give"));
  }
  // check single (case-insensitive)
  {
    const { interaction, out } = makeInteraction("check", { strings: { playerid: "perp" } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /Active Warrant - perp/);
    assert.match(text(out), /grand theft auto/);
  }
  // check all
  {
    const { interaction, out } = makeInteraction("check", { strings: {} });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /Active Warrants \(1\)/);
  }
  // remove
  {
    const { interaction, out } = makeInteraction("remove", { strings: { playerid: "Perp" } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /Warrant Cleared - Perp/);
    assert.equal(store["perp"], undefined);
    assert.ok(calls.some(c => c[0] === "writeModLog" && c[1].action === "warrant-remove"));
  }
});

test("/warrant check and remove handle a clean slate", async () => {
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
  {
    const { interaction, out } = makeInteraction("remove", { strings: { playerid: "Nobody" } });
    await h.warrant(interaction, "warrant");
    assert.match(text(out), /No Warrant/);
  }
});
