/* ---------------- web/server: info dashboard + password-gated moderation panel ----------------
   A tiny, dependency-free web server (Node core http/crypto only) that runs inside
   the bot process as another ctx-wired subsystem. It exposes:

     • a PUBLIC read-only info dashboard  (server status, online counts, ban totals,
       uptime, recent-activity feed — aggregate only, no IPs / Discord IDs / player PII)
     • a PASSWORD-GATED admin panel limited to MODERATION actions
       (kick · tempban · permban · unban · mute · unmute), plus read views
       (ban list, online players, mod log, firewall status).

   Because it runs in-process and calls the exact same moderation functions the Discord
   commands use (banWithIp, unbanEverywhere, setMute, …), the panel's behaviour is
   identical to the bot's — no duplicated ban logic, no separate RCON path.

   Security model (single shared password):
     • Password comes from env WEB_ADMIN_PASSWORD. If unset, the admin panel is disabled.
     • Login compares in constant time (crypto.timingSafeEqual) and is rate-limited per IP.
     • On success a random 256-bit session id is stored server-side (idle-expiring) and
       set as an HttpOnly, SameSite=Strict cookie. No password or secret is ever sent to
       the client. Every mutating POST carries a per-session CSRF token.
     • All action inputs are validated (name sanitized, server whitelisted); master names
       can never be actioned.

   Opt-in: only starts when WEB_ENABLE is truthy. Bind host/port via WEB_HOST / WEB_PORT.

   Injected deps: see the destructure below. Usage:
     require("./web/server")(ctx).start();
*/
const http   = require("http");
const crypto = require("crypto");

