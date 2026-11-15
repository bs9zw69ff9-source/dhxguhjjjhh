# CrowdSec: banning repeated SSH attempts

The bot has no DDoS monitor or firewall any more. What stays is CrowdSec, which watches the
SSH log and bans an address that keeps failing to log in. CrowdSec does this on its own,
with no part of the bot involved, so it keeps working while the bot is down or being deployed.

## What it does and does not do

- **Does:** bans an address for repeated failed SSH logins and connection attempts, for 4 hours
  by default (`duration:` in `/etc/crowdsec/profiles.yaml`). A separate bouncer service blocks the
  banned address with nftables.
- **Does not:** stop a flood. It reads logs, and a flood that fills the server's connection
  leaves none. Only your host's DDoS protection can stop that.

## Install

```bash
cd /root/pavlov-bot && git pull
sudo OWNER_IP=<the IP you SSH from> bash scripts/crowdsec/install.sh
```

`OWNER_IP` goes on CrowdSec's allowlist **first**, so a fumbled password can never ban you. Without
it the script stops and asks you to confirm. The install:

- adds CrowdSec's signed apt repository and installs `crowdsec`;
- installs the `crowdsecurity/linux` and `crowdsecurity/sshd` collections, and reads SSH from
  `auth.log` (or journald when there is no `auth.log`);
- installs `crowdsec-firewall-bouncer-nftables`, which keeps its own tables (`ip crowdsec`,
  `ip6 crowdsec6`) and never touches ufw's rules, flushes the ruleset or restarts SSH.

It is safe to re-run, and it ends by running the checks.

## Check

```bash
sudo bash scripts/crowdsec/verify.sh
```

Prints PASS, WARN or FAIL for CrowdSec running, the sshd collection, the SSH log being read, the
bouncer, and the allowlist. Useful commands:

```bash
sudo cscli decisions list                   # who is banned now, and why
sudo cscli alerts list                      # what triggered the bans
sudo cscli decisions delete --ip <address>  # lift one ban
sudo cscli allowlists add owner <address> -d "whose it is"   # never ban this address
```

## If you locked yourself out

Use your provider's web console, not SSH, and run `systemctl stop crowdsec-firewall-bouncer`.
Then lift your ban with `cscli decisions delete --ip <your IP>` and allowlist your address.

## Removing the old anti-DDoS setup

A server that ran the earlier anti-DDoS version still has its pieces installed. Remove them with:

```bash
sudo bash scripts/crowdsec/remove-antiddos.sh
```

It removes the bot's nftables table, the packet capture service, ulogd2 logging, the rotation timer,
and CrowdSec's port-scan source. CrowdSec's SSH banning keeps running. Two things are kept unless
you ask, because they are data:

- `WIPE_LOGS=1` also deletes `/var/log/anti-ddos` (the old packet, source, traffic and event logs);
- `REMOVE_BOT_BANS=1` also lifts every ban the old bot placed (reason `pavlovbot: ...`). That
  includes the permanent flood-source bans. Without it they stay in force.

The ulogd2 and tshark packages stay installed, only stopped. `apt remove ulogd2 ulogd2-json tshark`
takes them off.

The old bot's `.env` settings (`ATTACK_*`, `ANTIDDOS_*`, `PACKET_*`) are ignored now and can be deleted.
