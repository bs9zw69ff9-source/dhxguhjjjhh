/* Tests for the architecture core: metrics, health, errors/retry/breaker, the service
   manager, the plugin loader and the monitoring endpoints.

   The recurring property under test is ISOLATION: this framework exists to contain
   failure, so a broken check, plugin or service tick must never propagate. Several
   tests deliberately throw inside the framework and assert the process carries on. */
const { test } = require("node:test");
const assert = require("node:assert");

const { createMetrics } = require("../core/metrics");
const { createHealth } = require("../core/health");
const { createErrorManager, CircuitBreaker, retry, backoffDelay } = require("../core/errors");
const { createServiceManager, orderByDeps } = require("../core/services");
const { createPluginManager, orderPlugins, validateManifest } = require("../core/plugins");
const { createMonitoringServer } = require("../core/monitoring-server");

const tick = () => new Promise(r => setTimeout(r, 5));

/* ---------------- metrics ---------------- */

test("counters, gauges and histograms render in Prometheus format", () => {
  const m = createMetrics({ prefix: "bot" });
  m.increment("commands_total", { command: "ban" });
  m.increment("commands_total", { command: "ban" });
  m.gauge("memory_bytes", 1234);
  m.observe("rcon_latency_ms", 40, { server: "server1" });
  const out = m.render();
  assert.match(out, /# TYPE bot_commands_total counter/);
  assert.match(out, /bot_commands_total\{command="ban"\} 2/);
  assert.match(out, /bot_memory_bytes 1234/);
  assert.match(out, /bot_rcon_latency_ms_bucket\{le="50",server="server1"\} 1/);
  assert.match(out, /bot_rcon_latency_ms_count\{server="server1"\} 1/);
});

test("label order does not create duplicate series", () => {
  // {a,b} and {b,a} are the same series; keying on insertion order silently doubles them.
  const m = createMetrics();
  m.increment("x", { a: "1", b: "2" });
  m.increment("x", { b: "2", a: "1" });
  assert.equal(m.stats().series, 1);
  assert.match(m.render(), /bot_x\{a="1",b="2"\} 2/);
});

test("histogram buckets are cumulative", () => {
  const m = createMetrics();
  for (const v of [1, 30, 300]) m.observe("d", v);
  const out = m.render();
  assert.match(out, /bot_d_bucket\{le="5"\} 1/);      // just the 1
  assert.match(out, /bot_d_bucket\{le="50"\} 2/);     // 1 and 30
  assert.match(out, /bot_d_bucket\{le="500"\} 3/);    // all three
});

test("metrics never throw, whatever they are handed", () => {
  const m = createMetrics();
  assert.doesNotThrow(() => { m.increment(null); m.gauge("g", undefined); m.observe("h", NaN); });
});

test("time() records duration and outcome, and still propagates the error", async () => {
  const m = createMetrics();
  await m.time("op_ms", { op: "a" }, async () => "ok");
  await assert.rejects(() => m.time("op_ms", { op: "b" }, async () => { throw new Error("boom"); }));
  const out = m.render();
  assert.match(out, /op_ms_count\{op="a",outcome="success"\} 1/);
  assert.match(out, /op_ms_count\{op="b",outcome="failure"\} 1/);
});

/* ---------------- health ---------------- */

test("health rolls components up worst-wins", async () => {
  const h = createHealth({ cacheMs: 0 });
  h.register("db", () => "healthy");
  h.register("rcon", () => ({ status: "degraded", detail: "1 of 2 servers down" }));
  const r = await h.report();
  assert.equal(r.status, "degraded");
  assert.equal(r.components.rcon.detail, "1 of 2 servers down");
  h.register("discord", () => "unhealthy");
  assert.equal((await h.report()).status, "unhealthy");
});

test("a check that throws is unhealthy, not an exception", async () => {
  const h = createHealth({ cacheMs: 0 });
  h.register("bad", () => { throw new Error("kaboom"); });
  const r = await h.report();
  assert.equal(r.status, "unhealthy");
  assert.match(r.components.bad.detail, /kaboom/);
});

test("a hanging check times out instead of hanging the report", async () => {
  // Monitoring that can hang is monitoring that causes outages.
  const h = createHealth({ timeoutMs: 40, cacheMs: 0 });
  h.register("slow", () => new Promise(() => {}));
  const r = await h.report();
  assert.equal(r.components.slow.status, "unhealthy");
  assert.match(r.components.slow.detail, /timed out/);
});

test("non-critical components do not drag the overall status down", async () => {
  const h = createHealth({ cacheMs: 0 });
  h.register("core", () => "healthy");
  h.register("optional-webhook", () => "unhealthy", { critical: false });
  const r = await h.report();
  assert.equal(r.status, "healthy", "an unset optional feature is not an outage");
  assert.equal(r.components["optional-webhook"].status, "unhealthy");
});

/* ---------------- errors, retry, circuit breaker ---------------- */

test("the error manager counts by category and keeps a recent ring", () => {
  const em = createErrorManager({ recentLimit: 3 });
  em.record(new Error("a"), { category: "rcon", subsystem: "server1" });
  em.record(new Error("b"), { category: "rcon", subsystem: "server1" });
  em.record(new Error("c"), { category: "vpn", subsystem: "sentinel" });
  assert.equal(em.counts()["rcon|server1"], 2);
  assert.equal(em.total(), 3);
  for (let i = 0; i < 5; i++) em.record(new Error("x"), { category: "spam" });
  assert.equal(em.recent(99).length, 3, "the ring is bounded");
});

test("recording an error never throws, even on rubbish input", () => {
  const em = createErrorManager();
  assert.doesNotThrow(() => { em.record(null); em.record(undefined, { category: null }); em.record("a string"); });
});

test("retry backs off and eventually succeeds", async () => {
  let n = 0;
  const out = await retry(async () => { if (++n < 3) throw new Error("transient"); return "done"; },
    { tries: 5, baseMs: 1 });
  assert.equal(out, "done");
  assert.equal(n, 3);
});

test("retry gives up on a non-retryable error immediately", async () => {
  let n = 0;
  await assert.rejects(() => retry(async () => { n++; const e = new Error("401"); e.status = 401; throw e; },
    { tries: 5, baseMs: 1, shouldRetry: (e) => e.status !== 401 }));
  assert.equal(n, 1, "retrying an auth failure is just a slower way to stay broken");
});

test("backoff jitter stays within 50-100% of the exponential ceiling", () => {
  for (let attempt = 0; attempt < 4; attempt++) {
    const ceiling = 300 * Math.pow(2, attempt);
    for (let i = 0; i < 50; i++) {
      const d = backoffDelay(attempt, { baseMs: 300 });
      assert.ok(d >= ceiling * 0.5 - 1 && d <= ceiling, `${d} outside [${ceiling * 0.5}, ${ceiling}]`);
    }
  }
});

test("the circuit breaker opens, fails fast, then recovers", async () => {
  const b = new CircuitBreaker({ name: "t", failureThreshold: 2, cooldownMs: 30 });
  const boom = () => Promise.reject(new Error("down"));
  await assert.rejects(() => b.run(boom));
  await assert.rejects(() => b.run(boom));
  assert.equal(b.status().state, "open");
  // While open it must not attempt the call at all.
  let attempted = false;
  await assert.rejects(() => b.run(async () => { attempted = true; }), /is open/);
  assert.equal(attempted, false, "an open circuit must not touch the dependency");
  await new Promise(r => setTimeout(r, 45));
  assert.equal(await b.run(async () => "back"), "back");
  assert.equal(b.status().state, "closed");
});

test("a failed trial call re-opens the circuit rather than closing it", async () => {
  const b = new CircuitBreaker({ name: "t2", failureThreshold: 1, cooldownMs: 20 });
  await assert.rejects(() => b.run(() => Promise.reject(new Error("x"))));
  assert.equal(b.status().state, "open");
  await new Promise(r => setTimeout(r, 30));
  await assert.rejects(() => b.run(() => Promise.reject(new Error("still down"))));
  assert.equal(b.status().state, "open", "one failing trial must not be read as recovery");
});

/* ---------------- service manager ---------------- */

test("services start in dependency order and stop in reverse", async () => {
  const order = [];
  const sm = createServiceManager();
  sm.register({ name: "c", deps: ["b"], start: async () => order.push("start:c"), stop: async () => order.push("stop:c") });
  sm.register({ name: "a", start: async () => order.push("start:a"), stop: async () => order.push("stop:a") });
  sm.register({ name: "b", deps: ["a"], start: async () => order.push("start:b"), stop: async () => order.push("stop:b") });
  await sm.startAll();
  await sm.stopAll();
  assert.deepEqual(order, ["start:a", "start:b", "start:c", "stop:c", "stop:b", "stop:a"]);
});

test("a circular service dependency is reported by name", () => {
  assert.throws(() => orderByDeps([{ name: "a", deps: ["b"] }, { name: "b", deps: ["a"] }]),
    /circular service dependency: a -> b -> a/);
});

test("an unknown dependency is named rather than silently ignored", () => {
  assert.throws(() => orderByDeps([{ name: "a", deps: ["ghost"] }]), /unknown service "ghost"/);
});

test("a throwing tick is contained and counted, and the service keeps running", async () => {
  const sm = createServiceManager();
  let n = 0;
  sm.register({ name: "flaky", intervalMs: 10, tick: async () => { n++; throw new Error("tick failed"); } });
  await sm.startAll();
  await new Promise(r => setTimeout(r, 60));
  await sm.stopAll();
  assert.ok(n >= 2, "it must keep ticking after a failure");
  const st = sm.status().flaky;
  assert.ok(st.failures >= 2);
  assert.match(st.lastError, /tick failed/);
});

test("a slow tick is never re-entered", async () => {
  // Without this an RCON sweep slower than its interval stacks up copies of itself.
  const sm = createServiceManager();
  let concurrent = 0, maxConcurrent = 0;
  sm.register({ name: "slow", intervalMs: 5, tick: async () => {
    maxConcurrent = Math.max(maxConcurrent, ++concurrent);
    await new Promise(r => setTimeout(r, 40));
    concurrent--;
  } });
  await sm.startAll();
  await new Promise(r => setTimeout(r, 120));
  await sm.stopAll();
  assert.equal(maxConcurrent, 1, "ticks must not overlap");
});

test("a non-critical service that fails to start does not abort startup", async () => {
  const sm = createServiceManager();
  sm.register({ name: "broken", start: async () => { throw new Error("nope"); } });
  sm.register({ name: "fine", start: async () => {} });
  await sm.startAll();
  assert.equal(sm.status().broken.state, "failed");
  assert.equal(sm.status().fine.state, "running", "one dark feature, not a dead bot");
});

test("a critical service that fails to start does abort startup", async () => {
  const sm = createServiceManager();
  sm.register({ name: "must-work", critical: true, start: async () => { throw new Error("fatal"); } });
  await assert.rejects(() => sm.startAll(), /fatal/);
});

test("restart clears state and health reflects failures", async () => {
  const sm = createServiceManager();
  sm.register({ name: "s", intervalMs: 10, tick: async () => { throw new Error("bad"); } });
  await sm.startAll();
  await new Promise(r => setTimeout(r, 35));
  const degraded = await sm.health();
  assert.equal(degraded.status, "degraded");
  await sm.restart("s");
  assert.equal(sm.status().s.state, "running");
  await sm.stopAll();
});

test("stopAll clears timers so nothing keeps ticking after shutdown", async () => {
  const sm = createServiceManager();
  let n = 0;
  sm.register({ name: "t", intervalMs: 5, tick: async () => { n++; } });
  await sm.startAll();
  await new Promise(r => setTimeout(r, 30));
  await sm.stopAll();
  const after = n;
  await new Promise(r => setTimeout(r, 30));
  assert.equal(n, after, "a stopped service must be genuinely stopped");
});

/* ---------------- plugins ---------------- */

const plugin = (name, over = {}) => ({
  name, version: "1.0.0", create: () => ({}), ...over,
});

test("plugins initialise in dependency order and contribute commands", async () => {
  const order = [];
  const pm = createPluginManager();
  pm.register(plugin("b", { dependsOn: ["a"], create: () => ({
    initialize: async () => order.push("b"), registerCommands: () => ({ bcmd: () => {} }) }) }));
  pm.register(plugin("a", { create: () => ({
    initialize: async () => order.push("a"), registerCommands: () => ({ acmd: () => {} }) }) }));
  const { commands } = await pm.initializeAll({});
  assert.deepEqual(order, ["a", "b"]);
  assert.deepEqual(Object.keys(commands).sort(), ["acmd", "bcmd"]);
});

test("a plugin that throws while initialising is skipped, not fatal", async () => {
  const pm = createPluginManager();
  pm.register(plugin("bad", { create: () => ({ initialize: async () => { throw new Error("broken"); } }) }));
  pm.register(plugin("good", { create: () => ({ registerCommands: () => ({ ok: () => {} }) }) }));
  const { commands } = await pm.initializeAll({});
  assert.ok(commands.ok, "the healthy plugin still loads");
  assert.match(pm.failures().bad, /broken/);
  assert.equal(pm.list().find(p => p.name === "bad").state, "failed");
});

test("disabled plugins are never registered", () => {
  const pm = createPluginManager({ isEnabled: (n) => n !== "casino" });
  pm.register(plugin("casino"));
  pm.register(plugin("moderation"));
  assert.deepEqual(pm.list().map(p => p.name), ["moderation"]);
});

test("an invalid manifest is rejected with a specific reason", () => {
  assert.match(validateManifest({}, "x").message, /missing a string `name`/);
  assert.match(validateManifest({ name: "a" }, "x").message, /missing a `create\(ctx\)` factory/);
  assert.match(validateManifest({ name: "a", version: "banana", create() {} }, "x").message, /not x\.y\.z/);
  assert.equal(validateManifest({ name: "a", version: "1.2.3", create() {} }, "x"), null);
});

test("circular and missing plugin dependencies are named", () => {
  assert.throws(() => orderPlugins([plugin("a", { dependsOn: ["b"] }), plugin("b", { dependsOn: ["a"] })]),
    /circular plugin dependency/);
  assert.throws(() => orderPlugins([plugin("a", { dependsOn: ["nope"] })]), /not loaded or is disabled/);
});

test("stopAll tears plugins down in reverse and survives a throwing stop", async () => {
  const order = [];
  const pm = createPluginManager();
  pm.register(plugin("a", { create: () => ({ stop: async () => order.push("a") }) }));
  pm.register(plugin("b", { dependsOn: ["a"], create: () => ({ stop: async () => { order.push("b"); throw new Error("leaky"); } }) }));
  await pm.initializeAll({});
  await pm.stopAll();
  assert.deepEqual(order, ["b", "a"], "a throwing stop must not strand the plugins behind it");
});

/* ---------------- monitoring endpoints ---------------- */

function fakeRes() {
  return { code: null, headers: null, body: null,
           writeHead(c, h) { this.code = c; this.headers = h; },
           end(b) { this.body = b; } };
}

test("/metrics serves Prometheus text", async () => {
  const metrics = createMetrics();
  metrics.increment("commands_total", { command: "ban" });
  const s = createMonitoringServer({ metrics, health: createHealth() });
  const res = fakeRes();
  await s._handle({ method: "GET", url: "/metrics", headers: {} }, res);
  assert.equal(res.code, 200);
  assert.match(res.body, /bot_commands_total\{command="ban"\} 1/);
  assert.match(res.headers["Content-Type"], /version=0\.0\.4/);
});

test("/health is 200 when degraded and 503 only when unhealthy", async () => {
  // A degraded bot must not be restarted or pulled from a load balancer.
  const health = createHealth({ cacheMs: 0 });
  let state = "degraded";
  health.register("x", () => state);
  const s = createMonitoringServer({ metrics: createMetrics(), health });
  let res = fakeRes();
  await s._handle({ method: "GET", url: "/health", headers: {} }, res);
  assert.equal(res.code, 200);
  state = "unhealthy";
  res = fakeRes();
  await s._handle({ method: "GET", url: "/health", headers: {} }, res);
  assert.equal(res.code, 503);
});

test("a token is required when set, but never for liveness", async () => {
  const s = createMonitoringServer({ metrics: createMetrics(), health: createHealth(), token: "secret" });
  let res = fakeRes();
  await s._handle({ method: "GET", url: "/metrics", headers: {} }, res);
  assert.equal(res.code, 401);
  res = fakeRes();
  await s._handle({ method: "GET", url: "/metrics", headers: { authorization: "Bearer secret" } }, res);
  assert.equal(res.code, 200);
  // /healthz stays open so an orchestrator can still restart a misconfigured bot.
  res = fakeRes();
  await s._handle({ method: "GET", url: "/healthz", headers: {} }, res);
  assert.equal(res.code, 200);
});

test("the endpoint is disabled entirely without a port", async () => {
  const s = createMonitoringServer({ metrics: createMetrics(), health: createHealth() });
  assert.equal(await s.start(), null);
  assert.equal(s.listening, false, "no port means no listener and no attack surface");
});

test("it binds to loopback by default when a port IS given", async () => {
  const s = createMonitoringServer({ metrics: createMetrics(), health: createHealth(), port: 0 });
  await s.start();
  assert.equal(s.address().address, "127.0.0.1", "these endpoints leak internal state - never public by default");
  await s.stop();
});

test("non-GET methods and unknown paths are refused", async () => {
  const s = createMonitoringServer({ metrics: createMetrics(), health: createHealth() });
  let res = fakeRes();
  await s._handle({ method: "POST", url: "/metrics", headers: {} }, res);
  assert.equal(res.code, 405);
  res = fakeRes();
  await s._handle({ method: "GET", url: "/../../etc/passwd", headers: {} }, res);
  assert.equal(res.code, 404);
  assert.equal(res.body, "", "nothing to enumerate");
});
