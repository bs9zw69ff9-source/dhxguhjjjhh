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
