/* ---------------- core/monitoring-server: /metrics and /health over HTTP ----------------
   A small node:http server (no express, no new dependency) exposing:

     GET /metrics   Prometheus text exposition - scrape target
     GET /health    full JSON report, 200 healthy/degraded, 503 unhealthy
     GET /healthz   liveness only: is the process up. Always 200 if it can answer.
     GET /ready     readiness: 200 once the bot has finished starting

   Security posture, deliberately conservative:
     - BINDS TO 127.0.0.1 BY DEFAULT. These endpoints expose service names, error
       messages and internal state; on a game server with a public interface, a default
       of 0.0.0.0 would publish that to the internet. Set METRICS_HOST to widen it
       consciously.
     - Optional bearer token (METRICS_TOKEN) for when it must be exposed.
     - Disabled unless METRICS_PORT is set. No port, no listener, no attack surface.
     - Unknown paths get a bare 404 with no body - nothing to enumerate. */
"use strict";

const http = require("http");

function createMonitoringServer({ metrics, health, logger = null, errors = null,
                                  port = null, host = "127.0.0.1", token = null,
                                  readiness = () => true } = {}) {
  let server = null;

  const send = (res, code, body, type = "text/plain; charset=utf-8") => {
    res.writeHead(code, { "Content-Type": type, "Cache-Control": "no-store" });
    res.end(body);
  };

  async function handle(req, res) {
    // Only GET/HEAD. Nothing here mutates state, so anything else is a mistake or a probe.
    if (req.method !== "GET" && req.method !== "HEAD") return send(res, 405, "method not allowed");
    const url = (req.url || "/").split("?")[0];

    if (token) {
      const auth = req.headers.authorization || "";
      // Liveness stays open so an orchestrator can restart a bot whose config is broken.
      if (url !== "/healthz" && auth !== `Bearer ${token}`) return send(res, 401, "unauthorized");
    }

    try {
      if (url === "/metrics") return send(res, 200, metrics.render(), "text/plain; version=0.0.4; charset=utf-8");
      if (url === "/healthz") return send(res, 200, "ok");
      if (url === "/ready") {
        const ready = await readiness();
        return send(res, ready ? 200 : 503, JSON.stringify({ ready }), "application/json");
      }
      if (url === "/health") {
        const report = await health.report();
        // 503 only for unhealthy: "degraded" must not take a bot out of a load balancer
        // or trigger a restart loop when one optional detector is down.
        return send(res, report.status === "unhealthy" ? 503 : 200,
          JSON.stringify(report, null, 2), "application/json");
      }
      return send(res, 404, "");
    } catch (err) {
      errors?.record(err, { category: "monitoring_http", subsystem: "monitoring" });
      return send(res, 500, "internal error");
    }
  }

  return {
    async start() {
      /* Explicitly "not configured", not merely falsy: port 0 is a valid request for an
         ephemeral port, and `!port` would silently disable it. */
      const disabled = port === null || port === undefined || port === "" || Number.isNaN(Number(port));
      if (disabled) { logger?.debug?.("Monitoring", "METRICS_PORT not set - metrics endpoint disabled"); return null; }
      if (server) return server;
      server = http.createServer((req, res) => { handle(req, res).catch(() => { try { send(res, 500, "internal error"); } catch {} }); });
      server.on("error", (err) => {
        errors?.record(err, { category: "monitoring_listen", subsystem: "monitoring" });
        logger?.warn?.("Monitoring", `metrics endpoint failed: ${err.message}`);
        server = null;
      });
      await new Promise((resolve) => server.listen(port, host, resolve));
      if (server.unref) server.unref();     // must not keep the process alive on shutdown
      logger?.info?.("Monitoring", `metrics on http://${host}:${port}/metrics  health on /health${token ? " (token required)" : ""}`);
      return server;
    },
    async stop() {
      if (!server) return;
      await new Promise((resolve) => server.close(resolve));
      server = null;
    },
    get listening() { return !!server?.listening; },
    address() { return server?.address() ?? null; },
    // Exposed for tests: exercise routing without binding a socket.
    _handle: handle,
  };
}

module.exports = { createMonitoringServer };
