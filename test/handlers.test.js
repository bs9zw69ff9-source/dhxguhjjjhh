/* Handler-level tests for the riskiest command flows: kick/tempban/unban/checkban.
   Builds commands/moderation.js against a stubbed ctx (real discord.js builders,
   real hierarchy rules) and drives the handlers with fake interactions, asserting
   on which moderation functions actually get called and what the replies say. */
const { test } = require("node:test");
const assert = require("node:assert");
const { disableValidators, EmbedBuilder } = require("discord.js");
disableValidators();
const { TIER, canOverride, commandTierName } = require("../moderation/hierarchy");

function makeCtx(over = {}) {
  const calls = [];
  const rec = (n) => (...a) => { calls.push([n, ...a]); };
  const arec = (n) => async (...a) => { calls.push([n, ...a]); return {}; };
  const base = {
    EmbedBuilder, MessageFlags: { Ephemeral: 64 },
    NV: { NCR_TAN: 1, RUST_RED: 2, IRRAD_GREEN: 3, AMBER: 4, BLUE_VATS: 5, GOLD: 6, LEGION_RED: 7, DEAD_GREY: 8 },
    CLIN: { red: 1, green: 2, grey: 3 }, GLYPH: { deny: "🚫", dot: "•" },
    ACTIVE_SERVERS: ["server1"],
    PUNISH_BY_VALUE: { vdm: { name: "VDM", ms: 2 * 86400000 }, hardr: { name: "Hard R", permanent: true } },
    BAN_REASON_LABELS: {},
    brand: (e) => e, clinical: (e) => e, hero: (t) => t,
    emptyIdEmbed: () => new EmbedBuilder().setTitle("need id"),
    warningEmbed: (t, d) => new EmbedBuilder().setTitle(`warn ${t}`).setDescription(String(d)),
    errorEmbed: (t, d) => new EmbedBuilder().setTitle(`err ${t}`).setDescription(String(d)),
    successEmbed: (t, d) => new EmbedBuilder().setTitle(`ok ${t}`).setDescription(String(d)),
    modOnlyEmbed: () => new EmbedBuilder().setTitle("mods only"),
    ownerOnlyEmbed: () => new EmbedBuilder().setTitle("owner only"),
    sanitizeId: (x) => String(x ?? "").trim(), sanitizeBanName: (x) => String(x ?? "").trim(),
    serverLabel: (s) => "Server 1", punishDurationLabel: (p) => "2 days", easternNoonUTC: () => null,
    formatTimeLeft: () => "1d", randomQuote: () => "q",
    isMasterName: (n) => String(n).toLowerCase() === "master",
    isAutobanExempt: () => false, isProtectedPlayer: () => false,
    isDonator: () => false, isMasterIp: () => false, isOwner: () => false,
    hasModRole: () => true,
    commandTier: () => TIER.MOD, commandTierName, canOverride,
    loadBans: () => [], loadModLog: () => [], loadVpnChecks: () => ({}),
    blacklistHas: () => [], blacklistAll: () => [],
    ipBans: { getRecord: () => null },
    discordIdForPavlov: () => null,
    dmUserForPavlov: async () => null, dmPunishmentNotice: async () => ({ sent: false }), dmStatusField: () => null,
    getOnlinePlayers: async () => [],
    preserveBalanceAcrossKick: rec("preserveBalanceAcrossKick"),
    sendRcon: arec("sendRcon"),
    banWithIp: async (...a) => { calls.push(["banWithIp", ...a]); return { field: null, blacklist: { servers: 1 }, ok: true }; },
    unbanEverywhere: (...a) => { calls.push(["unbanEverywhere", ...a]); return { blacklist: { removed: 1 }, cleared: { ips: 1, names: 0 } }; },
    enforceBansSweep: async () => {},
    addAutobanExempt: arec("addAutobanExempt"), removeBans: arec("removeBans"),
    upsertTempBan: arec("upsertTempBan"), upsertPermBan: arec("upsertPermBan"),
    suspendDonator: async () => ({ wasDonator: false }),
    writeModLog: rec("writeModLog"), logAction: async () => {}, logBan: async () => {},
    logger: { info() {}, warn() {}, error() {} }, log: () => {},
    update: async () => {}, confirmDialog: async () => false, paginate: async () => {},
    checkVpn: async () => null, firewallBlockIps: async () => ({}), firewallUnblockIps: async () => ({}),
    firewallStatus: async () => ({ off: true }), UFW_BLOCK: false, _IPV4_RE: /^\d+\.\d+\.\d+\.\d+$/,
    bar: () => "", DIVIDER: "", DAY_MS: 86400000, FILES: {}, IPHUB_API_KEY: "",
    ...over,
  };
  return { ctx: base, calls };
}

function makeInteraction(opts = {}) {
  const out = { replies: [] };
  const interaction = {
    user: { id: "u1", tag: "Mod#1", username: "Mod" },
    member: { id: "u1" }, guild: null,
    options: {
      getString: (k) => opts.strings?.[k] ?? null,
      getUser: () => null,
      getBoolean: () => null,
      getInteger: () => null,
    },
    reply: async (p) => { out.replies.push(p); },
    editReply: async (p) => { out.replies.push(p); },
    deferReply: async () => {},
  };
  return { interaction, out };
}
const text = (out) => out.replies.map(p => (p.embeds ?? []).map(e =>
  `${e.data.title ?? ""} ${e.data.description ?? ""} ${(e.data.fields ?? []).map(f => `${f.name} ${f.value}`).join(" ")}`).join(" ")).join(" ");

