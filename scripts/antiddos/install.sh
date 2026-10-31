#!/usr/bin/env bash
# INSTALL the anti-DDoS logging and CrowdSec, step by step, safely.
#
#   sudo OWNER_IP=<your home IP> bash scripts/antiddos/install.sh
#
# Run scripts/antiddos/audit.sh first and read it. Then this script:
#   1. backs up everything it will touch into /root/anti-ddos-backup-<time>/
#   2. installs ulogd2 (+json) and logrotate from Ubuntu
#   3. creates /var/log/anti-ddos with owner-only permissions
#   4. points ulogd2 at the bot's NFLOG group, writing JSON Lines there
#   5. installs the 10-minute log rotation timer and the boot unit for the bot's table
#   5b. starts the packet capture as its own service (dumpcap), so the header of every
#      inbound packet is recorded even while the bot is down
#   6. installs CrowdSec and its nftables bouncer from CrowdSec's signed repository,
#      allowlists OWNER_IP first, adds the ufw port-scan source, keeps 30 days of history
#   7. checks every service and prints what to do next
#
# WHAT IT NEVER DOES: flush the ruleset, touch ufw's rules, enable nftables.service (whose
# stock /etc/nftables.conf starts with `flush ruleset` and would wipe ufw and Docker), apply
# a default-deny policy, or restart SSH. Every new firewall table is its own, with an accept
# policy. Safe to re-run: each step checks before it changes anything.
#
# OWNER_IP: the address you SSH from. CrowdSec bans brute-forcers after a few failed
# logins; putting yourself on its allowlist first means a fumbled password can never lock
# you out. Without it the script asks you to confirm. SKIP_CROWDSEC=1 installs only the
# logging.
#
# If SSH is ever lost anyway: use the provider's web console and run
#   bash /root/pavlov-bot/scripts/antiddos/rollback.sh
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo bash $0"; exit 1; }
. /etc/os-release
[ "${ID:-}" = "ubuntu" ] || { echo "This targets Ubuntu (24.04); found ${PRETTY_NAME:-unknown}. Stopping."; exit 1; }
[ "${VERSION_ID:-}" = "24.04" ] || echo "WARNING: tested on Ubuntu 24.04; this is ${VERSION_ID}. Continuing."

HERE="$(cd "$(dirname "$0")" && pwd)"
FILES="$HERE/files"
LOGDIR=/var/log/anti-ddos
STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/anti-ddos-backup-$STAMP"
OWNER_IP="${OWNER_IP:-}"

step() { printf '\n==> %s\n' "$1"; }
ok()   { printf '    ok: %s\n' "$1"; }
warn() { printf '    WARNING: %s\n' "$1"; }

# Validate OWNER_IP before anything changes: it goes on a command line.
if [ -n "$OWNER_IP" ] && ! python3 -c 'import ipaddress,sys; ipaddress.ip_network(sys.argv[1], strict=True)' "$OWNER_IP" 2>/dev/null; then
  echo "OWNER_IP '$OWNER_IP' is not an IP address or network. Stopping."; exit 1
fi
if [ -z "$OWNER_IP" ] && [ "${SKIP_CROWDSEC:-0}" != 1 ]; then
  SSH_FROM="$(echo "${SSH_CLIENT:-}" | awk '{print $1}')"
  echo "OWNER_IP is not set. CrowdSec bans addresses with repeated SSH login failures."
  [ -n "$SSH_FROM" ] && echo "You are connected from $SSH_FROM - re-run with OWNER_IP=$SSH_FROM to allowlist it."
  read -r -p "Continue WITHOUT allowlisting yourself? Type yes: " answer
  [ "$answer" = "yes" ] || { echo "Stopped. Nothing was changed."; exit 1; }
fi

step "1. backup to $BACKUP"
install -d -m 0700 "$BACKUP"
nft list ruleset > "$BACKUP/nft-ruleset.txt" 2>/dev/null || true
ufw status verbose > "$BACKUP/ufw-status.txt" 2>/dev/null || true
for f in /etc/ulogd.conf /etc/crowdsec/config.yaml.local /etc/crowdsec/acquis.yaml /etc/anti-ddos/logrotate.conf; do
  [ -f "$f" ] && cp -a "$f" "$BACKUP/$(echo "$f" | tr / _)"
done
ok "ruleset, ufw status and configs saved"

