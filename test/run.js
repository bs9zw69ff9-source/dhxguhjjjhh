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
module.exports={Client:Chain,GatewayIntentBits:e,PermissionFlagsBits:e,MessageFlags:e,ActivityType:e,REST:Chain,Routes:e,SlashCommandBuilder:Chain,EmbedBuilder:Chain,ActionRowBuilder:Chain,ButtonBuilder:Chain,ButtonStyle:e,ComponentType:e,disableValidators:()=>{}};
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

// Point the faction tree at a sandbox install so the destruction-guard tests
// write to real (throwaway) files instead of /home/steam/pavlovserver.
const pavBase = path.join(sandbox, "pavlovserver");
fs.mkdirSync(path.join(pavBase, "Pavlov/Saved/Config/ModSave/FactionRoles"), { recursive: true });
process.env.PAVLOV_BASE_1 = pavBase;
process.env.PAVLOV_BASES  = pavBase;

const bot = require(path.join(sandbox, "index.js"));

let pass = 0, fail = 0;
const ok = (cond, msg) => {
  try { assert.ok(cond, msg); console.log("  ✓ " + msg); pass++; }
  catch { console.error("  ✗ " + msg); fail++; }
};

(async () => {
  console.log("Faction file destruction guard:");
  {
    const roster = ["a","b","c","d","e","f","g","h","i","j"];
    fs.writeFileSync(path.join(bot.FACTION_ROLES_PATH, "ncrspawn.txt"), roster.join("\n") + "\n");
    ok(bot.writeFactionFile("ncrspawn.txt", [...roster, "k"]) === true, "single add (no drops) allowed");
    ok(bot.writeFactionFile("ncrspawn.txt", roster) === true, "single remove allowed");
    ok(bot.writeFactionFile("ncrspawn.txt", ["a"]) === false, "mass deletion refused");
    ok(bot.readFactionFile("ncrspawn.txt").length === 10, "roster left intact after refused write");
    ok(bot.writeFactionFile("ncrspawn.txt", ["a"], { allowBulk: true }) === true, "allowBulk overrides the guard");
    ok(bot.readFactionFile("ncrspawn.txt").length === 1, "bulk write applied");
    ok(bot.writeFactionFile("ncrspawn.txt", "not-an-array") === false, "non-array payload rejected");
  }

  console.log("ModSave full sync:");
  {
    const sub = path.join("Pavlov", "Saved", "Config", "ModSave");
    const ia = path.join(sandbox, "msync_a"), ib = path.join(sandbox, "msync_b");
    fs.mkdirSync(path.join(ia, sub, "stats"), { recursive: true });
    fs.mkdirSync(path.join(ib, sub), { recursive: true });
    // a new file present only in A propagates to B
    fs.writeFileSync(path.join(ia, sub, "stats", "p1.txt"), "100");
    bot.syncAllModSave([ia, ib]);
    ok(fs.existsSync(path.join(ib, sub, "stats", "p1.txt")), "new ModSave file propagates to the other install");
    ok(fs.readFileSync(path.join(ib, sub, "stats", "p1.txt"), "utf8") === "100", "propagated content matches");
    // newest-wins: same file in both, B is newer -> A gets B's content
    const fa = path.join(ia, sub, "econ.txt"), fb = path.join(ib, sub, "econ.txt");
    fs.writeFileSync(fa, "old"); fs.utimesSync(fa, new Date(1_000_000), new Date(1_000_000));
    fs.writeFileSync(fb, "new"); // mtime ~now, newer
    bot.syncAllModSave([ia, ib]);
    ok(fs.readFileSync(fa, "utf8") === "new", "newest version wins across installs");
    // single install -> no-op (no throw)
    ok(bot.syncAllModSave([ia]).synced === 0, "single install is a no-op");
    // RCON+ menu-access files are NEVER synced (would wipe menu access)
    fs.writeFileSync(path.join(ia, sub, "MenuAccess.cfg"), "player1\nplayer2");
    bot.syncAllModSave([ia, ib]);
    ok(!fs.existsSync(path.join(ib, sub, "MenuAccess.cfg")), "MenuAccess.cfg is never mirrored across installs");
    fs.writeFileSync(path.join(ia, sub, "AccessManager.cfg"), "player1");
    bot.syncAllModSave([ia, ib]);
    ok(!fs.existsSync(path.join(ib, sub, "AccessManager.cfg")), "AccessManager.cfg is never mirrored across installs");
    // MONEY GUARD: a fresh 0-cap file with a newer mtime must NOT wipe a real balance
    const balA = path.join(ia, sub, "player9.txt"), balB = path.join(ib, sub, "player9.txt");
    fs.writeFileSync(balA, "1000"); fs.utimesSync(balA, new Date(1_000_000), new Date(1_000_000)); // older, real balance
    fs.writeFileSync(balB, "0");    // newer, fresh 0 (would be "newest" and clobber A)
    bot.syncAllModSave([ia, ib]);
    ok(fs.readFileSync(balA, "utf8") === "1000", "sync refuses to overwrite a positive balance with 0");
    // but a real (non-zero) newer balance still propagates (legit spend/earn)
    const spA = path.join(ia, sub, "player10.txt"), spB = path.join(ib, sub, "player10.txt");
    fs.writeFileSync(spA, "1000"); fs.utimesSync(spA, new Date(1_000_000), new Date(1_000_000));
    fs.writeFileSync(spB, "250");  // newer, real value
    bot.syncAllModSave([ia, ib]);
    ok(fs.readFileSync(spA, "utf8") === "250", "a newer non-zero balance still syncs normally");
  }

  console.log("Last seen:");
  await bot.recordLastSeen(["Alice", "Bob"], 1000);
  ok(bot.getLastSeen("ALICE") === 1000, "case-insensitive lookup");
  ok(bot.getLastSeen("nobody") === null, "unknown -> null");
  await bot.recordLastSeen(["Alice"], 2000);
  ok(bot.getLastSeen("alice") === 2000, "later sighting overwrites");

  console.log("Known-player registry:");
  await bot.recordKnownPlayers(["NCR_Private", "NCR_Sergeant", "Legion_Recruit"], 1000);
  await bot.recordKnownPlayers(["NCR_Private"], 5000); // already known -> updates lastSeen, no dupe
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
  await bot.seedKnownPlayers();
  ok(bot.getKnownPlayerChoices("seeded").some(c => c.value === "Seeded_Courier"), "seedKnownPlayers backfills from playtime");

  console.log("RCON menu roles:");
  await bot.setMenuRole("staff", "staffRole");
  ok(bot.loadMenuRoles().staff === "staffRole", "setMenuRole stores the Staff role");
  await bot.setMenuRole("highstaff", "hsRole");
  ok(bot.loadMenuRoles().highstaff === "hsRole", "setMenuRole stores the High Staff role");
  await bot.setMenuRole("staff", null);
  ok(bot.loadMenuRoles().staff && bot.loadMenuRoles().staff !== "staffRole", "clearing a menu role falls back to the default");

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
  ok(bot.isMasterName("LxPXHam") && bot.isMasterName("holosight1"), "master names recognised");
  ok(bot.isMasterName("  lxpxham  ") && !bot.isMasterName("SomeoneElse"), "master match is trimmed + case-insensitive; others are not master");

  console.log("Protected players (flush + auto-ban immune):");
  bot.safeWrite(bot.FILES.MENU_GRANTS, {   // cache-aware write (a bare fs write would be invisible to the primed read cache)
    "staff_guy":   [{ server: "both", menuValue: "staff",     menuId: "x" }],
    "boss_guy":    [{ server: "both", menuValue: "highstaff", menuId: "x" }],
    "faction_guy": [{ server: "both", menuValue: "faction",   menuId: "x" }],
  });
  ok(bot.isProtectedPlayer("Staff_Guy"), "staff menu holder is flush-immune (case-insensitive)");
  ok(bot.isProtectedPlayer("boss_guy"), "high staff menu holder is flush-immune");
  ok(!bot.isProtectedPlayer("faction_guy"), "faction menu holder is NOT flush-immune");
  ok(bot.isProtectedPlayer("LxPXHam"), "master name is flush-immune");
  bot.addDonator("Whale_Dan");
  ok(bot.isProtectedPlayer("whale_dan"), "donator.txt entry is flush-immune");
  bot.removeDonator("Whale_Dan");
  ok(!bot.isProtectedPlayer("whale_dan"), "removed donator loses flush immunity");
  ok(!bot.isProtectedPlayer("Rando"), "regular player is not immune");

  console.log("Auto-rotate time parsing:");
  ok(bot.parseClockTime("18:30") === "18:30", "24h HH:MM parses");
  ok(bot.parseClockTime("3pm") === "15:00", "12h pm parses");
  ok(bot.parseClockTime("6:30pm") === "18:30", "12h with minutes parses");
  ok(bot.parseClockTime("3am") === "03:00", "12h am parses + zero-pads");
  ok(bot.parseClockTime("12am") === "00:00", "12am -> midnight");
  ok(bot.parseClockTime("12pm") === "12:00", "12pm -> noon");
  ok(bot.parseClockTime("0:00") === "00:00", "midnight 24h");
  ok(bot.parseClockTime("9") === "09:00", "bare hour parses");
  ok(bot.parseClockTime("25:00") === null, "hour out of range -> null");
  ok(bot.parseClockTime("10:99") === null, "minute out of range -> null");
  ok(bot.parseClockTime("banana") === null, "garbage -> null");
  { const c = bot.easternClock(new Date("2026-07-08T16:30:00Z")); ok(/^\d{2}:\d{2}$/.test(c.hm) && /^\d{4}-\d{2}-\d{2}$/.test(c.date), "easternClock returns HH:MM + YYYY-MM-DD"); }
  ok(bot.easternClock(new Date("2026-01-15T17:00:00Z")).hm === "12:00", "easternClock: 17:00 UTC in Jan = 12:00 EST");
  ok(bot.easternClock(new Date("2026-07-15T16:00:00Z")).hm === "12:00", "easternClock: 16:00 UTC in Jul = 12:00 EDT (DST-aware)");

  console.log("Mute duration + store:");
  ok(bot.parseDuration("30s") === 30_000, "30s -> ms");
  ok(bot.parseDuration("10m") === 600_000, "10m -> ms");
  ok(bot.parseDuration("2h") === 7_200_000, "2h -> ms");
  ok(bot.parseDuration("1d") === 86_400_000, "1d -> ms");
  ok(bot.parseDuration("5") === 300_000, "bare number -> minutes");
  ok(bot.parseDuration("0m") === null && bot.parseDuration("abc") === null && bot.parseDuration("") === null, "invalid durations -> null");
  await bot.setMute("MutedGuy", { name: "MutedGuy", expires: Date.now() + 60_000, moderator: "m", at: Date.now() });
  ok(bot.getMute("mutedguy") && bot.getMute("MUTEDGUY").name === "MutedGuy", "mute stored + fetched case-insensitively");
  await bot.clearMute("MUTEDguy");
  ok(bot.getMute("MutedGuy") === null, "clearMute removes it (case-insensitive)");

  console.log("IP-Guard escalation decisions:");
  {
    const now = 1_000_000_000;
    const active  = { playerId: "Teno", permanent: false, expires: now + 60_000 };
    const expired = { playerId: "Teno", permanent: false, expires: now - 60_000 };
    ok(bot.autoBanDecision(active, "blacklisted IP", now) === "block", "unexpired temp ban -> blocked, no escalation");
    ok(bot.autoBanDecision(expired, "blacklisted IP", now) === "lift", "EXPIRED temp ban + IP flag -> lifted, never escalated to permanent");
    ok(bot.autoBanDecision(null, "blacklisted IP", now) === "ban", "no covering ban (evasion alt) -> auto-ban proceeds");
    ok(bot.autoBanDecision({ playerId: "X", permanent: true }, "blacklisted IP", now) === "ban", "permanent entry -> auto-ban path unchanged");
    ok(bot.autoBanDecision(expired, "blacklisted username", now) === "ban", "expired temp + manual USERNAME flag -> still bans (manual flags are deliberate)");
    ok(bot.autoBanDecision(expired, "blacklisted account (EOS id)", now) === "lift", "EXPIRED temp ban + EOS flag -> lifted (temp bans now flag EOS; served ban must not perma-escalate)");
    ok(bot.autoBanDecision(active, "blacklisted account (EOS id)", now) === "block", "active temp ban + same-account rejoin -> blocked");
  }

  console.log("Auto-ban record shows the real punishment:");
  ok(bot.isRealBan({ reason: "Cheating", moderator: "ModX" }), "a normal ban is a 'real' source ban");
  ok(!bot.isRealBan({ reason: "Auto-ban — blacklisted account (EOS id)", moderator: "IP-Guard" }), "a legacy IP-Guard auto-ban is NOT a source");
  ok(!bot.isRealBan({ reason: "Ban evasion", moderator: "Ban evasion (auto)" }), "a 'Ban evasion' auto-ban is NOT a source");
  await bot.upsertPermBan({ playerId: "OrigCheater", reason: "Hard R", moderator: "Chan-Chan" });
  ok(bot.sourceBanFor("OrigCheater")?.reason === "Hard R", "sourceBanFor finds the original punishment by name");
  ok(bot.sourceBanFor("SomeoneUnrelated") === null, "sourceBanFor returns null when no real ban matches");
  await bot.removeBans("OrigCheater");

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
    ok(!enf.ips.includes("203.0.113.7") && !ipBans.blacklist.includes("203.0.113.7"), "join-correlated (tentative) IPs are NOT flagged — a mis-correlation would ban a stranger");
    ok(enf.alts.includes("AltGuy"), "blacklist summary reports shared-IP alts by username (alt detection keys on CONFIRMED IPs)");
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
      "[2026.06.26-10.01.00:000][3]LogTemp: Rcon: BanPlayer FreshBan\n");   // no confirmed IP yet -> pendingFlag armed, nothing flagged yet
    await new Promise(r => setTimeout(r, 80));
    ok(!ipBans.blacklist.includes("5.6.7.8"), "no confirmed IP at ban time -> tentative IP is NOT flagged yet (avoids banning a mis-correlated stranger)");
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

    // K/D from KillData log blocks (multi-line: Killer / Killed / KilledBy)
    const logKD = path.join(sandbox, "PavlovKD.log");
    fs.writeFileSync(logKD, "");
    const tKD = ipBans.init({ logFiles: [logKD], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(logKD,
      '[2026.07.01-10.00.00:000][1]LogStats: "Killer": "Alpha",\n' +
      '"KillerTeamID": 0,\n"Killed": "Bravo",\n"KilledTeamID": 1,\n"KilledBy": "AK",\n' +
      '"Killer": "Bravo",\n"Killed": "Bravo",\n"KilledBy": "None",\n');   // suicide
    await new Promise(r => setTimeout(r, 80));
    clearInterval(tKD);
    ok(ipBans.getKD("Alpha").kills === 1 && ipBans.getKD("Alpha").deaths === 0, "kill credited to the killer");
    ok(ipBans.getKD("Bravo").deaths === 2 && ipBans.getKD("Bravo").kills === 0, "deaths credited; suicide counts as a death only (no kill)");
    // single-line JSON KillData record (all fields on one line) must also register
    const logKD2 = path.join(sandbox, "PavlovKD2.log");
    fs.writeFileSync(logKD2, "");
    const tKD2 = ipBans.init({ logFiles: [logKD2], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(logKD2,
      '[2026.07.01-11.00.00:000][1]LogStats: KillData: {"Killer": "Solo", "KillerTeamID": 0, "Killed": "Target", "KilledBy": "Sniper"}\n');
    await new Promise(r => setTimeout(r, 80));
    clearInterval(tKD2);
    ok(ipBans.getKD("Solo").kills === 1, "single-line JSON KillData credits the kill");
    ok(ipBans.getKD("Target").deaths === 1, "single-line JSON KillData credits the death");

    // Per-victim kill matrix (drives /stats faction kill counts)
    const logKM = path.join(sandbox, "PavlovKM.log");
    fs.writeFileSync(logKM, "");
    const tKM = ipBans.init({ logFiles: [logKM], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(logKM,
      '[2026.07.02-10.00.00:000][1]LogStats: KillData: {"Killer": "Hunter", "Killed": "NCR_Bob", "KilledBy": "AK"}\n' +
      '[2026.07.02-10.00.01:000][2]LogStats: KillData: {"Killer": "Hunter", "Killed": "NCR_Bob", "KilledBy": "AK"}\n' +
      '[2026.07.02-10.00.02:000][3]LogStats: KillData: {"Killer": "Hunter", "Killed": "Legion_Cae", "KilledBy": "Knife"}\n' +
      '[2026.07.02-10.00.03:000][4]LogStats: KillData: {"Killer": "Hunter", "Killed": "Hunter", "KilledBy": "None"}\n');   // suicide - not a kill
    await new Promise(r => setTimeout(r, 80));
    clearInterval(tKM);
    const hk = ipBans.getKills("Hunter");
    ok(hk.length === 2, "getKills lists each distinct victim once");
    ok(hk[0].name === "NCR_Bob" && hk[0].count === 2, "most-killed victim first, with the right tally");
    ok(hk.some(v => v.name === "Legion_Cae" && v.count === 1), "second victim tracked with its count");
    ok(!hk.some(v => v.name.toLowerCase() === "hunter"), "suicide is not counted as a kill on the victim matrix");
    ok(ipBans.getKills("NeverKilled").length === 0, "unknown killer -> empty list");

    // Master names are never IP-logged (init masters option)
    const logMaster = path.join(sandbox, "PavlovMaster.log");
    fs.writeFileSync(logMaster,
      "[2026.07.03-10.00.00:000][1]LogNet: Login request: ?Name=BossMan?platform=oculus?pid=BossMan?name=BossMan userId: NULL:master01 platform: NULL\n" +
      "[2026.07.03-10.05.00:000][2]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 9.9.9.9:40000, Name: IpConnection_1, IsServer: YES, UniqueId: NULL:master01\n");
    clearInterval(ipBans.init({ logFiles: [logMaster], masters: ["BossMan"], onAutoBan: async () => {}, pollMs: 9e8 }));
    ok(ipBans.getIPsForPlayer("BossMan").length === 0, "master name is never IP-logged");
    ok(!ipBans.registry["master01"], "master name's id is not recorded in the registry");

    // ...but a LIVE master join still fires onConnect (so the bot can hand them a menu)
    const logMaster2 = path.join(sandbox, "PavlovMaster2.log");
    fs.writeFileSync(logMaster2, "");
    let masterJoin = null;
    const tM = ipBans.init({ logFiles: [logMaster2], masters: ["BossMan"], onConnect: async (e) => { masterJoin = e; }, onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(logMaster2,
      "[2026.07.03-11.00.00:000][3]LogNet: Login request: ?Name=BossMan?platform=oculus?pid=BossMan?name=BossMan userId: NULL:master01 platform: NULL\n");
    await new Promise(r => setTimeout(r, 80));
    clearInterval(tM);
    ok(masterJoin && masterJoin.name === "BossMan", "live master join still fires onConnect (menu can be granted)");
    ok(masterJoin && masterJoin.ip === null, "master onConnect carries NO ip (never IP-logged)");
    ok(ipBans.getIPsForPlayer("BossMan").length === 0, "master join still records no IP after a live join");

    // Un-ignoring a player must resume tracking LIVE (not after a restart)
    const logUn = path.join(sandbox, "PavlovUn.log");
    fs.writeFileSync(logUn, "");
    const tUn = ipBans.init({ logFiles: [logUn], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(logUn,
      "[2026.07.04-10.00.00:000][1]LogNet: Login request: ?Name=Ghosty?platform=oculus?pid=Ghosty?name=Ghosty userId: NULL:ghost01 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    ipBans.addUntracked("Ghosty");                     // purge + ignore (id now in the untracked-id map)
    ipBans.removeUntracked("Ghosty");                  // un-ignore — must release the id too
    fs.appendFileSync(logUn,
      "[2026.07.04-10.05.00:000][2]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 8.8.4.4:40000, Name: IpConnection_1, IsServer: YES, UniqueId: NULL:ghost01\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tUn);
    ok(ipBans.getIPsForPlayer("ghost01").includes("8.8.4.4"), "un-ignored player is tracked again immediately (no restart needed)");

    // A manually flagged IP survives an unrelated player's unban
    ipBans.flagIp("77.77.77.77");
    // NCR_Ranger's record includes 198.51.100.9; give the manual IP to him too via registry? Not needed:
    // unblacklistPlayer clears by raw token as well — the manual flag must survive that path.
    ipBans.unblacklistPlayer("77.77.77.77");
    ok(ipBans.blacklist.includes("77.77.77.77"), "manual IP flag survives unblacklistPlayer (raw-token path)");
    ipBans.clearIp("77.77.77.77");
    ok(!ipBans.blacklist.includes("77.77.77.77"), "clearIp still removes a manual IP deliberately");

    // Wiping all flags must also drop pendingFlag so a ban-kick's disconnect can't re-flag
    ipBans.blacklistPlayer("NCR_Ranger");              // sets pendingFlag for aaa111
    ipBans.clearFlags();                               // admin wipes everything
    const logPf = path.join(sandbox, "PavlovPf.log");
    fs.writeFileSync(logPf, "");
    const tPf = ipBans.init({ logFiles: [logPf], onAutoBan: async () => {}, pollMs: 20 });
    fs.appendFileSync(logPf,
      "[2026.07.04-11.00.00:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 66.66.66.66:40000, Name: IpConnection_9, IsServer: YES, UniqueId: NULL:aaa111\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tPf);
    ok(!ipBans.blacklist.includes("66.66.66.66"), "clearFlags kills pendingFlag — post-wipe disconnect does not re-flag the IP");

    // /24 is GONE — only the exact IP matters. Two accounts on the same /24, different IPs.
    const logSub = path.join(sandbox, "PavlovSub.log");
    fs.writeFileSync(logSub,
      "[2026.07.05-10.00.00:000][1]LogNet: Login request: ?Name=Evader?pid=Evader userId: NULL:sub01 platform: NULL\n" +
      "[2026.07.05-10.05.00:000][2]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 42.42.42.10:40000, UniqueId: NULL:sub01\n" +
      "[2026.07.05-11.00.00:000][3]LogNet: Login request: ?Name=EvaderAlt?pid=EvaderAlt userId: NULL:sub02 platform: NULL\n" +
      "[2026.07.05-11.05.00:000][4]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 42.42.42.99:40000, UniqueId: NULL:sub02\n");
    clearInterval(ipBans.init({ logFiles: [logSub], onAutoBan: async () => {}, pollMs: 9e8 }));
    ok(ipBans.getRecord("Evader").subnetAlts === undefined, "no /24 subnet data on the record — subnet feature fully removed");
    ipBans.flagIp("42.42.42.10");                       // flag Evader's EXACT IP
    ok(ipBans.getRecord("EvaderAlt").flagged === false, "a same-/24 (different IP) account is NOT flagged — /24 is gone");
    // A LIVE join from a DIFFERENT IP in the same /24 is NOT auto-banned
    const logNet = path.join(sandbox, "PavlovSubnet.log");
    fs.writeFileSync(logNet, "");
    let subnetAuto = null;
    const tNet = ipBans.init({ logFiles: [logNet], onAutoBan: async (i) => { subnetAuto = i; }, pollMs: 20 });
    fs.appendFileSync(logNet,
      "[2026.07.05-13.00.00:000][7]LogNet: NotifyAcceptedConnection: RemoteAddr: 42.42.42.200:1, UniqueId: INVALID\n" +
      "[2026.07.05-13.00.00:200][8]LogNet: Login request: ?Name=FreshAlt?pid=FreshAlt userId: NULL:sub09 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tNet);
    ok(subnetAuto === null, "live join from a /24 neighbour is NOT auto-banned");
    // ...but the SAME exact flagged IP still auto-bans
    const logNet2 = path.join(sandbox, "PavlovSubnet2.log");
    fs.writeFileSync(logNet2, "");
    let exactAuto = null;
    const tNet2 = ipBans.init({ logFiles: [logNet2], onAutoBan: async (i) => { exactAuto = i; }, pollMs: 20 });
    fs.appendFileSync(logNet2,
      "[2026.07.05-14.00.00:000][9]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 42.42.42.10:1, UniqueId: NULL:sub02\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tNet2);
    ok(exactAuto && exactAuto.name === "EvaderAlt", "the EXACT flagged IP still auto-bans");
    ipBans.clearIp("42.42.42.10");

    // EOS flagging catches an evader who keeps their account but changes IP.
    const logEos = path.join(sandbox, "PavlovEosCatch.log");
    fs.writeFileSync(logEos,
      "[2026.07.06-10.00.00:000][1]LogNet: Login request: ?Name=Cheater?pid=Cheater userId: NULL:acct99 platform: NULL\n" +
      "[2026.07.06-10.05.00:000][2]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 30.30.30.30:40000, UniqueId: NULL:acct99\n");
    clearInterval(ipBans.init({ logFiles: [logEos], onAutoBan: async () => {}, pollMs: 9e8 }));
    ipBans.blacklistPlayer("Cheater", { flagId: true });   // a ban (temp or perm) now flags the EOS id
    ok(ipBans.getBlacklist().ids.includes("acct99"), "ban flags the account's EOS id so a name+IP change is still caught");
    // The evader returns with a NEW name and a NEW IP but the SAME account id -> caught
    const logEos2 = path.join(sandbox, "PavlovEosCatch2.log");
    fs.writeFileSync(logEos2, "");
    let eosAuto = null;
    const tEos = ipBans.init({ logFiles: [logEos2], onAutoBan: async (i) => { eosAuto = i; }, pollMs: 20 });
    fs.appendFileSync(logEos2,
      "[2026.07.06-11.00.00:000][3]LogNet: Login request: ?Name=TotallyNew?pid=TotallyNew userId: NULL:acct99 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tEos);
    ok(eosAuto && eosAuto.name === "TotallyNew" && /EOS id/.test(eosAuto.reason), "evader with a new name + new IP but the same EOS id is auto-banned on join");
    ipBans.unblacklistPlayer("Cheater");

    // EOS/account-id flagging: same id from a NEW IP + NEW name is still auto-banned
    const logEid = path.join(sandbox, "PavlovEID.log");
    fs.writeFileSync(logEid, "");
    let autoEid = null;
    const tEid = ipBans.init({ logFiles: [logEid], onAutoBan: async (i) => { autoEid = i; }, pollMs: 20 });
    fs.appendFileSync(logEid,
      "[2026.07.02-10.00.00:000][1]LogNet: NotifyAcceptingConnection accepted from: 20.20.20.20:1\n" +
      "[2026.07.02-10.00.00:200][2]LogNet: Login request: ?Name=Evader2?pid=Evader2 userId: NULL:eos01 platform: NULL\n" +
      "[2026.07.02-10.00.30:000][3]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 20.20.20.20:1, UniqueId: NULL:eos01\n");
    await new Promise(r => setTimeout(r, 60));
    ipBans.blacklistPlayer("Evader2");   // temp/default ban — must NOT flag the EOS id
    ok(!ipBans.getBlacklist().ids.includes("eos01"), "default ban does NOT flag the EOS id (temp/kick-safe)");
    ipBans.blacklistPlayer("Evader2", { flagId: true });   // permanent ban — flags the EOS id
    ok(ipBans.getBlacklist().ids.includes("eos01"), "permanent ban (flagId) flags the player's EOS/account id");
    fs.appendFileSync(logEid,
      "[2026.07.02-10.05.00:000][4]LogNet: NotifyAcceptingConnection accepted from: 30.30.30.30:9\n" +
      "[2026.07.02-10.05.00:200][5]LogNet: Login request: ?Name=NewAlias?pid=NewAlias userId: NULL:eos01 platform: NULL\n");
    await new Promise(r => setTimeout(r, 60));
    clearInterval(tEid);
    ok(autoEid && autoEid.name === "NewAlias", "same EOS id from a new IP/name is auto-banned");

    // REAL Pavlov login format has NO "?" terminator after the name ("?Name=Bob userId: …").
    // The name must stop at the next field, and names WITH spaces must survive intact.
    const logNm = path.join(sandbox, "PavlovNM.log");
    fs.writeFileSync(logNm,
      "[2026.07.02-11.00.00:000][1]LogNet: Login request: ?Name=Butter Life userId: RedpointEOS:beef01 platform: NULL\n" +
      "[2026.07.02-11.00.30:000][2]LogNet: UChannel::Close: [UNetConnection] RemoteAddr: 44.44.44.44:1, UniqueId: RedpointEOS:beef01\n");
    clearInterval(ipBans.init({ logFiles: [logNm], pollMs: 9e8 }));
    ok(ipBans.registry["beef01"]?.name === "Butter Life", "real-format login (no ? terminator) captures the exact name incl. spaces");
    ok(ipBans.getIPsForPlayer("Butter Life").includes("44.44.44.44"), "that name resolves to its confirmed IP");
    ok(ipBans.resolveIds("RedpointEOS:beef01").includes("beef01"), "resolveIds handles a prefixed (uncleaned) id token");
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
  ok(bot.bar(199, 200, 12) === "████████████", "bar carries a rounded-up fraction into a full block (199/200)");
  {
    const fillCells = (s) => [...s].filter(c => c !== "░").length;
    ok(fillCells(bot.bar(199, 200, 12)) >= fillCells(bot.bar(190, 200, 12)), "bar is monotonic — higher value never draws shorter");
  }

  console.log("Command wiring (defs <-> handlers):");
  {
    // Read the real source and strip comments so a commented-out `case`
    // (e.g. a dangling /* that swallowed a handler) does NOT count as wired.
    const src = fs.readFileSync(path.join(__dirname, "..", "index.js"), "utf8");
    const noComments = src
      .replace(/\/\*[\s\S]*?\*\//g, "")   // block comments
      .replace(/(^|[^:])\/\/[^\n]*/g, "$1"); // line comments (keep :// in urls)
    // Top-level commands only: `new SlashCommandBuilder().setName("x")`
    // (subcommands/options use their own builders, so they won't match).
    const defined = new Set([...src.matchAll(/new SlashCommandBuilder\(\)\s*\.setName\(["']([a-z0-9_-]+)["']\)/gi)]
      .map(m => m[1].toLowerCase()));
    const handled = new Set([...noComments.matchAll(/case\s+["']([a-z0-9_-]+)["']\s*:/gi)]
      .map(m => m[1].toLowerCase()));
    // Every top-level slash command must have a reachable handler case.
    const missing = [...defined].filter(c => !handled.has(c));
    ok(missing.length === 0, `every command has a live handler${missing.length ? " (missing: " + missing.join(", ") + ")" : ""}`);
    ok(handled.has("configure"), "configure handler is not commented out");
  }

  console.log("Serialized temp-ban writes:");
  await bot.upsertTempBan({ playerId: "Banned1", reason: "x", expires: Date.now() + 1e6, durationLabel: "1d", moderator: "m", server: "both" });
  await bot.upsertTempBan({ playerId: "banned1", reason: "y", expires: Date.now() + 1e6, durationLabel: "2d", moderator: "m", server: "both" });
  ok(bot.loadBans().filter(b => b.playerId.toLowerCase() === "banned1").length === 1, "upsert dedupes case-insensitively (no duplicate)");
  ok(bot.loadBans().find(b => b.playerId.toLowerCase() === "banned1").reason === "y", "upsert replaces the existing entry");
  await bot.upsertTempBan({ playerId: "Banned2", reason: "z", expires: Date.now() + 1e6, durationLabel: "1d", moderator: "m", server: "both" });
  await bot.removeBans("BANNED1");
  ok(!bot.loadBans().some(b => b.playerId.toLowerCase() === "banned1"), "removeBans removes case-insensitively");
  ok(bot.loadBans().some(b => b.playerId === "Banned2"), "removeBans leaves other bans intact");
  // permanent bans recorded in the same ban JSON (so /banlist shows them) without an expiry
  await bot.upsertPermBan({ playerId: "PermGuy", reason: "cheating", moderator: "m" });
  const pg = bot.loadBans().find(b => b.playerId === "PermGuy");
  ok(pg && pg.permanent === true && !pg.expires, "upsertPermBan records a permanent entry with no expiry");
  await bot.upsertTempBan({ playerId: "PermGuy", reason: "t", expires: Date.now() + 1e6, durationLabel: "1d", moderator: "m", server: "both" });
  ok(bot.loadBans().filter(b => b.playerId === "PermGuy").length === 1 && !bot.loadBans().find(b => b.playerId === "PermGuy").permanent, "perm/temp share one entry (upsert by id) — temp supersedes");
  await bot.removeBans("PermGuy", "Banned2");

  console.log("Punishment DM status field:");
  ok(bot.dmStatusField(null, null) === null, "no linked account -> no field");
  ok(bot.dmStatusField(true, { id: "42" }).value.includes("delivered"), "delivered field mentions success");
  ok(bot.dmStatusField(false, { id: "42" }).value.includes("Couldn't DM"), "failed field mentions failure");

  console.log(`\n${pass} passed, ${fail} failed`);
  try { fs.rmSync(sandbox, { recursive: true, force: true }); } catch {}
  process.exit(fail ? 1 : 0);
})().catch(e => { console.error("TEST CRASHED:", e); process.exit(1); });
