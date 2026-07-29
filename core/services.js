/* ---------------- core/services: background service lifecycle + supervision ----------------
   Long-running work (log tailing, RCON polling, sweeps, leaderboard refreshes) currently
   lives as bare setInterval calls. Those have three problems this module fixes:

     1. An unhandled rejection inside a tick can take down the process. A service tick is
        always caught, recorded, and counted - one failing sweep never affects another.
     2. They cannot be stopped, restarted, or inspected. Shutdown has to leave them
        mid-flight and hope.
     3. Nothing knows whether they are actually running. A timer that has been throwing
        every tick for a week looks identical to a healthy one.

   A service is { name, deps?, intervalMs?, start?, stop?, tick?, healthCheck? }.
   Supplying `tick` gets you a supervised interval; supplying `start`/`stop` gives you
   full control (e.g. a file watcher that owns its own loop).

   Overlap protection is built in: a tick that runs longer than its interval will not be
   re-entered. Without it, a slow RCON sweep stacks up copies of itself until the event
   loop drowns - which is exactly how a healthy bot becomes an unresponsive one. */
"use strict";

const STOPPED = "stopped";
const STARTING = "starting";
const RUNNING = "running";
const FAILED = "failed";

/* Topological sort over service dependencies. Returns start order; throws on a cycle
   naming the participants, because "maximum call stack exceeded" at 3am is not a
   diagnosis. Unknown dependencies are reported rather than silently ignored. */
function orderByDeps(entries) {
  const byName = new Map(entries.map(e => [e.name, e]));
  const state = new Map();      // name -> 0 unvisited | 1 visiting | 2 done
  const out = [];
  const path = [];
  const visit = (name) => {
    const s = state.get(name) ?? 0;
    if (s === 2) return;
    if (s === 1) throw new Error(`circular service dependency: ${[...path, name].join(" -> ")}`);
    const e = byName.get(name);
    if (!e) throw new Error(`service "${path[path.length - 1] ?? "?"}" depends on unknown service "${name}"`);
    state.set(name, 1); path.push(name);
    for (const d of e.deps ?? []) visit(d);
    path.pop(); state.set(name, 2);
    out.push(e);
  };
  for (const e of entries) visit(e.name);
  return out;
}

