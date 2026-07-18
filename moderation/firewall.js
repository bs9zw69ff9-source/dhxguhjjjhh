/* ---------------- moderation/firewall: OS-level ufw block control ----------------
   Extracted from index.js. Blocks/unblocks IPs at the OS firewall via `sudo ufw`.
   ufw is a MANUAL owner action only — driven exclusively by the /firewall command
   (block/unblock/status). It is never applied automatically on a ban, unban, or on
   startup. IPs are strictly validated (_IPV4_RE) and passed as argv — never a shell.

   Injected deps:
     logger    - shared structured logger
     ipBans    - ban-evasion tracker (flagged IPs, used by /firewall status)
     masterIps - Set of protected IPs that must NEVER be firewall-blocked

   Usage:
     const { UFW_BLOCK, _IPV4_RE, firewallBlockIps, firewallUnblockIps,
             firewallStatus } =
       require("./moderation/firewall")({ logger, ipBans, masterIps });
*/
const { execFile, spawn } = require("child_process");

module.exports = function createFirewall({ logger, ipBans, masterIps }) {
  const UFW_BLOCK = /^(1|true|yes|on)$/i.test(process.env.UFW_BLOCK || "");
  const _IPV4_RE = /^(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)$/;
  // Protected IPs the bot must never deny at the firewall (master/owner IPs).
  const _masterIps = masterIps instanceof Set ? masterIps : new Set(masterIps || []);
  const isMasterIp = (ip) => _masterIps.has(String(ip ?? "").trim());
  function _ufw(args) {
    return new Promise((resolve) => {
      execFile("sudo", ["ufw", ...args], { timeout: 10_000 }, (err, stdout, stderr) => {
        resolve({ ok: !err, out: `${stdout || ""}${stderr || ""}`.trim(), err: err?.message });
      });
    });
  }
  // Same as _ufw but feeds `input` to stdin — for `ufw delete <n>` which prompts "Proceed
  // with operation (y|n)?". We answer "y".
  function _ufwInput(args, input) {
    return new Promise((resolve) => {
      let out = "";
      try {
        const p = spawn("sudo", ["ufw", ...args], { timeout: 10_000 });
        p.stdout.on("data", d => { out += d; });
        p.stderr.on("data", d => { out += d; });
        p.on("error", (err) => resolve({ ok: false, out: (out + err.message).trim() }));
        p.on("close", (code) => resolve({ ok: code === 0, out: out.trim() }));
        try { if (input != null) p.stdin.write(input); p.stdin.end(); } catch {}
      } catch (err) { resolve({ ok: false, out: err.message }); }
    });
  }
  async function firewallBlockIps(ips) {
    if (!UFW_BLOCK) return { blocked: 0, off: true };
    // Master/owner IPs are never denied — filter them out no matter which path asks.
    const skipped = (ips || []).map(String).filter(ip => isMasterIp(ip));
    if (skipped.length) logger.warn("Firewall", `Refused to block protected master IP(s): ${[...new Set(skipped)].join(", ")}`);
    const valid = [...new Set((ips || []).map(String).filter(ip => _IPV4_RE.test(ip) && !isMasterIp(ip)))];
    if (!valid.length) return { blocked: 0 };
    let blocked = 0;
    for (const ip of valid) {
      // Delete any stale rule first, then INSERT at position 1. ufw is first-match-wins and
      // `deny from` appends to the end, so a rule below an `allow <game port>` never fires.
      await _ufw(["delete", "deny", "from", ip]);
      const r = await _ufw(["insert", "1", "deny", "from", ip]);
      if (r.ok || /added|existing|skipping/i.test(r.out)) { blocked++; logger.info("Firewall", `ufw insert 1 deny from ${ip}`); }
      else logger.warn("Firewall", `ufw deny from ${ip} failed: ${r.err || r.out}`);
    }
    if (blocked) { const rl = await _ufw(["reload"]); if (!rl.ok) logger.warn("Firewall", `ufw reload failed: ${rl.err || rl.out}`); }
    return { blocked };
  }
  async function firewallUnblockIps(ips) {
    if (!UFW_BLOCK) return { unblocked: 0, off: true };
    const valid = [...new Set((ips || []).map(String).filter(ip => _IPV4_RE.test(ip)))];
    if (!valid.length) return { unblocked: 0 };
    let unblocked = 0;
    for (const ip of valid) {
      // Locate every rule number whose source is this IP, delete highest-first (deleting a
      // rule renumbers the ones below it), answering the confirmation prompt with `y`.
      const st = await _ufw(["status", "numbered"]);
      const ipRe = new RegExp(`(^|\\s)${ip.replace(/\./g, "\\.")}(\\s|$)`);
      const nums = String(st.out || "").split("\n")
        .map(l => { const m = l.match(/^\[\s*(\d+)\]/); return (m && ipRe.test(l)) ? Number(m[1]) : null; })
        .filter(n => n != null).sort((a, b) => b - a);
      for (const n of nums) {
        const r = await _ufwInput(["delete", String(n)], "y\n");
        if (r.ok) { unblocked++; logger.info("Firewall", `ufw delete ${n} (deny from ${ip})`); }
        else logger.warn("Firewall", `ufw delete ${n} failed: ${r.out}`);
      }
    }
    if (unblocked) { const rl = await _ufw(["reload"]); if (!rl.ok) logger.warn("Firewall", `ufw reload failed: ${rl.err || rl.out}`); }
    return { unblocked };
  }
  // Parse `ufw status numbered` into the set of IPv4 addresses currently DENYed
  // (our block rules are `deny from <ip>`, so the IP is the rule's source).
  function _parseDeniedIps(out) {
    const set = new Set();
    for (const line of String(out || "").split("\n")) {
      if (!/\bDENY\b/i.test(line)) continue;
      const m = line.match(/(?:^|\s)((?:25[0-5]|2[0-4]\d|1?\d?\d)\.(?:25[0-5]|2[0-4]\d|1?\d?\d)\.(?:25[0-5]|2[0-4]\d|1?\d?\d)\.(?:25[0-5]|2[0-4]\d|1?\d?\d))(?:\s|$)/);
      if (m) set.add(m[1]);
    }
    return set;
  }
  /* Owner-facing snapshot of the firewall: every IP currently denied at ufw, cross-
     referenced against ipBans-flagged IPs and the master allowlist. One status read. */
  async function firewallStatus() {
    if (!UFW_BLOCK) return { off: true };
    let st;
    try { st = await _ufw(["status", "numbered"]); }
    catch (e) { return { error: e.message }; }
    const denied  = [..._parseDeniedIps(st.out)].sort();
    let flagged = [];
    try { flagged = (ipBans.blacklist || []).map(String).filter(ip => _IPV4_RE.test(ip)); } catch {}
    const flaggedSet = new Set(flagged);
    return {
      ok: st.ok,
      active:  /Status:\s*active/i.test(st.out || ""),
      denied,                                                   // IPs currently blocked
      mastersBlocked: denied.filter(ip => isMasterIp(ip)),      // should always be empty
      flaggedNotBlocked: flagged.filter(ip => !denied.includes(ip) && !isMasterIp(ip)),  // gaps
      flaggedCount: flagged.length,
      isFlagged: (ip) => flaggedSet.has(ip),
    };
  }
  return { UFW_BLOCK, _IPV4_RE, firewallBlockIps, firewallUnblockIps, firewallStatus };
};
