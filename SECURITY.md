# Security Policy

## Reporting a vulnerability

If you discover a security issue in this bot, please report it privately to
the copyright holder rather than opening a public issue. Include:

- a description of the issue and its impact,
- steps to reproduce, and
- any relevant logs (with secrets redacted).

Please allow a reasonable window for a fix before any public disclosure.

## Operator guidance

This bot holds privileged credentials. To run it safely:

- **Never commit secrets.** Keep `DISCORD_TOKEN`, RCON passwords, and all
  other secrets in `.env` (git-ignored). Rotate any credential that leaks.
- **Restrict the bot's host.** RCON access is effectively server-admin
  access — run the bot on a trusted machine and lock down the RCON ports.
- **Use roles.** Configure `/setroles` so mod/admin/faction commands are
  limited to the right people. Unset role = unrestricted.
- **Review the owner list.** `OWNER_IDS` in `index.js` grants full,
  un-blacklistable, rate-limit-exempt access. Keep it minimal and audited.
- **Keep data private.** The runtime JSON stores (bans, notes, playtime,
  etc.) contain moderation data and are git-ignored by design.

## Anti-tampering

The application embeds visible authorship/build attribution (see `/help` and
`/ping`) and its integrity is backed by the git commit history. Removing or
altering attribution violates the LICENSE.
