#!/usr/bin/env bash
# CHECK that CrowdSec's SSH banning is in place and working. Read-only.
#
#   sudo bash scripts/crowdsec/verify.sh
#
# Each line is PASS, WARN or FAIL with what to do.
set -uo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }

fails=0
pass() { printf '  PASS  %s\n' "$1"; }
warn() { printf '  WARN  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; fails=$((fails + 1)); }
have() { command -v "$1" >/dev/null 2>&1; }

if ! have cscli; then
  fail "CrowdSec is not installed: sudo OWNER_IP=<your IP> bash scripts/crowdsec/install.sh"
  exit 1
fi

echo "CrowdSec"
systemctl is-active --quiet crowdsec && pass "crowdsec running" || fail "crowdsec not running: systemctl status crowdsec"
cscli lapi status >/dev/null 2>&1 && pass "local API answering" || fail "cscli cannot reach the local API: systemctl status crowdsec"
cscli collections list -o raw 2>/dev/null | grep -q 'crowdsecurity/sshd' && pass "sshd collection installed" || fail "sshd collection missing: cscli collections install crowdsecurity/sshd"
cscli scenarios list -o raw 2>/dev/null | grep -q 'ssh-bf' && pass "ssh-bf scenario installed" || warn "ssh-bf scenario not listed"
cscli metrics show acquisition 2>/dev/null | grep -qE 'auth.log|journalctl' && pass "the SSH log is being read" || warn "no SSH log source in 'cscli metrics show acquisition'"

echo
echo "blocking"
systemctl is-active --quiet crowdsec-firewall-bouncer && pass "nftables bouncer running" || fail "bouncer not running: systemctl status crowdsec-firewall-bouncer"
nft list table ip crowdsec >/dev/null 2>&1 && pass "bouncer tables loaded (ip crowdsec, ip6 crowdsec6)" || warn "no crowdsec table yet (created on the first decision sync)"
[ "$(cscli bouncers list -o raw 2>/dev/null | grep -c firewall)" -ge 1 ] && pass "bouncer registered with CrowdSec" || warn "no bouncer registered"

echo
echo "staying out of your own way"
if cscli allowlists list -o raw 2>/dev/null | grep -q .; then
  pass "allowlists: $(cscli allowlists list -o raw 2>/dev/null | tail -n +2 | cut -d, -f1 | tr '\n' ' ')"
else
  warn "no allowlist (CrowdSec older than 1.6.8?): a fumbled password could ban you"
fi
echo "        active bans: $(cscli decisions list -o raw 2>/dev/null | tail -n +2 | grep -c .)"

echo
[ "$fails" -eq 0 ] && echo "All required checks passed." || echo "$fails check(s) FAILED - see above."
exit "$fails"
