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

  async function sendRcon(command, server = "server1", timeoutMs = 3000, retries = 2) {
    let lastErr;
    for (let attempt = 0; attempt <= retries; attempt++) {
      try {
        return await sendRconRaw(command, server, timeoutMs);
      } catch (err) {
        lastErr = err;
        if (attempt < retries) {
          const wait = 500 * Math.pow(2, attempt);
          logger.warn("RCON", `Attempt ${attempt + 1} failed for [${server}] "${command}", retrying in ${wait}ms: ${err.message}`);
          await new Promise(r => setTimeout(r, wait));
        }
      }
    }
    logger.error("RCON", `All ${retries + 1} attempts failed for [${server}] "${command}": ${lastErr.message}`);
    throw lastErr;
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

  return { getServerConfig, sendRconRaw, sendRcon, sendRconBoth, wasIssuedByBot };
};
