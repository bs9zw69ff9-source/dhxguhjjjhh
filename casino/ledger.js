/* ---------------- casino/ledger: atomic caps debit/credit ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./casino/ledger")(ctx). */
module.exports = function(ctx) {
  const {
  logger, readPlayerBalance, writePlayerBalance,
  } = ctx;

/* ---------------- atomic ledger ops ----------------
   readPlayerBalance/writePlayerBalance are a plain read-then-write with no
   locking. mutateBalance() queues per playerId (same pattern as the JSON
   update() helper) so concurrent credit/debit calls on one account can't race
   each other into a double-spend. */
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
    logger.error("Ledger", `mutateBalance failed for ${playerId}: ${err.message}`);
    return { ok: false, before: 0, after: 0 };
  });
  // Track the chain tail so writes stay serialized, then drop the entry once the
  // chain goes idle - otherwise the Map grows one entry per unique player forever.
  const tail = next.catch(() => {});
  _ledgerQueues.set(key, tail);
  tail.finally(() => { if (_ledgerQueues.get(key) === tail) _ledgerQueues.delete(key); });
  return next;
}
function debitCaps(playerId, amount)  { return mutateBalance(playerId, (bal) => bal >= amount ? bal - amount : null); }
function creditCaps(playerId, amount) { return mutateBalance(playerId, (bal) => bal + amount); }


  return { _ledgerQueues, creditCaps, debitCaps, mutateBalance };
};
