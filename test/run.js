/* Copyright (c) 2026 bs9zw69ff9-source. All rights reserved. Proprietary — see LICENSE. */
/* ================================================================
   Self-contained unit tests for the bot's pure logic.
   ================================================================
   The bot file requires discord.js + dotenv and opens a Discord
   connection when run directly. To test the logic without those
   dependencies or any network, this runner:
     1. builds a throwaway sandbox dir with tiny stub modules,
     2. copies index.js into it and requires it (login is guarded
        behind require.main === module, so it won't connect),
     3. exercises the exported helpers against real temp files.
   Run with:  npm test
   ================================================================ */
const fs   = require("fs");
const os   = require("os");
const path = require("path");
const assert = require("assert");

const sandbox = fs.mkdtempSync(path.join(os.tmpdir(), "bot-test-"));
const nm = path.join(sandbox, "node_modules");
fs.mkdirSync(path.join(nm, "dotenv"), { recursive: true });
fs.mkdirSync(path.join(nm, "discord.js"), { recursive: true });

fs.writeFileSync(path.join(nm, "dotenv", "index.js"),
  "module.exports = { config: () => ({}) };");

// Chainable proxy stands in for every discord.js builder/class.
fs.writeFileSync(path.join(nm, "discord.js", "index.js"), `
function chainable(){const p=new Proxy(function(){},{get(t,k){if(k==='toJSON')return()=>({});if(k==='then')return undefined;return()=>p;},apply(){return p;}});return p;}
class Chain{constructor(){return chainable();}}
const e=new Proxy({},{get:()=>1});
module.exports={Client:Chain,GatewayIntentBits:e,ActivityType:e,REST:Chain,Routes:e,SlashCommandBuilder:Chain,EmbedBuilder:Chain,ActionRowBuilder:Chain,ButtonBuilder:Chain,ButtonStyle:e,ComponentType:e};
`);

fs.copyFileSync(path.join(__dirname, "..", "index.js"), path.join(sandbox, "index.js"));
fs.copyFileSync(path.join(__dirname, "..", "ipBans.js"), path.join(sandbox, "ipBans.js"));

process.chdir(sandbox);
process.env.DISCORD_TOKEN   = "x";
process.env.CLIENT_ID       = "x";
process.env.RCON_HOST_1     = "x";
process.env.RCON_PORT_1     = "1";
process.env.RCON_PASSWORD_1 = "x";
process.env.LOG_LEVEL       = "ERROR";
process.env.DONATOR_PATH    = path.join(sandbox, "donator.txt");
process.env.BLACKLIST_IDS   = "55, 66";

const bot = require(path.join(sandbox, "index.js"));

let pass = 0, fail = 0;
const ok = (cond, msg) => {
  try { assert.ok(cond, msg); console.log("  ✓ " + msg); pass++; }
  catch { console.error("  ✗ " + msg); fail++; }
};