step "2. packages: ulogd2, ulogd2-json, logrotate"
DEBIAN_FRONTEND=noninteractive apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ulogd2 ulogd2-json logrotate >/dev/null
ok "$(dpkg-query -W -f '${Package} ${Version}\n' ulogd2 ulogd2-json logrotate | tr '\n' ' ')"

step "3. $LOGDIR"
# root owns it; ulogd (user ulog) may only enter it to reach packets/; nobody else can list it.
install -d -m 0710 -o root -g ulog "$LOGDIR"
install -d -m 0700 -o ulog -g ulog "$LOGDIR/packets"
install -d -m 0700 -o root -g root "$LOGDIR/capture" "$LOGDIR/exports" /var/lib/anti-ddos /etc/anti-ddos
ok "$(stat -c '%A %U:%G %n' "$LOGDIR" "$LOGDIR/packets" | tr '\n' ' ')"

step "4. ulogd2 -> $LOGDIR/packets/packets.jsonl"
PLUGINS="$(dirname "$(dpkg -L ulogd2 | grep -m1 'ulogd_inppkt_NFLOG.so')")"
[ -n "$PLUGINS" ] || { echo "Cannot find ulogd's plugin folder. Stopping."; exit 1; }
[ -f /etc/ulogd.conf.pre-antiddos ] || cp -a /etc/ulogd.conf /etc/ulogd.conf.pre-antiddos
sed "s#/usr/lib/x86_64-linux-gnu/ulogd#$PLUGINS#g" "$FILES/ulogd.conf" > /etc/ulogd.conf
install -d /etc/systemd/system/ulogd2.service.d
install -m 0644 "$FILES/ulogd2-override.conf" /etc/systemd/system/ulogd2.service.d/anti-ddos.conf
systemctl daemon-reload
systemctl enable --quiet ulogd2.service
systemctl restart ulogd2.service
sleep 1
systemctl is-active --quiet ulogd2.service && ok "ulogd2 running (NFLOG group 7)" || { warn "ulogd2 did not start: journalctl -u ulogd2"; }

step "5. log rotation timer and the boot unit"
install -m 0644 "$FILES/logrotate.conf" /etc/anti-ddos/logrotate.conf
/usr/sbin/logrotate --debug --state /var/lib/logrotate/anti-ddos.status /etc/anti-ddos/logrotate.conf >/dev/null 2>&1 \
  && ok "logrotate config parses" || { warn "logrotate rejected /etc/anti-ddos/logrotate.conf"; }
install -m 0644 "$FILES/anti-ddos-logrotate.service" "$FILES/anti-ddos-logrotate.timer" "$FILES/anti-ddos-firewall.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now --quiet anti-ddos-logrotate.timer
systemctl enable --quiet anti-ddos-firewall.service
ok "anti-ddos-logrotate.timer every 10 min; anti-ddos-firewall.service loads the bot's table at boot (once the bot has saved one)"

step "5b. packet capture service (every inbound header, independent of the bot)"
IFACE="${CAPTURE_IFACE:-$(ip route show default 2>/dev/null | awk '{print $5; exit}')}"
case "$IFACE" in ''|lo|*[!A-Za-z0-9_.@-]*) echo "Cannot tell the public interface (got '$IFACE'). Set CAPTURE_IFACE=ens3 and re-run."; exit 1;; esac
# The same budget the bot holds the folder to (PACKET_RING_MB in the bot's .env).
RING_MB="${RING_MB:-$(sed -n 's/^PACKET_RING_MB=\([0-9][0-9]*\).*/\1/p' "$HERE/../../.env" 2>/dev/null | tail -1)}"
RING_MB="${RING_MB:-2048}"
case "$RING_MB" in ''|*[!0-9]*) echo "RING_MB must be a number of megabytes."; exit 1;; esac
command -v dumpcap >/dev/null 2>&1 || DEBIAN_FRONTEND=noninteractive apt-get install -y -qq tshark >/dev/null
install -m 0755 "$FILES/anti-ddos-capture-prune" /usr/local/sbin/anti-ddos-capture-prune
printf 'IFACE=%s\nRING_MB=%s\nRING_FILES=%s\n' "$IFACE" "$RING_MB" "$(( RING_MB / 50 > 2 ? RING_MB / 50 : 2 ))" > /etc/anti-ddos/capture.env
chmod 0600 /etc/anti-ddos/capture.env
install -m 0644 "$FILES/anti-ddos-capture.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now --quiet anti-ddos-capture.service
sleep 3
systemctl is-active --quiet anti-ddos-capture.service \
  && ok "capturing on $IFACE into $LOGDIR/capture, at most $RING_MB MB (the bot steps aside within 15s)" \
  || warn "the capture service did not start: journalctl -u anti-ddos-capture -n 30"

