/* ---------------- rcon: Pavlov RCON transport ----------------
   Extracted from index.js. Speaks Pavlov's line-based RCON over a raw TCP
   socket (md5-authenticated). No Discord/db/ipBans coupling — it only needs a
   logger and the live ACTIVE_SERVERS list, both injected below, so the
   transport can be reasoned about (and eventually tested) on its own.

   Usage:
     const { sendRcon, sendRconBoth, getServerConfig } =
       require("./rcon")({ logger, activeServers });
*/
const net = require("net");
const { md5 } = require("../utils");

module.exports = function createRcon({ logger, activeServers }) {
  function getServerConfig(server) {
    if (server === "server2") return {
      host: process.env.RCON_HOST_2, port: Number(process.env.RCON_PORT_2), password: process.env.RCON_PASSWORD_2,
    };
    if (server === "server3") return {
      host: process.env.RCON_HOST_3, port: Number(process.env.RCON_PORT_3), password: process.env.RCON_PASSWORD_3,
    };
    return {
      host: process.env.RCON_HOST_1, port: Number(process.env.RCON_PORT_1), password: process.env.RCON_PASSWORD_1,
    };
  }

  /* Every RCON command this bot issues, remembered briefly. Pavlov logs ALL RCON
     traffic to Pavlov.log, including the bot's own, so the log watcher needs a way to
     tell "the bot just did this" from "somebody ran this with another tool". Keyed on
     the normalised command text and expired after a couple of minutes. */
  const _issued = new Map();                 // normalised command -> ts issued
  const ISSUED_TTL_MS = 120_000;
  const _normCmd = (c) => String(c ?? "").trim().replace(/\s+/g, " ").toLowerCase();
  function noteIssued(command) {
    const now = Date.now();
    if (_issued.size > 200) for (const [k, t] of _issued) if (now - t > ISSUED_TTL_MS) _issued.delete(k);
    _issued.set(_normCmd(command), now);
  }
  /* True when the bot sent this exact command recently. Deliberately does NOT consume
     the entry: one /givemenu fans out to every server and so appears in the log more
     than once. The cost is that a genuine external repeat of the same command inside
     the window is attributed to the bot - quieter, and this is an alerting aid, not an
     access control. */
  function wasIssuedByBot(command) {
    const k = _normCmd(command);
    const t = _issued.get(k);
    if (t === undefined) return false;
    if (Date.now() - t > ISSUED_TTL_MS) { _issued.delete(k); return false; }
    return true;
  }

  function sendRconRaw(command, server = "server1", timeoutMs = 3000) {
    const { host, port, password } = getServerConfig(server);
    return new Promise((resolve, reject) => {
      const socket = new net.Socket();
      let response = "", authenticated = false, settled = false;
      // Guard so the promise settles exactly once and the fallback timer is
      // always cleared - otherwise every call leaked a live timer for `timeoutMs`.
      let fallbackTimer = null;
      const cleanup = () => { try { socket.destroy(); } catch {} };
      const finish = (fn, value) => {
        if (settled) return;
        settled = true;
        if (fallbackTimer) clearTimeout(fallbackTimer);
        cleanup();
        fn(value);
      };

      noteIssued(command);          // record before sending, so the log line can never beat us
      socket.setTimeout(timeoutMs);
      socket.connect(port, host);

      socket.on("data", (data) => {
        const text = data.toString();
        if (text.includes("Password:")) { socket.write(md5(password)); return; }
        if (text.includes("Authenticated=1") && !authenticated) {
          authenticated = true;
          socket.write(command + "\n");
          return;
        }
        response += text;
        /* Settle as soon as the payload is a complete JSON document instead of waiting
           for the peer to close. Pavlov's replies are a single JSON object, and a
           truncated one does not parse - so this can only ever fire on a whole
           response, never on a partial chunk. If a command replies with something that
           is not JSON, the close/timeout path below still handles it exactly as before. */
        if (response.length > 1 && (response.charCodeAt(0) === 123 || response.charCodeAt(0) === 91)) {
          try { JSON.parse(response); finish(resolve, response); } catch { /* not complete yet */ }
        }
      });
      // A socket that times out / closes BEFORE authenticating never ran the command -
      // that's a failure (wrong password, dead host), not an empty response. Resolving
      // it would make sendRconBoth report ok for a server that silently did nothing.
      const settle = () => authenticated
        ? finish(resolve, response)
        : finish(reject, new Error(`no RCON auth from ${host}:${port}`));
      socket.on("timeout", settle);
      socket.on("error",   (err) => finish(reject, err));
      socket.on("close",   settle);
      fallbackTimer = setTimeout(settle, timeoutMs);
    });
  }

  /* ---------------- read coalescing ----------------
     Every RCON command costs a fresh TCP connect + MD5 auth handshake, and Pavlov
     services RCON on the game thread - so each one is work taken away from the tick.
     Several independent timers ask for the SAME read seconds apart (the player list,
     the dashboard, the leaderboards, the ban sweep and the health check all want
     RefreshList), which showed up in the server log as 2-3 "User authenticated" +
     RefreshList pairs every 30s per server.

     Idempotent reads are therefore coalesced two ways:
       - concurrent callers share one in-flight request rather than opening N sockets
       - a completed read is reused for RCON_READ_CACHE_MS (default 2.5s)

     Only read-only commands qualify; anything that changes state always goes to the
     server. The window is far shorter than the 30s refresh cycles that consume this
     data, so nothing observable gets staler. Set RCON_READ_CACHE_MS=0 to disable. */
  const READ_ONLY_RCON     = /^(?:RefreshList|ServerInfo|ItemList|MapList)$/i;
  const READ_CACHE_TTL_MS  = Math.max(0, Number(process.env.RCON_READ_CACHE_MS ?? 2500));
  const _readCache    = new Map();   // "server|command" -> { ts, value }
  const _readInflight = new Map();   // "server|command" -> Promise
  const _readKey = (command, server) => `${server}|${String(command).trim().toLowerCase()}`;
  const isCacheableRead = (command) => READ_CACHE_TTL_MS > 0 && READ_ONLY_RCON.test(String(command).trim());

  async function sendRconOnce(command, server, timeoutMs, retries) {
    let lastErr;
    for (let attempt = 0; attempt <= retries; attempt++) {
      try {
        return await sendRconRaw(command, server, timeoutMs);
      } catch (err) {
        lastErr = err;
        if (attempt < retries) {
          /* Exponential backoff with jitter. A fixed schedule makes every caller retry
             in lockstep after a blip, so the server gets a synchronised burst exactly
             when it is least able to serve one. Full jitter spreads them out. */
          const ceiling = 500 * Math.pow(2, attempt);
          const wait    = Math.round(ceiling * (0.5 + Math.random() * 0.5));   // 50-100% of the ceiling
          logger.warn("RCON", `Attempt ${attempt + 1} failed for [${server}] "${command}", retrying in ${wait}ms: ${err.message}`);
          await new Promise(r => setTimeout(r, wait));
        }
      }
    }
    logger.error("RCON", `All ${retries + 1} attempts failed for [${server}] "${command}": ${lastErr.message}`);
    throw lastErr;
  }

  async function sendRcon(command, server = "server1", timeoutMs = 3000, retries = 2) {
    if (!isCacheableRead(command)) {
      /* Anything that is not a pure read may change who is on the server or what it
         reports (Kick, Ban, SwitchMap, GiveMenu...), so drop that server's cached reads
         once it lands. Without this a kick could be followed by a roster up to
         RCON_READ_CACHE_MS old that still lists the player. */
      try { return await sendRconOnce(command, server, timeoutMs, retries); }
      finally { invalidateRconReads(server); }
    }
    const key = _readKey(command, server);
    const hit = _readCache.get(key);
    if (hit && Date.now() - hit.ts < READ_CACHE_TTL_MS) return hit.value;
    const flying = _readInflight.get(key);
    if (flying) return flying;                    // a caller is already fetching this exact read
    /* The FIRST caller's timeout and retry count govern the shared request. They only
       differ between callers by a second or so, and every one of them wants the same
       bytes back. A failure is never cached - only a success. */
    const p = sendRconOnce(command, server, timeoutMs, retries)
      .then((value) => { _readCache.set(key, { ts: Date.now(), value }); return value; })
      .finally(() => { _readInflight.delete(key); });
    _readInflight.set(key, p);
    return p;
  }
  /* Drop cached reads for a server (or all of them). Called after a command that
     changes who is on the server, so the next read reflects it immediately. */
  function invalidateRconReads(server = null) {
    if (!server) return _readCache.clear();
    for (const k of _readCache.keys()) if (k.startsWith(`${server}|`)) _readCache.delete(k);
  }

  async function sendRconBoth(command, server) {
    // Interactive commands use this - keep it snappy (2.5s, 1 retry) so a slow/down
    // server can't make a slash command spin for ~10s ("infinite load"). allSettled:
    // a failure on one server must not abort/mask the command on the other.
    const T = 2500, R = 1;
    if (server === "both") {   // "both" = every active server (2 or 3 of them)
      const results = await Promise.allSettled(activeServers.map(s => sendRcon(command, s, T, R)));
      const out = { s1: null, s2: null, s3: null, ok1: false, ok2: false, ok3: false };
      activeServers.forEach((s, i) => {
        const n = s.replace("server", "");
        if (results[i].status === "fulfilled") { out[`s${n}`] = results[i].value; out[`ok${n}`] = true; }
        else logger.warn("RCON", `[${s}] "${command}" failed: ${results[i].reason?.message || results[i].reason}`);
      });
      return out;
    }
    try {
      const v = await sendRcon(command, server, T, R);
      const n = String(server).replace("server", "");
      return { s1: null, s2: null, s3: null, ok1: false, ok2: false, ok3: false, [`s${n}`]: v, [`ok${n}`]: true };
    }
    catch (err) { logger.warn("RCON", `[${server}] "${command}" failed: ${err.message}`); return { s1: null, s2: null, s3: null, ok1: false, ok2: false, ok3: false }; }
  }

  return { getServerConfig, sendRconRaw, sendRcon, sendRconBoth, wasIssuedByBot, invalidateRconReads };
};
