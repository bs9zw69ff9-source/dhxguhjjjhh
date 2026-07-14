/* ---------------- moderation/firewall: OS-level ufw block control ----------------
   Extracted from index.js. Blocks/unblocks IPs at the OS firewall via `sudo ufw`,
   used on bans (opt-in, UFW_BLOCK=1) and by the owner /firewall command. IPs are
   strictly validated (_IPV4_RE) and passed as argv — never through a shell.

   Injected deps:
     logger    - shared structured logger
     loadBans  - typed loader for the active ban list (firewallResyncAll)
     ipBans    - ban-evasion tracker (confirmed IPs per player, for resync)

   Usage:
     const { UFW_BLOCK, _IPV4_RE, firewallBlockIps, firewallUnblockIps,
             firewallResyncAll, firewallField } =
       require("./moderation/firewall")({ logger, loadBans, ipBans });
*/
const { execFile, spawn } = require("child_process");

module.exports = function createFirewall({ logger, loadBans, ipBans }) {
  const UFW_BLOCK = /^(1|true|yes|on)$/i.test(process.env.UFW_BLOCK || "");
  const _IPV4_RE = /^(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)$/;
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
    const valid = [...new Set((ips || []).map(String).filter(ip => _IPV4_RE.test(ip)))];
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
  /* Re-apply firewall blocks for EVERY currently-active ban's confirmed IP(s) at ufw
     position 1 — rebuilds the block list after a restart and heals any stale rules. */
  async function firewallResyncAll() {
    if (!UFW_BLOCK) return { off: true };
    const now = Date.now();
    const active = loadBans().filter(b => b.permanent || !b.expires || b.expires > now);   // perm + active temp
    const ips = new Set();
    for (const b of active) {
      try { for (const ip of (ipBans.getConfirmedIPsForPlayer(b.playerId) || [])) ips.add(ip); } catch {}
    }
    if (!ips.size) { logger.info("Firewall", "Resync: no active-ban IPs on record to block"); return { blocked: 0 }; }
    const r = await firewallBlockIps([...ips]);
    logger.info("Firewall", `Resync: re-applied ${r.blocked}/${ips.size} active-ban IP(s) at ufw position 1`);
    return r;
  }
  // Embed field summarising the ufw firewall action (null when the feature is off).
  function firewallField(fw) {
    if (!fw || fw.off) return null;
    return { name: "Firewall (ufw)", value: fw.blocked
      ? `Blocked **${fw.blocked}** IP${fw.blocked !== 1 ? "s" : ""} — \`ufw deny from <ip>\``
      : "No confirmed IP on record yet — nothing to block.", inline: false };
  }

  return { UFW_BLOCK, _IPV4_RE, firewallBlockIps, firewallUnblockIps, firewallResyncAll, firewallField };
};
