/* Pavlov logs ALL RCON traffic, including the bot's own. The audit alert is only
   useful if it can tell "the bot just ran this" from "somebody ran this with another
   tool" - otherwise every /givemenu would alert on itself and the signal is worthless. */
const { test } = require("node:test");
const assert = require("node:assert");

function makeRcon() {
  delete require.cache[require.resolve("../rcon/index.js")];
  return require("../rcon/index.js")({ logger: { warn() {}, info() {}, error() {} }, activeServers: ["server1"] });
}

test("a command the bot never sent is not attributed to it", () => {
  const { wasIssuedByBot } = makeRcon();
  assert.equal(wasIssuedByBot("GiveMenu SomeStranger"), false);
});

test("a command the bot sent is recognised as its own", async () => {
  const r = makeRcon();
  // sendRconRaw records before it connects, so an unreachable host still registers it -
  // which is the point: the log line must never beat the record.
  await r.sendRconRaw("GiveMenu Butter Life", "server1", 50).catch(() => {});
  assert.equal(r.wasIssuedByBot("GiveMenu Butter Life"), true);
});

test("matching ignores case and whitespace, as the log line's spacing may differ", async () => {
  const r = makeRcon();
  await r.sendRconRaw("GiveMenu  Butter Life", "server1", 50).catch(() => {});
  assert.equal(r.wasIssuedByBot("givemenu Butter Life"), true);
  assert.equal(r.wasIssuedByBot("GiveMenu   butter life  "), true);
});

test("a different target is NOT covered by a similar command", async () => {
  // The dangerous false negative: granting yourself a menu right after the bot granted
  // someone else must still alert.
  const r = makeRcon();
  await r.sendRconRaw("GiveMenu Butter Life", "server1", 50).catch(() => {});
  assert.equal(r.wasIssuedByBot("GiveMenu Attacker"), false);
});

test("one command fanned out to several servers stays attributed for all of them", async () => {
  // sendRconBoth issues the same text per server, so the log carries it more than once.
  // The record must not be consumed by the first match or the rest would false-alarm.
  const r = makeRcon();
  await r.sendRconRaw("AddMod Someone", "server1", 50).catch(() => {});
  assert.equal(r.wasIssuedByBot("AddMod Someone"), true);
  assert.equal(r.wasIssuedByBot("AddMod Someone"), true);
  assert.equal(r.wasIssuedByBot("AddMod Someone"), true);
});

/* RCON+ has a much wider command surface than the menu verbs: Ban, Kick, Gag, Kill,
   Slap, SwitchTeam, GiveCash, SetCash, GiveItem, SetPlayerSkin, MovementSpeed,
   DropItems. Alerting on all of them would bury the ones that matter, so they are
   classified. This mirrors the map in events/index.js. */
const RCON_CMD_CLASS = new Map([
  ["givemenu", "privilege"], ["removemenu", "privilege"], ["clearmenuaccess", "privilege"],
  ["addmod", "privilege"], ["removemod", "privilege"],
  ["addaccessmanager", "privilege"], ["removeaccessmanager", "privilege"],
  ["givecash", "economy"], ["setcash", "economy"], ["giveitem", "economy"],
  ["ban", "punishment"], ["unban", "punishment"],
]);
const classOf = (cmd) => RCON_CMD_CLASS.get(String(cmd).trim().split(/\s+/)[0].toLowerCase()) ?? null;

test("money and item commands are classified as economy", () => {
  // These write to the game economy directly - the bot's ledger never sees them.
  assert.equal(classOf("GiveCash LxPXHam 5665"), "economy");
  assert.equal(classOf("SetCash RedTeam 100"), "economy");
  assert.equal(classOf("GiveItem LxPXHam vvf1 1"), "economy");
});

test("bans are classified as punishment so their IPs still get flagged", () => {
  assert.equal(classOf("Ban RedTeam"), "punishment");
  assert.equal(classOf("Unban RedTeam"), "punishment");
});

test("routine in-game moderation and RP commands are ignored", () => {
  for (const cmd of ["Kill RedTeam", "Slap RedTeam 555555", "SwitchTeam RedTeam",
                     "Gag LxPXHam True", "Kick RedTeam", "SetPlayerSkin LxPXHam gggg",
                     "MovementSpeed  tttt", "DropItems  ", "RefreshList"]) {
    assert.equal(classOf(cmd), null, `${cmd} must not alert`);
  }
});

test("the target is the second token, even with the mod's doubled spacing", () => {
  // "MovementSpeed  tttt" in the real log has two spaces - splitting on /\s+/ matters.
  const target = (c) => String(c).trim().split(/\s+/)[1] ?? null;
  assert.equal(target("Ban RedTeam"), "RedTeam");
  assert.equal(target("GiveCash LxPXHam 5665"), "LxPXHam");
  assert.equal(target("MovementSpeed  tttt"), "tttt");
  assert.equal(target("DropItems  "), null);
});
