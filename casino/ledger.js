/* ---------------- casino/ledger: atomic caps debit/credit, jackpot pot, shared intake ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./casino/ledger")(ctx). */
module.exports = function(ctx) {
  const {
  FILES, MessageFlags, checkRateLimit, errorEmbed, loadCasinoConfig, loadDiscordLinks,
  logger, rateLimitEmbed, readPlayerBalance, safeRead, safeWrite, update,
  warningEmbed, writePlayerBalance,
  } = ctx;

/* ---------------- casino: atomic ledger ops + shared intake ----------------
   readPlayerBalance/writePlayerBalance are a plain read-then-write with no
   locking - fine for rare admin commands (/givecaps, /adjustcaps) but not for
   a self-service, spammable feature. mutateBalance() queues per playerId (same
   pattern as the JSON update() helper) so concurrent gambles on one account
   can't race each other into a double-spend. */
const _ledgerQueues = new Map();   // lowercased playerId -> tail promise
function mutateBalance(playerId, mutator) {
  const key  = String(playerId).toLowerCase();
  const prev = _ledgerQueues.get(key) ?? Promise.resolve();
  const next = prev.then(async () => {
    const before = readPlayerBalance(playerId) ?? 0;
    const after  = mutator(before);
    if (after === null) return { ok: false, before, after: before };   // mutator veto (e.g. insufficient funds)
    const ok = writePlayerBalance(playerId, after);
    return { ok, before, after: ok ? after : before };
  }).catch(err => {
    logger.error("Casino", `mutateBalance failed for ${playerId}: ${err.message}`);
    return { ok: false, before: 0, after: 0 };
  });
  _ledgerQueues.set(key, next.catch(() => {}));
  return next;
}
function debitCaps(playerId, amount)  { return mutateBalance(playerId, (bal) => bal >= amount ? bal - amount : null); }
function creditCaps(playerId, amount) { return mutateBalance(playerId, (bal) => bal + amount); }

/* The jackpot pot: every losing gamble across the casino feeds it instead of the
   caps just vanishing, and /jackpot lets a player with a big enough bank risk their
   entire balance to take the whole thing. Uses the same serialized update() queue
   as everything else, so concurrent games adding to the pot can't race each other. */
function currentPot() { return safeRead(FILES.CASINO_POT, { amount: 0 }).amount || 0; }
function addToPot(amount) {
  if (!(amount > 0)) return Promise.resolve();
  return update(FILES.CASINO_POT, { amount: 0 }, (p) => ({ amount: (p.amount || 0) + Math.floor(amount) }));
}
// Empties the pot and returns however much was in it (0 if nothing).
async function drainPot() {
  let drained = 0;
  await update(FILES.CASINO_POT, { amount: 0 }, (p) => { drained = p.amount || 0; return { amount: 0 }; });
  return drained;
}

/* Shared 5-per-3-hours cap across EVERY casino game (including /jackpot), keyed to
   the linked Pavlov identity rather than the Discord id so it can't be dodged by
   switching accounts. Persisted to disk so a bot restart doesn't hand out a fresh
   allowance. Each check both reads AND (if allowed) consumes one attempt. */
const GAMBLE_QUOTA_MAX = 5;
const GAMBLE_QUOTA_WINDOW_MS = 3 * 60 * 60 * 1000;
function checkGambleQuota(playerId) {
  const key = String(playerId).toLowerCase();
  const all = safeRead(FILES.CASINO_QUOTA, {});
  const cutoff = Date.now() - GAMBLE_QUOTA_WINDOW_MS;
  const recent = (all[key] || []).filter(ts => ts > cutoff);
  if (recent.length >= GAMBLE_QUOTA_MAX) return { ok: false, resetAt: recent[0] + GAMBLE_QUOTA_WINDOW_MS };
  recent.push(Date.now());
  safeWrite(FILES.CASINO_QUOTA, { ...all, [key]: recent });
  return { ok: true, remaining: GAMBLE_QUOTA_MAX - recent.length };
}
function gambleQuotaLimitEmbed(resetAt) {
  return warningEmbed("Gambling Limit Reached",
    `You've hit the limit of **${GAMBLE_QUOTA_MAX} gambles per 3 hours**. Try again <t:${Math.floor(resetAt / 1000)}:R>.`);
}

/* Shared preflight for every gambling command: casino enabled, caller linked to
   a Pavlov identity, not rate-limited, under the 3-hour gamble quota, bet within
   the configured bounds and no larger than the caller's balance. Replies and
   returns null on any failure; otherwise returns { cfg, playerId, bet, balance }
   with nothing yet debited (and one gamble already counted against the quota). */
async function casinoIntake(interaction) {
  const cfg = loadCasinoConfig();
  if (!cfg.enabled) {
    await interaction.reply({ embeds: [warningEmbed("The House Is Closed", "Gambling is currently disabled.")], flags: MessageFlags.Ephemeral });
    return null;
  }
  const playerId = loadDiscordLinks()[interaction.user.id]?.name;
  if (!playerId) {
    await interaction.reply({ embeds: [warningEmbed("Not Linked", "Link your Discord to your Pavlov username first - use `/link add`.")], flags: MessageFlags.Ephemeral });
    return null;
  }
  if (!checkRateLimit(interaction.user.id, "casino", cfg.cooldownMs)) {
    await interaction.reply({ embeds: [rateLimitEmbed()], flags: MessageFlags.Ephemeral });
    return null;
  }
  const quota = checkGambleQuota(playerId);
  if (!quota.ok) {
    await interaction.reply({ embeds: [gambleQuotaLimitEmbed(quota.resetAt)], flags: MessageFlags.Ephemeral });
    return null;
  }
  const bet = interaction.options.getInteger("bet");
  if (bet < cfg.minBet || bet > cfg.maxBet) {
    await interaction.reply({ embeds: [errorEmbed("Bad Bet", `Bet must be between **${cfg.minBet.toLocaleString()}** and **${cfg.maxBet.toLocaleString()}** credits.`)], flags: MessageFlags.Ephemeral });
    return null;
  }
  const balance = readPlayerBalance(playerId) ?? 0;
  if (bet > balance) {
    await interaction.reply({ embeds: [errorEmbed("Insufficient Credits", `You only have **${balance.toLocaleString()}** credits.`)], flags: MessageFlags.Ephemeral });
    return null;
  }
  return { cfg, playerId, bet, balance };
}


  return { GAMBLE_QUOTA_MAX, GAMBLE_QUOTA_WINDOW_MS, _ledgerQueues, addToPot, casinoIntake, checkGambleQuota, creditCaps, currentPot, debitCaps, drainPot, gambleQuotaLimitEmbed, mutateBalance };
};
