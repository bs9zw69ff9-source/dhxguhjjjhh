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
  ok(ncr[0].name.includes("(offline)"), "offline players are labelled");
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

  console.log("Auto-ban patterns:");
  let ab = await bot.addAutobanPattern("NCR", "tester");
  ok(ab.added && ab.pattern === "ncr", "pattern normalized to lowercase + added");
  ok((await bot.addAutobanPattern("ncr", "tester")).added === false, "duplicate pattern not re-added");
  ok(bot.matchesAutoban("xX_NCR_trooper") === "ncr", "matchesAutoban: substring (any position), case-insensitive");
  ok(bot.matchesAutoban("legion_guy") === null, "matchesAutoban: non-match -> null");
  ok((await bot.removeAutobanPattern("NCR")).removed === true, "remove pattern (case-insensitive)");
  ok(bot.matchesAutoban("xX_NCR_trooper") === null, "no patterns left -> no match");

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
      "[2026.06.16-19.09.00:000][4]LogNet: UChannel::Close: Sending CloseBunch. ChIndex == 0. Name: [UChannel] ChIndex: 0, Closing: 0 [UNetConnection] RemoteAddr: 198.51.100.9:40000, Name: IpConnection_2147419312, Driver: GameNetDriver IpNetDriver_2147482354, IsServer: YES, PC: BP_PavlovPlayerController_C_2147419242, Owner: BP_PavlovPlayerController_C_2147419242, UniqueId: NULL:aaa111",
    ].join("\n") + "\n");   // real logs end with a newline; the parser buffers an incomplete trailing line
    const ipBans = require(path.join(sandbox, "ipBans.js"));
    clearInterval(ipBans.init({ logFiles: [logPath], onAutoBan: async () => {}, pollMs: 9e8 }));
    ok(ipBans.registry["aaa111"] && ipBans.registry["aaa111"].name === "NCR_Ranger", "login line -> id + name learned");
    ok(ipBans.getIPsForPlayer("NCR_Ranger").includes("203.0.113.7"), "resolve by NAME -> IPs");
    ok(ipBans.getIPsForPlayer("aaa111").includes("198.51.100.9"), "disconnect line -> same-line IP+id learned");
    ok(ipBans.getAltsOf("NCR_Ranger").includes("bbb222"), "alt sharing an IP is detected");
    ok(ipBans.getAltsOf("NCR_Ranger").length === 1, "non-sharing ids are not flagged as alts");
    const enf = ipBans.blacklistPlayer("NCR_Ranger");
    ok(enf.ips.includes("203.0.113.7") && ipBans.blacklist.includes("203.0.113.7"), "blacklistPlayer flags the player's IPs");
    ok(enf.alts.includes("bbb222"), "blacklist summary reports shared-IP alts");
    ipBans.unblacklistPlayer("NCR_Ranger");
    ok(!ipBans.blacklist.includes("203.0.113.7"), "unblacklistPlayer clears the flags");

    // live "Rcon: BanPlayer <name>" line (any admin tool) auto-flags the IPs
    const log2 = path.join(sandbox, "Pavlov2.log");
    fs.writeFileSync(log2, [
      "[2026.06.16-20.00.00:000][1]LogNet: NotifyAcceptedConnection: Name: NWB, TimeStamp: x, [UNetConnection] RemoteAddr: 9.9.9.9:1000, Name: IpConnection_9, Driver: GameNetDriver D, IsServer: YES, PC: NULL, Owner: NULL, UniqueId: INVALID",
      "[2026.06.16-20.00.00:200][2]LogNet: Login request: ?Name=Evader?platform=oculus?pid=Evader?name=Evader userId: NULL:ccc333 platform: NULL",
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
    // first the bad guy joins (learn IP), then gets banned, then an alt joins from the same IP
    fs.appendFileSync(log4,
      "[2026.06.21-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 4.4.4.4:5000\n" +
      "[2026.06.21-10.00.00:200][2]LogNet: Login request: ?Name=BadGuy?pid=BadGuy userId: NULL:dead01 platform: NULL\n" +
      "[2026.06.21-10.01.00:000][3]LogTemp: Rcon: BanPlayer BadGuy\n");
    await new Promise(r => setTimeout(r, 80));
    fs.appendFileSync(log4,
      "[2026.06.21-10.02.00:000][4]LogNet: NotifyAcceptingConnection accepted from: 4.4.4.4:6000\n" +
      "[2026.06.21-10.02.00:200][5]LogNet: Login request: ?Name=AltOfBad?pid=AltOfBad userId: NULL:beef02 platform: NULL\n");
    await new Promise(r => setTimeout(r, 80));
    clearInterval(t4);
    ok(ipBans.blacklist.includes("4.4.4.4"), "ban flags the player's IP (live)");
    ok(autoBanned && autoBanned.uniqueId === "beef02" && autoBanned.ip === "4.4.4.4", "alt connecting from a flagged IP triggers onAutoBan");

    // log auto-discovery: probe a temp tree shaped like a real Pavlov install
    const root = path.join(sandbox, "discovery");
    const realLog = path.join(root, "steamuser", "pavlovserver", "Pavlov", "Saved", "Logs", "Pavlov.log");
    fs.mkdirSync(path.dirname(realLog), { recursive: true });
    fs.writeFileSync(realLog,
      "[2026.06.21-12.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 7.7.7.7:5000\n" +
      "[2026.06.21-12.00.00:200][2]LogNet: Login request: ?Name=Found?pid=Found userId: NULL:f00d03 platform: NULL\n");
    const discovered = ipBans.discoverLogs([root, path.join(root, "steamuser")]);
    ok(discovered.includes(realLog), "discoverLogs finds Pavlov/Saved/Logs/Pavlov.log under a root");
  }

  console.log("Faction rank caps:");
  ok(bot.getFactionRankCap("NCR", "Officer") === null, "unset rank cap -> null (unlimited)");
  await bot.setFactionRankCap("NCR", "Officer", 2);
  ok(bot.getFactionRankCap("NCR", "Officer") === 2, "set cap -> 2");
  ok(JSON.stringify(bot.getFactionRankCaps("NCR")) === '{"Officer":2}', "getFactionRankCaps reflects config");
  await bot.setFactionRankCap("NCR", "Officer", 0);
  ok(bot.getFactionRankCap("NCR", "Officer") === null, "cap 0 clears -> unlimited");
  ok(JSON.stringify(bot.getFactionRankCaps("NCR")) === "{}", "cleared cap removed from config");

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
