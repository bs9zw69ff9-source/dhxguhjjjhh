#!/usr/bin/env bash
# READ-ONLY AUDIT of the box before the anti-DDoS upgrade.
#
#   sudo bash scripts/antiddos/audit.sh            # prints, and saves a copy
#
# Changes NOTHING. It reports the facts install.sh depends on - the OS, the public
# interface, every firewall already loaded (ufw, Docker, CrowdSec, this bot), what is
# listening and on which address (RCON must not be public), the logging stack, disk
# space and what is already installed - so the upgrade is planned against the real
# box, not a guess.
#
# PRINTS NO SECRETS: no .env values, no keys, no tokens. Safe to paste into a chat.
set -uo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run as root (sudo): reading the firewall needs it."; exit 1; }

OUT="/root/anti-ddos-audit-$(date -u +%Y%m%d-%H%M%S).txt"
exec > >(tee "$OUT") 2>&1

line() { printf '\n=== %s ===\n' "$1"; }
have() { command -v "$1" >/dev/null 2>&1; }

line "system"
. /etc/os-release 2>/dev/null && echo "os:        $PRETTY_NAME"
echo "kernel:    $(uname -r)"
echo "uptime:    $(uptime -p 2>/dev/null)"
echo "memory:    $(free -h 2>/dev/null | awk '/Mem:/ {print $2 " total, " $7 " available"}')"
echo "cpus:      $(nproc)"
[ "${VERSION_ID:-}" = "24.04" ] || echo "WARNING: install.sh targets Ubuntu 24.04; this is ${VERSION_ID:-unknown}"

line "network"
IFACE="$(ip route show default 2>/dev/null | awk '{print $5; exit}')"
echo "default route leaves by: ${IFACE:-NONE FOUND}"
ip -br addr 2>/dev/null
echo
echo "IPv6 default route: $(ip -6 route show default 2>/dev/null | head -1 || true)"
echo "conntrack: $(cat /proc/sys/net/netfilter/nf_conntrack_count 2>/dev/null || echo '?') used of $(cat /proc/sys/net/netfilter/nf_conntrack_max 2>/dev/null || echo '?')"
echo "rp_filter (all/${IFACE:-?}): $(cat /proc/sys/net/ipv4/conf/all/rp_filter 2>/dev/null)/$(cat /proc/sys/net/ipv4/conf/${IFACE:-lo}/rp_filter 2>/dev/null)"

line "firewalls loaded (nftables tables)"
if have nft; then
  nft list tables
  echo
  echo "base chains (hook, priority, policy):"
  nft -a list ruleset 2>/dev/null | grep -E "^table|type filter hook|type nat hook" | sed 's/^/  /'
else
  echo "nft not installed"
fi

line "ufw"
if have ufw; then
  ufw status verbose 2>/dev/null | head -40
else
  echo "ufw not installed"
fi
echo "iptables backend: $(iptables -V 2>/dev/null || echo 'not installed')"

line "listening sockets (address:port process)"
ss -lntupH 2>/dev/null | awk '{print $1, $5, $7}' | sort -u
echo
echo "RCON check: Pavlov RCON is TCP, usually 9100-9199. Anything below listening on 0.0.0.0 or [::]"
echo "must be closed to the internet by ufw (allow only from the bot's own host):"
ss -lntH 2>/dev/null | awk '{print $4}' | grep -E ':(9[01][0-9]{2})$' | sed 's/^/  RCON? /' || true

line "docker"
if have docker; then
  docker ps --format '{{.Names}}  {{.Image}}  {{.Networks}}  {{.Ports}}' 2>/dev/null
  echo "(a container on a bridge network has its traffic forwarded; the bot's table hooks prerouting, so it is covered)"
else
  echo "docker not installed"
fi

line "services"
for s in ssh ufw nftables docker rsyslog systemd-journald ulogd2 crowdsec crowdsec-firewall-bouncer pavlov-bot anti-ddos-firewall anti-ddos-logrotate.timer; do
  printf '  %-28s %s / %s\n' "$s" "$(systemctl is-active "$s" 2>/dev/null)" "$(systemctl is-enabled "$s" 2>/dev/null)"
done
have pm2 && { echo; echo "pm2:"; pm2 jlist 2>/dev/null | grep -o '"name":"[^"]*"' | sed 's/^/  /' || true; }

line "installed versions"
for p in nftables ulogd2 ulogd2-json logrotate rsyslog crowdsec crowdsec-firewall-bouncer-nftables tshark; do
  printf '  %-36s %s\n' "$p" "$(dpkg-query -W -f '${Version}' "$p" 2>/dev/null || echo '-')"
done
have cscli && { echo; cscli version 2>/dev/null | head -2; echo; cscli collections list 2>/dev/null | head -20; }

line "logging"
echo "journald storage: $(grep -hE '^\s*Storage=' /etc/systemd/journald.conf /etc/systemd/journald.conf.d/*.conf 2>/dev/null | tail -1 || echo 'default (persistent if /var/log/journal exists)')"
echo "/var/log/journal exists: $([ -d /var/log/journal ] && echo yes || echo no)"
journalctl --disk-usage 2>/dev/null
echo "auth.log:  $([ -f /var/log/auth.log ] && ls -la /var/log/auth.log || echo 'none - sshd logs only to journald')"
echo "ufw.log:   $([ -f /var/log/ufw.log ] && ls -la /var/log/ufw.log || echo 'none')"
echo "kern.log:  $([ -f /var/log/kern.log ] && ls -la /var/log/kern.log || echo 'none')"
[ -d /var/log/anti-ddos ] && { echo; echo "anti-ddos logs:"; du -sh /var/log/anti-ddos/* 2>/dev/null; }

line "disk"
df -h / /var/log 2>/dev/null | sort -u
echo
du -sh /var/log 2>/dev/null
du -sh /root/pavlov-bot/data 2>/dev/null

line "the bot"
ENVF=/root/pavlov-bot/.env
if [ -f "$ENVF" ]; then
  for k in ATTACK_CHANNEL ATTACK_INTERFACE ATTACK_MITIGATION PACKET_LOG ANTIDDOS_LOG_DIR ANTIDDOS_SSH_PORTS ANTIDDOS_ALERT_CHANNEL ANTIDDOS_CROWDSEC; do
    v="$(grep -E "^${k}=" "$ENVF" | tail -1 | cut -d= -f2-)"
    printf '  %-26s %s\n' "$k" "$([ -n "$v" ] && echo SET || echo unset)"
  done
  echo "  (values are not printed)"
else
  echo "no $ENVF"
fi

echo
echo "Saved to $OUT"
