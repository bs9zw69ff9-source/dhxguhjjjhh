/* ---------------- moderation/evasion: ban-evasion correlation (pure) ----------------
   Given who just joined and what the bot already knows, decide whether they look like
   a returning banned player. Every signal is scored independently and the strongest
   ones are reported, so a moderator can see WHY a match fired rather than a bare
   "suspicious" verdict.

   Signals, strongest first:
     eos        - the joining EOS account id is itself banned/flagged. Definitive.
     ip         - the join IP is one a banned account has connected from. Very strong.
     username   - the joining name matches a banned name (or a known alias). Strong.
     asn+vpn    - same network AND both on a VPN/proxy. Weak alone, meaningful together.
     provider   - same VPN brand as a banned account (e.g. both NordVPN).
     asn        - same autonomous system. Weakest: whole ISPs share an ASN, so this
                  NEVER stands on its own - it only adds confidence to another hit.

   Deliberately pure: all state is passed in, so this unit-tests without Discord, the
   filesystem or any network. The caller decides what to do with the verdict - this
   module never bans anybody. */

const norm = (s) => String(s ?? "").trim().toLowerCase();
const uniq = (a) => [...new Set(a.filter(Boolean))];

/* Weights are additive. A single weak signal cannot reach the threshold on its own;
   `definitive` signals short-circuit to a certain match. */
const WEIGHTS = { eos: 100, ip: 70, username: 50, provider: 25, asnVpn: 20, asn: 10 };
const MATCH_THRESHOLD = 50;          // score at or above this is reported to moderators

/**
 * @param join  { name, eosId, ip, asn, provider, hosting, vpn, altNames[] }
 * @param bans  [ { playerId, reason, moderator, permanent, expires, network? } ]
 *              where network = { asn, provider, hosting, vpn, ips[], names[] }
 * @param opts  { flaggedIds:Set, flaggedIps:Set, flaggedNames:Set }
 */
