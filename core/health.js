/* ---------------- core/health: component health checks with three-state reporting ----------------
   Every component registers a check that returns healthy / degraded / unhealthy. The
   three states matter operationally: "degraded" is the difference between "page someone"
   and "watch it". A server that is reachable but slow, or one of three RCON hosts being
   down, is degraded - not a failure, not fine.

   Rules that fall out of running this in production:
     - A check that throws is UNHEALTHY, not an exception. The framework must not be
       able to take down the thing it is monitoring.
     - A check that hangs is UNHEALTHY after a timeout. An unbounded await inside a
       health endpoint turns a scrape into a hang, which is how monitoring causes the
       outage it was meant to detect.
     - Results are cached briefly so a scrape loop cannot itself become load. */
"use strict";

const HEALTHY = "healthy";
const DEGRADED = "degraded";
const UNHEALTHY = "unhealthy";
// Worst-wins ordering when rolling components up into one overall status.
const RANK = { [HEALTHY]: 0, [DEGRADED]: 1, [UNHEALTHY]: 2 };

const normalise = (r) => {
  if (r === true) return { status: HEALTHY };
  if (r === false) return { status: UNHEALTHY };
  if (typeof r === "string") return { status: RANK[r] !== undefined ? r : HEALTHY };
  if (r && typeof r === "object") {
    return { status: RANK[r.status] !== undefined ? r.status : HEALTHY,
             detail: r.detail ?? null, ...(r.meta ? { meta: r.meta } : {}) };
  }
  return { status: HEALTHY };
};

function createHealth({ logger = null, timeoutMs = 3000, cacheMs = 1000 } = {}) {
  const checks = new Map();   // name -> { fn, critical, cached: {at, result} }

  async function run(name, entry) {
    const now = Date.now();
    if (entry.cached && now - entry.cached.at < cacheMs) return entry.cached.result;
    const t0 = process.hrtime.bigint();
    let result;
    try {
      // A hung dependency must not hang the endpoint.
      result = normalise(await Promise.race([
        Promise.resolve(entry.fn()),
        new Promise((_, rej) => setTimeout(() => rej(new Error(`health check timed out after ${timeoutMs}ms`)), timeoutMs)),
      ]));
    } catch (err) {
      result = { status: UNHEALTHY, detail: err.message };
      if (logger) logger.debug?.("Health", `${name}: ${err.message}`);
    }
    result = { name, ...result, durationMs: Number(process.hrtime.bigint() - t0) / 1e6, checkedAt: now };
    entry.cached = { at: now, result };
    return result;
  }

  return {
    HEALTHY, DEGRADED, UNHEALTHY,
    /** `critical: false` means this component can be unhealthy without failing the
        overall rollup - use it for optional features (a webhook, an unset detector). */
    register(name, fn, { critical = true } = {}) {
      checks.set(name, { fn, critical, cached: null });
      return () => checks.delete(name);
    },
    unregister(name) { return checks.delete(name); },
    names() { return [...checks.keys()]; },
    /** Every component, plus a worst-wins overall status across the CRITICAL ones. */
    async report() {
      const entries = [...checks.entries()];
      const results = await Promise.all(entries.map(([name, e]) => run(name, e)));
      let overall = HEALTHY;
      for (let i = 0; i < results.length; i++) {
        if (!entries[i][1].critical) continue;
        if (RANK[results[i].status] > RANK[overall]) overall = results[i].status;
      }
      return {
        status: overall,
        checkedAt: new Date().toISOString(),
        components: results.reduce((acc, r) => { acc[r.name] = r; return acc; }, {}),
      };
    },
    async check(name) {
      const e = checks.get(name);
      return e ? run(name, e) : null;
    },
  };
}

module.exports = { createHealth, HEALTHY, DEGRADED, UNHEALTHY };
