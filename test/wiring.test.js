const { test } = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");

/* Guards the ctx-injection contract of the module split. Every factory module
   `const { … } = ctx`s its dependencies AND may reference more via `...spread`
   (which an identifier scanner with a `.`-lookbehind silently drops — the bug
   that crashed definitions.js on PUNISH_CHOICES). This test proves, statically,
   that every name a module needs is actually provided by the ctx literal that
   feeds it in index.js — no boot or Discord connection required. */

const ROOT = path.join(__dirname, "..");
const read = (f) => fs.readFileSync(path.join(ROOT, f), "utf8");
const decomment = (c) => c.replace(/\/\/[^\n]*/g, "").replace(/\/\*[\s\S]*?\*\//g, "");

// module-scope bindings declared in index.js (col-0 decls + multi-line destructures)
function indexBindings() {
  const lines = read("index.js").split("\n");
  const s = new Set();
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    let m = l.match(/^(?:async function|function|const|let|var)\s+([A-Za-z_$][\w$]*)/);
    if (m) { s.add(m[1]); continue; }
    if (/^const\s*\{/.test(l)) {
      let j = i, buf = "";
      while (j < lines.length) { buf += lines[j] + "\n"; if (/\}\s*=/.test(lines[j])) break; j++; }
      const inner = buf.slice(buf.indexOf("{") + 1, buf.lastIndexOf("}"));
      for (const p of inner.split(",")) { const k = p.trim().split(":").pop().trim(); if (/^[A-Za-z_$][\w$]*$/.test(k)) s.add(k); }
    }
  }
  return s;
}
function topNames(inner) {
  const out = []; let d = 0, cur = "";
  for (const ch of inner) { if ("{([".includes(ch)) d++; else if ("})]".includes(ch)) d--; if (ch === "," && d === 0) { out.push(cur); cur = ""; } else cur += ch; }
  if (cur.trim()) out.push(cur);
  return out.map(e => e.trim().split(":")[0].trim()).filter(k => /^[A-Za-z_$][\w$]*$/.test(k));
}
// the object literal passed to require("<rp>")({ … }) in index.js
function providedFor(rp) {
  const idx = decomment(read("index.js"));
  const call = `require("${rp}")(`;
  const ci = idx.indexOf(call);
  if (ci < 0) return null;
  const ob = idx.indexOf("{", ci + call.length - 1);
  let d = 0, je = -1;
  for (let p = ob; p < idx.length; p++) { const c = idx[p]; if (c === "{") d++; else if (c === "}") { d--; if (!d) { je = p; break; } } }
  return new Set(topNames(idx.slice(ob + 1, je)));
}

// modules wired directly from index.js: [file, requirePath]
const DIRECT = [
  ["commands/index.js", "./commands"], ["commands/definitions.js", "./commands/definitions"],
  ["events/index.js", "./events"], ["leaderboards/index.js", "./leaderboards"],
  ["moderation/bans.js", "./moderation/bans"], ["moderation/vpn.js", "./moderation/vpn"],
  ["moderation/firewall.js", "./moderation/firewall"], ["casino/ledger.js", "./casino/ledger"],
  ["factions/files.js", "./factions/files"], ["factions/whitelist.js", "./factions/whitelist"],
];
// domain modules receive the SAME ctx that index.js passes to ./commands
const DOMAIN = ["commands/info.js", "commands/moderation.js", "commands/admin.js",
  "commands/factions.js", "commands/economy.js", "commands/casino.js"];

function moduleDestructure(file) {
  const m = decomment(read(file)).match(/const \{([\s\S]*?)\} = ctx;/);
  return m ? topNames(m[1]) : [];
}
function moduleSpreads(file) {
  const body = decomment(read(file));
  const s = new Set(); let m; const re = /\.\.\.([A-Za-z_$][\w$]*)/g;
  while ((m = re.exec(body))) s.add(m[1]);
  return [...s];
}

test("every module's destructured ctx deps are provided", () => {
  for (const [file, rp] of DIRECT) {
    const provided = providedFor(rp);
    assert.ok(provided, `no require("${rp}")({…}) found in index.js`);
    const missing = moduleDestructure(file).filter(n => !provided.has(n));
    assert.deepEqual(missing, [], `${file} destructures unprovided: ${missing.join(", ")}`);
  }
  const cmdProvided = providedFor("./commands");
  for (const file of DOMAIN) {
    const missing = moduleDestructure(file).filter(n => !cmdProvided.has(n));
    assert.deepEqual(missing, [], `${file} destructures unprovided (commands ctx): ${missing.join(", ")}`);
  }
});

test("every ...spread of an index.js binding is injected (the PUNISH_CHOICES bug)", () => {
  const idxB = indexBindings();
  const check = (file, provided) => {
    const inj = new Set(moduleDestructure(file));
    const local = new Set(["ctx", "interaction"]);
    for (const re of [/\b(?:const|let|var)\s+([A-Za-z_$][\w$]*)/g, /\bfunction\s+([A-Za-z_$][\w$]*)/g]) {
      let m; const body = decomment(read(file)); while ((m = re.exec(body))) local.add(m[1]);
    }
    const missing = moduleSpreads(file).filter(n => idxB.has(n) && !inj.has(n) && !local.has(n));
    assert.deepEqual(missing, [], `${file} spreads uninjected index.js binding(s): ${missing.join(", ")}`);
    if (provided) for (const n of moduleSpreads(file)) if (inj.has(n)) assert.ok(provided.has(n), `${file}: spread ${n} injected but not provided`);
  };
  for (const [file, rp] of DIRECT) check(file, providedFor(rp));
  const cmdProvided = providedFor("./commands");
  for (const file of DOMAIN) check(file, cmdProvided);
});
