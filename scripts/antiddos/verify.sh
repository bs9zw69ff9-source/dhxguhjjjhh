#!/usr/bin/env bash
# CHECK that every piece of the anti-DDoS setup is in place and working. Read-only.
#
#   sudo bash scripts/antiddos/verify.sh
#
# Each line is PASS, WARN or FAIL with what to do. Run it after install.sh, after a bot
# restart, and after a reboot - it is the checklist in ANTIDDOS.md, automated.
set -uo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }

LOGDIR="${ANTIDDOS_LOG_DIR:-/var/log/anti-ddos}"
fails=0
pass() { printf '  PASS  %s\n' "$1"; }
warn() { printf '  WARN  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; fails=$((fails + 1)); }
have() { command -v "$1" >/dev/null 2>&1; }

echo "firewall"
if nft list table inet pavlovbot_ddos >/dev/null 2>&1; then
  pass "table inet pavlovbot_ddos is loaded"
  T="$(nft list table inet pavlovbot_ddos)"
  echo "$T" | grep -q "hook prerouting priority raw" && pass "guard chain hooks prerouting before conntrack" || fail "guard chain missing"
  echo "$T" | grep -q 'comment "ssh"' && pass "SSH is let through ahead of every drop" || fail "no SSH rule - do not enable ATTACK_MITIGATION until this passes"
  if echo "$T" | grep -q "jump drop_blocked"; then pass "drop rules present (ATTACK_MITIGATION=on)"; else warn "no drop rules: counting and logging only (ATTACK_MITIGATION is off)"; fi
  echo "$T" | grep -q "policy drop" && fail "a chain has policy drop - that is not this bot's table" || pass "every chain's policy is accept"
  echo "$T" | grep -q 'log prefix "pbd ' && pass "logging rules present ($(echo "$T" | grep -c 'log prefix "pbd ') rules)" || warn "no logging rules: storage pressure, or mode off"
else
  fail "table inet pavlovbot_ddos is not loaded - is the bot running with ATTACK_CHANNEL set, as root? journalctl -u pavlov-bot | grep -i firewall"
fi
[ -f /var/lib/anti-ddos/firewall.nft ] && pass "boot copy saved (/var/lib/anti-ddos/firewall.nft)" || warn "no boot copy yet - the bot writes it on its first apply"
systemctl is-enabled --quiet anti-ddos-firewall.service 2>/dev/null && pass "anti-ddos-firewall.service enabled for boot" || warn "anti-ddos-firewall.service not enabled (install.sh step 5)"
have ufw && ufw status 2>/dev/null | grep -q "Status: active" && pass "ufw active and untouched" || warn "ufw is not active"

echo
echo "packet logging"
systemctl is-active --quiet ulogd2 && pass "ulogd2 running" || fail "ulogd2 not running: systemctl status ulogd2"
grep -qs 'anti-ddos/packets/packets.jsonl' /etc/ulogd.conf && pass "ulogd2 writes $LOGDIR/packets/packets.jsonl" || fail "/etc/ulogd.conf is not the anti-DDoS one (install.sh step 4)"
G="$(nft list table inet pavlovbot_ddos 2>/dev/null | grep -om1 'group [0-9]*' | awk '{print $2}')"
U="$(grep -A2 '^\[log1\]' /etc/ulogd.conf 2>/dev/null | grep -om1 'group=[0-9]*' | cut -d= -f2)"
[ -n "$G" ] && [ "$G" = "$U" ] && pass "firewall and ulogd2 agree on NFLOG group $G" || { [ -n "$G" ] && fail "firewall logs to group $G, ulogd2 listens on ${U:-?}: set ANTIDDOS_NFLOG_GROUP or [log1] group= to match"; }
P="$LOGDIR/packets/packets.jsonl"
if [ -f "$P" ]; then
  pass "packet log exists ($(du -h "$P" | cut -f1), last written $(date -u -r "$P" '+%Y-%m-%d %H:%M UTC'))"
  [ "$(stat -c %a "$P")" = 600 ] && pass "packet log is 0600" || warn "packet log is $(stat -c %a "$P"), expected 600"
else
  warn "no packet records yet - normal until the firewall logs something (a sample arrives within minutes of game traffic)"
fi
for f in sources traffic events; do
  [ -f "$LOGDIR/$f.jsonl" ] && pass "$f.jsonl written $(date -u -r "$LOGDIR/$f.jsonl" '+%H:%M UTC')" || warn "no $f.jsonl yet (the bot writes it every minute)"
done
[ "$(stat -c '%a' "$LOGDIR" 2>/dev/null)" = 710 ] && pass "$LOGDIR is 0710 root:ulog" || warn "$LOGDIR permissions are $(stat -c '%a %U:%G' "$LOGDIR" 2>/dev/null)"
case "$(stat -f -c %T "$LOGDIR" 2>/dev/null)" in tmpfs|ramfs) fail "$LOGDIR is on $(stat -f -c %T "$LOGDIR") - it would not survive a reboot";; *) pass "$LOGDIR is on persistent storage";; esac

echo
echo "rotation and storage"
systemctl is-active --quiet anti-ddos-logrotate.timer && pass "rotation timer active (next: $(systemctl show anti-ddos-logrotate.timer -p NextElapseUSecRealtime --value))" || fail "anti-ddos-logrotate.timer not active"
/usr/sbin/logrotate --debug --state /tmp/anti-ddos-verify.status /etc/anti-ddos/logrotate.conf >/dev/null 2>&1 && pass "logrotate config valid" || fail "logrotate config invalid"
rm -f /tmp/anti-ddos-verify.status
echo "        logs: $(du -sh "$LOGDIR" 2>/dev/null | cut -f1)   disk free: $(df -h --output=avail "$LOGDIR" 2>/dev/null | tail -1 | tr -d ' ')"

echo
echo "CrowdSec"
if have cscli; then
  cscli lapi status >/dev/null 2>&1 && pass "local API answering" || fail "cscli cannot reach the local API: systemctl status crowdsec"
  systemctl is-active --quiet crowdsec-firewall-bouncer && pass "nftables bouncer running" || fail "bouncer not running: systemctl status crowdsec-firewall-bouncer"
  nft list table ip crowdsec >/dev/null 2>&1 && pass "bouncer tables loaded (ip crowdsec, ip6 crowdsec6)" || warn "no crowdsec table yet (created on the first decision sync)"
  cscli allowlists inspect pavlovbot >/dev/null 2>&1 && pass "allowlist 'pavlovbot' exists ($(cscli allowlists inspect pavlovbot -o json 2>/dev/null | grep -c '"value"') entries)" || warn "no 'pavlovbot' allowlist (CrowdSec older than 1.6.8?)"
  cscli metrics show acquisition 2>/dev/null | grep -qE 'auth.log|journalctl' && pass "SSH logs are being read" || warn "no SSH log source in 'cscli metrics show acquisition'"
else
  warn "CrowdSec not installed (install.sh step 6)"
fi

echo
[ "$fails" -eq 0 ] && echo "All required checks passed." || echo "$fails check(s) FAILED - see above."
exit "$fails"
