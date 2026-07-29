# Architecture

How the bot is put together, and how to add to it.

> **Status.** The framework described here is complete, tested and running. The
> migration of existing features into plugins is **in progress** — see
> [Migration status](#migration-status) for exactly what has and has not moved. Nothing
> below describes something that is not in the repository.

---

## Layers

```
index.js                    composition root — builds everything, wires it, starts it
  │
  ├── core/                 architecture: no domain knowledge, no bot logic
  │     metrics.js            counters/gauges/histograms + Prometheus exposition
  │     health.js             three-state component health with timeouts
  │     errors.js             ErrorManager, retry(), CircuitBreaker
  │     services.js           ServiceManager — supervised background work
  │     plugins.js            discovery, dependency ordering, lifecycle
  │     container.js          DI container (lazy, memoised, cycle-detecting)
  │     monitoring-server.js  /metrics, /health, /healthz, /ready
  │     background-services.js  the bot's recurring work, registered as services
  │
  ├── plugins/              optional features, auto-discovered
  │     analytics/            reference implementation
  │     _example/             template (leading _ = never loaded)
  │
  └── <domain modules>      commands/ moderation/ rcon/ database/ events/
                            factions/ leaderboards/ penal/ casino/ stats/ utils/
```

**The dependency rule:** `core/` knows nothing about Pavlov, Discord or moderation.
Domain modules know nothing about `core/` unless it is handed to them. `index.js` is
the only file that knows about everything — which is why nothing imports it and there
are no circular requires. `test/wiring.test.js` enforces this statically.

---

## Dependency injection

Every non-pure module is a factory taking one context object:

```js
module.exports = (ctx) => {
  const { logger, sendRcon, metrics } = ctx;
  return { /* public interface only */ };
};
```

`index.js` builds that context and passes it down. A module never reaches for a global,
never requires `index.js`, and exposes only what callers need. `core/container.js`
formalises the same pattern with lazy resolution and cycle detection by name; its
`ctx()` produces the identical flat object, so both styles interoperate and migration
can proceed one module at a time.

---

## Background services

Recurring work runs under the `ServiceManager` rather than as bare `setInterval` calls.
That buys four things a raw timer cannot:

| | |
|---|---|
| **Failure containment** | A throwing tick is caught, counted and reported. It never becomes an unhandled rejection. |
| **No re-entry** | A tick slower than its interval is skipped, not stacked. A stalled RCON sweep cannot pile up copies of itself until the event loop drowns. |
| **Lifecycle** | `start` / `stop` / `restart`, in dependency order, reversed on shutdown. |
| **Observability** | Tick counts, failures, durations and uptime, per service. |

Registering one:

```js
services.register({
  name: "ban-sweep",
  deps: ["rcon"],          // started after these, stopped before them
  intervalMs: 30_000,
  runOnStart: false,       // also fire immediately on start
  critical: false,         // true = a start failure aborts boot
  tick: async () => { /* one pass */ },
  healthCheck: async () => ({ status: "healthy" }),   // optional
});
```

Supply `tick` for a supervised interval, or `start`/`stop` for something owning its own
loop (a file watcher). A **non-critical** service that fails to start leaves the bot up
with one feature dark — that is deliberate, and the opposite of what a bare timer does.

A supervisor tick restarts any service with 5+ consecutive failures.

Current services live in `core/background-services.js`: ban expiry, ban sweep, ban
reconcile, sentence sweep, rank suspensions, donator restores, leaderboards, arrest
board, player list, dashboard, RCON health, player cache, DB export, faction backup,
banlist sync, ModSave sync, plus `metrics-sampler` and `service-supervisor`.

---

## Plugins

Auto-discovered from `plugins/`. A directory with an `index.js`, or a bare `.js` file.
A leading `_` or `.` means "never load" — that is how `_example/` sits next to real
plugins without running.

```js
module.exports = {
  name: "analytics",
  version: "1.0.0",
  description: "one line",
  dependsOn: ["rcon"],
  enabledByDefault: true,
  create(ctx) {
    return {
      async initialize() {},
      registerCommands() { return { mycommand: handler }; },
      registerEvents()   { return { onPlayerJoin: handler }; },
      async start() {},
      async stop()  {},
    };
  },
};
```

Every hook is optional. Order is topological over `dependsOn`; teardown is reversed.
**A plugin that throws while loading, initialising or starting is skipped and recorded —
never fatal.** One broken feature must not be a dead bot.

Control which load:

```bash
PLUGINS_DISABLED=analytics,casino     # deny-list
PLUGINS_ENABLED=moderation,rcon       # allow-list (wins over enabledByDefault)
```

### Adding a plugin

1. `cp -r plugins/_example plugins/myfeature`
2. Set `name`, `version`, `description`, `dependsOn`.
3. Implement only the hooks you need.
4. Take everything from `ctx`. Never `require("../../index.js")`.
5. `unref()` every timer, and release in `stop()` exactly what you acquired in `start()`.

No registration step — it is found on next boot.

---

## Monitoring

Disabled unless `METRICS_PORT` is set. **Binds to `127.0.0.1` by default**, because
these endpoints expose service names, error messages and internal state; on a game
server with a public interface, a default of `0.0.0.0` would publish that to the
internet. Widen it consciously with `METRICS_HOST`, and set `METRICS_TOKEN` if you do.

| Endpoint | Purpose |
|---|---|
| `GET /metrics` | Prometheus text exposition |
| `GET /health` | Full JSON report. `200` healthy **or degraded**, `503` unhealthy |
| `GET /healthz` | Liveness. Always open, even with a token set |
| `GET /ready` | Readiness — has the bot finished starting |

`/health` returns 200 when degraded on purpose: a degraded bot must not be pulled from a
load balancer or restarted for one optional detector being down. `/healthz` stays
unauthenticated so an orchestrator can still restart a bot whose config is broken.

### Health states

- **healthy** — working
- **degraded** — working, but not fully. One of three RCON servers down; a slow gateway
- **unhealthy** — not working

A check that throws is *unhealthy*, not an exception. A check that hangs is *unhealthy*
after a timeout — monitoring that can hang is monitoring that causes outages.
Non-critical components (`{ critical: false }`) never drag the overall status down.

Registered checks: `discord`, `database`, `rcon`, `services`, `filesystem`,
`webhooks` (non-critical), `plugins` (non-critical).

### Metrics

Everything is prefixed `pavlovbot_`.

| Metric | Type | |
|---|---|---|
| `commands_total{command,outcome}` | counter | slash commands run |
| `command_duration_ms{command}` | histogram | execution time |
| `rcon_latency_ms{server,outcome}` | histogram | RCON round trip |
| `rcon_servers_up` | gauge | reachable servers |
| `discord_ping_ms` | gauge | gateway latency |
| `service_ticks_total{service,outcome}` | counter | background work |
| `service_tick_duration_ms{service}` | histogram | tick time |
| `service_tick_skipped_total{service}` | counter | overlap protection firing |
| `service_restarts_total{service}` | counter | supervisor restarts |
| `service_up{service}` / `services_running` | gauge | liveness |
| `plugin_load_duration_ms{plugin}` / `plugin_up{plugin}` | histogram / gauge | plugin load |
| `errors_total{category,subsystem}` | counter | handled failures |
| `circuit_state_changes_total{breaker,to}` | counter | breaker transitions |
| `memory_rss_bytes`, `memory_heap_used_bytes`, `uptime_seconds` | gauge | process |
| `flagged_ips`, `flagged_names`, `flagged_ids` | gauge | ban-evasion registry |

Series are capped at 5,000. Metrics are fed from user-influenced values (command names,
server labels); an unbounded registry is a memory leak with extra steps.

---

## Error handling

`core/errors.js` provides three things that are only useful together:

**ErrorManager** — one place that records every handled failure. Counts by
category and subsystem, keeps a bounded recent ring for `/health`, feeds metrics.
`record()` never throws; callers are usually already in a `catch`.

**retry()** — exponential backoff with **full jitter** (50–100% of the ceiling). Fixed
backoff makes every caller retry in lockstep after a blip, producing a synchronised
burst exactly when the dependency is least able to serve one. `shouldRetry` narrows
what counts as transient — retrying a 401 forever is a slower way to stay broken.

**CircuitBreaker** — after N consecutive failures it opens, fails fast for a cooldown,
then allows one trial call. A failing trial re-opens rather than closing; one success is
not recovery.

```js
await errors.breaker("sentinel", { failureThreshold: 5, cooldownMs: 60_000 })
  .run(() => fetchJson(url));
```

Process-level `unhandledRejection` and `uncaughtException` handlers record and continue.
An error in one subsystem is not a reason to drop every other one.

---

## Migration status

Being precise, because "refactored" can mean very little.

**Done**

- `core/` framework: metrics, health, errors, services, plugins, container, monitoring
  server — 40 dedicated tests
- All 16 background `setInterval` calls now run as supervised services
- Health checks across every subsystem
- `/metrics`, `/health`, `/healthz`, `/ready`
- Command execution instrumented
- Process-level error handlers
- Plugin framework with auto-discovery, one reference plugin, one template
- Graceful shutdown stops plugins → services → monitoring before draining writes

**Not done**

- **`index.js` is still ~3,100 lines, not under 200.** The framework it needs is in
  place and the recurring work has moved out, but the ~2,700 lines of helpers, embed
  builders and feature logic have not yet been extracted into plugins. Doing that
  wholesale in one pass, against a bot running a live server, would risk exactly the
  kind of regression the brief forbids. The remaining extraction is mechanical and can
  proceed one domain at a time — `moderation`, `factions`, `economy`, `leaderboards` —
  each behind the existing test suite.
- Existing domain modules are not yet plugins. They already take `ctx`, so wrapping each
  in the plugin contract is small; the risk is in the ordering and shared state, not the
  shape.

**Deliberately unchanged**

No command, permission, embed, database schema or configuration key was altered. The
236-test suite passes unchanged, which is the evidence for that claim.