test("/kick refuses master names and never sends RCON", async () => {
  const { ctx, calls } = makeCtx();
  const h = require("../commands/moderation.js")(ctx);
  const { interaction, out } = makeInteraction({ strings: { playerid: "Master" } });
  await h.kick(interaction, "kick");
  assert.match(text(out), /master name/i);
  assert.ok(!calls.some(c => c[0] === "sendRcon"), "no kick RCON for a master name");
});

test("/kick kicks on every active server and logs it", async () => {
  const { ctx, calls } = makeCtx();
  const h = require("../commands/moderation.js")(ctx);
  const { interaction } = makeInteraction({ strings: { playerid: "Griefer", reason: "vdm" } });
  await h.kick(interaction, "kick");
  assert.ok(calls.some(c => c[0] === "sendRcon" && c[1] === "Kick Griefer"));
  assert.ok(calls.some(c => c[0] === "writeModLog" && c[1].action === "kick"));
});

test("/tempban records the actor's tier on the ban", async () => {
  const { ctx, calls } = makeCtx({ commandTier: () => TIER.ADMIN });
  const h = require("../commands/moderation.js")(ctx);
  const { interaction } = makeInteraction({ strings: { playerid: "Griefer", reason: "vdm" } });
  await h.tempban(interaction, "tempban");
  const up = calls.find(c => c[0] === "upsertTempBan");
  assert.ok(up, "tempban record written");
  assert.equal(up[1].moderatorRank, TIER.ADMIN);
  assert.ok(calls.some(c => c[0] === "banWithIp" && c[1] === "Griefer"));
});

test("/tempban: a mod cannot replace a super owner's existing ban", async () => {
  const { ctx, calls } = makeCtx({
    commandTier: () => TIER.MOD,
    loadBans: () => [{ playerId: "griefer", permanent: true, moderatorRank: TIER.SUPER_OWNER, reason: "x" }],
  });
  const h = require("../commands/moderation.js")(ctx);
  const { interaction, out } = makeInteraction({ strings: { playerid: "Griefer", reason: "vdm" } });
  await h.tempban(interaction, "tempban");
  assert.match(text(out), /Super Owner/);
  assert.ok(!calls.some(c => c[0] === "banWithIp"), "ban must not proceed");
});

test("/unban: a mod cannot lift a super owner's ban; a super owner can", async () => {
  const bans = [{ playerId: "griefer", permanent: true, moderatorRank: TIER.SUPER_OWNER, reason: "x" }];
  {
    const { ctx, calls } = makeCtx({ commandTier: () => TIER.MOD, loadBans: () => bans });
    const h = require("../commands/moderation.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Griefer" } });
    await h.unban(interaction, "unban");
    assert.match(text(out), /can't lift a higher tier/i);
    assert.ok(!calls.some(c => c[0] === "unbanEverywhere"));
  }
  {
    const { ctx, calls } = makeCtx({ commandTier: () => TIER.SUPER_OWNER, loadBans: () => bans });
    const h = require("../commands/moderation.js")(ctx);
    const { interaction } = makeInteraction({ strings: { playerid: "Griefer" } });
    await h.unban(interaction, "unban");
    // exempt FIRST so the sweep can't re-ban mid-unban, then clear records + flags
    assert.ok(calls.some(c => c[0] === "addAutobanExempt" && c[1] === "Griefer"));
    assert.ok(calls.some(c => c[0] === "removeBans" && c[1] === "Griefer"));
    assert.ok(calls.some(c => c[0] === "unbanEverywhere" && c[1] === "Griefer"));
  }
});

test("/checkban shows the flag warning to mods but not to regular users", async () => {
  const flaggedRec = { getRecord: () => ({ flagged: true }) };
  {
    const { ctx } = makeCtx({ ipBans: flaggedRec, hasModRole: () => false });
    const h = require("../commands/moderation.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Sneaky" } });
    await h.checkban(interaction, "checkban");
    assert.ok(!/flagged/i.test(text(out)), "regular users must not see flag status");
  }
  {
    const { ctx } = makeCtx({ ipBans: flaggedRec, hasModRole: () => true });
    const h = require("../commands/moderation.js")(ctx);
    const { interaction, out } = makeInteraction({ strings: { playerid: "Sneaky" } });
    await h.checkban(interaction, "checkban");
    assert.ok(/flagged/i.test(text(out)), "mods do see flag status");
  }
});

test("/tempban permanent preset writes a perm ban and flags accordingly", async () => {
  const { ctx, calls } = makeCtx({ commandTier: () => TIER.ADMIN });
  const h = require("../commands/moderation.js")(ctx);
  const { interaction } = makeInteraction({ strings: { playerid: "Slur", reason: "hardr" } });
  await h.tempban(interaction, "tempban");
  assert.ok(calls.some(c => c[0] === "upsertPermBan"), "permanent preset -> perm ban record");
  const bw = calls.find(c => c[0] === "banWithIp");
  assert.deepEqual(bw[3], { permanent: true }, "banWithIp told it is permanent (EOS flag allowed)");
});
