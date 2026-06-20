#!/usr/bin/env node
/* ================================================================
 * diagnose.js — figure out why IPs aren't being logged.
 *
 * Run it on the SAME machine/user as the bot:
 *     node diagnose.js
 *     node diagnose.js /home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log
 *
 * It checks the log path, shows raw sample lines, tells you which of the
 * parser's regexes match them, then runs the real ipBans backfill and prints
 * the resulting IP registry. Paste the whole output back if it's still empty.
 * ================================================================ */

const fs   = require("fs");
const path = require("path");

// same defaults/resolution as ipBans.js
const DEFAULT = "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log";
const args = process.argv.slice(2);
const files = args.length
  ? args
  : (process.env.PAVLOV_LOGS
      ? process.env.PAVLOV_LOGS.split(/[,:]/).map(s => s.trim()).filter(Boolean)
      : [DEFAULT]);

// the exact regexes ipBans.js uses
const ACCEPT_RE = /(?:NotifyAcceptingConnection accepted from:|NotifyAcceptedConnection:.*?RemoteAddr:|AddClientConnection:.*?RemoteAddr:)\s*((?:\d{1,3}\.){3}\d{1,3})/;
const LOGIN_RE  = /Login request:\s*\?Name=([^?]+).*?userId:\s*(\S+)/i;
const CLOSE_RE  = /RemoteAddr:\s*((?:\d{1,3}\.){3}\d{1,3}):\d+.*?UniqueId:\s*([^\s,]+)/;
const BAN_RE    = /Rcon:\s*BanPlayer\s+(\S+)/i;

const line = "─".repeat(64);
function readTail(f, maxBytes = 2 * 1024 * 1024) {
  const st = fs.statSync(f);
  const from = Math.max(0, st.size - maxBytes);
  const fd = fs.openSync(f, "r");
  const buf = Buffer.alloc(st.size - from);
  fs.readSync(fd, buf, 0, buf.length, from);
  fs.closeSync(fd);
  return buf.toString("utf8").split(/\r?\n/);
}

console.log(line);
console.log("ipBans diagnostic");
console.log("running as uid", process.getuid ? process.getuid() : "?", "  PAVLOV_LOGS=", process.env.PAVLOV_LOGS || "(unset -> using default)");
console.log(line);

for (const f of files) {
  console.log(`\n### LOG: ${f}`);
  let st;
  try { st = fs.statSync(f); }
  catch (e) { console.log(`  ✗ CANNOT STAT: ${e.code || e.message}  -> wrong path, or not readable by this user`); continue; }
  console.log(`  ✓ exists, ${(st.size / 1048576).toFixed(2)} MB, modified ${st.mtime.toISOString()}`);
  try { fs.accessSync(f, fs.constants.R_OK); console.log("  ✓ readable by this process"); }
  catch { console.log("  ✗ NOT READABLE by this process — fix permissions or run as the owner"); continue; }

  // sibling logs (what the rotation backfill will also read)
  try {
    const sibs = fs.readdirSync(path.dirname(f)).filter(n => /^Pavlov.*\.log$/i.test(n));
    console.log(`  logs in this folder: ${sibs.join(", ") || "(none?!)"}`);
  } catch {}

  const lines = readTail(f);
  let nAccept = 0, nLogin = 0, nClose = 0, nBan = 0;
  const sampleLogin = [], sampleClose = [];
  for (const l of lines) {
    if (ACCEPT_RE.test(l)) nAccept++;
    if (LOGIN_RE.test(l)) { nLogin++; if (sampleLogin.length < 3) sampleLogin.push(l); }
    else if (/Login request/i.test(l) && sampleLogin.length < 3) sampleLogin.push("UNMATCHED> " + l);
    if (CLOSE_RE.test(l)) { nClose++; if (sampleClose.length < 3) sampleClose.push(l); }
    if (BAN_RE.test(l)) nBan++;
  }
  console.log(`  matches in last ${lines.length} lines:  accept(IP)=${nAccept}  login(name+id)=${nLogin}  close(IP+id)=${nClose}  ban=${nBan}`);
  if (sampleLogin.length) { console.log("  -- sample Login request lines --"); sampleLogin.forEach(s => console.log("    " + s.slice(0, 200))); }
  if (sampleClose.length) { console.log("  -- sample close (IP+UniqueId) lines --"); sampleClose.forEach(s => console.log("    " + s.slice(0, 200))); }
  if (!nLogin && !nClose) console.log("  ⚠ NO join/disconnect lines matched — either this is the wrong (empty) log after a restart, or the format differs. Paste the sample lines above.");
}

// now run the REAL parser end-to-end
console.log("\n" + line);
console.log("Running the real ipBans backfill against these logs…");
console.log(line);
const ipBans = require(path.join(__dirname, "ipBans"));
clearInterval(ipBans.init({ logFiles: files, pollMs: 9e8, onConnect: async () => {}, onAutoBan: async () => {} }));

const reg = ipBans.registry;
const ids = Object.keys(reg);
const withIp = ids.filter(id => (reg[id].ips || []).length);
console.log(`\nregistry: ${ids.length} players, ${withIp.length} with at least one IP`);
withIp.slice(0, 25).forEach(id => console.log(`  ${reg[id].name || "?"}  [${id}]  ->  ${reg[id].ips.join(", ")}`));
if (!withIp.length) console.log("  (no IPs captured — see the per-log section above for why)");
console.log("\n" + line);
console.log(withIp.length ? "RESULT: IPs ARE being captured. If /inspect shows none, the bot process is reading a different path than this — compare PAVLOV_LOGS." : "RESULT: no IPs captured from this log. Paste this entire output back.");
console.log(line);
