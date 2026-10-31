#!/usr/bin/env bash
# UNDO the anti-DDoS upgrade, safely, from SSH or the provider's web console.
#
#   sudo bash scripts/antiddos/rollback.sh              # firewall tables + logging off
#   sudo PURGE_CROWDSEC=1 bash scripts/antiddos/rollback.sh   # also remove CrowdSec
#
# Removes only what install.sh and the bot added: the table inet pavlovbot_ddos, the
# CrowdSec bouncer (which deletes its own tables when it stops), the boot unit, the
# rotation timer and the ulogd configuration (the original is restored). ufw, Docker and
# SSH are never touched. The logs in /var/log/anti-ddos are KEPT - delete them by hand if
# you want them gone.
#
# THE BOT PUTS ITS TABLE BACK when it starts with ATTACK_CHANNEL set. To keep it off, also
# remove ATTACK_MITIGATION (drop rules) or ATTACK_CHANNEL (everything) from the .env.
#
# LOCKED OUT? This script is the recovery: open the provider's console (VNC/serial), log in
# as root, and run it. If even that is not possible, `nft delete table inet pavlovbot_ddos`
# and `systemctl stop crowdsec-firewall-bouncer` are the two commands that matter.
set -uo pipefail
[ "$(id -u)" -eq 0 ] || { echo "Run as root."; exit 1; }

echo "==> firewall"
systemctl disable --now anti-ddos-firewall.service 2>/dev/null || true
nft delete table inet pavlovbot_ddos 2>/dev/null && echo "    removed table inet pavlovbot_ddos" || echo "    table inet pavlovbot_ddos was not loaded"
systemctl disable --now crowdsec-firewall-bouncer 2>/dev/null && echo "    CrowdSec bouncer stopped (its tables removed)" || true
nft delete table ip crowdsec 2>/dev/null || true
nft delete table ip6 crowdsec6 2>/dev/null || true

echo "==> logging"
systemctl disable --now anti-ddos-logrotate.timer 2>/dev/null || true
if [ -f /etc/ulogd.conf.pre-antiddos ]; then
  cp -a /etc/ulogd.conf.pre-antiddos /etc/ulogd.conf
  rm -f /etc/systemd/system/ulogd2.service.d/anti-ddos.conf
  systemctl daemon-reload
  systemctl restart ulogd2 2>/dev/null || true
  echo "    /etc/ulogd.conf restored"
fi

if [ "${PURGE_CROWDSEC:-0}" = 1 ]; then
  echo "==> CrowdSec"
  DEBIAN_FRONTEND=noninteractive apt-get purge -y -qq crowdsec-firewall-bouncer-nftables crowdsec >/dev/null 2>&1 || true
  echo "    purged"
else
  echo "==> CrowdSec left installed (detection only, nothing enforced). PURGE_CROWDSEC=1 removes it."
fi

echo
echo "Remaining tables:"
nft list tables
echo
echo "Done. ufw: $(ufw status 2>/dev/null | head -1)"
echo "To stop the bot re-adding its table: remove ATTACK_MITIGATION (and/or ATTACK_CHANNEL) from /root/pavlov-bot/.env, then systemctl restart pavlov-bot"