function createServiceManager({ logger = null, metrics = null, errors = null } = {}) {
  const services = new Map();   // name -> record

  const rec = (def) => ({
    name: def.name,
    deps: def.deps ?? [],
    intervalMs: def.intervalMs ?? null,
    runOnStart: def.runOnStart ?? false,
    critical: def.critical ?? false,
    def,
    state: STOPPED,
    timer: null,
    startedAt: null,
    ticks: 0,
    failures: 0,
    consecutiveFailures: 0,
    lastError: null,
    lastTickAt: null,
    lastDurationMs: null,
    busy: false,
  });

  async function runTick(r) {
    /* Never re-enter. A tick slower than its interval would otherwise pile up copies of
       itself; with N servers and a slow RCON that becomes unbounded concurrency. */
    if (r.busy) {
      metrics?.increment("service_tick_skipped_total", { service: r.name }, 1, "Ticks skipped because the previous one was still running");
      return;
    }
    r.busy = true;
    const t0 = process.hrtime.bigint();
    try {
      await r.def.tick();
      r.consecutiveFailures = 0;
      metrics?.increment("service_ticks_total", { service: r.name, outcome: "success" }, 1, "Service ticks by outcome");
    } catch (err) {
      r.failures++; r.consecutiveFailures++; r.lastError = String(err?.message ?? err);
      metrics?.increment("service_ticks_total", { service: r.name, outcome: "failure" });
      errors?.record(err, { category: "service_tick", subsystem: r.name });
      if (!errors && logger) logger.warn("Services", `${r.name} tick failed: ${r.lastError}`);
    } finally {
      r.busy = false;
      r.ticks++;
      r.lastTickAt = Date.now();
      r.lastDurationMs = Number(process.hrtime.bigint() - t0) / 1e6;
      metrics?.observe("service_tick_duration_ms", r.lastDurationMs, { service: r.name }, "Service tick duration in milliseconds");
    }
  }

  const manager = {
    STOPPED, STARTING, RUNNING, FAILED,

    register(def) {
      if (!def?.name) throw new Error("a service needs a name");
      if (services.has(def.name)) throw new Error(`service "${def.name}" is already registered`);
      if (!def.tick && !def.start) throw new Error(`service "${def.name}" needs a tick() or a start()`);
      services.set(def.name, rec(def));
      return manager;
    },

    async startOne(name) {
      const r = services.get(name);
      if (!r) throw new Error(`unknown service "${name}"`);
      if (r.state === RUNNING) return r;
      r.state = STARTING;
      try {
        if (r.def.start) await r.def.start();
        if (r.def.tick && r.intervalMs) {
          r.timer = setInterval(() => { runTick(r); }, r.intervalMs);
          if (r.timer.unref) r.timer.unref();   // a timer must never hold the process open by itself
          if (r.runOnStart) runTick(r);         // fire-and-forget, never blocks startup
        }
        r.state = RUNNING;
        r.startedAt = Date.now();
        r.consecutiveFailures = 0;
        logger?.debug?.("Services", `${name} started${r.intervalMs ? ` (every ${r.intervalMs}ms)` : ""}`);
        metrics?.gauge("service_up", 1, { service: name }, "1 when a service is running");
        return r;
      } catch (err) {
        r.state = FAILED;
        r.lastError = String(err?.message ?? err);
        metrics?.gauge("service_up", 0, { service: name });
        errors?.record(err, { category: "service_start", subsystem: name });
        /* A non-critical service that fails to start must not abort the rest of
           startup - the bot should come up with one feature dark, not not at all. */
        if (r.critical) throw err;
        logger?.warn?.("Services", `${name} failed to start (non-critical, continuing): ${r.lastError}`);
        return r;
      }
    },

    async stopOne(name) {
      const r = services.get(name);
      if (!r || r.state === STOPPED) return;
      if (r.timer) { clearInterval(r.timer); r.timer = null; }
      try { if (r.def.stop) await r.def.stop(); }
      catch (err) { errors?.record(err, { category: "service_stop", subsystem: name }); }
      r.state = STOPPED;
      r.startedAt = null;
      metrics?.gauge("service_up", 0, { service: name });
      logger?.debug?.("Services", `${name} stopped`);
    },

    async restart(name) {
      await manager.stopOne(name);
      metrics?.increment("service_restarts_total", { service: name }, 1, "Service restarts");
      return manager.startOne(name);
    },

    /** Start everything in dependency order. */
    async startAll() {
      const ordered = orderByDeps([...services.values()]);
      for (const r of ordered) await manager.startOne(r.name);
      logger?.info?.("Services", `${ordered.filter(r => r.state === RUNNING).length}/${ordered.length} background services running`);
      return manager.status();
    },

    /** Stop everything in REVERSE dependency order, so a service is never torn down
        while something that depends on it is still ticking. */
    async stopAll() {
      const ordered = orderByDeps([...services.values()]).reverse();
      for (const r of ordered) await manager.stopOne(r.name);
    },

    /** Restart any service that has failed repeatedly. Called by the supervisor tick. */
    async reviveFailed({ threshold = 5 } = {}) {
      const revived = [];
      for (const r of services.values()) {
        if (r.state === FAILED || (r.state === RUNNING && r.consecutiveFailures >= threshold)) {
          logger?.warn?.("Services", `restarting ${r.name} after ${r.consecutiveFailures} consecutive failures`);
          await manager.restart(r.name);
          revived.push(r.name);
        }
      }
      return revived;
    },

    status() {
      const out = {};
      for (const r of services.values()) {
        out[r.name] = {
          state: r.state,
          uptimeMs: r.startedAt ? Date.now() - r.startedAt : 0,
          intervalMs: r.intervalMs, ticks: r.ticks, failures: r.failures,
          consecutiveFailures: r.consecutiveFailures,
          lastError: r.lastError, lastTickAt: r.lastTickAt,
          lastDurationMs: r.lastDurationMs, busy: r.busy,
        };
      }
      return out;
    },

    /** Roll every service into one health verdict, honouring per-service checks. */
    async health() {
      const components = {};
      let worst = "healthy";
      for (const r of services.values()) {
        let s = r.state === RUNNING ? "healthy" : r.state === STOPPED ? "degraded" : "unhealthy";
        let detail = r.lastError;
        if (r.state === RUNNING && r.consecutiveFailures > 0) { s = "degraded"; detail = `${r.consecutiveFailures} consecutive tick failures`; }
        if (r.state === RUNNING && r.def.healthCheck) {
          try {
            const h = await r.def.healthCheck();
            if (h && h.status && h.status !== "healthy") { s = h.status; detail = h.detail ?? detail; }
          } catch (err) { s = "unhealthy"; detail = String(err.message); }
        }
        components[r.name] = { status: s, detail: detail ?? null, ticks: r.ticks, failures: r.failures };
        if (s === "unhealthy" || (s === "degraded" && worst === "healthy")) worst = s === "unhealthy" ? "unhealthy" : "degraded";
      }
      return { status: worst, components };
    },

    get(name) { return services.get(name) ?? null; },
    names() { return [...services.keys()]; },
    count() { return services.size; },
    running() { return [...services.values()].filter(r => r.state === RUNNING).length; },
  };
  return manager;
}

module.exports = { createServiceManager, orderByDeps, STOPPED, STARTING, RUNNING, FAILED };
