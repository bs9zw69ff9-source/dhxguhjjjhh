#!/usr/bin/env bash
# INSTALL CrowdSec to ban addresses that keep trying SSH, step by step, safely.
#
#   sudo OWNER_IP=<your home IP> bash scripts/crowdsec/install.sh
#
# What it does:
#   1. installs CrowdSec from CrowdSec's signed repository
#   2. allowlists OWNER_IP FIRST, so a fumbled password can never lock you out
#   3. installs the sshd scenarios (crowdsecurity/linux + crowdsecurity/sshd) and makes sure
#      the SSH log is read (auth.log, or journald when there is no auth.log)
#   4. installs the nftables bouncer, which is what actually blocks a banned address
#   5. checks it all (scripts/crowdsec/verify.sh)
#
# WHAT IT NEVER DOES: flush the ruleset, touch ufw's rules, apply a default-deny policy or
# restart SSH. The bouncer keeps its own tables (ip crowdsec, ip6 crowdsec6). Safe to re-run:
# each step checks before it changes anything.
#
# CrowdSec's default for a repeat SSH offender is a 4 hour ban. Change it in
# /etc/crowdsec/profiles.yaml (duration:), not here.
#
# If SSH is ever lost anyway: use the provider's web console and run
#   systemctl stop crowdsec-firewall-bouncer
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo bash $0"; exit 1; }
. /etc/os-release
[ "${ID:-}" = "ubuntu" ] || { echo "This targets Ubuntu (24.04); found ${PRETTY_NAME:-unknown}. Stopping."; exit 1; }
[ "${VERSION_ID:-}" = "24.04" ] || echo "WARNING: tested on Ubuntu 24.04; this is ${VERSION_ID}. Continuing."

HERE="$(cd "$(dirname "$0")" && pwd)"
FILES="$HERE/files"
OWNER_IP="${OWNER_IP:-}"
ALLOWLIST=owner

step() { printf '\n==> %s\n' "$1"; }
ok()   { printf '    ok: %s\n' "$1"; }
warn() { printf '    WARNING: %s\n' "$1"; }

# Validate OWNER_IP before anything changes: it goes on a command line.
if [ -n "$OWNER_IP" ] && ! python3 -c 'import ipaddress,sys; ipaddress.ip_network(sys.argv[1], strict=True)' "$OWNER_IP" 2>/dev/null; then
  echo "OWNER_IP '$OWNER_IP' is not an IP address or network. Stopping."; exit 1
fi
if [ -z "$OWNER_IP" ]; then
  SSH_FROM="$(echo "${SSH_CLIENT:-}" | awk '{print $1}')"
  echo "OWNER_IP is not set. CrowdSec bans addresses with repeated SSH login failures."
  [ -n "$SSH_FROM" ] && echo "You are connected from $SSH_FROM - re-run with OWNER_IP=$SSH_FROM to allowlist it."
  read -r -p "Continue WITHOUT allowlisting yourself? Type yes: " answer
  [ "$answer" = "yes" ] || { echo "Stopped. Nothing was changed."; exit 1; }
fi

step "1. CrowdSec (signed repository)"
if [ ! -f /etc/apt/sources.list.d/crowdsec_crowdsec.list ]; then
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq gnupg curl ca-certificates >/dev/null
  install -d -m 0755 /etc/apt/keyrings
  curl -fsSL https://packagecloud.io/crowdsec/crowdsec/gpgkey | gpg --dearmor > /etc/apt/keyrings/crowdsec_crowdsec-archive-keyring.gpg
  chmod 0644 /etc/apt/keyrings/crowdsec_crowdsec-archive-keyring.gpg
  echo "deb [signed-by=/etc/apt/keyrings/crowdsec_crowdsec-archive-keyring.gpg] https://packagecloud.io/crowdsec/crowdsec/any/ any main" \
    > /etc/apt/sources.list.d/crowdsec_crowdsec.list
fi
DEBIAN_FRONTEND=noninteractive apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq crowdsec >/dev/null
ok "$(cscli version 2>&1 | grep -m1 -i version)"

step "2. allowlist (before anything can ban)"
cscli allowlists inspect "$ALLOWLIST" -o json >/dev/null 2>&1 || cscli allowlists create "$ALLOWLIST" -d "Addresses CrowdSec must never ban" >/dev/null
if [ -n "$OWNER_IP" ]; then
  cscli allowlists add "$ALLOWLIST" "$OWNER_IP" -d "owner (install.sh)" >/dev/null 2>&1 || true
  ok "$OWNER_IP is on the '$ALLOWLIST' allowlist"
fi

step "3. SSH scenarios and the SSH log"
cscli collections install crowdsecurity/linux crowdsecurity/sshd >/dev/null 2>&1 || warn "could not install the collections (hub unreachable?)"
if [ ! -f /var/log/auth.log ] && ! grep -rqs "auth.log" /etc/crowdsec/acquis.yaml /etc/crowdsec/acquis.d; then
  install -d /etc/crowdsec/acquis.d
  install -m 0644 "$FILES/crowdsec-acquis-sshd-journal.yaml" /etc/crowdsec/acquis.d/sshd-journal.yaml
  ok "no auth.log: sshd is read from journald"
else
  ok "SSH is read from auth.log"
fi
crowdsec -c /etc/crowdsec/config.yaml -t -error && ok "configuration valid" \
  || { echo "CrowdSec rejected its configuration. Stopping before the bouncer."; exit 1; }
systemctl restart crowdsec
sleep 3

step "4. the nftables bouncer (what actually blocks)"
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq crowdsec-firewall-bouncer-nftables >/dev/null
BOUNCER=/etc/crowdsec/bouncers/crowdsec-firewall-bouncer.yaml
grep -q '^mode: nftables' "$BOUNCER" || warn "the bouncer is not in nftables mode - check $BOUNCER"
systemctl enable --quiet --now crowdsec-firewall-bouncer
sleep 5
ok "bouncer: $(systemctl is-active crowdsec-firewall-bouncer)"

step "5. checks"
bash "$HERE/verify.sh" || true
