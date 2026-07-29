/* Read coalescing in front of RCON.

   Every RCON command is a fresh TCP connect + MD5 handshake, served on Pavlov's game
   thread. Six independent timers want RefreshList within the same second, so the cost
   that matters here is CONNECTIONS OPENED, not wall time - each test asserts on how
   many times the fake server was actually contacted. */
const { test } = require("node:test");
const assert = require("node:assert");
const net = require("net");

// Minimal stand-in for Pavlov's RCON: password prompt, auth, then a JSON reply.
function fakeRcon() {
  const state = { connections: 0, commands: [] };
  const server = net.createServer((sock) => {
    state.connections++;
    sock.write("Password:");
    sock.on("data", (d) => {
      const t = d.toString();
      if (t.length === 32 && !t.includes("\n")) { sock.write("Authenticated=1"); return; }
      state.commands.push(t.trim());
      sock.write(JSON.stringify({ Successful: true, PlayerList: [] }));
      sock.end();
    });
    sock.on("error", () => {});
  });
  return { state, server, listen: () => new Promise(r => server.listen(0, "127.0.0.1", r)),
           port: () => server.address().port, close: () => new Promise(r => server.close(r)) };
}

function makeRcon(port, ttlMs) {
  process.env.RCON_HOST_1 = "127.0.0.1";
  process.env.RCON_PORT_1 = String(port);
  process.env.RCON_PASSWORD_1 = "x";
  process.env.RCON_READ_CACHE_MS = String(ttlMs);
  delete require.cache[require.resolve("../rcon/index.js")];
  return require("../rcon/index.js")({ logger: { warn() {}, info() {}, error() {} }, activeServers: ["server1"] });
}

test("concurrent identical reads share one connection", async () => {
  const f = fakeRcon(); await f.listen();
  const r = makeRcon(f.port(), 2500);
  await Promise.all([
    r.sendRcon("RefreshList", "server1"), r.sendRcon("RefreshList", "server1"),
    r.sendRcon("RefreshList", "server1"), r.sendRcon("RefreshList", "server1"),
  ]);
  assert.equal(f.state.connections, 1, "four simultaneous callers must not open four sockets");
  await f.close();
});

test("a completed read is reused inside the TTL, and refetched after it", async () => {
  const f = fakeRcon(); await f.listen();
  const r = makeRcon(f.port(), 150);
  await r.sendRcon("RefreshList", "server1");
  await r.sendRcon("RefreshList", "server1");
  assert.equal(f.state.connections, 1, "second read inside the window is served from cache");
  await new Promise(s => setTimeout(s, 220));
  await r.sendRcon("RefreshList", "server1");
  assert.equal(f.state.connections, 2, "past the TTL it must go back to the server");
  await f.close();
});

test("different commands and different servers are cached separately", async () => {
  const f = fakeRcon(); await f.listen();
  const r = makeRcon(f.port(), 2500);
  await r.sendRcon("RefreshList", "server1");
  await r.sendRcon("ServerInfo", "server1");
  assert.equal(f.state.connections, 2, "ServerInfo must not be answered from RefreshList's entry");
  assert.deepEqual(f.state.commands.map(c => c.trim()).sort(), ["RefreshList", "ServerInfo"]);
  await f.close();
});

test("a state-changing command is never cached, and clears the cached roster", async () => {
  // The dangerous case: kick someone, then read a roster that still lists them.
  const f = fakeRcon(); await f.listen();
  const r = makeRcon(f.port(), 2500);
  await r.sendRcon("RefreshList", "server1");
  await r.sendRcon("Kick Griefer", "server1");
  await r.sendRcon("RefreshList", "server1");
  assert.equal(f.state.connections, 3, "the post-kick roster must be refetched, not served stale");
  await r.sendRcon("Kick Griefer", "server1");
  await r.sendRcon("Kick Griefer", "server1");
  assert.equal(f.state.connections, 5, "a mutation must never be answered from cache");
  await f.close();
});

test("a failed read is not cached as a result", async () => {
  // Nothing listening: every attempt must reach out again rather than caching failure.
  const r = makeRcon(59999, 2500);
  await assert.rejects(() => r.sendRcon("RefreshList", "server1", 200, 0));
  await assert.rejects(() => r.sendRcon("RefreshList", "server1", 200, 0),
    "a second call must retry, not replay a cached rejection");
});

test("setting RCON_READ_CACHE_MS=0 disables coalescing entirely", async () => {
  const f = fakeRcon(); await f.listen();
  const r = makeRcon(f.port(), 0);
  await r.sendRcon("RefreshList", "server1");
  await r.sendRcon("RefreshList", "server1");
  assert.equal(f.state.connections, 2, "the escape hatch must actually opt out");
  await f.close();
});

test("invalidateRconReads drops the cache on demand", async () => {
  const f = fakeRcon(); await f.listen();
  const r = makeRcon(f.port(), 5000);
  await r.sendRcon("RefreshList", "server1");
  r.invalidateRconReads("server1");
  await r.sendRcon("RefreshList", "server1");
  assert.equal(f.state.connections, 2);
  await f.close();
});