module.exports = function createWebServer(ctx) {
  const {
    logger, ACTIVE_SERVERS, serverLabel, getOnlinePlayers, loadBans, loadModLog,
    firewallStatus, banWithIp, unbanEverywhere, removeBans, addAutobanExempt,
    upsertPermBan, upsertTempBan, enforceBansSweep, writeModLog, sendRcon,
    preserveBalanceAcrossKick, setMute, clearMute, getMute, gagEverywhere,
    ungagEverywhere, sanitizeBanName, sanitizeId, isMasterName, parseDuration,
    formatUptime, BOT_START_MS,
  } = ctx;

  // ---- config ----------------------------------------------------------------
  const ENABLED  = /^(1|true|yes|on)$/i.test(process.env.WEB_ENABLE || "");
  const PORT     = Number(process.env.WEB_PORT) || 8080;
  const HOST     = process.env.WEB_HOST || "0.0.0.0";
  const PASSWORD = process.env.WEB_ADMIN_PASSWORD || "";
  const SITE     = process.env.WEB_SITE_NAME || "NuclearRP";
  // When behind a reverse proxy (nginx), the socket IP is always 127.0.0.1, which
  // would collapse per-IP login throttling into one global bucket. Set WEB_TRUST_PROXY=1
  // to read the real client IP from the proxy's X-Forwarded-For header instead. Only
  // enable this when a trusted proxy actually sets that header — otherwise a client
  // could spoof it.
  const TRUST_PROXY = /^(1|true|yes|on)$/i.test(process.env.WEB_TRUST_PROXY || "");
  const SESSION_TTL_MS = 2 * 60 * 60 * 1000;                 // 2h idle
  const COOKIE   = "sid";
  const VALID_SERVERS = new Set(["both", ...ACTIVE_SERVERS]);

  // ---- session + login-throttle state ---------------------------------------
  const sessions = new Map();   // sid -> { csrf, exp }
  const attempts = new Map();   // ip  -> { n, until }
  const MAX_FAILS = 6, LOCK_MS = 10 * 60 * 1000;

  function clientIp(req) {
    if (TRUST_PROXY) {
      const xff = String(req.headers["x-forwarded-for"] || "").split(",")[0].trim();
      if (xff) return xff;   // leftmost = original client (nginx appends downstream)
    }
    return (req.socket && req.socket.remoteAddress) || "?";
  }
  function newSession() {
    const sid  = crypto.randomBytes(32).toString("hex");
    const csrf = crypto.randomBytes(32).toString("hex");
    sessions.set(sid, { csrf, exp: Date.now() + SESSION_TTL_MS });
    return { sid, csrf };
  }
  function getSession(req) {
    const sid = parseCookies(req)[COOKIE];
    if (!sid) return null;
    const s = sessions.get(sid);
    if (!s) return null;
    if (s.exp < Date.now()) { sessions.delete(sid); return null; }
    s.exp = Date.now() + SESSION_TTL_MS;   // sliding idle window
    return { sid, ...s };
  }
  function dropSession(req) {
    const sid = parseCookies(req)[COOKIE];
    if (sid) sessions.delete(sid);
  }
  // periodic sweep of expired sessions/locks
  const sweep = setInterval(() => {
    const now = Date.now();
    for (const [k, v] of sessions) if (v.exp < now) sessions.delete(k);
    for (const [k, v] of attempts) if (v.until && v.until < now) attempts.delete(k);
  }, 5 * 60 * 1000);
  if (sweep.unref) sweep.unref();

  function passwordOk(supplied) {
    if (!PASSWORD) return false;
    const a = Buffer.from(String(supplied || ""));
    const b = Buffer.from(PASSWORD);
    // timingSafeEqual requires equal length — hash both to a fixed width first.
    const ha = crypto.createHash("sha256").update(a).digest();
    const hb = crypto.createHash("sha256").update(b).digest();
    return crypto.timingSafeEqual(ha, hb);
  }

  // ---- tiny http helpers ------------------------------------------------------
  function parseCookies(req) {
    const out = {};
    for (const part of String(req.headers.cookie || "").split(";")) {
      const i = part.indexOf("=");
      if (i > 0) out[part.slice(0, i).trim()] = decodeURIComponent(part.slice(i + 1).trim());
    }
    return out;
  }
  function readBody(req, limit = 16 * 1024) {
    return new Promise((resolve) => {
      let data = "", tooBig = false;
      req.on("data", (c) => { data += c; if (data.length > limit) { tooBig = true; req.destroy(); } });
      req.on("end", () => resolve(tooBig ? {} : parseForm(data)));
      req.on("error", () => resolve({}));
    });
  }
  function parseForm(s) {
    const out = {};
    for (const pair of String(s || "").split("&")) {
      if (!pair) continue;
      const i = pair.indexOf("=");
      const k = decodeURIComponent((i < 0 ? pair : pair.slice(0, i)).replace(/\+/g, " "));
      const v = i < 0 ? "" : decodeURIComponent(pair.slice(i + 1).replace(/\+/g, " "));
      out[k] = v;
    }
    return out;
  }
  function send(res, status, body, headers = {}) {
    res.writeHead(status, {
      "Content-Type": "text/html; charset=utf-8",
      "X-Content-Type-Options": "nosniff",
      "X-Frame-Options": "DENY",
      "Referrer-Policy": "no-referrer",
      "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'self'",
      ...headers,
    });
    res.end(body);
  }
  function redirect(res, to, cookie) {
    const h = { Location: to };
    if (cookie) h["Set-Cookie"] = cookie;
    res.writeHead(302, h); res.end();
  }
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // ---- data collectors --------------------------------------------------------
  async function collectServers() {
    return Promise.all(ACTIVE_SERVERS.map(async (srv) => {
      let players = [], online = false;
      try {
        players = (await getOnlinePlayers(srv)) || [];
        online = true;
      } catch { online = false; }
      return { id: srv, label: serverLabel(srv), online, count: players.length, players };
    }));
  }
  function banTotals() {
    const now = Date.now();
    let perm = 0, temp = 0;
    for (const b of loadBans()) {
      if (b.permanent || !b.expires) perm++;
      else if (b.expires > now) temp++;
    }
    return { perm, temp, total: perm + temp };
  }
  // Public-safe recent feed: action type + relative time only. No names/moderators/IPs.
  function publicFeed(limit = 12) {
    const log = loadModLog();
    return log.slice(-limit).reverse().map((e) => ({
      action: String(e.action || "action"),
      at: e.at || 0,
    }));
  }

  // ---- HTML rendering ---------------------------------------------------------
  function layout(title, bodyHtml, { refresh = false } = {}) {
    return `<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>${esc(title)} · ${esc(SITE)}</title>
<style>
  :root{color-scheme:light dark}
  *{box-sizing:border-box}
  body{margin:0;font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;
       background:#0e1116;color:#e6edf3}
  a{color:#6cb6ff;text-decoration:none}
  header{padding:18px 22px;border-bottom:1px solid #222a35;display:flex;
         align-items:center;justify-content:space-between;background:#11161d}
  header .brand{font-weight:700;letter-spacing:.3px}
  header nav a{margin-left:16px;font-size:14px}
  main{max-width:960px;margin:0 auto;padding:22px}
  h1{font-size:20px;margin:0 0 4px}
  h2{font-size:15px;text-transform:uppercase;letter-spacing:.6px;color:#9aa7b4;margin:26px 0 10px}
  .muted{color:#8b98a5}
  .grid{display:grid;gap:14px;grid-template-columns:repeat(auto-fit,minmax(180px,1fr))}
  .card{background:#161b22;border:1px solid #222a35;border-radius:10px;padding:16px}
  .stat{font-size:30px;font-weight:700}
  .stat.small{font-size:20px}
  .pill{display:inline-block;padding:2px 9px;border-radius:999px;font-size:12px;font-weight:600}
  .on{background:#12331f;color:#4ade80}.off{background:#3a1620;color:#f87171}
  table{width:100%;border-collapse:collapse;font-size:14px}
  th,td{text-align:left;padding:8px 10px;border-bottom:1px solid #222a35;vertical-align:top}
  th{color:#8b98a5;font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.5px}
  form.inline{display:inline}
  input,select,button{font:inherit}
  input,select{background:#0d1117;border:1px solid #2b3440;color:#e6edf3;border-radius:7px;padding:8px 10px;width:100%}
  label{display:block;font-size:12px;color:#9aa7b4;margin:10px 0 4px}
  button{background:#1f6feb;border:0;color:#fff;border-radius:7px;padding:9px 14px;font-weight:600;cursor:pointer}
  button.ghost{background:#21262d;color:#e6edf3;border:1px solid #30363d}
  button.danger{background:#b62324}
  button.warn{background:#9a6700}
  .row{display:grid;gap:12px;grid-template-columns:1fr 1fr}
  .flash{padding:12px 14px;border-radius:8px;margin:0 0 16px;border:1px solid}
  .flash.ok{background:#0f2a17;border-color:#1f6f3a;color:#7ee2a3}
  .flash.err{background:#2a1416;border-color:#7a2a2d;color:#f4a3a3}
  .actions form{margin:0}
  footer{max-width:960px;margin:20px auto;padding:0 22px 30px;color:#6b7681;font-size:12px}
  @media(max-width:640px){.row{grid-template-columns:1fr}}
</style>
${refresh ? `<script>setTimeout(()=>location.reload(),20000)</script>` : ""}
</head><body>
<header>
  <div class="brand">${esc(SITE)}</div>
  <nav><a href="/">Dashboard</a><a href="/admin">Admin</a></nav>
</header>
<main>${bodyHtml}</main>
<footer>Read-only public view. Moderation actions require an admin login.</footer>
</body></html>`;
  }

  async function pageDashboard() {
    const servers = await collectServers();
    const bans = banTotals();
    const feed = publicFeed();
    const totalOnline = servers.reduce((s, x) => s + (x.online ? x.count : 0), 0);
    const upMs = Date.now() - BOT_START_MS;

    const cards = `
      <div class="grid">
        <div class="card"><div class="muted">Servers online</div>
          <div class="stat">${servers.filter(s => s.online).length}/${servers.length}</div></div>
        <div class="card"><div class="muted">Players online</div>
          <div class="stat">${totalOnline}</div></div>
        <div class="card"><div class="muted">Active bans</div>
          <div class="stat">${bans.total}</div>
          <div class="muted">${bans.perm} perm · ${bans.temp} temp</div></div>
        <div class="card"><div class="muted">Bot uptime</div>
          <div class="stat small">${esc(formatUptime(upMs))}</div></div>
      </div>`;

    const srvRows = servers.map(s => `
      <tr><td>${esc(s.label)}</td>
      <td>${s.online ? `<span class="pill on">Online</span>` : `<span class="pill off">Offline</span>`}</td>
      <td>${s.online ? s.count : "—"}</td></tr>`).join("");

    const feedRows = feed.length ? feed.map(f => `
      <tr><td>${esc(f.action)}</td>
      <td class="muted">${f.at ? new Date(f.at).toISOString().replace("T", " ").slice(0, 16) + " UTC" : "—"}</td></tr>`).join("")
      : `<tr><td colspan="2" class="muted">No recent activity.</td></tr>`;

    return layout("Dashboard", `
      <h1>Live Status</h1>
      <p class="muted">Auto-refreshes every 20s.</p>
      ${cards}
      <h2>Servers</h2>
      <div class="card"><table><thead><tr><th>Server</th><th>Status</th><th>Players</th></tr></thead>
        <tbody>${srvRows}</tbody></table></div>
      <h2>Recent moderation activity</h2>
      <div class="card"><table><thead><tr><th>Action</th><th>When</th></tr></thead>
        <tbody>${feedRows}</tbody></table></div>
    `, { refresh: true });
  }

  function pageLogin(err) {
    const disabled = !PASSWORD;
    return layout("Admin Login", `
      <h1>Admin Login</h1>
      ${err ? `<div class="flash err">${esc(err)}</div>` : ""}
      ${disabled ? `<div class="flash err">The admin panel is disabled — no <code>WEB_ADMIN_PASSWORD</code> is set on the server.</div>` : `
      <div class="card" style="max-width:360px">
        <form method="POST" action="/login">
          <label for="p">Password</label>
          <input id="p" type="password" name="password" autocomplete="current-password" autofocus required>
          <div style="margin-top:14px"><button type="submit">Sign in</button></div>
        </form>
      </div>`}
    `);
  }

  const serverOptions = (sel = "both") =>
    ["both", ...ACTIVE_SERVERS].map(v =>
      `<option value="${esc(v)}"${v === sel ? " selected" : ""}>${esc(serverLabel(v))}</option>`).join("");

  async function pageAdmin(sess, flash) {
    const servers = await collectServers();
    const now = Date.now();
    const bans = loadBans()
      .filter(b => b.permanent || (b.expires && b.expires > now))
      .sort((a, b) => (b.permanent ? 1 : 0) - (a.permanent ? 1 : 0) || (a.expires ?? 0) - (b.expires ?? 0));
    const csrf = sess.csrf;
    let fw = null;
    try { fw = await firewallStatus(); } catch (e) { fw = { error: e.message }; }

    const t = `<input type="hidden" name="csrf" value="${esc(csrf)}">`;

    // Online players with a per-player kick button.
    const onlineRows = servers.flatMap(s => (s.online ? s.players : []).map(p => `
      <tr><td>${esc(p.name || "?")}</td><td>${esc(s.label)}</td>
      <td class="actions">
        <form class="inline" method="POST" action="/admin/action">${t}
          <input type="hidden" name="op" value="kick">
          <input type="hidden" name="playerid" value="${esc(p.name || "")}">
          <input type="hidden" name="server" value="${esc(s.id)}">
          <button class="warn" onclick="return confirm('Kick ${esc(p.name)}?')">Kick</button>
        </form>
      </td></tr>`)).join("") || `<tr><td colspan="3" class="muted">No players online (or servers unreachable).</td></tr>`;

    const banRows = bans.length ? bans.map(b => {
      const when = (b.permanent || !b.expires) ? `<span class="pill off">PERM</span>`
        : new Date(b.expires).toISOString().slice(0, 10);
      return `<tr><td>${esc(b.playerId)}</td><td>${when}</td>
        <td>${esc((b.reason || "—").slice(0, 60))}</td><td class="muted">${esc(b.moderator || "?")}</td>
        <td class="actions"><form class="inline" method="POST" action="/admin/action">${t}
          <input type="hidden" name="op" value="unban">
          <input type="hidden" name="playerid" value="${esc(b.playerId)}">
          <button class="ghost" onclick="return confirm('Unban ${esc(b.playerId)}?')">Unban</button>
        </form></td></tr>`;
    }).join("") : `<tr><td colspan="5" class="muted">No active bans.</td></tr>`;

    let fwHtml;
    if (!fw || fw.off) fwHtml = `<p class="muted">Firewall control is off (<code>UFW_BLOCK</code> not set).</p>`;
    else if (fw.error) fwHtml = `<p class="flash err">Could not read firewall: ${esc(fw.error)}</p>`;
    else fwHtml = `<p class="muted">ufw ${fw.active ? "active" : "inactive"} · <b>${fw.denied.length}</b> IP(s) denied${
      fw.mastersBlocked && fw.mastersBlocked.length ? ` · <span style="color:#f87171">${fw.mastersBlocked.length} master IP wrongly blocked!</span>` : ""}</p>`;

    return layout("Admin", `
      <h1>Moderation Panel</h1>
      ${flash ? `<div class="flash ${flash.ok ? "ok" : "err"}">${esc(flash.msg)}</div>` : ""}
      <form class="inline" method="POST" action="/logout" style="float:right">${t}<button class="ghost">Sign out</button></form>

      <h2>Ban / Mute a player</h2>
      <div class="row">
        <div class="card">
          <form method="POST" action="/admin/action">${t}
            <input type="hidden" name="op" value="ban">
            <label>Player name</label><input name="playerid" required placeholder="in-game name">
            <label>Server</label><select name="server">${serverOptions()}</select>
            <label>Duration <span class="muted">(e.g. 2h, 7d — blank = permanent)</span></label>
            <input name="duration" placeholder="permanent">
            <label>Reason</label><input name="reason" placeholder="reason">
            <div style="margin-top:12px"><button class="danger" type="submit">Ban</button></div>
          </form>
        </div>
        <div class="card">
          <form method="POST" action="/admin/action">${t}
            <input type="hidden" name="op" value="mute">
            <label>Player name</label><input name="playerid" required placeholder="in-game name">
            <label>Duration <span class="muted">(e.g. 30m, 2h)</span></label><input name="duration" required placeholder="1h">
            <label>Reason</label><input name="reason" placeholder="reason">
            <div style="margin-top:12px;display:flex;gap:8px">
              <button class="warn" type="submit">Mute</button>
            </div>
          </form>
          <form method="POST" action="/admin/action" style="margin-top:10px">${t}
            <input type="hidden" name="op" value="unmute">
            <label>Unmute player</label>
            <div style="display:flex;gap:8px"><input name="playerid" required placeholder="in-game name">
            <button class="ghost" type="submit" style="white-space:nowrap">Unmute</button></div>
          </form>
        </div>
      </div>

      <h2>Online players</h2>
      <div class="card"><table><thead><tr><th>Player</th><th>Server</th><th></th></tr></thead>
        <tbody>${onlineRows}</tbody></table></div>

      <h2>Active bans (${bans.length})</h2>
      <div class="card"><table><thead><tr><th>Player</th><th>Until</th><th>Reason</th><th>By</th><th></th></tr></thead>
        <tbody>${banRows}</tbody></table></div>

      <h2>Firewall</h2>
      <div class="card">${fwHtml}</div>
    `);
  }

  // ---- moderation action dispatch --------------------------------------------
  // Mirrors the Discord command handlers' side effects. `by` is recorded as a web actor.
  async function runAction(body) {
    const op   = String(body.op || "");
    const name = sanitizeBanName(body.playerid || "");
    const server = VALID_SERVERS.has(body.server) ? body.server : "both";
    const reason = String(body.reason || "").trim().slice(0, 200) || "No reason given";
    const by = "web-admin";

    if (!name && op !== "noop") return { ok: false, msg: "A player name is required." };
    if (name && isMasterName(name)) return { ok: false, msg: `“${name}” is a protected master name and cannot be actioned.` };

    switch (op) {
      case "kick": {
        preserveBalanceAcrossKick(name);
        const target = sanitizeId(name);
        let kicked = false;
        for (const srv of (server === "both" ? ACTIVE_SERVERS : [server])) {
          try { await sendRcon(`Kick ${target}`, srv, 2500, 1); kicked = true; } catch {}
        }
        writeModLog({ action: "kick", playerId: name, reason, by, server });
        logger.info("Web", `kick ${name} on ${server}`);
        return { ok: true, msg: `Kick sent for ${name}${kicked ? "" : " (no RCON confirmation)"}.` };
      }
      case "ban": {
        const durRaw = String(body.duration || "").trim();
        let expires = null, permanent = !durRaw;
        let label = "Permanent";
        if (!permanent) {
          const ms = parseDuration(durRaw);
          if (!ms) return { ok: false, msg: `Invalid duration “${durRaw}” — use e.g. 30m, 2h, 7d, or leave blank for permanent.` };
          expires = Date.now() + ms;
          label = durRaw;
        }
        const ipEnf = await banWithIp(name, server, permanent ? { permanent: true } : {});
        if (ipEnf && ipEnf.master) return { ok: false, msg: `“${name}” is protected and cannot be banned.` };
        enforceBansSweep().catch(() => {});
        if (permanent) {
          await upsertPermBan({ playerId: name, reason, moderator: by, server });
          writeModLog({ action: "permban", playerId: name, reason, by, server });
        } else {
          await upsertTempBan({ playerId: name, reason, expires, durationLabel: label, moderator: by, server });
          writeModLog({ action: "tempban", playerId: name, reason, duration: label, by, server });
        }
        logger.info("Web", `${permanent ? "permban" : "tempban"} ${name} on ${server}`);
        return { ok: true, msg: `Banned ${name} ${permanent ? "permanently" : `for ${label}`} on ${serverLabel(server)}.` };
      }
      case "unban": {
        await addAutobanExempt(name, by);
        await removeBans(name);
        let cleared = {};
        try { cleared = unbanEverywhere(name); } catch {}
        writeModLog({ action: "unban", playerId: name, by, server });
        logger.info("Web", `unban ${name}`);
        return { ok: true, msg: `Unbanned ${name}.` };
      }
      case "mute": {
        const durRaw = String(body.duration || "").trim();
        const ms = parseDuration(durRaw);
        if (!ms) return { ok: false, msg: `Invalid duration “${durRaw}” — use e.g. 30m, 2h.` };
        const expires = Date.now() + ms;
        await setMute(name, { name, expires, reason, moderator: by, at: Date.now() });
        try { gagEverywhere(name); } catch {}
        writeModLog({ action: "mute", playerId: name, reason, duration: durRaw, by });
        logger.info("Web", `mute ${name} for ${durRaw}`);
        return { ok: true, msg: `Muted ${name} for ${durRaw}.` };
      }
      case "unmute": {
        const had = !!getMute(name);
        await clearMute(name);
        if (had) { try { ungagEverywhere(name); } catch {} }
        writeModLog({ action: "unmute", playerId: name, by });
        logger.info("Web", `unmute ${name}`);
        return { ok: true, msg: had ? `Unmuted ${name}.` : `${name} was not muted.` };
      }
      default:
        return { ok: false, msg: "Unknown action." };
    }
  }

  // ---- request router ---------------------------------------------------------
  async function handle(req, res) {
    const u = new URL(req.url, "http://localhost");
    const path = u.pathname;
    const method = req.method || "GET";

    try {
      if (method === "GET" && path === "/") return send(res, 200, await pageDashboard());

      if (method === "GET" && path === "/api/status") {
        const servers = await collectServers();
        const bans = banTotals();
        return send(res, 200, JSON.stringify({
          servers: servers.map(s => ({ label: s.label, online: s.online, count: s.online ? s.count : null })),
          bans, uptimeMs: Date.now() - BOT_START_MS, feed: publicFeed(),
        }), { "Content-Type": "application/json; charset=utf-8" });
      }

      if (method === "GET" && path === "/login") {
        if (getSession(req)) return redirect(res, "/admin");
        return send(res, 200, pageLogin());
      }

      if (method === "POST" && path === "/login") {
        const ip = clientIp(req);
        const lock = attempts.get(ip);
        if (lock && lock.until && lock.until > Date.now())
          return send(res, 429, pageLogin("Too many attempts — try again later."));
        const body = await readBody(req);
        if (passwordOk(body.password)) {
          attempts.delete(ip);
          const { sid } = newSession();
          const secure = /^(1|true|yes|on)$/i.test(process.env.WEB_SECURE_COOKIE || "");
          const cookie = `${COOKIE}=${sid}; HttpOnly; SameSite=Strict; Path=/; Max-Age=${SESSION_TTL_MS / 1000}${secure ? "; Secure" : ""}`;
          logger.info("Web", `admin login OK from ${ip}`);
          return redirect(res, "/admin", cookie);
        }
        const a = attempts.get(ip) || { n: 0 };
        a.n++; if (a.n >= MAX_FAILS) a.until = Date.now() + LOCK_MS;
        attempts.set(ip, a);
        logger.warn("Web", `admin login FAILED from ${ip} (${a.n})`);
        return send(res, 401, pageLogin("Incorrect password."));
      }

      if (method === "POST" && path === "/logout") {
        const sess = getSession(req);
        if (sess && !csrfOk(req, sess, await readBody(req))) return send(res, 403, pageLogin("Session expired. Sign in again."));
        dropSession(req);
        return redirect(res, "/login", `${COOKIE}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0`);
      }

      if (method === "GET" && path === "/admin") {
        const sess = getSession(req);
        if (!sess) return redirect(res, "/login");
        const flash = readFlash(u);
        return send(res, 200, await pageAdmin(sess, flash));
      }

      if (method === "POST" && path === "/admin/action") {
        const sess = getSession(req);
        if (!sess) return redirect(res, "/login");
        const body = await readBody(req);
        if (!csrfOk(req, sess, body)) return send(res, 403, await pageAdmin(sess, { ok: false, msg: "Security check failed (bad CSRF token). Try again." }));
        const result = await runAction(body);
        // Redirect back with a short-lived flash encoded in the URL (avoids resubmit-on-refresh).
        return redirect(res, "/admin?" + new URLSearchParams({ ok: result.ok ? "1" : "0", m: result.msg }).toString());
      }

      return send(res, 404, layout("Not found", `<h1>404</h1><p class="muted">Nothing here.</p>`));
    } catch (err) {
      logger.error("Web", `request error on ${method} ${path}: ${err.message}`);
      try { send(res, 500, layout("Error", `<h1>500</h1><p class="muted">Something went wrong.</p>`)); } catch {}
    }
  }

  function csrfOk(req, sess, body) {
    return !!sess && typeof body.csrf === "string" && body.csrf.length > 0 && body.csrf === sess.csrf;
  }
  function readFlash(u) {
    if (!u.searchParams.has("ok")) return null;
    return { ok: u.searchParams.get("ok") === "1", msg: (u.searchParams.get("m") || "").slice(0, 300) };
  }

  // ---- lifecycle --------------------------------------------------------------
  function start() {
    if (!ENABLED) { logger.info("Web", "dashboard disabled (set WEB_ENABLE=1 to turn it on)"); return null; }
    if (!PASSWORD) logger.warn("Web", "WEB_ADMIN_PASSWORD is not set — the admin panel will be locked out until you set one");
    const server = http.createServer((req, res) => { handle(req, res); });
    server.on("error", (err) => {
      if (err && err.code === "EADDRINUSE") logger.error("Web", `port ${PORT} already in use — dashboard not started`);
      else logger.error("Web", `server error: ${err.message}`);
    });
    server.listen(PORT, HOST, () => {
      logger.info("Web", `dashboard listening on http://${HOST}:${PORT}  (admin panel ${PASSWORD ? "enabled" : "LOCKED — no password"})`);
    });
    return server;
  }

  return { start, _runAction: runAction, _passwordOk: passwordOk };
};
