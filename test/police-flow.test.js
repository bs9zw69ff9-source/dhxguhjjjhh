/* Runtime flow test for /backgroundcheck and /suspendrank against a stubbed ctx.
   (/arrest is an interactive component loop and is covered by the penal-code unit
   tests instead.) */
const { test } = require("node:test");
const assert = require("node:assert");
const { disableValidators, EmbedBuilder } = require("discord.js");
disableValidators();

function makeCtx(over = {}) {
  const calls = [];
  const ctx = {
    EmbedBuilder, ActionRowBuilder: class {}, ButtonBuilder: class {}, ButtonStyle: {},
    StringSelectMenuBuilder: class {}, MessageFlags: { Ephemeral: 64 },
    NV: { AMBER: 1, RUST_RED: 2, BLUE_VATS: 3, NCR_TAN: 4 }, brand: (e) => e,
    emptyIdEmbed: () => new EmbedBuilder().setTitle("need id"),
    errorEmbed: (t, d) => new EmbedBuilder().setTitle(`err ${t}`).setDescription(String(d)),
    warningEmbed: (t, d) => new EmbedBuilder().setTitle(`warn ${t}`).setDescription(String(d)),
    policeOnlyEmbed: () => new EmbedBuilder().setTitle("Police only"),
    modOnlyEmbed: () => new EmbedBuilder().setTitle("Mods only"),
    sanitizeId: (x) => String(x ?? "").trim(),
    parseDuration: (s) => { const m = String(s).match(/^(\d+)([smhd]?)$/); if (!m) return null; return +m[1] * ({ s: 1000, m: 60000, h: 3600000, d: 86400000 }[m[2] || "m"]); },
    hasPoliceRole: () => true, hasModRole: () => true,
    getWarrants: () => [{ reason: "assault", by: "cop" }],
    getArrests: () => [{ charges: [{ code: "1.02" }], minutes: 3, untilSober: false, at: Date.now() }],
    totalJailServed: () => 12,
    suspendRank: async () => ({ ok: true, faction: "NYPD", rank: "Sergeant" }),
    recordArrest: async () => ({}), startSentence: () => {}, announceArrest: () => {},
    logAction: async () => {}, writeModLog: (...a) => calls.push(["modlog", ...a]),
    ...over,
  };
  return { ctx, calls };
}

function makeInteraction(opts = {}) {
  const out = { replies: [] };
  const interaction = {
    user: { id: "u1", tag: "Cop#1", username: "Cop" }, member: { id: "u1" }, guild: null,
    options: { getString: (k) => opts.strings?.[k] ?? null },
    reply: async (p) => { out.replies.push(p); }, editReply: async (p) => { out.replies.push(p); }, deferReply: async () => {},
  };
  return { interaction, out };
}
const text = (out) => out.replies.map(p => (p.embeds ?? []).map(e => `${e.data.title ?? ""} ${e.data.description ?? ""} ${(e.data.fields ?? []).map(f => `${f.name} ${f.value}`).join(" ")}`).join(" ")).join(" ");

test("/backgroundcheck shows warrants, arrests, and jail time; gated to police", async () => {
  {
    const { ctx } = makeCtx({ hasPoliceRole: () => false });
    const h = require("../commands/police.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Ricky" } });
    await h.backgroundcheck(interaction, "backgroundcheck");
    assert.match(text(out), /Police only/);
  }
  {
    const { ctx } = makeCtx();
    const h = require("../commands/police.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Ricky" } });
    await h.backgroundcheck(interaction, "backgroundcheck");
    assert.match(text(out), /Background Check: Ricky/);
    assert.match(text(out), /Active Warrants \(1\)/);
    assert.match(text(out), /Arrests \(1\)/);
    assert.match(text(out), /12 min/);
  }
});

test("/suspendrank validates the time, gates to mod, and records the suspension", async () => {
  {
    const { ctx } = makeCtx({ hasModRole: () => false });
    const h = require("../commands/police.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Sal", time: "30m" } });
    await h.suspendrank(interaction, "suspendrank");
    assert.match(text(out), /Mods only/);
  }
  {
    const { ctx } = makeCtx();
    const h = require("../commands/police.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Sal", time: "banana" } });
    await h.suspendrank(interaction, "suspendrank");
    assert.match(text(out), /Bad Time/);
  }
  {
    const { ctx, calls } = makeCtx();
    const h = require("../commands/police.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Sal", time: "2h" } });
    await h.suspendrank(interaction, "suspendrank");
    assert.match(text(out), /Rank Suspended: Sal/);
    assert.match(text(out), /Sergeant.*NYPD.*120 min/s);
    assert.ok(calls.some(c => c[0] === "modlog" && c[1].action === "suspendrank"));
  }
});

test("/suspendrank on a non-whitelisted player is refused", async () => {
  const { ctx } = makeCtx({ suspendRank: async () => ({ ok: false, error: "not in a whitelist" }) });
  const h = require("../commands/police.js")(ctx);
  const { interaction, out } = makeInteraction({ strings: { playerid: "Nobody", time: "1h" } });
  await h.suspendrank(interaction, "suspendrank");
  assert.match(text(out), /No Rank to Suspend/);
});