(async () => {
  console.log("Player notes:");
  ok(await bot.addPlayerNote("P1", "a", "m") === 1, "first note -> 1");
  ok(await bot.addPlayerNote("p1", "b", "m") === 2, "case-insensitive key -> 2");
  ok(bot.getPlayerNotes("P1").length === 2, "reads back 2 notes");
  ok(await bot.clearPlayerNotes("p1") === 2 && bot.getPlayerNotes("p1").length === 0, "clear empties");

  console.log("Last seen:");
  bot.recordLastSeen(["Alice", "Bob"], 1000);
  ok(bot.getLastSeen("ALICE") === 1000, "case-insensitive lookup");
  ok(bot.getLastSeen("nobody") === null, "unknown -> null");
  bot.recordLastSeen(["Alice"], 2000);
  ok(bot.getLastSeen("alice") === 2000, "later sighting overwrites");

  console.log("Known-player registry:");
  bot.recordKnownPlayers(["NCR_Private", "NCR_Sergeant", "Legion_Recruit"], 1000);
  bot.recordKnownPlayers(["NCR_Private"], 5000); // already known -> updates lastSeen, no dupe
  ok(Object.keys(bot.loadKnownPlayers()).length === 3, "registry stores each player once (case-insensitive key)");
  ok(bot.loadKnownPlayers()["ncr_private"].name === "NCR_Private", "stores original display casing");
  const ncr = bot.getKnownPlayerChoices("ncr_");
  ok(ncr.length === 2 && ncr.every(c => c.value.toLowerCase().startsWith("ncr_")), "substring query 'ncr_' matches both NCR players");
  ok(ncr[0].name === ncr[0].value && !/\(offline\)|\[S\d/.test(ncr[0].name), "autocomplete shows the bare name (no decorations)");
  ok(bot.getKnownPlayerChoices("private").length === 1, "mid-string match works");
  const excl = bot.getKnownPlayerChoices("ncr_", new Set(["ncr_private"]));
  ok(excl.length === 1 && excl[0].value === "NCR_Sergeant", "exclude set drops already-listed names");
  // seed from existing data (playtime keys) — FactionRoles path won't exist here, that branch is skipped
  fs.writeFileSync(bot.FILES.PLAYTIME, JSON.stringify({ "Seeded_Courier": 42 }));
  bot.seedKnownPlayers();
  ok(bot.getKnownPlayerChoices("seeded").some(c => c.value === "Seeded_Courier"), "seedKnownPlayers backfills from playtime");

  console.log("Context-aware autocomplete:");
  const mk = (cmd, opts = {}, sub = null) => ({ commandName: cmd, options: { getSubcommand: () => sub, getString: (n) => opts[n] ?? null } });
  ok(bot.commandPlayerCandidates(mk("kick")) === null, "kick → null (default online+known list)");
  ok(bot.commandPlayerCandidates(mk("stats")) === null, "stats → null (default)");
  fs.writeFileSync(bot.FILES.MENU_GRANTS, JSON.stringify({ "rcon_guy": [{ server: "both", menuValue: "staff" }] }));
  ok(bot.commandPlayerCandidates(mk("stripmenu")).length === 1, "stripmenu → only menu-grant holders");
  await bot.upsertTempBan({ playerId: "Exiled_One", reason: "x", expires: Date.now() + 1e6, durationLabel: "1d", moderator: "m", server: "both" });
  ok(bot.commandPlayerCandidates(mk("unban")).some(n => n === "Exiled_One"), "unban → only currently-banned players");
  fs.writeFileSync(process.env.DONATOR_PATH, "Big_Spender\n");
  ok(bot.commandPlayerCandidates(mk("donator", {}, "remove")).includes("Big_Spender"), "donator remove → donator file entries");
  ok(bot.commandPlayerCandidates(mk("donator", {}, "add")) === null, "donator add → null (any player)");
  fs.rmSync(process.env.DONATOR_PATH);   // restore missing-file state for the later donator tests
  ok(bot.commandPlayerCandidates(mk("faction", {}, "remove")) === null, "faction remove w/o faction selected → null (fallback)");
  ok(Array.isArray(bot.commandPlayerCandidates(mk("faction", { faction: "NCR" }, "remove"))), "faction remove w/ faction → member list (array)");

  console.log("Playtime leaderboard:");
  bot.savePlaytime({ Alpha: 500, Bravo: 1200, Charlie: 0, Delta: 30 });
  const lb = bot.buildPlaytimeLeaderboardData();
  ok(lb[0].playerId === "Bravo" && lb[1].playerId === "Alpha", "sorted by minutes desc");
  ok(!lb.some(e => e.playerId === "Charlie"), "zero-playtime players excluded");
  ok(lb.length === 3, "top-N excludes the zero entry (3 of 4)");

  console.log("Warning removal:");
  fs.writeFileSync(bot.FILES.WARNS, JSON.stringify({ p1: [
    { reason: "a", by: "m", at: 1 }, { reason: "b", by: "m", at: 2 }, { reason: "c", by: "m", at: 3 } ] }));
  let r = await bot.removeWarningAt("P1", 2);
  ok(r.removed.reason === "b" && r.remaining === 2, "removes #2, 2 remain");
  ok((await bot.removeWarningAt("p1", 99)).removed === null, "out-of-range -> nothing");
  r = await bot.removeWarningAt("p1", 1);
  ok(r.removed.reason === "a" && r.remaining === 1, "remove #1 -> 1 remains");
  await bot.removeWarningAt("p1", 1);
  ok(JSON.parse(fs.readFileSync(bot.FILES.WARNS, "utf8")).p1 === undefined, "empty key deleted");

  console.log("Donators:");
  ok(!fs.existsSync(bot.DONATOR_FILE) && bot.readDonatorFile().length === 0, "missing file reads empty");
  ok(bot.addDonator("D1").ok && bot.isDonator("d1"), "add + case-insensitive membership");
  ok(bot.addDonator("D1").already, "duplicate detected");
  ok(bot.removeDonator("ghost").missing, "remove missing -> missing");
  ok(bot.removeDonator("d1").ok && !bot.isDonator("d1"), "remove existing");

  console.log("Access control:");
  ok(bot.isOwner("1014251293159731310") && !bot.isOwner("9"), "hardcoded owner");
  ok(bot.isBlacklisted("55") && bot.isBlacklisted("66"), "BLACKLIST_IDS env parsed (comma/space separated)");
  ok(!bot.isBlacklisted("56"), "non-listed id not blacklisted");

  console.log("IP bans (ipBans.js):");
  {
    const logPath = path.join(sandbox, "Pavlov.log");
    // Real Pavlov line formats: pre-auth accept (UniqueId INVALID -> ignored),
    // login (?Name=...userId: NULL:<hex>...platform: NULL), and the on-disconnect
    // UChannel::Close line (RemoteAddr + UniqueId, with fields between).
    fs.writeFileSync(logPath, [
      "[2026.06.16-19.00.39:100][0]LogNet: NotifyAcceptedConnection: Name: NewWorldBlues, TimeStamp: x, [UNetConnection] RemoteAddr: 203.0.113.7:54321, Name: IpConnection_1, Driver: GameNetDriver IpNetDriver_1, IsServer: YES, PC: NULL, Owner: NULL, UniqueId: INVALID",
      "[2026.06.16-19.00.39:200][1]LogNet: Login request: ?Name=NCR_Ranger?playerHeight=160.000000?platform=oculus?pid=NCR_Ranger?name=NCR_Ranger userId: NULL:aaa111 platform: NULL",
      "[2026.06.16-19.06.00:000][2]LogNet: NotifyAcceptedConnection: Name: NewWorldBlues, TimeStamp: x, [UNetConnection] RemoteAddr: 203.0.113.7:5000, Name: IpConnection_2, Driver: GameNetDriver IpNetDriver_1, IsServer: YES, PC: NULL, Owner: NULL, UniqueId: INVALID",
      "[2026.06.16-19.06.01:000][3]LogNet: Login request: ?Name=AltGuy?platform=oculus?pid=AltGuy?name=AltGuy userId: NULL:bbb222 platform: NULL",
      // both aaa111 and bbb222 disconnect from the SAME ip -> confirmed shared IP -> alts
      "[2026.06.16-19.09.00:000][4]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 198.51.100.9:40000, Name: IpConnection_1, IsServer: YES, UniqueId: NULL:aaa111",
      "[2026.06.16-19.09.05:000][5]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 198.51.100.9:40001, Name: IpConnection_2, IsServer: YES, UniqueId: NULL:bbb222",
    ].join("\n") + "\n");   // real logs end with a newline; the parser buffers an incomplete trailing line
    const ipBans = require(path.join(sandbox, "ipBans.js"));
    clearInterval(ipBans.init({ logFiles: [logPath], onAutoBan: async () => {}, pollMs: 9e8 }));
    ok(ipBans.registry["aaa111"] && ipBans.registry["aaa111"].name === "NCR_Ranger", "login line -> id + name learned");
    ok(ipBans.getIPsForPlayer("NCR_Ranger").includes("203.0.113.7"), "resolve by NAME -> IPs (incl. tentative)");
    ok(ipBans.getConfirmedIPsForPlayer("NCR_Ranger").includes("198.51.100.9"), "getConfirmedIPsForPlayer returns the confirmed IP");
    ok(!ipBans.getConfirmedIPsForPlayer("NCR_Ranger").includes("203.0.113.7"), "confirmed lookup excludes tentative IPs");
    const rec = ipBans.getRecord("NCR_Ranger");
    ok(rec && rec.name === "NCR_Ranger" && rec.cips.includes("198.51.100.9") && rec.lastSeen, "getRecord returns name + confirmed IPs + lastSeen");
    ok(rec.logins === 1, "getRecord counts one completed connection (login count)");
    clearInterval(ipBans.init({ logFiles: [logPath], onAutoBan: async () => {}, pollMs: 9e8 }));   // simulate a restart re-reading the same log
    ok(ipBans.getRecord("NCR_Ranger").logins === 1, "login count does NOT inflate when the log is re-read on restart");
    ok(Array.isArray(rec.recent) && rec.recent[0] && rec.recent[0].ip === "198.51.100.9", "getRecord lists recent connections (newest first, with IP)");
    ok(rec.bypass === false && typeof rec.flagged === "boolean", "getRecord reports bypass + flag status");
    ok(ipBans.getIPsForPlayer("aaa111").includes("198.51.100.9"), "disconnect line -> same-line IP+id learned");
    ok(ipBans.getAltsOf("NCR_Ranger").includes("bbb222"), "alt sharing a CONFIRMED IP is detected");
    ok(ipBans.getAltsOf("NCR_Ranger").length === 1, "non-sharing ids are not flagged as alts");
    const enf = ipBans.blacklistPlayer("NCR_Ranger");
    ok(enf.ips.includes("198.51.100.9") && ipBans.blacklist.includes("198.51.100.9"), "blacklistPlayer flags the player's CONFIRMED IPs");
    ok(!enf.ips.includes("203.0.113.7") && !ipBans.blacklist.includes("203.0.113.7"), "tentative (join-correlated) IPs are NOT flagged");
    ok(enf.alts.includes("AltGuy"), "blacklist summary reports shared-IP alts by username");
    ok(!ipBans.getBlacklist().names.includes("ncr_ranger"), "blacklistPlayer does NOT auto-flag the username (no 'banned for no reason')");
    ipBans.unblacklistPlayer("NCR_Ranger");
    ok(!ipBans.blacklist.includes("198.51.100.9"), "unblacklistPlayer clears the flags");
    // explicit username blacklist still works, and can be cleared on its own
    ipBans.flagTarget("NCR_Ranger");
    ok(ipBans.getBlacklist().names.includes("ncr_ranger"), "flagTarget (explicit owner action) flags the username");
    ok(ipBans.clearFlaggedNames() >= 1 && ipBans.getBlacklist().names.length === 0, "clearFlaggedNames clears flagged usernames only");

    // live "Rcon: BanPlayer <name>" line (any admin tool) auto-flags the IPs
    const log2 = path.join(sandbox, "Pavlov2.log");
    fs.writeFileSync(log2, [
      "[2026.06.16-20.00.00:000][1]LogNet: NotifyAcceptedConnection: Name: NWB, TimeStamp: x, [UNetConnection] RemoteAddr: 9.9.9.9:1000, Name: IpConnection_9, Driver: GameNetDriver D, IsServer: YES, PC: NULL, Owner: NULL, UniqueId: INVALID",
      "[2026.06.16-20.00.00:200][2]LogNet: Login request: ?Name=Evader?platform=oculus?pid=Evader?name=Evader userId: NULL:ccc333 platform: NULL",
      "[2026.06.16-20.00.30:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 9.9.9.9:1000, Name: IpConnection_9, IsServer: YES, UniqueId: NULL:ccc333",   // confirms 9.9.9.9
    ].join("\n") + "\n");
    const timer = ipBans.init({ logFiles: [log2], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(log2, "[2026.06.16-20.01.00:000][3]LogTemp: Rcon: BanPlayer Evader\n");
    await new Promise(r => setTimeout(r, 150));
    clearInterval(timer);
    ok(ipBans.blacklist.includes("9.9.9.9"), "live 'Rcon: BanPlayer <name>' flags that player's IP");

    // LIVE cross-poll correlation: the accept line (IP) and the login line
    // (name+id) arrive in SEPARATE poll cycles, exactly like a real live join.
    // The per-file pending IP must survive between polls so the IP is captured.
    const log3 = path.join(sandbox, "Pavlov3.log");
    fs.writeFileSync(log3, "");
    const t3 = ipBans.init({ logFiles: [log3], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(log3, "[2026.06.20-11.04.45:721][488]LogNet: NotifyAcceptingConnection accepted from: 1.159.95.133:49090\n");
    await new Promise(r => setTimeout(r, 60));   // let a poll consume the accept line
    fs.appendFileSync(log3, "[2026.06.20-11.04.46:592][540]LogNet: Login request: ?Name=Arcusmay_vr?playerHeight=160.000000?rightHanded=1?vstock=1?platform=oculus?pid=Arcusmay_vr?name=Arcusmay_vr userId: NULL:000287f2eda04be68fb2f7a69b4facd9 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));   // next poll consumes the login line
    clearInterval(t3);
    ok(ipBans.getIPsForPlayer("Arcusmay_vr").includes("1.159.95.133"), "live join split across polls -> IP still captured");

    // LIVE auto-ban: an account that connects from a flagged IP fires onAutoBan
    const log4 = path.join(sandbox, "Pavlov4.log");
    fs.writeFileSync(log4, "");
    let autoBanned = null;
    const t4 = ipBans.init({ logFiles: [log4], onAutoBan: async (info) => { autoBanned = info; }, pollMs: 20 });
    // bad guy joins, then DISCONNECTS (confirms his IP), then gets banned, then an alt joins from the same IP
    fs.appendFileSync(log4,
      "[2026.06.21-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 4.4.4.4:5000\n" +
      "[2026.06.21-10.00.00:200][2]LogNet: Login request: ?Name=BadGuy?pid=BadGuy userId: NULL:dead01 platform: NULL\n" +
      "[2026.06.21-10.00.30:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 4.4.4.4:5000, Name: IpConnection_4, IsServer: YES, UniqueId: NULL:dead01\n" +
      "[2026.06.21-10.01.00:000][4]LogTemp: Rcon: BanPlayer BadGuy\n");
    await new Promise(r => setTimeout(r, 80));
    fs.appendFileSync(log4,
      "[2026.06.21-10.02.00:000][4]LogNet: NotifyAcceptingConnection accepted from: 4.4.4.4:6000\n" +
      "[2026.06.21-10.02.00:200][5]LogNet: Login request: ?Name=AltOfBad?pid=AltOfBad userId: NULL:beef02 platform: NULL\n");
    await new Promise(r => setTimeout(r, 80));
    clearInterval(t4);
    ok(ipBans.blacklist.includes("4.4.4.4"), "ban flags the player's IP (live)");
    ok(autoBanned && autoBanned.name === "AltOfBad" && autoBanned.ip === "4.4.4.4", "alt connecting from a flagged IP triggers onAutoBan");

    // returning evader: a known id whose CONFIRMED IP is flagged is banned at LOGIN,
    // even with no accept line / ambiguous correlation (the previous build only
    // caught these at disconnect, so flagged-IP players seemed to slip through).
    const logR = path.join(sandbox, "PavlovR.log");
    fs.writeFileSync(logR, "");
    let autoRet = null;
    const tR = ipBans.init({ logFiles: [logR], onAutoBan: async (i) => { autoRet = i; }, pollMs: 20 });
    // first session: connect + disconnect to record a CONFIRMED IP (5.5.5.5)
    fs.appendFileSync(logR,
      "[2026.06.21-11.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 5.5.5.5:5000\n" +
      "[2026.06.21-11.00.00:200][2]LogNet: Login request: ?Name=Returner?pid=Returner userId: NULL:ret001 platform: NULL\n" +
      "[2026.06.21-11.00.30:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 5.5.5.5:5000, Name: IpConnection_5, IsServer: YES, UniqueId: NULL:ret001\n");
    await new Promise(r => setTimeout(r, 60));
    ipBans.flagIp("5.5.5.5");                              // flag the IP (no one auto-banned yet)
    ok(autoRet === null, "flagging an IP alone does not retroactively fire onAutoBan");
    // they come back with ONLY a login line (no accept, not 'confident') -> banned anyway
    fs.appendFileSync(logR,
      "[2026.06.21-11.05.00:200][9]LogNet: Login request: ?Name=Returner?pid=Returner userId: NULL:ret001 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tR);
    ok(autoRet && autoRet.name === "Returner" && autoRet.reason === "blacklisted IP", "returning id with a flagged confirmed IP is banned at login (no accept line needed)");

    // log auto-discovery: probe a temp tree shaped like a real Pavlov install
    const root = path.join(sandbox, "discovery");
    const realLog = path.join(root, "steamuser", "pavlovserver", "Pavlov", "Saved", "Logs", "Pavlov.log");
    fs.mkdirSync(path.dirname(realLog), { recursive: true });
    fs.writeFileSync(realLog,
      "[2026.06.21-12.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 7.7.7.7:5000\n" +
      "[2026.06.21-12.00.00:200][2]LogNet: Login request: ?Name=Found?pid=Found userId: NULL:f00d03 platform: NULL\n");
    const discovered = ipBans.discoverLogs([root, path.join(root, "steamuser")]);
    ok(discovered.includes(realLog), "discoverLogs finds Pavlov/Saved/Logs/Pavlov.log under a root");

    // untracked (ignore-list) usernames are never recorded
    ipBans.addUntracked("GhostName");
    const log5 = path.join(sandbox, "Pavlov5.log");
    fs.writeFileSync(log5, "");
    const t5 = ipBans.init({ logFiles: [log5], onAutoBan: async () => {}, onConnect: async () => {}, pollMs: 20 });
    fs.appendFileSync(log5,
      "[2026.06.22-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 8.8.8.8:5000\n" +
      "[2026.06.22-10.00.00:200][2]LogNet: Login request: ?Name=GhostName?pid=GhostName userId: NULL:ghost99 platform: NULL\n");
    await new Promise(r => setTimeout(r, 70));
    clearInterval(t5);
    ok(!ipBans.registry["ghost99"], "ignore-listed username is not tracked");
    ok(ipBans.getUntracked().includes("ghostname"), "getUntracked lists the ignored name (lowercased)");
    ipBans.removeUntracked("GhostName");
    ok(!ipBans.getUntracked().includes("ghostname"), "removeUntracked clears it");

    // malformed ids (trailing junk from quoted error lines) are sanitized to bare hex
    const log6 = path.join(sandbox, "Pavlov6.log");
    fs.writeFileSync(log6,
      "[2026.06.23-10.00.00:000][1]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 5.5.5.5:1000, UniqueId: NULL:abc123'\n");
    clearInterval(ipBans.init({ logFiles: [log6], pollMs: 9e8 }));
    ok(ipBans.registry["abc123"] && !ipBans.registry["abc123'"], "trailing quote stripped from unique id");

    // alt-maker: several accounts on ONE home IP -> linked as alts and flagged
    const log7b = path.join(sandbox, "Pavlov7b.log");
    fs.writeFileSync(log7b,
      "[2026.06.23-12.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 50.50.50.50:1\n" +
      "[2026.06.23-12.00.00:200][2]LogNet: Login request: ?Name=Solo1?pid=Solo1 userId: NULL:solo01 platform: NULL\n" +
      "[2026.06.23-12.00.30:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 50.50.50.50:1, UniqueId: NULL:solo01\n" +
      "[2026.06.23-12.05.00:000][4]LogNet: NotifyAcceptingConnection accepted from: 50.50.50.50:2\n" +
      "[2026.06.23-12.05.00:200][5]LogNet: Login request: ?Name=Solo2?pid=Solo2 userId: NULL:solo02 platform: NULL\n" +
      "[2026.06.23-12.05.30:000][6]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 50.50.50.50:2, UniqueId: NULL:solo02\n");
    clearInterval(ipBans.init({ logFiles: [log7b], pollMs: 9e8 }));
    ok(ipBans.getAltsOf("Solo1").includes("solo02"), "sequential same-IP accounts ARE linked as alts");
    const eSolo = ipBans.blacklistPlayer("Solo1");
    ok(eSolo.ips.includes("50.50.50.50"), "alt-maker's home IP IS flagged (not treated as shared)");
    const undo = ipBans.unblacklistPlayer("Solo1");
    ok(undo.cleared && undo.cleared.ips >= 1, "unblacklist clears the IP flag");
    ok(!ipBans.blacklist.includes("50.50.50.50"), "unblacklist removes the IP flag");

    // manual IP blacklist: flag an IP + report known accounts on it
    const fr = ipBans.flagIp("50.50.50.50");
    ok(fr.added && ipBans.blacklist.includes("50.50.50.50"), "flagIp adds the IP to the flag list");
    ok(fr.ids.includes("solo01") && fr.ids.includes("solo02"), "flagIp reports known accounts on that IP");
    ipBans.clearIp("50.50.50.50");

    // same account, NEW username, from a flagged IP -> still auto-banned (id/IP based, not name based)
    const logD = path.join(sandbox, "PavlovD.log");
    fs.writeFileSync(logD, "");
    let autoD = null;
    const tD = ipBans.init({ logFiles: [logD], onAutoBan: async (i) => { autoD = i; }, pollMs: 20 });
    fs.appendFileSync(logD,
      "[2026.06.28-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 6.6.6.6:1\n" +
      "[2026.06.28-10.00.00:200][2]LogNet: Login request: ?Name=OldName?pid=OldName userId: NULL:ev01 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    fs.appendFileSync(logD, "[2026.06.28-10.00.30:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 6.6.6.6:1, UniqueId: NULL:ev01\n");
    await new Promise(r => setTimeout(r, 60));
    fs.appendFileSync(logD, "[2026.06.28-10.01.00:000][4]LogTemp: Rcon: BanPlayer OldName\n");
    await new Promise(r => setTimeout(r, 60));
    // same id ev01 returns under a DIFFERENT name from the same IP
    fs.appendFileSync(logD,
      "[2026.06.28-10.02.00:000][5]LogNet: NotifyAcceptingConnection accepted from: 6.6.6.6:2\n" +
      "[2026.06.28-10.02.00:200][6]LogNet: Login request: ?Name=BrandNewName?pid=BrandNewName userId: NULL:ev01 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tD);
    ok(autoD && autoD.name === "BrandNewName", "renamed account from a flagged IP is still auto-banned (by name)");

    // username blacklist is now EXPLICIT-ONLY. A plain ban flags IPs, never the
    // username (auto-flagging names caused "blacklisted username" false-positives
    // on temp bans / kicks / reused names). Reusing a banned name is NOT auto-banned
    // unless an owner explicitly blacklists that username.
    const logE = path.join(sandbox, "PavlovE.log");
    const cheatId = "aaaa1111bbbb2222cccc3333dddd4444";   // real 32-hex id
    fs.writeFileSync(logE, "");
    let autoE = null;
    const tE = ipBans.init({ logFiles: [logE], onAutoBan: async (i) => { autoE = i; }, pollMs: 20 });
    fs.appendFileSync(logE,
      "[2026.06.29-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 11.11.11.11:1\n" +
      `[2026.06.29-10.00.00:200][2]LogNet: Login request: ?Name=Cheater?pid=Cheater userId: NULL:${cheatId} platform: NULL\n`);
    await new Promise(r => setTimeout(r, 60));
    fs.appendFileSync(logE, "[2026.06.29-10.01.00:000][3]LogTemp: Rcon: BanPlayer Cheater\n");
    await new Promise(r => setTimeout(r, 60));
    ok(!ipBans.getBlacklist().names.includes("cheater"), "a plain ban does NOT flag the username");
    // a brand new account (new id, new IP) reusing the SAME username -> NOT auto-banned
    fs.appendFileSync(logE,
      "[2026.06.29-10.02.00:000][4]LogNet: NotifyAcceptingConnection accepted from: 99.99.99.99:1\n" +
      "[2026.06.29-10.02.00:200][5]LogNet: Login request: ?Name=Cheater?pid=Cheater userId: NULL:9999888877776666555544443333eeee platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    ok(autoE === null, "reusing a banned username alone no longer triggers an auto-ban");
    // but the OWNER can still explicitly blacklist a username -> then it auto-bans
    ipBans.flagTarget("Cheater");
    fs.appendFileSync(logE,
      "[2026.06.29-10.03.00:000][6]LogNet: NotifyAcceptingConnection accepted from: 88.88.88.88:1\n" +
      "[2026.06.29-10.03.00:200][7]LogNet: Login request: ?Name=Cheater?pid=Cheater userId: NULL:77778888aaaabbbbccccddddeeeeffff platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tE);
    ok(autoE && autoE.name === "Cheater" && autoE.reason === "blacklisted username", "explicitly blacklisted username still auto-bans");

    // getBlacklist exposes IPs + usernames
    const bl = ipBans.getBlacklist();
    ok(Array.isArray(bl.ips) && Array.isArray(bl.names), "getBlacklist returns ips/names arrays");
    ok(bl.names.includes("cheater"), "getBlacklist includes the explicitly flagged username");

    // onConfirm fires with the CONFIRMED ip (from the disconnect line), not a tentative one
    const log8 = path.join(sandbox, "Pavlov8.log");
    fs.writeFileSync(log8, "");
    let confirmed = null;
    const t8 = ipBans.init({ logFiles: [log8], onConfirm: async (info) => { confirmed = info; }, pollMs: 20 });
    fs.appendFileSync(log8,
      "[2026.06.24-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 9.1.2.3:5000\n" +
      "[2026.06.24-10.00.00:200][2]LogNet: Login request: ?Name=Confirmee?pid=Confirmee userId: NULL:conf01 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    ok(confirmed === null, "no feed on the tentative join");
    fs.appendFileSync(log8,
      "[2026.06.24-10.05.00:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 9.1.2.3:5000, Name: IpConnection_1, IsServer: YES, UniqueId: NULL:conf01\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(t8);
    ok(confirmed && confirmed.ip === "9.1.2.3" && confirmed.name === "Confirmee", "onConfirm fires with confirmed IP + resolved name");

    // clearing logged data (stop false bans)
    const log9 = path.join(sandbox, "Pavlov9.log");
    fs.writeFileSync(log9,
      "[2026.06.25-10.00.00:000][1]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 3.3.3.3:1, UniqueId: NULL:clr01\n" +
      "[2026.06.25-10.00.05:000][2]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 3.3.3.3:2, UniqueId: NULL:clr02\n");
    clearInterval(ipBans.init({ logFiles: [log9], pollMs: 9e8 }));
    ipBans.blacklistPlayer("clr01");
    ok(ipBans.blacklist.includes("3.3.3.3"), "setup: 3.3.3.3 flagged");
    const ci = ipBans.clearIp("3.3.3.3");
    ok(!ipBans.blacklist.includes("3.3.3.3") && ci.players >= 1, "clearIp removes flag + record");
    ipBans.blacklistPlayer("clr02");           // re-flag via the other account's confirmed... (cleared, so none)
    ipBans.clearFlags();
    ok(ipBans.blacklist.length === 0, "clearFlags empties the flag list");
    ipBans.clearAll();
    ok(!ipBans.registry["clr01"] && !ipBans.registry["clr02"], "clearAll wipes the registry");

    // ban a never-disconnected account: the kick's confirmed IP gets flagged so alts are still caught
    const logB = path.join(sandbox, "PavlovB.log");
    fs.writeFileSync(logB, "");
    const tB = ipBans.init({ logFiles: [logB], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(logB,
      "[2026.06.26-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 5.6.7.8:5000\n" +
      "[2026.06.26-10.00.00:200][2]LogNet: Login request: ?Name=FreshBan?pid=FreshBan userId: NULL:fresh01 platform: NULL\n" +
      "[2026.06.26-10.01.00:000][3]LogTemp: Rcon: BanPlayer FreshBan\n");   // no confirmed IP yet -> nothing flagged
    await new Promise(r => setTimeout(r, 80));
    ok(!ipBans.blacklist.includes("5.6.7.8"), "no confirmed IP at ban time -> not flagged yet");
    fs.appendFileSync(logB,
      "[2026.06.26-10.01.05:000][4]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 5.6.7.8:5000, Name: IpConnection_1, IsServer: YES, UniqueId: NULL:fresh01\n");
    await new Promise(r => setTimeout(r, 80));
    clearInterval(tB);
    ok(ipBans.blacklist.includes("5.6.7.8"), "kick-confirmed IP of a recently-banned account is flagged");

    // ambiguous concurrent join is NOT instantly banned (could be mis-attributed),
    // but is caught at disconnect with the confirmed IP
    const logC = path.join(sandbox, "PavlovC.log");
    fs.writeFileSync(logC, "");
    let autoC = null;
    const tC = ipBans.init({ logFiles: [logC], onAutoBan: async (info) => { autoC = info; }, pollMs: 20 });
    // main plays and leaves (own session) — confirms 7.7.7.7
    fs.appendFileSync(logC,
      "[2026.06.27-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 7.7.7.7:1\n" +
      "[2026.06.27-10.00.00:200][2]LogNet: Login request: ?Name=Main7?pid=Main7 userId: NULL:aa01 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    fs.appendFileSync(logC, "[2026.06.27-10.00.30:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 7.7.7.7:1, UniqueId: NULL:aa01\n");
    await new Promise(r => setTimeout(r, 60));
    // admin bans the (now offline) main -> flags confirmed 7.7.7.7
    fs.appendFileSync(logC, "[2026.06.27-10.01.00:000][4]LogTemp: Rcon: BanPlayer Main7\n");
    await new Promise(r => setTimeout(r, 60));
    ok(ipBans.blacklist.includes("7.7.7.7") && autoC === null, "setup: 7.7.7.7 flagged, nothing auto-banned yet");
    // two connections pending at once -> ambiguous -> no instant auto-ban for the alt
    fs.appendFileSync(logC,
      "[2026.06.27-10.02.00:000][5]LogNet: NotifyAcceptingConnection accepted from: 8.8.8.9:2\n" +
      "[2026.06.27-10.02.00:100][6]LogNet: NotifyAcceptingConnection accepted from: 7.7.7.7:3\n" +
      "[2026.06.27-10.02.00:200][7]LogNet: Login request: ?Name=AltSeven?pid=AltSeven userId: NULL:aa02 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    ok(autoC === null, "ambiguous concurrent join is NOT instantly auto-banned");
    // alt disconnects -> confirmed IP 7.7.7.7 -> retroactive auto-ban
    fs.appendFileSync(logC, "[2026.06.27-10.05.00:000][8]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 7.7.7.7:3, UniqueId: NULL:aa02\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tC);
    ok(autoC && autoC.name === "AltSeven" && autoC.ip === "7.7.7.7", "slipped alt is caught at disconnect via confirmed IP");
  }

  console.log("Faction rank caps:");
  ok(bot.getFactionRankCap("NCR", "Officer") === null, "unset rank cap -> null (unlimited)");
  await bot.setFactionRankCap("NCR", "Officer", 2);
  ok(bot.getFactionRankCap("NCR", "Officer") === 2, "set cap -> 2");
  ok(JSON.stringify(bot.getFactionRankCaps("NCR")) === '{"Officer":2}', "getFactionRankCaps reflects config");
  await bot.setFactionRankCap("NCR", "Officer", 0);
  ok(bot.getFactionRankCap("NCR", "Officer") === null, "cap 0 clears -> unlimited");
  ok(JSON.stringify(bot.getFactionRankCaps("NCR")) === "{}", "cleared cap removed from config");

  console.log("sanitizeId — strips autocomplete labels:");
  ok(bot.sanitizeId("CoolKid2013 (manual entry)") === "CoolKid2013", "strips (manual entry)");
  ok(bot.sanitizeId("gfhyg [S1]") === "gfhyg", "strips [S1]");
  ok(bot.sanitizeId("Player [S1+S2]") === "Player", "strips [S1+S2]");
  ok(bot.sanitizeId("ncr_private (offline)") === "ncr_private", "strips (offline)");
  ok(bot.sanitizeId("00027f60483f4422b9f00d5c75dcd481") === "00027f60483f4422b9f00d5c75dcd481", "normal id unchanged");

  console.log("UI / parsing helpers:");
  ok(JSON.stringify(bot.splitPages([1,2,3,4,5], 2)) === "[[1,2],[3,4],[5]]", "splitPages chunks");
  ok(JSON.stringify(bot.splitPages([], 5)) === "[[]]", "splitPages empty -> one page");
  const data = { PlayerList: [ { Username: " Al " }, { username: "Bo" }, { PlayerName: "Ca" }, { name: "Da" }, { Name: "Ev" }, {}, { Username: "  " } ] };
  ok(JSON.stringify(bot.extractPlayerNames(data)) === '["Al","Bo","Ca","Da","Ev"]', "extractPlayerNames variants/trim/blank");
  ok(JSON.stringify(bot.extractPlayerNames(null)) === "[]", "extractPlayerNames null-safe");
  ok(bot.bar(5, 10, 10) === "█████░░░░░", "bar renders half meter");
  ok(bot.bar(0, 0, 6) === "░░░░░░", "bar handles max=0 safely");
  ok(bot.bar(99, 10, 4) === "████", "bar clamps over-full to width");

  console.log("Serialized temp-ban writes:");
  await bot.upsertTempBan({ playerId: "Banned1", reason: "x", expires: Date.now() + 1e6, durationLabel: "1d", moderator: "m", server: "both" });
  await bot.upsertTempBan({ playerId: "banned1", reason: "y", expires: Date.now() + 1e6, durationLabel: "2d", moderator: "m", server: "both" });
  ok(bot.loadBans().filter(b => b.playerId.toLowerCase() === "banned1").length === 1, "upsert dedupes case-insensitively (no duplicate)");
  ok(bot.loadBans().find(b => b.playerId.toLowerCase() === "banned1").reason === "y", "upsert replaces the existing entry");
  await bot.upsertTempBan({ playerId: "Banned2", reason: "z", expires: Date.now() + 1e6, durationLabel: "1d", moderator: "m", server: "both" });
  await bot.removeBans("BANNED1");
  ok(!bot.loadBans().some(b => b.playerId.toLowerCase() === "banned1"), "removeBans removes case-insensitively");
  ok(bot.loadBans().some(b => b.playerId === "Banned2"), "removeBans leaves other bans intact");

  console.log("Punishment DM status field:");
  ok(bot.dmStatusField(null, null) === null, "no linked account -> no field");
  ok(bot.dmStatusField(true, { id: "42" }).value.includes("delivered"), "delivered field mentions success");
  ok(bot.dmStatusField(false, { id: "42" }).value.includes("Couldn't DM"), "failed field mentions failure");

  console.log(`\n${pass} passed, ${fail} failed`);
  try { fs.rmSync(sandbox, { recursive: true, force: true }); } catch {}
  process.exit(fail ? 1 : 0);
})().catch(e => { console.error("TEST CRASHED:", e); process.exit(1); });
