"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const Module = require("node:module");
const path = require("node:path");
const fs = require("node:fs");
const os = require("node:os");

// index.js is a hard `require("discord.js")` + `require("dotenv").config()` at
// module scope. Stub both so the pure helpers can be exercised without a real
// Discord connection, a token, or discord.js/dotenv actually installed.
const originalResolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
  if (request === "dotenv" || request === "discord.js") {
    return request;
  }
  return originalResolve.call(this, request, ...rest);
};

const originalLoad = Module._load;
Module._load = function (request, ...rest) {
  if (request === "dotenv") {
    return { config: () => ({}) };
  }
  if (request === "discord.js") {
    class Stub { constructor() { } }
    return {
      Client: class { constructor() { this.channels = { fetch: async () => null }; this.on = () => { }; this.once = () => { }; this.user = null; } },
      GatewayIntentBits: { Guilds: 1 },
      ActivityType: { Watching: 3 },
      REST: class { setToken() { return this; } },
      Routes: { applicationCommands: () => "" },
      SlashCommandBuilder: class {
        setName() { return this; } setDescription() { return this; }
        addStringOption() { return this; } addIntegerOption() { return this; }
        addRoleOption() { return this; } addUserOption() { return this; }
        addSubcommand() { return this; } toJSON() { return {}; }
      },
      EmbedBuilder: class {
        setColor() { return this; } setTitle() { return this; } setDescription() { return this; }
        addFields() { return this; } setFooter() { return this; } setTimestamp() { return this; }
      },
      ActionRowBuilder: Stub, ButtonBuilder: Stub, ButtonStyle: {}, ComponentType: {},
    };
  }
  return originalLoad.call(this, request, ...rest);
};

// index.js runs in a throwaway CWD so the data-file bootstrap (tempbans.json,
// warnings.json, etc.) and DONATOR_FILE writes don't touch the real repo.
// The FILES paths are relative and resolved against process.cwd() on every
// read/write (not just at module load), so cwd must stay pointed at the
// tmpdir for the whole test run — not just during the require() below.
const workdir = fs.mkdtempSync(path.join(os.tmpdir(), "mab-test-"));
process.env.DISCORD_TOKEN = "test";
process.env.CLIENT_ID = "test";
process.env.RCON_HOST_1 = "127.0.0.1";
process.env.RCON_PORT_1 = "1234";
process.env.RCON_PASSWORD_1 = "test";
process.env.DONATOR_PATH = path.join(workdir, "donator.txt");
process.chdir(workdir);

const bot = require("../index.js");

Module._load = originalLoad;
Module._resolveFilename = originalResolve;

test("splitPages chunks lines into pages of the requested size", () => {
  const pages = bot.splitPages(["a", "b", "c", "d", "e"], 2);
  assert.deepEqual(pages, [["a", "b"], ["c", "d"], ["e"]]);
});

test("splitPages returns a single empty page for an empty list", () => {
  assert.deepEqual(bot.splitPages([], 10), [[]]);
});

test("extractPlayerNames trims names and drops blanks across key variants", () => {
  const names = bot.extractPlayerNames({
    PlayerList: [{ Username: " Alice " }, { username: "Bob" }, { PlayerName: "" }, { name: "Eve" }],
  });
  assert.deepEqual(names, ["Alice", "Bob", "Eve"]);
});

test("extractPlayerNames handles a missing/empty PlayerList", () => {
  assert.deepEqual(bot.extractPlayerNames({}), []);
  assert.deepEqual(bot.extractPlayerNames(null), []);
});

test("isOwner matches only hardcoded owner IDs", () => {
  assert.equal(bot.isOwner("1014251293159731310"), true);
  assert.equal(bot.isOwner("999999999999999999"), false);
});

test("donator add/remove round-trip through the whitelist file", () => {
  const first = bot.addDonator("Courier1");
  assert.deepEqual(first, { ok: true, already: false });
  assert.equal(bot.isDonator("courier1"), true);

  const dup = bot.addDonator("courier1");
  assert.deepEqual(dup, { ok: true, already: true });

  const removed = bot.removeDonator("COURIER1");
  assert.deepEqual(removed, { ok: true, missing: false });
  assert.equal(bot.isDonator("Courier1"), false);
});

test("player notes add/list/clear are case-insensitive on player ID", async () => {
  await bot.addPlayerNote("Player1", "first note", "tester");
  const count = await bot.addPlayerNote("player1", "second note", "tester");
  assert.equal(count, 2);
  assert.equal(bot.getPlayerNotes("PLAYER1").length, 2);

  const cleared = await bot.clearPlayerNotes("player1");
  assert.equal(cleared, 2);
  assert.equal(bot.getPlayerNotes("player1").length, 0);
});
