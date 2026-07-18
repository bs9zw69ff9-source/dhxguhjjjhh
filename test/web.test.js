/* Functional tests for the in-process web dashboard + moderation panel.
   Spins up the real http server against a stubbed ctx and drives it over HTTP:
   verifies the public view leaks no PII, the login/session/CSRF flow, and that
   each admin action dispatches to the correct injected moderation function. */
const { test, before, after } = require("node:test");
const assert = require("node:assert");
const http = require("node:http");
const createWebServer = require("../web/server.js");

const PORT = 8231, BASE = `http://127.0.0.1:${PORT}`;
let calls = [], srv;
const rec = (n) => (...a) => { calls.push([n, ...a]); };

const ctx = {
  logger: { info() {}, warn() {}, error() {} },
  ACTIVE_SERVERS: ["server1"],
  serverLabel: (s) => (s === "both" ? "All Servers" : "Server 1"),
  getOnlinePlayers: async () => [{ name: "Alice" }, { name: "Bob" }],
  loadBans: () => [{ playerId: "Evil", permanent: true, reason: "cheating", moderator: "mod1" }],
  loadModLog: () => [{ action: "kick", at: Date.now() - 60000 }, { action: "permban", at: Date.now() }],
  firewallStatus: async () => ({ off: true }),
  banWithIp: async (...a) => { calls.push(["banWithIp", ...a]); return {}; },
  unbanEverywhere: (...a) => { calls.push(["unbanEverywhere", ...a]); return {}; },
  removeBans: rec("removeBans"), addAutobanExempt: rec("addAutobanExempt"),
  upsertPermBan: rec("upsertPermBan"), upsertTempBan: rec("upsertTempBan"),
  enforceBansSweep: async () => {}, writeModLog: rec("writeModLog"),
  sendRcon: async (...a) => { calls.push(["sendRcon", ...a]); },
  preserveBalanceAcrossKick: rec("preserveBalanceAcrossKick"),
  setMute: async (...a) => { calls.push(["setMute", ...a]); },
  clearMute: async (...a) => { calls.push(["clearMute", ...a]); },
  getMute: () => null, gagEverywhere: rec("gag"), ungagEverywhere: rec("ungag"),
  sanitizeBanName: (s) => String(s || "").trim(), sanitizeId: (s) => String(s || "").trim(),
  isMasterName: (n) => n === "lxpxham",
  parseDuration: (r) => { const m = String(r).match(/^(\d+)([smhd])$/); return m ? +m[1] * ({ s: 1e3, m: 6e4, h: 36e5, d: 864e5 }[m[2]]) : null; },
  formatUptime: () => "1h 2m", BOT_START_MS: Date.now() - 3720000,
};

function req(method, path, { body, cookie } = {}) {
  return new Promise((resolve) => {
    const data = body ? new URLSearchParams(body).toString() : null;
    const r = http.request(BASE + path, { method, headers: {
      ...(data ? { "Content-Type": "application/x-www-form-urlencoded", "Content-Length": Buffer.byteLength(data) } : {}),
      ...(cookie ? { Cookie: cookie } : {}),
    } }, (res) => {
      let out = ""; res.on("data", (c) => (out += c));
      res.on("end", () => resolve({ status: res.statusCode, body: out, setCookie: res.headers["set-cookie"], location: res.headers.location }));
    });
    if (data) r.write(data); r.end();
  });
}
async function loginCookie() {
  const r = await req("POST", "/login", { body: { password: "s3cret-pw" } });
  return r.setCookie[0].split(";")[0];
}
async function csrfFor(cookie) {
  const r = await req("GET", "/admin", { cookie });
  return (r.body.match(/name="csrf" value="([^"]+)"/) || [])[1];
}

before(() => {
  Object.assign(process.env, { WEB_ENABLE: "1", WEB_PORT: String(PORT), WEB_HOST: "127.0.0.1", WEB_ADMIN_PASSWORD: "s3cret-pw" });
  srv = createWebServer(ctx).start();
});
after(() => { srv && srv.close(); });

