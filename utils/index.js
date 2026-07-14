/* ---------------- utils: pure, dependency-free helpers ----------------
   Extracted from index.js. These touch no bot state (no db, RCON, Discord,
   or ipBans) — just string/time/crypto transforms — so they're safe to unit
   test and import anywhere. Storage-backed helpers (role checks, command
   blacklist, formatKD) stay in index.js because they reach into shared state. */
const crypto = require("crypto");

// ---- input sanitization ----
function sanitizeId(raw) {
  return String(raw ?? "")
    .replace(/\s*\((?:manual entry|offline)\)/gi, "")   // strip autocomplete display labels
    .replace(/\s*\[(?:s1|s2|s1\+s2)\]/gi, "")
    .trim()
    .replace(/[^a-zA-Z0-9_\-.]/g, "")
    .slice(0, 64);
}
function sanitizeMessage(raw) {
  return String(raw ?? "").replace(/[\r\n\t]/g, " ").replace(/[^\x20-\x7E]/g, "").slice(0, 200);
}

// ---- misc ----
function md5(text) { return crypto.createHash("md5").update(text).digest("hex"); }

function formatTimeLeft(expiresMs) {
  const diff = expiresMs - Date.now();
  if (diff <= 0) return "expired";
  const s = Math.floor(diff / 1000);
  const m = Math.floor(s / 60) % 60;
  const h = Math.floor(s / 3600) % 24;
  const d = Math.floor(s / 86400);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

// "30s" "10m" "2h" "1d" (bare number = minutes) -> ms, or null.
function parseDuration(raw) {
  const m = String(raw ?? "").trim().toLowerCase().match(/^(\d+)\s*(s|m|h|d)?$/);
  if (!m) return null;
  const n = +m[1]; if (!n) return null;
  return n * ({ s: 1000, m: 60000, h: 3600000, d: 86400000 }[m[2] || "m"]);
}

// ---- scheduled map rotation (Eastern time) clock helpers ----
// Current wall clock in America/New_York (Eastern — EST/EDT, DST-aware) as
// { date: "YYYY-MM-DD", hm: "HH:MM" }.
function easternClock(d = new Date()) {
  const p = Object.fromEntries(new Intl.DateTimeFormat("en-CA", {
    timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit",
    hour: "2-digit", minute: "2-digit", hour12: false,
  }).formatToParts(d).map(x => [x.type, x.value]));
  const hh = p.hour === "24" ? "00" : p.hour;   // some ICU builds emit 24 at midnight
  return { date: `${p.year}-${p.month}-${p.day}`, hm: `${hh}:${p.minute}` };
}
// Parse "18:30", "6:30pm", "3pm", "0:00" -> normalized "HH:MM" (24h), or null.
function parseClockTime(raw) {
  const s = String(raw ?? "").trim().toLowerCase().replace(/\s+/g, "");
  const m = s.match(/^(\d{1,2})(?::(\d{2}))?(am|pm)?$/);
  if (!m) return null;
  let hh = +m[1]; const mm = m[2] ? +m[2] : 0; const ap = m[3];
  if (mm > 59) return null;
  if (ap) { if (hh < 1 || hh > 12) return null; if (ap === "pm" && hh !== 12) hh += 12; if (ap === "am" && hh === 12) hh = 0; }
  else if (hh > 23) return null;
  return `${String(hh).padStart(2, "0")}:${String(mm).padStart(2, "0")}`;
}

/* Scrub IPs and long ids from text bound for PUBLIC surfaces (update log).
   IPv4 octet-bounded; IPv6 by colon-count (handles :: compression — over-matches
   clock ranges like 12:30:45, acceptable for a public changelog); 24+ hex chars
   (tokens, EOS ids). Short git hashes, versions, times and URLs pass through. */
function redactPrivateInfo(text) {
  return String(text ?? "")
    .replace(/\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b/g, "[ip redacted]")
    .replace(/(?<![\w:.])[0-9a-f:]{3,}(?![\w:])/gi, (m) => (m.match(/:/g) || []).length >= 2 ? "[ip redacted]" : m)
    .replace(/\b[0-9a-f]{24,}\b/gi, "[id redacted]");
}

module.exports = {
  sanitizeId, sanitizeMessage, md5, formatTimeLeft,
  parseDuration, easternClock, parseClockTime, redactPrivateInfo,
};
