#!/usr/bin/env bash
# REMOVE the old anti-DDoS setup from this server. CrowdSec's SSH banning keeps running.
#
#   sudo bash scripts/crowdsec/remove-antiddos.sh
#   sudo WIPE_LOGS=1 REMOVE_BOT_BANS=1 bash scripts/crowdsec/remove-antiddos.sh
#
# Removes:
#   - the bot's nftables table (inet pavlovbot_ddos) and its boot unit
#   - the packet capture service (dumpcap) and its rolling ring
#   - ulogd2 packet logging, and the log rotation timer
#   - CrowdSec's ufw port-scan log and scenarios (SSH banning stays)
#
# Does NOT touch, unless you ask:
#   WIPE_LOGS=1        also delete /var/log/anti-ddos (packet, source, traffic and event logs)
#   REMOVE_BOT_BANS=1  also lift every CrowdSec ban the bot placed (reason "pavlovbot: ...",
#                      including the permanent flood-source bans)
#
# Never touched: ufw, CrowdSec itself, its bouncer, its allowlists, your SSH bans, installed
# packages (ulogd2 and tshark stay installed, just stopped: apt remove them if you want).
# Safe to re-run.
set -uo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo bash $0"; exit 1; }

step() { printf '\n==> %s\n' "$1"; }
ok()   { printf '    ok: %s\n' "$1"; }
have() { command -v "$1" >/dev/null 2>&1; }

step "1. the bot's firewall table"
if have nft && nft list table inet pavlovbot_ddos >/dev/null 2>&1; then
  nft delete table inet pavlovbot_ddos && ok "table inet pavlovbot_ddos deleted"
else
  ok "no table inet pavlovbot_ddos"
fi

step "2. services and timers"
for unit in anti-ddos-capture.service anti-ddos-logrotate.timer anti-ddos-logrotate.service anti-ddos-firewall.service; do
  if [ -f "/etc/systemd/system/$unit" ]; then
    have systemctl && systemctl disable --now "$unit" >/dev/null 2>&1
    rm -f "/etc/systemd/system/$unit" && ok "$unit removed"
  fi
done
if [ -f /etc/systemd/system/ulogd2.service.d/anti-ddos.conf ]; then
  rm -f /etc/systemd/system/ulogd2.service.d/anti-ddos.conf
  rmdir /etc/systemd/system/ulogd2.service.d 2>/dev/null
  ok "ulogd2 drop-in removed"
fi
if have systemctl && systemctl list-unit-files ulogd2.service >/dev/null 2>&1; then
  systemctl disable --now ulogd2.service >/dev/null 2>&1 && ok "ulogd2 stopped and disabled"
fi
if [ -f /etc/ulogd.conf.pre-antiddos ]; then
  mv -f /etc/ulogd.conf.pre-antiddos /etc/ulogd.conf && ok "/etc/ulogd.conf restored"
fi
have systemctl && systemctl daemon-reload

step "3. files"
for path in /usr/local/sbin/anti-ddos-capture-prune /etc/anti-ddos /var/lib/anti-ddos; do
  [ -e "$path" ] && { rm -rf -- "$path"; ok "$path removed"; }
done
if [ -d /var/log/anti-ddos ]; then
  if [ "${WIPE_LOGS:-0}" = 1 ]; then
    size="$(du -sh /var/log/anti-ddos | cut -f1)"
    rm -rf -- /var/log/anti-ddos && ok "/var/log/anti-ddos deleted ($size freed)"
  else
    ok "/var/log/anti-ddos kept ($(du -sh /var/log/anti-ddos | cut -f1)); WIPE_LOGS=1 deletes it"
  fi
fi

step "4. CrowdSec: keep the SSH banning, drop the port-scan source"
if have cscli; then
  if [ -f /etc/crowdsec/acquis.d/ufw.yaml ]; then
    rm -f /etc/crowdsec/acquis.d/ufw.yaml && ok "ufw.log acquisition removed"
    RESTART=1
  fi
  if cscli collections list -o raw 2>/dev/null | grep -q 'crowdsecurity/iptables'; then
    cscli collections remove crowdsecurity/iptables >/dev/null 2>&1 && ok "crowdsecurity/iptables (port scans) removed"
    RESTART=1
  fi
  if [ "${REMOVE_BOT_BANS:-0}" = 1 ]; then
    n=0
    while IFS= read -r scenario; do
      cscli decisions delete --scenario "$scenario" >/dev/null 2>&1 && n=$((n + 1))
    done < <(cscli decisions list -l 0 -o json 2>/dev/null | python3 -c '
import json, sys
seen = set()
for alert in json.load(sys.stdin) or []:
    for d in alert.get("decisions", []):
        s = d.get("scenario", "")
        if s.startswith("pavlovbot:") and s not in seen:
            seen.add(s); print(s)')
    ok "lifted the bans of $n bot reason(s)"
    RESTART=1
  else
    ok "the bot's bans are kept; REMOVE_BOT_BANS=1 lifts them"
  fi
  [ "${RESTART:-0}" = 1 ] && have systemctl && systemctl restart crowdsec >/dev/null 2>&1
  ok "SSH banning: $(systemctl is-active crowdsec 2>/dev/null || echo unknown) (crowdsec), $(systemctl is-active crowdsec-firewall-bouncer 2>/dev/null || echo unknown) (bouncer)"
else
  ok "CrowdSec is not installed"
fi

echo
echo "Done. Check CrowdSec with: sudo bash $(dirname "$0")/verify.sh"