if [ "${SKIP_CROWDSEC:-0}" = 1 ]; then
  echo; echo "SKIP_CROWDSEC=1: CrowdSec not installed."
else
  step "6. CrowdSec (signed repository) and the nftables bouncer"
  if [ ! -f /etc/apt/sources.list.d/crowdsec_crowdsec.list ]; then
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq gnupg curl ca-certificates >/dev/null
    install -d -m 0755 /etc/apt/keyrings
    curl -fsSL https://packagecloud.io/crowdsec/crowdsec/gpgkey | gpg --dearmor > /etc/apt/keyrings/crowdsec_crowdsec-archive-keyring.gpg
    chmod 0644 /etc/apt/keyrings/crowdsec_crowdsec-archive-keyring.gpg
    echo "deb [signed-by=/etc/apt/keyrings/crowdsec_crowdsec-archive-keyring.gpg] https://packagecloud.io/crowdsec/crowdsec/any/ any main" \
      > /etc/apt/sources.list.d/crowdsec_crowdsec.list
    apt-get update -qq
  fi
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq crowdsec >/dev/null
  ok "$(cscli version 2>/dev/null | head -1)"

  # The allowlist goes in BEFORE the bouncer can enforce anything.
  cscli allowlists inspect pavlovbot -o json >/dev/null 2>&1 || cscli allowlists create pavlovbot -d "Trusted addresses managed by the Pavlov bot" >/dev/null
  if [ -n "$OWNER_IP" ]; then
    cscli allowlists add pavlovbot "$OWNER_IP" -d "owner (install.sh)" >/dev/null 2>&1 || true
    ok "$OWNER_IP is on the pavlovbot allowlist (also add it with /antiddos allow add)"
  fi

  cscli collections install crowdsecurity/linux crowdsecurity/sshd crowdsecurity/iptables >/dev/null 2>&1 || warn "could not install collections (hub unreachable?)"
  install -m 0644 "$FILES/crowdsec-config.yaml.local" /etc/crowdsec/config.yaml.local
  install -d /etc/crowdsec/acquis.d
  if [ -f /var/log/ufw.log ]; then
    install -m 0644 "$FILES/crowdsec-acquis-ufw.yaml" /etc/crowdsec/acquis.d/ufw.yaml
    ok "ufw.log added (port scans)"
  else
    warn "no /var/log/ufw.log - turn on 'ufw logging low' for port-scan detection, then re-run"
  fi
  if [ ! -f /var/log/auth.log ] && ! grep -rqs "auth.log" /etc/crowdsec/acquis.yaml /etc/crowdsec/acquis.d; then
    install -m 0644 "$FILES/crowdsec-acquis-sshd-journal.yaml" /etc/crowdsec/acquis.d/sshd-journal.yaml
    ok "no auth.log: sshd is read from journald"
  fi
  crowdsec -c /etc/crowdsec/config.yaml -t -error && ok "configuration valid" || { echo "CrowdSec rejected its configuration. Stopping before the bouncer."; exit 1; }
  systemctl restart crowdsec
  sleep 3

  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq crowdsec-firewall-bouncer-nftables >/dev/null
  BOUNCER=/etc/crowdsec/bouncers/crowdsec-firewall-bouncer.yaml
  grep -q '^mode: nftables' "$BOUNCER" || warn "the bouncer is not in nftables mode - check $BOUNCER"
  systemctl enable --quiet --now crowdsec-firewall-bouncer
  sleep 5
  ok "bouncer: $(systemctl is-active crowdsec-firewall-bouncer)"
fi

step "7. checks"
bash "$HERE/verify.sh" || true

cat <<'NEXT'

NEXT, in the bot's .env (/root/pavlov-bot/.env), on the ONE bot that has ATTACK_CHANNEL:
  ATTACK_MITIGATION=on            # drop rules; leave unset to only count and log
  ANTIDDOS_SSH_PORTS=22           # every port sshd listens on
  ANTIDDOS_ALERT_CHANNEL=<id>     # staff-only channel (defaults to ATTACK_CHANNEL)
then:  sudo systemctl restart pavlov-bot
and in Discord:  /antiddos status   and   /antiddos allow add ip:<your IP> comment:owner

Rollback at any time:  sudo bash scripts/antiddos/rollback.sh
NEXT
