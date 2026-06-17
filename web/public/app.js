/* Copyright (c) 2026 bs9zw69ff9-source. Proprietary — see LICENSE. */
const $ = (s, el = document) => el.querySelector(s);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const api = (path) => fetch(path).then(r => r.json().then(j => ({ status: r.status, j })));
const fmtCaps = (n) => (n == null ? "—" : n.toLocaleString() + " caps");
const ago = (ms) => { if (!ms) return "never"; const s = Math.floor((Date.now() - ms) / 1000); for (const [k, v] of [["d", 86400], ["h", 3600], ["m", 60]]) if (s >= v) return Math.floor(s / v) + k + " ago"; return s + "s ago"; };
function fmtPlaytime(min) { if (min == null) return "—"; const h = Math.floor(min / 60), m = min % 60; if (h < 24) return `${h}h ${m}m`; const d = Math.floor(h / 24); return `${d}d ${h % 24}h`; }
const meter = (v, max) => `<div class="meter"><i style="width:${Math.round((max > 0 ? Math.min(1, v / max) : 0) * 100)}%"></i></div>`;

function mountNav(active) {
  const tabs = [
    { id: "servers", href: "/", label: "Servers" },
    { id: "leaderboard", href: "/leaderboard", label: "Leaderboard" },
    { id: "factions", href: "/factions", label: "Factions" },
    { id: "lookup", href: "/lookup", label: "Courier Lookup" },
  ];
  $("#nav").innerHTML = `
    <div class="inner">
      <a class="brand" href="/"><span class="dot"></span> MOJAVE AUTHORITY</a>
      <div class="tabs">${tabs.map(t => `<a class="tab ${t.id === active ? "active" : ""}" href="${t.href}">${t.label}</a>`).join("")}</div>
      <div class="spacer"></div><span class="who">New Vegas RP · public terminal</span>
    </div>`;
}

/* ---------------- SERVERS ---------------- */
async function renderServers() {
  const el = $("#view"); el.innerHTML = `<div class="loading">▣ Pinging the Mojave…</div>`;
  const { j } = await api("/api/servers");
  const servers = j.servers || [];
  el.innerHTML = `
    <div class="hero"><h1>ACTIVE SERVERS</h1><p>Live status across the wasteland · ${j.totalOnline ?? 0} couriers online</p></div>
    <div class="grid cols-2">${servers.map(serverCard).join("") || `<div class="empty">No servers configured.</div>`}</div>`;
}
function serverCard(s) {
  const cap = Number(s.maxPlayers) || Math.max(s.players.length, 1);
  return `<div class="card server">
    <div class="row between">
      <div class="title">${esc(s.name)}</div>
      <span class="pill ${s.online ? "online" : "offline"}"><span class="led"></span>${s.online ? "ONLINE" : "OFFLINE"}</span>
    </div>
    <div class="row" style="gap:26px">
      <div class="stat"><span class="n">${s.players.length}${s.maxPlayers ? `/${s.maxPlayers}` : ""}</span><span class="l">Players</span></div>
      <div class="stat"><span class="n" style="font-size:16px">${esc(s.map)}</span><span class="l">Map</span></div>
      <div class="stat"><span class="n" style="font-size:16px">${esc(s.mode)}</span><span class="l">Mode</span></div>
    </div>
    ${meter(s.players.length, cap)}
    <div>
      <div class="l muted" style="font-size:11px;text-transform:uppercase;letter-spacing:1.5px;margin-bottom:6px">Couriers</div>
      <div class="chips">${s.players.map(p => `<span class="chip">${esc(p)}</span>`).join("") || `<span class="muted">— empty —</span>`}</div>
    </div>
  </div>`;
}

