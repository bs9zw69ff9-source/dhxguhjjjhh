// ============================================================
//  GUARDIAN — Discord server security module (anti raid/nuke/spam/ping)
//  Integrated into the main bot, gated behind GUARDIAN_ENABLED=true.
//  All slash commands are suffixed with "discord" to avoid clashing with
//  the Pavlov moderation commands (bandiscord, kickdiscord, …).
//  Requires privileged intents (Server Members + Message Content) enabled
//  in the Discord Developer Portal.
// ============================================================
"use strict";
const fs   = require("fs");
const path = require("path");
const {
  GatewayIntentBits, Partials, EmbedBuilder, PermissionsBitField,
  AuditLogEvent, Events, SlashCommandBuilder, PermissionFlagsBits,
} = require("discord.js");

const ANTIPING_FILE = path.join(__dirname, "antiping.json");
const MUTED_FILE    = path.join(__dirname, "mutedroles.json");

// Intents/partials this module needs (added to the client by index.js when enabled).
const intents = [
  GatewayIntentBits.GuildMessages,
  GatewayIntentBits.GuildMembers,
  GatewayIntentBits.GuildModeration,
  GatewayIntentBits.GuildWebhooks,
  GatewayIntentBits.MessageContent,
];
const partials = [Partials.Message, Partials.Channel];

// ── Config ────────────────────────────────────────────────────
const config = {
  logChannelId:  process.env.GUARDIAN_LOG_CHANNEL_ID || process.env.LOG_CHANNEL_ID || "",
  modRoleId:     process.env.GUARDIAN_MOD_ROLE_ID    || process.env.MOD_ROLE_ID    || "",
  muteRoleId:    process.env.GUARDIAN_MUTE_ROLE_ID   || process.env.MUTE_ROLE_ID   || "",

  nukeWhitelistRoleIds: (process.env.NUKE_WHITELIST_ROLE_IDS || "").split(",").filter(Boolean),
  nukeWhitelistUserIds: (process.env.NUKE_WHITELIST_USER_IDS || "").split(",").filter(Boolean),

  spamThreshold: parseInt(process.env.SPAM_THRESHOLD) || 5,
  spamWindowMs:  parseInt(process.env.SPAM_WINDOW_MS)  || 3000,
  spamMuteMin:   parseInt(process.env.SPAM_MUTE_MIN)   || 10,

  raidJoinThreshold: parseInt(process.env.RAID_JOIN_THRESHOLD) || 10,
  raidWindowMs:      parseInt(process.env.RAID_WINDOW_MS)      || 10000,
  raidLockdownMin:   parseInt(process.env.RAID_LOCKDOWN_MIN)   || 5,

  nukeWindowMs:         parseInt(process.env.NUKE_WINDOW_MS)          || 10000,
  nukeChannelThreshold: parseInt(process.env.NUKE_CHANNEL_THRESHOLD)  || 3,
  nukeRoleThreshold:    parseInt(process.env.NUKE_ROLE_THRESHOLD)      || 3,
  nukeBanThreshold:     parseInt(process.env.NUKE_BAN_THRESHOLD)       || 5,
  nukeKickThreshold:    parseInt(process.env.NUKE_KICK_THRESHOLD)      || 5,
  nukeWebhookThreshold: parseInt(process.env.NUKE_WEBHOOK_THRESHOLD)   || 3,

  modBanLimit:      parseInt(process.env.MOD_BAN_LIMIT)      || 3,
  modKickLimit:     parseInt(process.env.MOD_KICK_LIMIT)     || 10,
  modMuteLimit:     parseInt(process.env.MOD_MUTE_LIMIT)     || 20,
  modPurgeLimit:    parseInt(process.env.MOD_PURGE_LIMIT)    || 5,
  modLockdownLimit: parseInt(process.env.MOD_LOCKDOWN_LIMIT) || 5,
  modWarnLimit:     parseInt(process.env.MOD_WARN_LIMIT)     || 30,
  modWindowMs:      parseInt(process.env.MOD_WINDOW_MS)      || 86400000,

  antiPingEnabled:       process.env.ANTIPING_ENABLED !== "false",
  antiPingAction:        process.env.ANTIPING_ACTION || "timeout",
  antiPingTimeoutMin:    parseInt(process.env.ANTIPING_TIMEOUT_MIN) || 5,
  antiPingDeleteMessage: process.env.ANTIPING_DELETE === "true",
  antiPingIgnoreReplies: process.env.ANTIPING_IGNORE_REPLIES !== "false",
  antiPingNotifyChannel: process.env.ANTIPING_NOTIFY !== "false",
  antiPingResponse:      process.env.ANTIPING_RESPONSE ||
                         "{user}, please don't ping {targets}. You have been {action}.",
  antiPingProtectedUserIds: (process.env.ANTIPING_PROTECTED_USER_IDS || "").split(",").filter(Boolean),
  antiPingProtectedRoleIds: (process.env.ANTIPING_PROTECTED_ROLE_IDS || "").split(",").filter(Boolean),
};

// ── State ─────────────────────────────────────────────────────
const spamTracker = new Map();
const joinTracker = [];
const nukeTracker = new Map();
const modRateTracker = new Map();
let   lockdownActive = false;
let   client = null;   // set in initEvents

// ── Anti-Ping runtime state ───────────────────────────────────
const antiPingDefaults = {
  enabled:          config.antiPingEnabled,
  action:           config.antiPingAction,
  timeoutMin:       config.antiPingTimeoutMin,
  deleteMessage:    config.antiPingDeleteMessage,
  ignoreReplies:    config.antiPingIgnoreReplies,
  notifyChannel:    config.antiPingNotifyChannel,
  responseTemplate: config.antiPingResponse,
  protectedUsers:   [...config.antiPingProtectedUserIds],
  protectedRoles:   [...config.antiPingProtectedRoleIds],
};
let antiPing = { ...antiPingDefaults };
function loadAntiPing() {
  try { if (fs.existsSync(ANTIPING_FILE)) antiPing = { ...antiPingDefaults, ...JSON.parse(fs.readFileSync(ANTIPING_FILE, "utf8")) }; }
  catch (e) { console.error("[guardian] antiping load failed:", e.message); }
}
function saveAntiPing() {
  try { fs.writeFileSync(ANTIPING_FILE, JSON.stringify(antiPing, null, 2)); }
  catch (e) { console.error("[guardian] antiping save failed:", e.message); }
}
loadAntiPing();

// ── Muted-role stash state ────────────────────────────────────
let mutedRoles = {};
function loadMutedRoles() {
  try { if (fs.existsSync(MUTED_FILE)) mutedRoles = JSON.parse(fs.readFileSync(MUTED_FILE, "utf8")); }
  catch (e) { console.error("[guardian] mutedRoles load failed:", e.message); }
}
function saveMutedRoles() {
  try { fs.writeFileSync(MUTED_FILE, JSON.stringify(mutedRoles, null, 2)); }
  catch (e) { console.error("[guardian] mutedRoles save failed:", e.message); }
}
loadMutedRoles();

