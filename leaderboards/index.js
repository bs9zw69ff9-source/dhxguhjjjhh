/* ---------------- leaderboards: caps/playtime boards, player list, live dashboard ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./leaderboards")(ctx). */
module.exports = function(ctx) {
  const {
  ACTIVE_SERVERS, ARREST_LEADERBOARD_CHANNEL, DASHBOARD_CHANNEL, DASHBOARD_INTERVAL_MS, DIVIDER, EmbedBuilder, FILES,
  GLYPH, LEADERBOARD_TOP_N, NV, PLAYERLIST_CHANNEL, allCachedPlayers,
  bar, brand, cell, client, loadMenuGrants,
  fs, getModsavePath, hero, logger, meter,
  parseRcon, path, refreshPlayerCache, safeRead, safeWrite, sendRcon,
  serverLabel,
  playerCache, easternClock,
  } = ctx;
  const { dailyPeak } = require("../stats/peaks");

// ---- leaderboard ----
function buildLeaderboardData() {
  const base = getModsavePath();
  if (!base) return null;
  const entries = [];
  try {
    for (const file of fs.readdirSync(base).filter(f => f.endsWith(".txt") && f.toLowerCase() !== "banlist.txt")) {
      const id  = path.basename(file, ".txt");
      try {
        const bal = parseInt(fs.readFileSync(path.join(base, file), "utf8").trim(), 10);
        if (!isNaN(bal)) entries.push({ playerId: id, balance: bal });
      } catch {}
    }
  } catch (err) { logger.error("Leaderboard", err.message); return null; }
  const sorted = entries.sort((a, b) => b.balance - a.balance);
  const top = sorted.slice(0, LEADERBOARD_TOP_N);
  // Whole-economy totals (every ledger, not just the shown top N) for the header.
  top.totalCaps    = sorted.reduce((s, e) => s + e.balance, 0);
  top.totalPlayers = sorted.length;
  return top;
}

function rankLabel(i) {
  // Top three get the ◆ badge; everyone else a plain aligned number.
  return `\`${i < 3 ? "◆" : "#"}${String(i + 1).padStart(2)}\``;
}

function buildLeaderboardEmbed() {
  const entries = buildLeaderboardData();
  const embed = new EmbedBuilder().setColor(NV.GOLD)
    .setTitle(`Richest Players - Top ${LEADERBOARD_TOP_N}`);
  if (!entries) return brand(embed.setColor(NV.RUST_RED)
    .setDescription(`${hero("Economy records inaccessible.")}\n\`MODSAVE_PATH\` not configured or unreadable - check your \`.env\`.`),
    { footer: { text: `Updated every 30s` } });
  if (!entries.length) return brand(embed.setColor(NV.IRRAD_GREEN)
    .setDescription(`${hero("No ledgers found.")}\nNo dollar records on file yet.`),
    { footer: { text: `Updated every 30s` } });
  const top = entries[0]?.balance || 1;
  const body = entries.map((e, i) => {
    const meter = i < 5 ? `  \`${bar(e.balance, top, 8)}\`` : "";
    return `${rankLabel(i)}  **${e.playerId}**  -  $${e.balance.toLocaleString()}${meter}`;
  }).join("\n");
  return brand(embed.setDescription(
    `${hero("Players ranked by account balance, highest first.")}\n${GLYPH.caps} **Combined: $${(entries.totalCaps ?? 0).toLocaleString()}** across **${entries.totalPlayers ?? entries.length}** ledgers\n${body}`),
    { thumb: true, footer: { text: `Updated every 30s` } });
}

/* Message IDs for the "edit this one message in place" auto-posts (both
   leaderboards, the player list, the dashboard) are persisted here - NOT kept
   in memory only. A bot restart used to forget which message it was editing,
   so the next tick couldn't find it and posted a fresh one instead, leaving
   the old one orphaned in the channel. Restart a few times and they stack up. */
const loadAutopostState = () => safeRead(FILES.AUTOPOST_STATE, {});
function getAutopostMsgId(key) { return loadAutopostState()[key] || null; }
function setAutopostMsgId(key, id) { safeWrite(FILES.AUTOPOST_STATE, { ...loadAutopostState(), [key]: id }); }

/* Shared "edit one message in place, or post it fresh" auto-post loop. Every board
   (leaderboard, arrests, player list, dashboard) does the same dance: fetch the
   channel, build the embed, edit the tracked message if it still exists (drop the
   stale id if it doesn't), else send a new one and remember its id. `getEmbed` may
   be sync or async; a falsy return skips the post. */
async function autopost(key, channelId, getEmbed) {
  if (!channelId) return;
  let channel;
  try { channel = await client.channels.fetch(channelId); } catch { return; }
  let embed;
  try { embed = await getEmbed(); } catch (e) { logger.warn("Autopost", `${key} build failed: ${e.message}`); return; }
  if (!embed) return;
  const existingId = getAutopostMsgId(key);
  if (existingId) {
    try { const m = await channel.messages.fetch(existingId); await m.edit({ embeds: [embed] }); return; }
    catch { setAutopostMsgId(key, null); }
  }
  try { const m = await channel.send({ embeds: [embed] }); setAutopostMsgId(key, m.id); } catch {}
}

const postLeaderboard = () => autopost("leaderboard", process.env.LEADERBOARD_CHANNEL, buildLeaderboardEmbed);

/* ---- arrests / jail-time "most wanted" board ---- */
// Rank players by total jail time served (then arrest count) from the arrests
// registry. Each stored arrest has { playerId, minutes, ... }; the map key is the
// lowercased name, so we prefer the display-case playerId off the entries.
const jailMin = (a) => Number(a.minutes) || 0;
function buildArrestBoardData() {
  const all = safeRead(FILES.ARRESTS, {});
  const rows = [];
  for (const [key, list] of Object.entries(all)) {
    const arr = Array.isArray(list) ? list : (list ? [list] : []);
    if (!arr.length) continue;
    rows.push({
      playerId: arr[arr.length - 1]?.playerId || key,
      arrests:  arr.length,
      jail:     arr.reduce((s, a) => s + jailMin(a), 0),
    });
  }
  rows.sort((a, b) => b.jail - a.jail || b.arrests - a.arrests);
  const top = rows.slice(0, LEADERBOARD_TOP_N);
  top.totalArrests = rows.reduce((s, r) => s + r.arrests, 0);
  top.totalJail    = rows.reduce((s, r) => s + r.jail, 0);
  top.totalPlayers = rows.length;
  return top;
}

function buildArrestBoardEmbed() {
  const rows = buildArrestBoardData();
  const embed = new EmbedBuilder().setColor(NV.RUST_RED).setTitle(`Most Wanted - Top ${LEADERBOARD_TOP_N}`);
  if (!rows.length) return brand(embed.setColor(NV.IRRAD_GREEN)
    .setDescription(hero("No arrests have been recorded yet.")),
    { footer: { text: "Updated every 30s" } });
  const topJail = rows[0]?.jail || 1;
  const body = rows.map((e, i) => {
    const m = i < 5 ? `  \`${bar(e.jail, topJail, 8)}\`` : "";
    return `${rankLabel(i)}  **${e.playerId}**  -  ${e.arrests} arrest${e.arrests !== 1 ? "s" : ""}, ${e.jail.toLocaleString()} min${m}`;
  }).join("\n");
  return brand(embed.setDescription(
    `${hero("Players ranked by total jail time served.")}\n${GLYPH.rank} **Combined: ${rows.totalArrests.toLocaleString()} arrest${rows.totalArrests !== 1 ? "s" : ""}, ${rows.totalJail.toLocaleString()} min served** across **${rows.totalPlayers}** offender${rows.totalPlayers !== 1 ? "s" : ""}\n${body}`),
    { footer: { text: "Updated every 30s" } });
}

const postArrestBoard = () => autopost("arrestBoard", ARREST_LEADERBOARD_CHANNEL, buildArrestBoardEmbed);

/* Live player list - edits its own message in a channel every 30s. */
function buildPlayerListEmbed() {
  // Tag connected players who hold a Staff / High Staff RCON menu (granted through
  // the RCON menu panel) as staff. Whitelist-menu holders aren't staff, so they're
  // not tagged. Everyone else is shown plain.
  const grants = loadMenuGrants();
  const staffTag = (name) => {
    const g = grants[name.toLowerCase()];
    return g && g.some(x => x.menuValue === "staff" || x.menuValue === "highstaff") ? "  -  Staff" : "";
  };
  const fmt = (arr) => {
    if (!arr.length) return "*Empty*";
    let out = arr.map(n => `• ${n}${staffTag(n)}`).join("\n");
    if (out.length > 1024) out = out.slice(0, 1000).replace(/\n[^\n]*$/, "") + "\n...";
    return out;
  };
  const total = allCachedPlayers().length;
  const stats = safeRead(FILES.SERVER_STATS, {});
  const today = dailyPeak(stats, easternClock().date);
  const embed = new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Live Player List")
    .setDescription(`${hero(`**${total}** player${total !== 1 ? "s" : ""} online right now.`)}\n-# Today's peak: **${today}**  -  all-time: **${stats?.combined?.peak ?? 0}**`);
  for (const srv of ACTIVE_SERVERS) {
    const list = [...playerCache[srv]].sort((a, b) => a.localeCompare(b));
    embed.addFields({ name: `${serverLabel(srv)} (${list.length})`, value: fmt(list), inline: true });
  }
  return brand(embed.setFooter({ text: "Updates every 30s" }));
}
const postPlayerList = () => autopost("playerList", PLAYERLIST_CHANNEL, async () => {
  // Refresh each server's live roster first (best-effort - a failed refresh still
  // renders the board from the last known cache).
  try { for (const srv of ACTIVE_SERVERS) await refreshPlayerCache(srv); } catch {}
  return buildPlayerListEmbed();
});

// Delete every message in a channel - used on startup so stale leaderboard/player-list
// messages from previous runs don't pile up (the bot re-posts a fresh one). Bulk-deletes
async function purgeChannel(channelId) {
  if (!channelId) return 0;
  let channel;
  try { channel = await client.channels.fetch(channelId); } catch { return 0; }
  if (!channel || typeof channel.bulkDelete !== "function") return 0;
  let total = 0;
  for (let pass = 0; pass < 12; pass++) {              // up to ~1200 messages
    let msgs;
    try { msgs = await channel.messages.fetch({ limit: 100 }); } catch { break; }
    if (!msgs.size) break;
    let removed = 0;
    try { const d = await channel.bulkDelete(msgs, true); removed = d.size; } catch {}   // true = ignore >14d
    if (removed < msgs.size) {                         // leftovers are >14d - bulkDelete skips them
      for (const m of msgs.values()) { try { await m.delete(); removed++; } catch {} }
    }
    total += removed;
    if (removed === 0) break;                          // nothing deletable (e.g. missing permission) - stop
  }
  return total;
}

// Clear stray messages from the leaderboard/player-list channels (anything left over
// from before the bot tracked message ids, manual posts, etc.), then post/edit the
// tracked message as usual. Deliberately does NOT reset the tracked ids first: if
// purgeChannel couldn't actually delete the tracked message (missing permission, a
// message >14 days old that the individual-delete fallback also failed on, a rate
// limit), postLeaderboard() et al. will still find and edit it - so a purge that only
// partially succeeds can't result in a duplicate the way an unconditional reset would.
async function refreshLeaderboardChannels() {
  const channels = [...new Set([process.env.LEADERBOARD_CHANNEL, ARREST_LEADERBOARD_CHANNEL, PLAYERLIST_CHANNEL, DASHBOARD_CHANNEL].filter(Boolean))];
  for (const ch of channels) {
    try { const n = await purgeChannel(ch); if (n) logger.info("Purge", `Cleared ${n} old message(s) from channel ${ch}`); }
    catch (e) { logger.warn("Purge", `Could not purge ${ch}: ${e.message}`); }
  }
  postLeaderboard(); postArrestBoard(); postPlayerList(); if (DASHBOARD_CHANNEL) postDashboard();
}

// Live server-status dashboard: one self-refreshing embed with per-server
// health, player-count progress bars, map/mode, and gateway ping.
async function serverSnapshot(srv) {
  try {
    const [list, info] = await Promise.all([
      sendRcon("RefreshList", srv, 2500, 0),
      sendRcon("ServerInfo",  srv, 2500, 0),
    ]);
    const ld = parseRcon(list), id = parseRcon(info);
    const players = (ld?.PlayerList ?? []).length;
    const max = Number(id?.ServerInfo?.MaxPlayers ?? id?.MaxPlayers ?? 24) || 24;
    return {
      up: !!ld?.Successful,
      players, max,
      map:  id?.ServerInfo?.MapLabel ?? id?.MapLabel ?? id?.ServerInfo?.MapName ?? "unknown",
      mode: id?.ServerInfo?.GameMode ?? id?.GameMode ?? "unknown",
      name: id?.ServerInfo?.ServerName ?? id?.ServerName ?? serverLabel(srv),
    };
  } catch { return { up: false, players: 0, max: 24, map: "-", mode: "-", name: serverLabel(srv) }; }
}
/* Two compact monospace lines per server. Plain code block (no ANSI color) so it
   renders identically on desktop and mobile, and lines stay under ~28 chars so
   phones don't wrap them. */
function hudRow(s) {
  const dot   = s.up ? GLYPH.up : GLYPH.down;
  const name  = cell(s.name, 22);
  if (!s.up) return `${dot} ${name}\n  offline`;
  const count = cell(`${s.players}/${s.max}`, 5);
  return `${dot} ${name}\n  ${count} ${bar(s.players, s.max, 10)}  ${cell(s.mode, 8)}`;
}
function buildDashboardEmbed(snaps) {
  const anyUp    = snaps.some(s => s.up);
  const totalP   = snaps.reduce((a, s) => a + (s.up ? s.players : 0), 0);
  const totalMax = snaps.reduce((a, s) => a + (Number(s.max) || 0), 0);
  const stats    = safeRead(FILES.SERVER_STATS, {});
  const peak     = stats?.combined?.peak ?? 0;
  const today    = dailyPeak(stats, easternClock().date);
  const gw       = Math.max(0, client.ws.ping);
  const lines = [
    "LIVE NETWORK STATUS",
    `${totalP}/${totalMax} online - gw ${gw}ms`,
    `peak ${peak} all-time - ${today} today`,
    "──────────────────────────",
    ...snaps.map(hudRow),
  ];
  const embed = new EmbedBuilder()
    .setColor(anyUp ? NV.IRRAD_GREEN : NV.RUST_RED)
    .setTitle("Live Server Status")
    .setDescription("```\n" + lines.join("\n") + "\n```");
  return brand(embed);
}
async function dashboardSnapshots() {
  return Promise.all(ACTIVE_SERVERS.map(serverSnapshot));
}
const postDashboard = () => autopost("dashboard", DASHBOARD_CHANNEL, async () => buildDashboardEmbed(await dashboardSnapshots()));


  return { buildArrestBoardData, buildArrestBoardEmbed, buildDashboardEmbed, buildLeaderboardData, buildLeaderboardEmbed, buildPlayerListEmbed, dashboardSnapshots, getAutopostMsgId, hudRow, loadAutopostState, postArrestBoard, postDashboard, postLeaderboard, postPlayerList, purgeChannel, rankLabel, refreshLeaderboardChannels, serverSnapshot, setAutopostMsgId };
};