test("public dashboard loads and leaks no player/moderator PII", async () => {
  calls = [];
  const r = await req("GET", "/");
  assert.equal(r.status, 200);
  assert.match(r.body, /Live Status/);
  assert.ok(!/Alice/.test(r.body), "must not show online player names publicly");
  assert.ok(!/mod1/.test(r.body), "must not show moderator names publicly");
});

test("api/status returns aggregate json", async () => {
  const r = await req("GET", "/api/status");
  assert.equal(r.status, 200);
  assert.equal(JSON.parse(r.body).bans.perm, 1);
});

test("admin is gated: redirect when unauthed, 401 on wrong password", async () => {
  const a = await req("GET", "/admin");
  assert.equal(a.status, 302);
  assert.equal(a.location, "/login");
  const b = await req("POST", "/login", { body: { password: "nope" } });
  assert.equal(b.status, 401);
});

test("correct password sets an HttpOnly SameSite=Strict cookie", async () => {
  const r = await req("POST", "/login", { body: { password: "s3cret-pw" } });
  assert.equal(r.status, 302);
  assert.match(r.setCookie[0], /HttpOnly/i);
  assert.match(r.setCookie[0], /SameSite=Strict/i);
});

test("admin panel shows players and bans once authed", async () => {
  const cookie = await loginCookie();
  const r = await req("GET", "/admin", { cookie });
  assert.match(r.body, /Moderation Panel/);
  assert.match(r.body, /Alice/);
  assert.match(r.body, /Evil/);
});

test("actions require a valid CSRF token", async () => {
  calls = [];
  const cookie = await loginCookie();
  const r = await req("POST", "/admin/action", { cookie, body: { op: "mute", playerid: "Bob", duration: "1h" } });
  assert.equal(r.status, 403);
  assert.ok(!calls.some((c) => c[0] === "setMute"));
});

test("mute action dispatches setMute + gag", async () => {
  calls = [];
  const cookie = await loginCookie();
  const csrf = await csrfFor(cookie);
  const r = await req("POST", "/admin/action", { cookie, body: { op: "mute", playerid: "Bob", duration: "30m", reason: "spam", csrf } });
  assert.match(r.location, /ok=1/);
  assert.ok(calls.some((c) => c[0] === "setMute" && c[1] === "Bob"));
  assert.ok(calls.some((c) => c[0] === "gag"));
});

test("master names can never be actioned", async () => {
  calls = [];
  const cookie = await loginCookie();
  const csrf = await csrfFor(cookie);
  const r = await req("POST", "/admin/action", { cookie, body: { op: "ban", playerid: "lxpxham", csrf } });
  assert.match(decodeURIComponent(r.location), /ok=0.*master/i);
  assert.ok(!calls.some((c) => c[0] === "banWithIp"));
});

test("blank duration = permanent ban, duration = tempban", async () => {
  const cookie = await loginCookie();
  const csrf = await csrfFor(cookie);
  calls = [];
  await req("POST", "/admin/action", { cookie, body: { op: "ban", playerid: "Cheater", server: "server1", reason: "aim", csrf } });
  assert.ok(calls.some((c) => c[0] === "banWithIp" && c[1] === "Cheater"));
  assert.ok(calls.some((c) => c[0] === "upsertPermBan"));
  calls = [];
  await req("POST", "/admin/action", { cookie, body: { op: "ban", playerid: "Timmy", duration: "2h", csrf } });
  assert.ok(calls.some((c) => c[0] === "upsertTempBan"));
});

test("kick sends RCON, unban clears everywhere, logout invalidates session", async () => {
  const cookie = await loginCookie();
  const csrf = await csrfFor(cookie);
  calls = [];
  await req("POST", "/admin/action", { cookie, body: { op: "kick", playerid: "Bob", server: "server1", csrf } });
  assert.ok(calls.some((c) => c[0] === "sendRcon" && /Kick Bob/.test(c[1])));
  await req("POST", "/admin/action", { cookie, body: { op: "unban", playerid: "Evil", csrf } });
  assert.ok(calls.some((c) => c[0] === "unbanEverywhere" && c[1] === "Evil"));
  await req("POST", "/logout", { cookie, body: { csrf } });
  const after = await req("GET", "/admin", { cookie });
  assert.equal(after.location, "/login");
});
