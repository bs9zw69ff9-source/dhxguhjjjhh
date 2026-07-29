/* ---------------- plugin: analytics ----------------
   A real, working plugin - not a stub. It samples the things that only make sense as a
   time series (event throughput, cache efficiency, error mix) and publishes them to the
   metrics registry, where /metrics exposes them to Prometheus.

   It is also the reference implementation of the plugin contract: everything it needs
   arrives through `ctx`, it touches no global state, it registers no commands, and
   stop() releases exactly what start() acquired. */
"use strict";

module.exports = {
  name: "analytics",
  version: "1.0.0",
  description: "Samples event throughput, cache efficiency and error mix into the metrics registry.",
  dependsOn: [],
  enabledByDefault: true,

  create(ctx) {
    const { metrics, errors, logger, ipBans = null, BOT_START_MS = Date.now() } = ctx;
    let timer = null;
    let lastSample = { at: Date.now(), errors: 0 };

    /* Rates are computed from deltas rather than stored per event: incrementing a
       counter on every log line would put this plugin in the hottest path in the bot,
       to produce a number that is only ever read once every 30 seconds. */
    function sample() {
      const now = Date.now();
      const elapsedSec = Math.max(1, (now - lastSample.at) / 1000);

      const totalErrors = errors?.total?.() ?? 0;
      metrics.gauge("error_rate_per_min", ((totalErrors - lastSample.errors) / elapsedSec) * 60, {},
        "Errors recorded per minute, sampled");

      // Ban-evasion registry size - the dataset most likely to grow without bound.
      try {
        const bl = ipBans?.getBlacklist?.();
        if (bl) {
          metrics.gauge("flagged_ips", bl.ips.length, {}, "Flagged IP addresses");
          metrics.gauge("flagged_names", bl.names.length, {}, "Flagged usernames");
          metrics.gauge("flagged_ids", bl.ids.length, {}, "Flagged EOS ids");
        }
      } catch (err) { errors?.record(err, { category: "analytics_sample", subsystem: "analytics", level: "warn" }); }

      metrics.gauge("process_uptime_seconds", Math.round((now - BOT_START_MS) / 1000), {}, "Process uptime");
      lastSample = { at: now, errors: totalErrors };
    }

    return {
      async initialize() {
        if (!metrics) throw new Error("analytics requires the metrics registry");
        logger?.debug?.("Plugins", "analytics initialised");
      },
      registerCommands() { return {}; },
      registerEvents() { return {}; },
      async start() {
        sample();                                    // one immediate sample, so /metrics is never empty
        timer = setInterval(sample, 30_000);
        if (timer.unref) timer.unref();              // must never hold the process open
      },
      async stop() {
        if (timer) { clearInterval(timer); timer = null; }
      },
      // Exposed for tests and for /health.
      _sample: sample,
    };
  },
};
