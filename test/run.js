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

  console.log("Punishment DM status field:");
  ok(bot.dmStatusField(null, null) === null, "no linked account -> no field");
  ok(bot.dmStatusField(true, { id: "42" }).value.includes("delivered"), "delivered field mentions success");
  ok(bot.dmStatusField(false, { id: "42" }).value.includes("Couldn't DM"), "failed field mentions failure");

  console.log(`\n${pass} passed, ${fail} failed`);
  try { fs.rmSync(sandbox, { recursive: true, force: true }); } catch {}
  process.exit(fail ? 1 : 0);
})().catch(e => { console.error("TEST CRASHED:", e); process.exit(1); });