// ── Slash Commands (all suffixed with "discord") ──────────────
const commands = [
  new SlashCommandBuilder().setName("mutediscord").setDescription("Mute a Discord member")
    .addUserOption(o => o.setName("user").setDescription("Member to mute").setRequired(true))
    .addIntegerOption(o => o.setName("minutes").setDescription("Duration in minutes (0 = permanent)").setMinValue(0))
    .addStringOption(o => o.setName("reason").setDescription("Reason for mute")),
  new SlashCommandBuilder().setName("unmutediscord").setDescription("Unmute a Discord member")
    .addUserOption(o => o.setName("user").setDescription("Member to unmute").setRequired(true)),
  new SlashCommandBuilder().setName("kickdiscord").setDescription("Kick a Discord member")
    .addUserOption(o => o.setName("user").setDescription("Member to kick").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for kick")),
  new SlashCommandBuilder().setName("bandiscord").setDescription("Ban a Discord member")
    .addUserOption(o => o.setName("user").setDescription("Member to ban").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for ban"))
    .addIntegerOption(o => o.setName("delete_days").setDescription("Days of messages to delete (0-7)").setMinValue(0).setMaxValue(7)),
  new SlashCommandBuilder().setName("purgediscord").setDescription("Bulk-delete messages in this channel")
    .addIntegerOption(o => o.setName("count").setDescription("Number of messages (1-100)").setRequired(true).setMinValue(1).setMaxValue(100))
    .addUserOption(o => o.setName("user").setDescription("Only delete messages from this user (optional)")),
  new SlashCommandBuilder().setName("lockdowndiscord").setDescription("Lock or unlock a channel")
    .addStringOption(o => o.setName("action").setDescription("Lock or unlock").setRequired(true)
      .addChoices({ name: "lock", value: "lock" }, { name: "unlock", value: "unlock" }))
    .addChannelOption(o => o.setName("channel").setDescription("Channel to lock/unlock (defaults to current)")),
  new SlashCommandBuilder().setName("warndiscord").setDescription("Issue a warning to a Discord member")
    .addUserOption(o => o.setName("user").setDescription("Member to warn").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for warning")),
  new SlashCommandBuilder().setName("configdiscord").setDescription("View Guardian security configuration")
    .setDefaultMemberPermissions(PermissionFlagsBits.Administrator),
  new SlashCommandBuilder().setName("nuketestdiscord").setDescription("Confirm anti-nuke system is active (server owner only)"),
  new SlashCommandBuilder().setName("limitsdiscord").setDescription("Check your remaining mod action limits for today"),
  new SlashCommandBuilder().setName("antipingdiscord").setDescription("Configure anti-ping protection for staff/VIPs")
    .addSubcommand(s => s.setName("status").setDescription("Show current anti-ping settings"))
    .addSubcommand(s => s.setName("toggle").setDescription("Enable or disable anti-ping")
      .addBooleanOption(o => o.setName("enabled").setDescription("On or off").setRequired(true)))
    .addSubcommand(s => s.setName("action").setDescription("Set punishment for pinging a protected target")
      .addStringOption(o => o.setName("type").setDescription("Punishment").setRequired(true)
        .addChoices(
          { name: "none (log only)",   value: "none"    },
          { name: "warn",              value: "warn"    },
          { name: "mute (mute role)",  value: "mute"    },
          { name: "timeout (native)",  value: "timeout" },
        )))
    .addSubcommand(s => s.setName("duration").setDescription("Mute/timeout duration in minutes")
      .addIntegerOption(o => o.setName("minutes").setDescription("Minutes").setRequired(true).setMinValue(1).setMaxValue(40320)))
    .addSubcommand(s => s.setName("delete").setDescription("Delete the offending message?")
      .addBooleanOption(o => o.setName("enabled").setDescription("True to delete").setRequired(true)))
    .addSubcommand(s => s.setName("ignorereplies").setDescription("Ignore reply-pings?")
      .addBooleanOption(o => o.setName("enabled").setDescription("True to ignore reply pings").setRequired(true)))
    .addSubcommand(s => s.setName("response").setDescription("Customize the warning message - {user} {targets} {action}")
      .addStringOption(o => o.setName("text").setDescription("Template text, or 'default' to reset").setRequired(true)))
    .addSubcommand(s => s.setName("notify").setDescription("Post the public warning message in the channel?")
      .addBooleanOption(o => o.setName("enabled").setDescription("True to post warning in channel").setRequired(true)))
    .addSubcommand(s => s.setName("protect").setDescription("Add/remove a protected user")
      .addStringOption(o => o.setName("action").setDescription("add or remove").setRequired(true)
        .addChoices({ name: "add", value: "add" }, { name: "remove", value: "remove" }))
      .addUserOption(o => o.setName("user").setDescription("User to protect").setRequired(true)))
    .addSubcommand(s => s.setName("protectrole").setDescription("Add/remove a protected role")
      .addStringOption(o => o.setName("action").setDescription("add or remove").setRequired(true)
        .addChoices({ name: "add", value: "add" }, { name: "remove", value: "remove" }))
      .addRoleOption(o => o.setName("role").setDescription("Role to protect").setRequired(true)))
    .addSubcommand(s => s.setName("list").setDescription("List protected users and roles")),
  new SlashCommandBuilder().setName("helpdiscord").setDescription("Show all Guardian security commands"),
];

const GUARDIAN_COMMAND_NAMES = new Set(commands.map(c => c.name));
function isGuardianCommand(name) { return GUARDIAN_COMMAND_NAMES.has(name); }

// ── Embed Helpers ─────────────────────────────────────────────
const COLORS = { success: 0x00e5a0, warn: 0xf5a623, danger: 0xff3b5c, info: 0x5865f2, muted: 0xff7518, nuke: 0xff0033, neutral: 0x2f3136 };
function gEmbed(color, description, title = null) {
  const e = new EmbedBuilder().setColor(color).setDescription(description).setTimestamp();
  if (title) e.setTitle(`🛡️ ${title}`);
  return e;
}
function secLog(guild, title, desc, color = COLORS.success) {
  if (!config.logChannelId) return;
  const ch = guild.channels.cache.get(config.logChannelId);
  if (!ch) return;
  ch.send({ embeds: [gEmbed(color, desc, title)] }).catch(() => {});
}
function isMod(member) {
  if (!member) return false;
  if (member.id === member.guild.ownerId) return true;
  if (!config.modRoleId) return false;
  return member.roles.cache.has(config.modRoleId);
}

// ── Mod Rate Limit Helpers ────────────────────────────────────
function getModEntry(userId) {
  if (!modRateTracker.has(userId)) modRateTracker.set(userId, { bans: [], kicks: [], mutes: [], purges: [], lockdowns: [], warns: [] });
  return modRateTracker.get(userId);
}
function pruneWindow(arr, windowMs = config.modWindowMs) { const cutoff = Date.now() - windowMs; return arr.filter(t => t > cutoff); }
function checkModLimit(memberId, action) {
  const entry = getModEntry(memberId);
  const limit = config[`mod${action.charAt(0).toUpperCase() + action.slice(1)}Limit`];
  entry[`${action}s`] = pruneWindow(entry[`${action}s`]);
  const used = entry[`${action}s`].length;
  const allowed = used < limit;
  let resetsInMin = 0;
  if (!allowed && entry[`${action}s`].length > 0) resetsInMin = Math.ceil((entry[`${action}s`][0] + config.modWindowMs - Date.now()) / 60000);
  return { allowed, used, limit, remaining: limit - used, resetsInMin };
}
function recordModAction(memberId, action) {
  const entry = getModEntry(memberId);
  entry[`${action}s`] = pruneWindow(entry[`${action}s`]);
  entry[`${action}s`].push(Date.now());
}
function limitDeniedEmbed(action, used, limit, resetsInMin) {
  return gEmbed(COLORS.danger,
    `⛔ **Rate limit reached for \`/${action}discord\`**\n\nYou've used **${used}/${limit}** ${action}s in the past ${config.modWindowMs / 3600000}h.\n⏱️ Resets in **~${resetsInMin} minute${resetsInMin === 1 ? "" : "s"}**.`);
}
function buildBar(used, limit, width = 10) { const filled = Math.round((used / limit) * width); return "█".repeat(filled) + "░".repeat(Math.max(0, width - filled)); }
function usageFooter(action, used, limit) {
  const remaining = limit - used;
  const warning = remaining <= Math.ceil(limit * 0.2) && remaining > 0 ? `\n⚠️ Only **${remaining}** ${action}${remaining === 1 ? "" : "s"} remaining today.` : "";
  return `\`${buildBar(used, limit, 10)}\` **${used}/${limit}** ${action}s used today${warning}`;
}

// ── Mute / Unmute (strip + stash roles, restore on unmute) ────
async function muteUser(member, durationMin, reason) {
  if (!config.muteRoleId) return false;
  const muteRole = member.guild.roles.cache.get(config.muteRoleId);
  if (!muteRole) return false;
  const removable = member.roles.cache.filter(r => r.id !== member.guild.id && r.id !== muteRole.id && !r.managed && r.editable);
  const strippedIds = [...removable.keys()];
  const unstrippable = member.roles.cache.filter(r => r.id !== member.guild.id && r.id !== muteRole.id && (r.managed || !r.editable));
  try {
    if (strippedIds.length) await member.roles.remove(strippedIds, `Mute: stash roles — ${reason}`);
    await member.roles.add(muteRole, reason);
  } catch (e) { console.error("[guardian] mute role op failed:", e.message); }
  if (!mutedRoles[member.guild.id]) mutedRoles[member.guild.id] = {};
  const prior = mutedRoles[member.guild.id][member.id]?.roles || [];
  mutedRoles[member.guild.id][member.id] = {
    roles: [...new Set([...prior, ...strippedIds])], reason, mutedAt: Date.now(),
    expiresAt: durationMin > 0 ? Date.now() + durationMin * 60000 : null,
  };
  saveMutedRoles();
  const stash = mutedRoles[member.guild.id][member.id].roles;
  secLog(member.guild, "Member Muted",
    `<@${member.id}> muted for **${durationMin > 0 ? durationMin + " min" : "permanent"}** — ${reason}\n📦 Stashed **${stash.length}** role${stash.length === 1 ? "" : "s"}: ${stash.length ? stash.map(id => `<@&${id}>`).join(", ") : "none"}` +
    (unstrippable.size ? `\n⚠️ Couldn't strip (managed / above bot): ${unstrippable.map(r => `<@&${r.id}>`).join(", ")}` : ""), COLORS.muted);
  if (durationMin > 0) setTimeout(() => unmuteUser(member.guild, member.id, "Auto-unmute (timer)"), durationMin * 60000);
  return true;
}
async function unmuteUser(guild, userId, reason = "Unmute") {
  const muteRole = config.muteRoleId ? guild.roles.cache.get(config.muteRoleId) : null;
  const member   = await guild.members.fetch(userId).catch(() => null);
  const stash    = mutedRoles[guild.id]?.[userId];
  if (member) {
    if (muteRole && member.roles.cache.has(muteRole.id)) await member.roles.remove(muteRole, reason).catch(() => {});
    if (stash?.roles?.length) {
      const restorable = stash.roles.filter(id => { const r = guild.roles.cache.get(id); return r && r.editable && !r.managed; });
      const lost = stash.roles.filter(id => !restorable.includes(id));
      if (restorable.length) await member.roles.add(restorable, `Restore stashed roles — ${reason}`).catch(() => {});
      secLog(guild, "Roles Restored",
        `<@${userId}> unmuted — restored **${restorable.length}** role${restorable.length === 1 ? "" : "s"}: ${restorable.length ? restorable.map(id => `<@&${id}>`).join(", ") : "none"}` +
        (lost.length ? `\n⚠️ Could not restore (deleted / above bot): ${lost.map(id => `<@&${id}>`).join(", ")}` : "") + `\n_(${reason})_`, COLORS.success);
    } else secLog(guild, "Member Unmuted", `<@${userId}> unmuted — no stashed roles to restore. _(${reason})_`);
  }
  if (mutedRoles[guild.id]) {
    delete mutedRoles[guild.id][userId];
    if (!Object.keys(mutedRoles[guild.id]).length) delete mutedRoles[guild.id];
    saveMutedRoles();
  }
}
async function recoverMutes() {
  for (const [guildId, users] of Object.entries(mutedRoles)) {
    const guild = client.guilds.cache.get(guildId);
    if (!guild) continue;
    for (const [userId, data] of Object.entries(users)) {
      if (data.expiresAt == null) continue;
      const remaining = data.expiresAt - Date.now();
      if (remaining <= 0) unmuteUser(guild, userId, "Auto-unmute (expired during downtime)");
      else setTimeout(() => unmuteUser(guild, userId, "Auto-unmute (timer, resumed post-restart)"), remaining);
    }
  }
}

// ── Anti-Spam ─────────────────────────────────────────────────
function checkSpam(message) {
  const uid = message.author.id, now = Date.now();
  const arr = (spamTracker.get(uid) || []).filter(t => now - t < config.spamWindowMs);
  arr.push(now); spamTracker.set(uid, arr);
  if (arr.length >= config.spamThreshold) {
    message.delete().catch(() => {});
    muteUser(message.member, config.spamMuteMin, "Anti-spam triggered");
    spamTracker.set(uid, []);
    return true;
  }
  return false;
}

// ── Anti-Ping ─────────────────────────────────────────────────
function renderAntiPingResponse(memberId, targets, actionText) {
  return antiPing.responseTemplate.split("{user}").join(`<@${memberId}>`).split("{targets}").join(targets).split("{action}").join(actionText);
}
async function checkAntiPing(message) {
  if (!antiPing.enabled) return;
  const member = message.member;
  if (!member || member.id === message.guild.ownerId) return;
  if (isMod(member) || isWhitelisted(member)) return;
  const hits = new Set();
  for (const [id, user] of message.mentions.users) {
    if (id === message.author.id || user.bot) continue;
    if (antiPing.ignoreReplies && message.mentions.repliedUser?.id === id) continue;
    if (antiPing.protectedUsers.includes(id)) { hits.add(`<@${id}>`); continue; }
    const t = message.guild.members.cache.get(id);
    if (t && t.roles.cache.some(r => antiPing.protectedRoles.includes(r.id))) hits.add(`<@${id}>`);
  }
  for (const [id] of message.mentions.roles) if (antiPing.protectedRoles.includes(id)) hits.add(`<@&${id}>`);
  if (hits.size === 0) return;
  const targets = [...hits].join(", ");
  const reason  = `Anti-ping: mentioned protected ${targets}`;
  if (antiPing.deleteMessage) message.delete().catch(() => {});
  let actionText = "logged only";
  switch (antiPing.action) {
    case "mute":    await muteUser(member, antiPing.timeoutMin, reason); actionText = `muted for ${antiPing.timeoutMin} min`; break;
    case "timeout": await member.timeout(antiPing.timeoutMin * 60000, reason).catch(() => {}); actionText = `timed out for ${antiPing.timeoutMin} min`; break;
    case "warn":    actionText = "warned"; break;
  }
  if (antiPing.notifyChannel) {
    message.channel.send({ embeds: [gEmbed(COLORS.warn, renderAntiPingResponse(member.id, targets, actionText), "Anti-Ping")] })
      .then(m => setTimeout(() => m.delete().catch(() => {}), 8000)).catch(() => {});
  }
  secLog(message.guild, "📡 Anti-Ping Triggered", `<@${member.id}> pinged ${targets} in <#${message.channel.id}> → **${actionText}**`, COLORS.warn);
}

// ── Anti-Nuke helpers ─────────────────────────────────────────
function isWhitelisted(member) {
  if (!member) return false;
  if (member.id === member.guild.ownerId) return true;
  if (config.nukeWhitelistUserIds.includes(member.id)) return true;
  return member.roles.cache.some(r => config.nukeWhitelistRoleIds.includes(r.id));
}
function getNukeEntry(userId) {
  if (!nukeTracker.has(userId)) nukeTracker.set(userId, { channels: [], roles: [], bans: [], kicks: [], webhooks: [] });
  return nukeTracker.get(userId);
}
function pruneOld(arr) { return arr.filter(t => Date.now() - t < config.nukeWindowMs); }
async function getAuditExecutor(guild, auditAction, targetId, maxAge = 5000) {
  try {
    const logs  = await guild.fetchAuditLogs({ type: auditAction, limit: 5 });
    const entry = logs.entries.find(e => (!targetId || e.target?.id === targetId) && (Date.now() - e.createdTimestamp) < maxAge);
    if (!entry) return null;
    return guild.members.fetch(entry.executor.id).catch(() => null);
  } catch { return null; }
}
async function nukeResponse(guild, member, reason) {
  secLog(guild, "🔴 ANTI-NUKE TRIGGERED",
    `**Executor:** <@${member?.id ?? "unknown"}>\n**Reason:** ${reason}\n**Action:** Dangerous roles stripped → banned\n> Whitelisted users are immune.`, COLORS.nuke);
  if (!member) return;
  try {
    const dangerPerms = [
      PermissionsBitField.Flags.Administrator, PermissionsBitField.Flags.ManageGuild,
      PermissionsBitField.Flags.ManageChannels, PermissionsBitField.Flags.ManageRoles,
      PermissionsBitField.Flags.BanMembers, PermissionsBitField.Flags.KickMembers,
    ];
    const toRemove = member.roles.cache.filter(r => r.permissions.any(dangerPerms) && r.editable);
    if (toRemove.size > 0) await member.roles.remove([...toRemove.keys()], "Anti-nuke: role strip");
    await member.ban({ reason: `Anti-Nuke: ${reason}` });
  } catch (e) { secLog(guild, "Anti-Nuke Error", `Could not action <@${member.id}>: ${e.message}`, COLORS.warn); }
}

// ── Event wiring ──────────────────────────────────────────────
function initEvents(c) {
  client = c;
  recoverMutes();

  client.on(Events.GuildMemberAdd, async (member) => {
    const now = Date.now(); joinTracker.push(now);
    const recent = joinTracker.filter(t => now - t < config.raidWindowMs).length;
    if (recent >= config.raidJoinThreshold && !lockdownActive) {
      lockdownActive = true;
      secLog(member.guild, "🚨 RAID DETECTED", `**${recent}** members joined within ${config.raidWindowMs / 1000}s.\nServer locked down for **${config.raidLockdownMin} minutes**.`, COLORS.nuke);
      member.guild.channels.cache.forEach(ch => { if (ch.isTextBased()) ch.permissionOverwrites.edit(member.guild.roles.everyone, { SendMessages: false }).catch(() => {}); });
      setTimeout(() => {
        member.guild.channels.cache.forEach(ch => { if (ch.isTextBased()) ch.permissionOverwrites.edit(member.guild.roles.everyone, { SendMessages: null }).catch(() => {}); });
        lockdownActive = false;
        secLog(member.guild, "Lockdown Lifted", `Auto-lifted after **${config.raidLockdownMin} minutes**.`);
      }, config.raidLockdownMin * 60000);
    }
  });

  client.on(Events.ChannelDelete, async (channel) => {
    if (!channel.guild) return;
    const executor = await getAuditExecutor(channel.guild, AuditLogEvent.ChannelDelete, channel.id);
    if (!executor || isWhitelisted(executor)) return;
    const entry = getNukeEntry(executor.id); entry.channels = pruneOld(entry.channels); entry.channels.push(Date.now());
    if (entry.channels.length >= config.nukeChannelThreshold) { entry.channels = []; await nukeResponse(channel.guild, executor, `Deleted ${config.nukeChannelThreshold}+ channels in ${config.nukeWindowMs / 1000}s`); }
  });

  client.on(Events.GuildRoleDelete, async (role) => {
    const executor = await getAuditExecutor(role.guild, AuditLogEvent.RoleDelete, role.id);
    if (!executor || isWhitelisted(executor)) return;
    const entry = getNukeEntry(executor.id); entry.roles = pruneOld(entry.roles); entry.roles.push(Date.now());
    if (entry.roles.length >= config.nukeRoleThreshold) { entry.roles = []; await nukeResponse(role.guild, executor, `Deleted ${config.nukeRoleThreshold}+ roles in ${config.nukeWindowMs / 1000}s`); }
  });

  client.on(Events.GuildBanAdd, async (ban) => {
    const executor = await getAuditExecutor(ban.guild, AuditLogEvent.MemberBanAdd, ban.user.id);
    if (!executor || isWhitelisted(executor)) return;
    const entry = getNukeEntry(executor.id); entry.bans = pruneOld(entry.bans); entry.bans.push(Date.now());
    if (entry.bans.length >= config.nukeBanThreshold) { entry.bans = []; await nukeResponse(ban.guild, executor, `Issued ${config.nukeBanThreshold}+ bans in ${config.nukeWindowMs / 1000}s`); }
  });

  client.on(Events.GuildMemberRemove, async (member) => {
    const executor = await getAuditExecutor(member.guild, AuditLogEvent.MemberKick, member.id);
    if (!executor || isWhitelisted(executor)) return;
    const entry = getNukeEntry(executor.id); entry.kicks = pruneOld(entry.kicks); entry.kicks.push(Date.now());
    if (entry.kicks.length >= config.nukeKickThreshold) { entry.kicks = []; await nukeResponse(member.guild, executor, `Issued ${config.nukeKickThreshold}+ kicks in ${config.nukeWindowMs / 1000}s`); }
  });

  client.on(Events.WebhooksUpdate, async (channel) => {
    const executor = await getAuditExecutor(channel.guild, AuditLogEvent.WebhookCreate);
    if (!executor || isWhitelisted(executor)) return;
    const entry = getNukeEntry(executor.id); entry.webhooks = pruneOld(entry.webhooks); entry.webhooks.push(Date.now());
    if (entry.webhooks.length >= config.nukeWebhookThreshold) {
      entry.webhooks = [];
      const webhooks = await channel.fetchWebhooks().catch(() => null);
      if (webhooks) for (const wh of webhooks.filter(w => w.owner?.id === executor.id).values()) await wh.delete("Anti-nuke: webhook abuse").catch(() => {});
      await nukeResponse(channel.guild, executor, `Created ${config.nukeWebhookThreshold}+ webhooks in ${config.nukeWindowMs / 1000}s`);
    }
  });

  client.on(Events.GuildRoleUpdate, async (oldRole, newRole) => {
    const adminAdded  = !oldRole.permissions.has(PermissionsBitField.Flags.Administrator) && newRole.permissions.has(PermissionsBitField.Flags.Administrator);
    const manageAdded = !oldRole.permissions.has(PermissionsBitField.Flags.ManageRoles)   && newRole.permissions.has(PermissionsBitField.Flags.ManageRoles);
    if (!adminAdded && !manageAdded) return;
    const executor = await getAuditExecutor(newRole.guild, AuditLogEvent.RoleUpdate, newRole.id);
    if (!executor || isWhitelisted(executor)) return;
    try { const perm = adminAdded ? PermissionsBitField.Flags.Administrator : PermissionsBitField.Flags.ManageRoles; await newRole.setPermissions(newRole.permissions.remove(perm), "Anti-nuke: perm escalation reverted"); } catch (_) {}
    secLog(newRole.guild, "⚠️ Permission Escalation Blocked", `<@${executor.id}> tried to grant **${adminAdded ? "Administrator" : "Manage Roles"}** to <@&${newRole.id}>. Reverted.`, COLORS.warn);
  });

  client.on(Events.MessageCreate, (message) => {
    if (message.author.bot || !message.guild) return;
    checkSpam(message);
    checkAntiPing(message);
  });

  console.log("[guardian] security module active");
}

// ── Slash Command Handler (called by index.js for *discord cmds) ─
async function handle(interaction) {
  if (!interaction.isChatInputCommand()) return;
  const { commandName, guild, member } = interaction;

  switch (commandName) {
    case "mutediscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const target  = interaction.options.getMember("user");
      const minutes = interaction.options.getInteger("minutes") ?? 10;
      const reason  = interaction.options.getString("reason") ?? "No reason provided";
      if (!target) return interaction.reply({ content: "❌ User not found.", ephemeral: true });
      if (!isWhitelisted(member)) {
        const { allowed, used, limit, resetsInMin } = checkModLimit(member.id, "mute");
        if (!allowed) return interaction.reply({ embeds: [limitDeniedEmbed("mute", used, limit, resetsInMin)], ephemeral: true });
        recordModAction(member.id, "mute");
        const { used: newUsed } = checkModLimit(member.id, "mute");
        const ok = await muteUser(target, minutes, reason);
        if (!ok) return interaction.reply({ content: "❌ Mute role not configured. Set `MUTE_ROLE_ID` in `.env`.", ephemeral: true });
        const stashed = mutedRoles[guild.id]?.[target.id]?.roles?.length ?? 0;
        return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.muted).setTitle("🔇 Member Muted")
          .setDescription(`<@${target.id}> has been muted for **${minutes > 0 ? minutes + " minutes" : "permanently"}**.\n**Reason:** ${reason}\n📦 **${stashed}** role${stashed === 1 ? "" : "s"} stashed — restored on unmute.`)
          .setFooter({ text: usageFooter("mute", newUsed, limit) }).setTimestamp()] });
      }
      const ok = await muteUser(target, minutes, reason);
      if (!ok) return interaction.reply({ content: "❌ Mute role not configured.", ephemeral: true });
      const stashed = mutedRoles[guild.id]?.[target.id]?.roles?.length ?? 0;
      return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.muted).setTitle("🔇 Member Muted")
        .setDescription(`<@${target.id}> muted for **${minutes > 0 ? minutes + " min" : "permanent"}** — ${reason}\n📦 **${stashed}** role${stashed === 1 ? "" : "s"} stashed.`).setTimestamp()] });
    }

    case "unmutediscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const target = interaction.options.getMember("user");
      if (!target) return interaction.reply({ content: "❌ User not found.", ephemeral: true });
      if (!config.muteRoleId) return interaction.reply({ content: "❌ Mute role not configured.", ephemeral: true });
      const stashed = mutedRoles[guild.id]?.[target.id]?.roles?.length ?? 0;
      await unmuteUser(guild, target.id, `Manual unmute by ${interaction.user.tag}`);
      return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.success).setTitle("🔊 Member Unmuted")
        .setDescription(`<@${target.id}> has been unmuted.\n♻️ Restored **${stashed}** stashed role${stashed === 1 ? "" : "s"}.`).setTimestamp()] });
    }

    case "kickdiscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const target = interaction.options.getMember("user");
      const reason = interaction.options.getString("reason") ?? "No reason provided";
      if (!target) return interaction.reply({ content: "❌ User not found.", ephemeral: true });
      if (!isWhitelisted(member)) {
        const nukeEntry = getNukeEntry(member.id); nukeEntry.kicks = pruneOld(nukeEntry.kicks); nukeEntry.kicks.push(Date.now());
        if (nukeEntry.kicks.length >= config.nukeKickThreshold) { nukeEntry.kicks = []; await interaction.reply({ content: "🚨 Anti-nuke triggered.", ephemeral: true }); return nukeResponse(guild, member, `Issued ${config.nukeKickThreshold}+ kicks in ${config.nukeWindowMs / 1000}s via commands`); }
        const { allowed, used, limit, resetsInMin } = checkModLimit(member.id, "kick");
        if (!allowed) return interaction.reply({ embeds: [limitDeniedEmbed("kick", used, limit, resetsInMin)], ephemeral: true });
        recordModAction(member.id, "kick");
        const { used: newUsed } = checkModLimit(member.id, "kick");
        await target.kick(reason).catch(() => {});
        secLog(guild, "Member Kicked", `<@${target.id}> kicked by <@${member.id}>: ${reason}`, COLORS.danger);
        return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.danger).setTitle("👢 Member Kicked")
          .setDescription(`<@${target.id}> has been kicked.\n**Reason:** ${reason}`).setFooter({ text: usageFooter("kick", newUsed, limit) }).setTimestamp()] });
      }
      await target.kick(reason).catch(() => {});
      secLog(guild, "Member Kicked", `<@${target.id}> kicked by <@${member.id}>: ${reason}`, COLORS.danger);
      return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.danger).setTitle("👢 Member Kicked").setDescription(`<@${target.id}> kicked — ${reason}`).setTimestamp()] });
    }

    case "bandiscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const target     = interaction.options.getMember("user");
      const reason     = interaction.options.getString("reason") ?? "No reason provided";
      const deleteDays = interaction.options.getInteger("delete_days") ?? 0;
      if (!target) return interaction.reply({ content: "❌ User not found.", ephemeral: true });
      if (!isWhitelisted(member)) {
        const nukeEntry = getNukeEntry(member.id); nukeEntry.bans = pruneOld(nukeEntry.bans); nukeEntry.bans.push(Date.now());
        if (nukeEntry.bans.length >= config.nukeBanThreshold) { nukeEntry.bans = []; await interaction.reply({ content: "🚨 Anti-nuke triggered.", ephemeral: true }); return nukeResponse(guild, member, `Issued ${config.nukeBanThreshold}+ bans in ${config.nukeWindowMs / 1000}s via commands`); }
        const { allowed, used, limit, resetsInMin } = checkModLimit(member.id, "ban");
        if (!allowed) return interaction.reply({ embeds: [limitDeniedEmbed("ban", used, limit, resetsInMin)], ephemeral: true });
        recordModAction(member.id, "ban");
        const { used: newUsed } = checkModLimit(member.id, "ban");
        await target.ban({ reason, deleteMessageSeconds: deleteDays * 86400 }).catch(() => {});
        secLog(guild, "Member Banned", `<@${target.id}> banned by <@${member.id}>: ${reason}\n📊 **${newUsed}/${limit}** bans used in 24h.`, COLORS.danger);
        return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.danger).setTitle("🔨 Member Banned")
          .setDescription(`<@${target.id}> has been banned.\n**Reason:** ${reason}`).setFooter({ text: usageFooter("ban", newUsed, limit) }).setTimestamp()] });
      }
      await target.ban({ reason, deleteMessageSeconds: deleteDays * 86400 }).catch(() => {});
      secLog(guild, "Member Banned", `<@${target.id}> banned by <@${member.id}>: ${reason}`, COLORS.danger);
      return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.danger).setTitle("🔨 Member Banned").setDescription(`<@${target.id}> banned — ${reason}`).setTimestamp()] });
    }

    case "purgediscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const count      = interaction.options.getInteger("count");
      const filterUser = interaction.options.getUser("user");
      if (!isWhitelisted(member)) {
        const { allowed, used, limit, resetsInMin } = checkModLimit(member.id, "purge");
        if (!allowed) return interaction.reply({ embeds: [limitDeniedEmbed("purge", used, limit, resetsInMin)], ephemeral: true });
        recordModAction(member.id, "purge");
        const { used: newUsed } = checkModLimit(member.id, "purge");
        await interaction.deferReply({ ephemeral: true });
        let messages = await interaction.channel.messages.fetch({ limit: 100 }).catch(() => null);
        if (!messages) return interaction.editReply("❌ Could not fetch messages.");
        if (filterUser) messages = messages.filter(m => m.author.id === filterUser.id);
        const deleted = await interaction.channel.bulkDelete([...messages.values()].slice(0, count), true).catch(() => null);
        const n = deleted?.size ?? 0;
        secLog(guild, "Purge", `<@${member.id}> purged **${n}** messages in <#${interaction.channelId}>${filterUser ? ` from <@${filterUser.id}>` : ""}`, COLORS.warn);
        return interaction.editReply({ embeds: [new EmbedBuilder().setColor(COLORS.warn).setTitle("🗑️ Purge Complete")
          .setDescription(`Deleted **${n}** messages${filterUser ? ` from <@${filterUser.id}>` : ""}.`).setFooter({ text: usageFooter("purge", newUsed, limit) }).setTimestamp()] });
      }
      await interaction.deferReply({ ephemeral: true });
      let messages = await interaction.channel.messages.fetch({ limit: 100 }).catch(() => null);
      if (!messages) return interaction.editReply("❌ Could not fetch messages.");
      if (filterUser) messages = messages.filter(m => m.author.id === filterUser.id);
      const deleted = await interaction.channel.bulkDelete([...messages.values()].slice(0, count), true).catch(() => null);
      const n = deleted?.size ?? 0;
      secLog(guild, "Purge", `<@${member.id}> purged **${n}** messages in <#${interaction.channelId}>`, COLORS.warn);
      return interaction.editReply(`🗑️ Deleted **${n}** messages.`);
    }

    case "lockdowndiscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const action  = interaction.options.getString("action");
      const channel = interaction.options.getChannel("channel") ?? interaction.channel;
      const lock    = action === "lock";
      if (lock && !isWhitelisted(member)) {
        const nukeEntry = getNukeEntry(member.id); nukeEntry.channels = pruneOld(nukeEntry.channels); nukeEntry.channels.push(Date.now());
        if (nukeEntry.channels.length >= config.nukeChannelThreshold) { nukeEntry.channels = []; await interaction.reply({ content: "🚨 Anti-nuke triggered.", ephemeral: true }); return nukeResponse(guild, member, `Locked ${config.nukeChannelThreshold}+ channels in ${config.nukeWindowMs / 1000}s via commands`); }
        const { allowed, used, limit, resetsInMin } = checkModLimit(member.id, "lockdown");
        if (!allowed) return interaction.reply({ embeds: [limitDeniedEmbed("lockdown", used, limit, resetsInMin)], ephemeral: true });
        recordModAction(member.id, "lockdown");
        const { used: newUsed } = checkModLimit(member.id, "lockdown");
        await channel.permissionOverwrites.edit(guild.roles.everyone, { SendMessages: false }).catch(() => {});
        secLog(guild, "Channel Locked", `<#${channel.id}> locked by <@${member.id}>`, COLORS.danger);
        return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.danger).setTitle("🔒 Channel Locked")
          .setDescription(`<#${channel.id}> has been locked.`).setFooter({ text: usageFooter("lockdown", newUsed, limit) }).setTimestamp()] });
      }
      await channel.permissionOverwrites.edit(guild.roles.everyone, { SendMessages: lock ? false : null }).catch(() => {});
      secLog(guild, lock ? "Channel Locked" : "Channel Unlocked", `<#${channel.id}> ${lock ? "locked" : "unlocked"} by <@${member.id}>`, lock ? COLORS.danger : COLORS.success);
      return interaction.reply({ embeds: [new EmbedBuilder().setColor(lock ? COLORS.danger : COLORS.success)
        .setTitle(lock ? "🔒 Channel Locked" : "🔓 Channel Unlocked").setDescription(`<#${channel.id}> has been ${lock ? "locked" : "unlocked"}.`).setTimestamp()] });
    }

    case "warndiscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const target = interaction.options.getMember("user");
      const reason = interaction.options.getString("reason") ?? "No reason provided";
      if (!target) return interaction.reply({ content: "❌ User not found.", ephemeral: true });
      if (!isWhitelisted(member)) {
        const { allowed, used, limit, resetsInMin } = checkModLimit(member.id, "warn");
        if (!allowed) return interaction.reply({ embeds: [limitDeniedEmbed("warn", used, limit, resetsInMin)], ephemeral: true });
        recordModAction(member.id, "warn");
        const { used: newUsed } = checkModLimit(member.id, "warn");
        secLog(guild, "Warning Issued", `<@${target.id}> warned by <@${member.id}>: ${reason}`, COLORS.warn);
        return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.warn).setTitle("⚠️ Warning Issued")
          .setDescription(`<@${target.id}> has been warned.\n**Reason:** ${reason}`).setFooter({ text: usageFooter("warn", newUsed, limit) }).setTimestamp()] });
      }
      secLog(guild, "Warning Issued", `<@${target.id}> warned by <@${member.id}>: ${reason}`, COLORS.warn);
      return interaction.reply({ embeds: [new EmbedBuilder().setColor(COLORS.warn).setTitle("⚠️ Warning Issued").setDescription(`<@${target.id}> warned — ${reason}`).setTimestamp()] });
    }

    case "limitsdiscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const windowHours = config.modWindowMs / 3600000;
      if (isWhitelisted(member)) return interaction.reply({ ephemeral: true, embeds: [new EmbedBuilder().setColor(COLORS.info).setTitle("🛡️ Your Mod Limits").setDescription("✨ **You are whitelisted.** No rate limits apply to your account.").setTimestamp()] });
      const actions = [
        { key: "ban", emoji: "🔨", label: "Bans" }, { key: "kick", emoji: "👢", label: "Kicks" },
        { key: "mute", emoji: "🔇", label: "Mutes" }, { key: "warn", emoji: "⚠️", label: "Warns" },
        { key: "purge", emoji: "🗑️", label: "Purges" }, { key: "lockdown", emoji: "🔒", label: "Lockdowns" },
      ];
      const fields = actions.map(({ key, emoji, label }) => {
        const { used, limit, remaining } = checkModLimit(member.id, key);
        const warn = remaining === 0 ? " 🚫" : remaining <= Math.ceil(limit * 0.2) ? " ⚠️" : "";
        return { name: `${emoji} ${label}${warn}`, value: `\`${buildBar(used, limit, 8)}\` **${used}/${limit}** used (${Math.round((used / limit) * 100)}%) — **${remaining}** remaining`, inline: false };
      });
      return interaction.reply({ ephemeral: true, embeds: [new EmbedBuilder().setColor(COLORS.info).setTitle("📊 Your Mod Action Limits")
        .setDescription(`Rolling **${windowHours}h** window. Limits reset automatically as old actions age out.`).addFields(...fields).setTimestamp()] });
    }

    case "antipingdiscord": {
      if (!isMod(member)) return interaction.reply({ content: "❌ You need the mod role.", ephemeral: true });
      const sub = interaction.options.getSubcommand();
      switch (sub) {
        case "status":
          return interaction.reply({ ephemeral: true, embeds: [new EmbedBuilder().setColor(antiPing.enabled ? COLORS.success : COLORS.neutral).setTitle("📡 Anti-Ping — Status")
            .addFields(
              { name: "Enabled", value: antiPing.enabled ? "✅ On" : "⛔ Off", inline: true },
              { name: "Action", value: `\`${antiPing.action}\``, inline: true },
              { name: "Duration", value: `${antiPing.timeoutMin} min`, inline: true },
              { name: "Delete message", value: antiPing.deleteMessage ? "Yes" : "No", inline: true },
              { name: "Ignore replies", value: antiPing.ignoreReplies ? "Yes" : "No", inline: true },
              { name: "Channel notice", value: antiPing.notifyChannel ? "On" : "Off", inline: true },
              { name: "Response", value: `\`\`\`${antiPing.responseTemplate}\`\`\``, inline: false },
              { name: "Protected users", value: antiPing.protectedUsers.length ? antiPing.protectedUsers.map(id => `<@${id}>`).join(", ") : "None", inline: false },
              { name: "Protected roles", value: antiPing.protectedRoles.length ? antiPing.protectedRoles.map(id => `<@&${id}>`).join(", ") : "None", inline: false },
            ).setTimestamp()] });
        case "toggle":
          antiPing.enabled = interaction.options.getBoolean("enabled"); saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(antiPing.enabled ? COLORS.success : COLORS.neutral, `Anti-ping is now **${antiPing.enabled ? "enabled" : "disabled"}**.`, "Anti-Ping")] });
        case "action":
          antiPing.action = interaction.options.getString("type"); saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.info, `Punishment set to **${antiPing.action}**.`, "Anti-Ping")] });
        case "duration":
          antiPing.timeoutMin = interaction.options.getInteger("minutes"); saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.info, `Mute/timeout duration set to **${antiPing.timeoutMin} min**.`, "Anti-Ping")] });
        case "delete":
          antiPing.deleteMessage = interaction.options.getBoolean("enabled"); saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.info, `Offending messages will ${antiPing.deleteMessage ? "**be deleted**" : "**not be deleted**"}.`, "Anti-Ping")] });
        case "ignorereplies":
          antiPing.ignoreReplies = interaction.options.getBoolean("enabled"); saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.info, `Reply-pings will ${antiPing.ignoreReplies ? "**be ignored**" : "**be punished**"}.`, "Anti-Ping")] });
        case "response": {
          const text = interaction.options.getString("text");
          antiPing.responseTemplate = text.toLowerCase() === "default" ? antiPingDefaults.responseTemplate : text; saveAntiPing();
          const preview = renderAntiPingResponse(member.id, "@ProtectedUser", `timed out for ${antiPing.timeoutMin} min`);
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.info, `Response template updated.\n\n**Template:**\n\`\`\`${antiPing.responseTemplate}\`\`\`\n**Preview:**\n${preview}\n\n_Placeholders: \`{user}\`, \`{targets}\`, \`{action}\`._`, "Anti-Ping")] });
        }
        case "notify":
          antiPing.notifyChannel = interaction.options.getBoolean("enabled"); saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.info, `Public channel warning is now **${antiPing.notifyChannel ? "on" : "off"}**.`, "Anti-Ping")] });
        case "protect": {
          const action = interaction.options.getString("action"); const user = interaction.options.getUser("user");
          if (action === "add") { if (antiPing.protectedUsers.includes(user.id)) return interaction.reply({ content: `⚠️ <@${user.id}> is already protected.`, ephemeral: true }); antiPing.protectedUsers.push(user.id); }
          else antiPing.protectedUsers = antiPing.protectedUsers.filter(id => id !== user.id);
          saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.success, `<@${user.id}> ${action === "add" ? "is now **protected**" : "is **no longer protected**"} from pings.`, "Anti-Ping")] });
        }
        case "protectrole": {
          const action = interaction.options.getString("action"); const role = interaction.options.getRole("role");
          if (action === "add") { if (antiPing.protectedRoles.includes(role.id)) return interaction.reply({ content: `⚠️ <@&${role.id}> is already protected.`, ephemeral: true }); antiPing.protectedRoles.push(role.id); }
          else antiPing.protectedRoles = antiPing.protectedRoles.filter(id => id !== role.id);
          saveAntiPing();
          return interaction.reply({ ephemeral: true, embeds: [gEmbed(COLORS.success, `<@&${role.id}> ${action === "add" ? "is now **protected**" : "is **no longer protected**"} from pings.`, "Anti-Ping")] });
        }
        case "list":
          return interaction.reply({ ephemeral: true, embeds: [new EmbedBuilder().setColor(COLORS.info).setTitle("📡 Anti-Ping — Protected")
            .addFields(
              { name: "Users", value: antiPing.protectedUsers.length ? antiPing.protectedUsers.map(id => `<@${id}>`).join("\n") : "None", inline: true },
              { name: "Roles", value: antiPing.protectedRoles.length ? antiPing.protectedRoles.map(id => `<@&${id}>`).join("\n") : "None", inline: true },
            ).setTimestamp()] });
      }
      return;
    }

    case "configdiscord": {
      const windowHours = config.modWindowMs / 3600000;
      return interaction.reply({ ephemeral: true, embeds: [new EmbedBuilder().setTitle("🛡️ Guardian — Configuration").setColor(COLORS.info)
        .addFields(
          { name: "Log Channel", value: config.logChannelId ? `<#${config.logChannelId}>` : "❌ Not set", inline: true },
          { name: "Mute Role", value: config.muteRoleId ? `<@&${config.muteRoleId}>` : "❌ Not set", inline: true },
          { name: "Mod Role", value: config.modRoleId ? `<@&${config.modRoleId}>` : "❌ Not set", inline: true },
          { name: "🏅 Nuke Whitelist Roles", value: config.nukeWhitelistRoleIds.length ? config.nukeWhitelistRoleIds.map(id => `<@&${id}>`).join(", ") : "None", inline: false },
          { name: "🏅 Nuke Whitelist Users", value: config.nukeWhitelistUserIds.length ? config.nukeWhitelistUserIds.map(id => `<@${id}>`).join(", ") : "None", inline: false },
          { name: "💬 Anti-Spam", value: `${config.spamThreshold} msgs / ${config.spamWindowMs}ms → ${config.spamMuteMin} min mute`, inline: false },
          { name: "🚪 Anti-Raid", value: `${config.raidJoinThreshold} joins / ${config.raidWindowMs}ms → ${config.raidLockdownMin} min lockdown`, inline: false },
          { name: "📡 Anti-Ping", value: `${antiPing.enabled ? "On" : "Off"} • action: \`${antiPing.action}\` • ${antiPing.timeoutMin} min • ${antiPing.protectedUsers.length} users / ${antiPing.protectedRoles.length} roles protected`, inline: false },
          { name: "💣 Anti-Nuke (fast window)", value: `Window: ${config.nukeWindowMs}ms • channels ≥${config.nukeChannelThreshold} • roles ≥${config.nukeRoleThreshold} • bans ≥${config.nukeBanThreshold} • kicks ≥${config.nukeKickThreshold} • webhooks ≥${config.nukeWebhookThreshold}`, inline: false },
          { name: `📊 Mod Daily Limits (${windowHours}h)`, value: `🔨 ${config.modBanLimit} • 👢 ${config.modKickLimit} • 🔇 ${config.modMuteLimit} • ⚠️ ${config.modWarnLimit} • 🗑️ ${config.modPurgeLimit} • 🔒 ${config.modLockdownLimit}`, inline: false },
        ).setFooter({ text: "Edit values in .env and restart to apply." }).setTimestamp()] });
    }

    case "nuketestdiscord": {
      if (interaction.user.id !== guild.ownerId) return interaction.reply({ content: "❌ Server owner only.", ephemeral: true });
      return interaction.reply({ ephemeral: true, embeds: [new EmbedBuilder().setColor(COLORS.success).setTitle("✅ Anti-Nuke Active").setDescription("The anti-nuke system is **online** and monitoring this server.").setTimestamp()] });
    }

    case "helpdiscord": {
      const windowHours = config.modWindowMs / 3600000;
      return interaction.reply({ ephemeral: true, embeds: [new EmbedBuilder().setColor(COLORS.info).setTitle("🛡️ Guardian — Discord Security Commands")
        .addFields(
          { name: "🔇 /mutediscord", value: "`@user [minutes] [reason]` — Mute a member (roles stashed & restored)", inline: false },
          { name: "🔊 /unmutediscord", value: "`@user` — Unmute & restore stashed roles", inline: false },
          { name: "👢 /kickdiscord", value: "`@user [reason]` — Kick a member", inline: false },
          { name: "🔨 /bandiscord", value: "`@user [reason] [delete_days]` — Ban a member", inline: false },
          { name: "🗑️ /purgediscord", value: "`count [user]` — Bulk-delete messages", inline: false },
          { name: "🔒 /lockdowndiscord", value: "`lock|unlock [channel]` — Lock/unlock a channel", inline: false },
          { name: "⚠️ /warndiscord", value: "`@user [reason]` — Warn a member", inline: false },
          { name: "📡 /antipingdiscord", value: "Configure ping protection — `status`, `toggle`, `action`, `protect`, etc.", inline: false },
          { name: "📊 /limitsdiscord", value: "Check your remaining mod action limits today", inline: false },
          { name: "⚙️ /configdiscord", value: "View security configuration *(admin)*", inline: false },
          { name: "🧪 /nuketestdiscord", value: "Confirm anti-nuke is active *(owner)*", inline: false },
          { name: "⏱️ Rate Limits", value: `Mod actions are rate-limited over a **${windowHours}h** window. Use \`/limitsdiscord\`.`, inline: false },
        ).setFooter({ text: "Guardian • Discord Security" }).setTimestamp()] });
    }
  }
}

module.exports = { commands, intents, partials, isGuardianCommand, initEvents, handle };