function findEvasion(join = {}, bans = [], opts = {}) {
  const flaggedIds   = opts.flaggedIds   ?? new Set();
  const flaggedIps   = opts.flaggedIps   ?? new Set();
  const flaggedNames = opts.flaggedNames ?? new Set();

  const joinName = norm(join.name);
  const joinEos  = norm(join.eosId);
  const joinIp   = String(join.ip ?? "").trim();
  const joinAsn  = norm(join.asn);
  const joinProv = norm(join.provider);
  const joinAlts = (join.altNames ?? []).map(norm).filter(Boolean);

  const matches = [];

  for (const b of bans) {
    if (!b || typeof b.playerId !== "string") continue;      // skip corrupt rows
    const net    = b.network ?? {};
    const banned = norm(b.playerId);
    const reasons = [];
    let score = 0, definitive = false;

    // -- EOS account: the same account returning under any name.
    const banEosIds = uniq([net.eosId, ...(net.eosIds ?? [])]).map(norm);
    if (joinEos && banEosIds.includes(joinEos)) {
      score += WEIGHTS.eos; definitive = true;
      reasons.push({ kind: "eos", detail: `same EOS account (${join.eosId})`, weight: WEIGHTS.eos });
    }

    // -- IP history: the join IP appears in this ban's recorded addresses.
    const banIps = uniq(net.ips ?? []);
    if (joinIp && banIps.includes(joinIp)) {
      score += WEIGHTS.ip;
      reasons.push({ kind: "ip", detail: `connected from a banned IP (${joinIp})`, weight: WEIGHTS.ip });
    }

    // -- Username: exact, or one of their known shared-IP aliases.
    const banNames = uniq([b.playerId, ...(net.names ?? [])]).map(norm);
    if (joinName && banNames.includes(joinName)) {
      score += WEIGHTS.username;
      reasons.push({ kind: "username", detail: `username matches a banned player (${b.playerId})`, weight: WEIGHTS.username });
    } else if (joinAlts.some(a => banNames.includes(a))) {
      const hit = joinAlts.find(a => banNames.includes(a));
      score += WEIGHTS.username;
      reasons.push({ kind: "username", detail: `known alias of a banned player (${hit})`, weight: WEIGHTS.username });
    }

    // -- VPN provider: same consumer brand (NordVPN, Mullvad...).
    if (joinProv && norm(net.provider) && joinProv === norm(net.provider)) {
      score += WEIGHTS.provider;
      reasons.push({ kind: "provider", detail: `same VPN provider (${join.provider})`, weight: WEIGHTS.provider });
    }

    /* -- ASN: on its own this is near-worthless (an ASN can be a whole national ISP),
          so it only ever ADDS confidence. It scores higher when both sides are also
          on a VPN/hosting network, because a shared *anonymising* ASN is far more
          suspicious than a shared consumer ISP. */
    if (joinAsn && norm(net.asn) && joinAsn === norm(net.asn)) {
      const bothAnon = (join.vpn || join.hosting) && (net.vpn || net.hosting);
      const w = bothAnon ? WEIGHTS.asnVpn : WEIGHTS.asn;
      score += w;
      reasons.push({
        kind: bothAnon ? "asn+vpn" : "asn",
        detail: bothAnon
          ? `same VPN/hosting network as a banned player (${join.asn}${net.organization ? ` - ${net.organization}` : ""})`
          : `same network/ASN (${join.asn}) - weak on its own`,
        weight: w,
      });
    }

    if (!reasons.length) continue;
    // A lone ASN hit never reports: entire ISPs share one, so it would flag neighbours.
    const onlyWeakAsn = reasons.every(r => r.kind === "asn");
    if (!definitive && (score < MATCH_THRESHOLD || onlyWeakAsn)) continue;

    matches.push({
      playerId: b.playerId,
      reason: b.reason ?? "unknown",
      moderator: b.moderator ?? "unknown",
      permanent: !!b.permanent,
      expires: b.expires ?? null,
      score, definitive,
      reasons: reasons.sort((a, z) => z.weight - a.weight),
    });
  }

  /* Standing flags in the ipBans registry, independent of any ban RECORD - covers an
     auto-ban that flagged an id/IP/name without writing a per-player ban row. */
  const flags = [];
  if (joinEos && flaggedIds.has(joinEos))   flags.push({ kind: "eos",      detail: "EOS account is flagged in the ban registry" });
  if (joinIp  && flaggedIps.has(joinIp))    flags.push({ kind: "ip",       detail: `IP ${joinIp} is flagged in the ban registry` });
  if (joinName && flaggedNames.has(joinName)) flags.push({ kind: "username", detail: "username is flagged in the ban registry" });

  matches.sort((a, b) => b.score - a.score);
  const top = matches[0] ?? null;
  return {
    evasion: matches.length > 0 || flags.length > 0,
    certain: matches.some(m => m.definitive) || flags.some(f => f.kind === "eos"),
    score: top?.score ?? (flags.length ? WEIGHTS.eos : 0),
    matches, flags,
  };
}

/* The network intelligence to persist ALONGSIDE a ban, so a future join can be
   correlated against it even after the VPN cache entry expires. */
function banNetworkSnapshot({ eosIds = [], ips = [], names = [], vpnRecord = null } = {}) {
  const v = vpnRecord ?? {};
  return {
    eosIds: uniq(eosIds), ips: uniq(ips), names: uniq(names),
    asn: v.asn ?? null,
    organization: v.organization ?? null,
    provider: v.provider ?? null,          // the VPN brand, when one was detected
    vpn: v.vpn ?? null,
    proxy: v.proxy ?? null,
    tor: v.tor ?? null,
    hosting: v.hosting ?? null,
    threatScore: v.threatScore ?? null,
    country: v.country ?? null,
    detectedAt: v.lastChecked ?? new Date().toISOString(),
  };
}

module.exports = { findEvasion, banNetworkSnapshot, WEIGHTS, MATCH_THRESHOLD };
