/* ---------------- core/errors: centralized error handling, retry policy, circuit breaker ----------------
   Three related pieces, kept in one module because they are only useful together.

   ErrorManager    one place that records every handled failure - counts by category
                   and subsystem, keeps a recent ring for /health, feeds metrics.
   retry()         exponential backoff with FULL JITTER. Fixed backoff makes every
                   caller retry in lockstep after a blip, producing a synchronised
                   burst exactly when the dependency is least able to serve one.
   CircuitBreaker  stops hammering a dependency that is already failing. After N
                   consecutive failures it opens for a cooldown and fails fast; one
                   trial call then decides whether to close again.

   The governing rule: this module exists to CONTAIN failure, so nothing in it may
   introduce any. Recording an error never throws, and a breaker never swallows a
   result - it only decides whether the attempt is made. */
"use strict";

const CLOSED = "closed";     // normal - calls pass through
const OPEN = "open";         // failing - calls rejected immediately
const HALF_OPEN = "half_open";   // cooldown elapsed - one trial call allowed

/** Sleep helper that never keeps the process alive on its own. */
const sleep = (ms) => new Promise(r => { const t = setTimeout(r, ms); if (t.unref) t.unref(); });

/* Full jitter: wait a random duration in [50%, 100%] of the exponential ceiling,
   capped. Spreads retries that would otherwise align. */
function backoffDelay(attempt, { baseMs = 300, maxMs = 30_000, factor = 2 } = {}) {
  const ceiling = Math.min(maxMs, baseMs * Math.pow(factor, attempt));
  return Math.round(ceiling * (0.5 + Math.random() * 0.5));
}

/**
 * Retry an async operation with jittered exponential backoff.
 * `shouldRetry(err)` decides what is transient - retrying a 401 forever is just a
 * slower way to stay broken, so the default retries everything but lets callers narrow.
 */
async function retry(fn, { tries = 3, baseMs = 300, maxMs = 30_000, factor = 2,
                           shouldRetry = () => true, onRetry = null, signal = null } = {}) {
  let lastErr;
  for (let attempt = 0; attempt < tries; attempt++) {
    if (signal?.aborted) throw signal.reason ?? new Error("aborted");
    try { return await fn(attempt); }
    catch (err) {
      lastErr = err;
      if (attempt === tries - 1 || !shouldRetry(err)) break;
      const delay = backoffDelay(attempt, { baseMs, maxMs, factor });
      if (onRetry) { try { onRetry(err, attempt + 1, delay); } catch {} }
      await sleep(delay);
    }
  }
  throw lastErr;
}

class CircuitBreaker {
  constructor({ name = "breaker", failureThreshold = 5, cooldownMs = 30_000,
                successThreshold = 1, onStateChange = null } = {}) {
    Object.assign(this, { name, failureThreshold, cooldownMs, successThreshold, onStateChange });
    this.state = CLOSED;
    this.failures = 0;
    this.successes = 0;
    this.openedAt = 0;
    this.rejected = 0;      // calls short-circuited while open
  }
  _to(state) {
    if (this.state === state) return;
    const from = this.state;
    this.state = state;
    if (state === OPEN) this.openedAt = Date.now();
    if (state === CLOSED) { this.failures = 0; this.successes = 0; }
    if (this.onStateChange) { try { this.onStateChange(from, state, this.name); } catch {} }
  }
  /** True when a call is allowed through right now. */
  allows() {
    if (this.state === CLOSED) return true;
    if (this.state === OPEN) {
      if (Date.now() - this.openedAt < this.cooldownMs) return false;
      this._to(HALF_OPEN);        // cooldown elapsed - let exactly one trial through
      return true;
    }
    return true;                  // HALF_OPEN: the trial call
  }
  recordSuccess() {
    if (this.state === HALF_OPEN) {
      if (++this.successes >= this.successThreshold) this._to(CLOSED);
      return;
    }
    this.failures = 0;
  }
  recordFailure() {
    if (this.state === HALF_OPEN) { this._to(OPEN); return; }   // trial failed - straight back to open
    if (++this.failures >= this.failureThreshold) this._to(OPEN);
  }
  /** Run `fn` through the breaker. Throws a tagged error when open. */
  async run(fn) {
    if (!this.allows()) {
      this.rejected++;
      const err = new Error(`circuit "${this.name}" is open - not attempting`);
      err.code = "CIRCUIT_OPEN";
      throw err;
    }
    try { const out = await fn(); this.recordSuccess(); return out; }
    catch (err) { this.recordFailure(); throw err; }
  }
  status() {
    return { name: this.name, state: this.state, failures: this.failures,
             rejected: this.rejected,
             openFor: this.state === OPEN ? Date.now() - this.openedAt : 0 };
  }
}

function createErrorManager({ logger = null, metrics = null, recentLimit = 50 } = {}) {
  const counts = new Map();     // "category|subsystem" -> n
  const recent = [];
  const breakers = new Map();

  return {
    /** Record a handled failure. Never throws - callers are usually in a catch already. */
    record(err, { category = "unhandled", subsystem = "unknown", context = null, level = "error" } = {}) {
      try {
        const key = `${category}|${subsystem}`;
        counts.set(key, (counts.get(key) ?? 0) + 1);
        const entry = {
          at: Date.now(), category, subsystem, level,
          message: String(err?.message ?? err).slice(0, 500),
          stack: err?.stack ? String(err.stack).split("\n").slice(0, 8).join("\n") : null,
          context: context ?? null,
        };
        recent.push(entry);
        if (recent.length > recentLimit) recent.splice(0, recent.length - recentLimit);
        metrics?.increment("errors_total", { category, subsystem }, 1, "Handled errors by category and subsystem");
        if (logger) {
          const fn = level === "warn" ? logger.warn : logger.error;
          fn?.call(logger, subsystem, `[${category}] ${entry.message}`, err);
        }
        return entry;
      } catch { return null; }
    },
    /** Wrap an async fn so any rejection is recorded and (optionally) swallowed. */
    guard(fn, opts = {}) {
      return async (...args) => {
        try { return await fn(...args); }
        catch (err) { this.record(err, opts); if (opts.rethrow) throw err; return opts.fallback ?? null; }
      };
    },
    breaker(name, opts = {}) {
      let b = breakers.get(name);
      if (!b) {
        b = new CircuitBreaker({
          name, ...opts,
          onStateChange: (from, to) => {
            logger?.warn?.("Circuit", `${name}: ${from} -> ${to}`);
            metrics?.increment("circuit_state_changes_total", { breaker: name, to });
          },
        });
        breakers.set(name, b);
      }
      return b;
    },
    breakers: () => [...breakers.values()].map(b => b.status()),
    counts: () => Object.fromEntries(counts),
    recent: (n = 10) => recent.slice(-n),
    total: () => [...counts.values()].reduce((a, b) => a + b, 0),
    reset() { counts.clear(); recent.length = 0; breakers.clear(); },
  };
}

module.exports = { createErrorManager, CircuitBreaker, retry, backoffDelay, sleep,
                   CLOSED, OPEN, HALF_OPEN };