/* ---------------- LEADERBOARD ---------------- */
async function renderLeaderboard() {
  const el = $("#view"); el.innerHTML = `<div class="loading">▣ Counting caps…</div>`;
  const { j } = await api("/api/leaderboard?limit=50");
  if (!j.configured) { el.innerHTML = `<div class="hero"><h1>CAPS LEADERBOARD</h1></div><div class="empty">The caps ledger isn't configured on this deployment.</div>`; return; }
  const lb = j.leaderboard || [];
  const top = lb[0]?.balance || 1;
  el.innerHTML = `
    <div class="hero"><h1>CAPS LEADERBOARD</h1><p>The richest couriers in New Vegas</p></div>
    <div class="card">${lb.length ? `<table>
      <thead><tr><th>#</th><th>Courier</th><th>Balance</th><th></th></tr></thead>
      <tbody>${lb.map((e, i) => `<tr>
        <td>${i === 0 ? "🥇" : i === 1 ? "🥈" : i === 2 ? "🥉" : `<span class="muted">#${i + 1}</span>`}</td>
        <td><code>${esc(e.id)}</code></td>
        <td><b style="color:var(--amber)">${e.balance.toLocaleString()}</b></td>
        <td style="width:160px">${meter(e.balance, top)}</td>
      </tr>`).join("")}</tbody></table>` : `<div class="empty">No cap records yet.</div>`}</div>`;
}

/* ---------------- FACTIONS (public) ---------------- */
async function renderFactions() {
  const el = $("#view"); el.innerHTML = `<div class="loading">▣ Reading faction rolls…</div>`;
  const { j } = await api("/api/factions");
  el.innerHTML = `
    <div class="hero"><h1>FACTIONS</h1><p>Rosters, ranks & capacity across every faction</p></div>
    <div class="grid cols-2">${(j.factions || []).map(factionCard).join("")}</div>`;
}
function factionCard(f) {
  if (f.unreadable) return `<div class="card"><h3>⚔️ ${esc(f.name)}</h3><div class="empty">Roster unavailable.</div></div>`;
  const over = f.count > f.cap;
  const rankRows = f.order.slice().reverse().map(r => {
    const n = f.members.filter(m => m.rank === r).length;
    const rc = f.rankCaps?.[r];
    if (!n && !rc) return "";
    return `<tr><td><span class="badge-rank">${f.badge[r] || "❓"} ${esc(r)}</span></td><td>${rc ? `<span class="${n > rc ? "over" : ""}">${n}/${rc}</span>` : n}</td></tr>`;
  }).join("");
  return `<div class="card">
    <div class="row between"><h3 style="margin:0">⚔️ ${esc(f.name)}</h3>
      <span class="pill ${over ? "offline" : "online"}"><span class="led"></span>${f.count}/${f.cap}${over ? " OVER" : ""}</span></div>
    ${meter(f.count, f.cap)}
    <div class="grid" style="grid-template-columns:1fr 1fr;gap:16px;margin-top:12px">
      <div><div class="l muted" style="font-size:11px;text-transform:uppercase;letter-spacing:1.5px;margin-bottom:6px">By rank</div>
        <table>${rankRows || `<tr><td class="muted">no members</td></tr>`}</table></div>
      <div><div class="l muted" style="font-size:11px;text-transform:uppercase;letter-spacing:1.5px;margin-bottom:6px">Roster</div>
        <div style="max-height:260px;overflow:auto"><table>${f.members.slice(0, 200).map(m => `<tr><td>${m.badge}</td><td><code>${esc(m.id)}</code></td><td><span class="tag">${esc(m.rank)}</span></td></tr>`).join("") || `<tr><td class="muted">empty</td></tr>`}</table></div></div>
    </div>
  </div>`;
}

/* ---------------- COURIER LOOKUP ---------------- */
function renderLookup() {
  const el = $("#view");
  el.innerHTML = `
    <div class="hero"><h1>COURIER LOOKUP</h1><p>Search any courier's public profile</p></div>
    <div class="card">
      <form id="lf" class="row" style="gap:10px">
        <input id="lq" placeholder="Courier ID or username…" autocomplete="off"
          style="flex:1;background:rgba(0,0,0,.35);border:1px solid var(--panel-line);color:var(--ink);padding:11px 14px;border-radius:10px;font-family:var(--mono)"/>
        <button class="btn" type="submit">🔍 Search</button>
      </form>
    </div>
    <div id="result" style="margin-top:18px"></div>`;
  const params = new URLSearchParams(location.search);
  if (params.get("id")) { $("#lq").value = params.get("id"); doLookup(params.get("id")); }
  $("#lf").addEventListener("submit", (e) => { e.preventDefault(); const v = $("#lq").value.trim(); if (v) { history.replaceState(null, "", `/lookup?id=${encodeURIComponent(v)}`); doLookup(v); } });
}
async function doLookup(id) {
  const r = $("#result"); r.innerHTML = `<div class="loading">▣ Pulling dossier…</div>`;
  const { status, j } = await api(`/api/player?id=${encodeURIComponent(id)}`);
  if (status !== 200) { r.innerHTML = `<div class="empty">${esc(j.error || "Lookup failed.")}</div>`; return; }
  const statusStr = j.online ? `🟢 Online · ${j.onServers.join(" + ")}` : (j.lastSeen ? `🔴 Offline · last seen ${ago(j.lastSeen)}` : "🔴 Offline");
  const factions = j.factions.length
    ? j.factions.map(f => `<div class="row" style="gap:8px"><span>${f.badge}</span><b>${esc(f.faction)}</b><span class="tag">${esc(f.rank)}</span></div>`).join("")
    : `<span class="muted">No faction membership</span>`;
  r.innerHTML = `
    <div class="grid cols-3" style="margin-bottom:18px">
      <div class="card"><div class="stat"><span class="n" style="font-size:18px">${esc(j.id)}</span><span class="l">Courier</span></div>
        <div style="margin-top:8px">${statusStr}${j.donator ? `  ·  <span class="tag green">💎 Donator</span>` : ""}</div></div>
      <div class="card"><div class="stat"><span class="n">${fmtPlaytime(j.playtimeMinutes)}</span><span class="l">Playtime</span></div></div>
      <div class="card"><div class="stat"><span class="n">${j.balance == null ? "—" : j.balance.toLocaleString()}</span><span class="l">Caps</span></div></div>
    </div>
    <div class="card"><h3>⚔️ Faction Membership</h3><div class="grid" style="gap:8px">${factions}</div></div>`;
}
