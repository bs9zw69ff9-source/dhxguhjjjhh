/* ---------------- core/metrics: counters, gauges, histograms + Prometheus exposition ----------------
   A dependency-free metrics registry. Deliberately not a wrapper around prom-client:
   this bot ships three production dependencies and adding a fourth for ~150 lines of
   arithmetic is a poor trade.

   Design notes:
     - Label sets are keyed by a stable, sorted serialisation so {a,b} and {b,a} are the
       same series. Without that you silently get two time series for one thing.
     - Histograms keep cumulative bucket counts (Prometheus semantics: le="0.5" counts
       everything <= 0.5), plus sum and count, so quantiles are computed by the scraper
       rather than stored here.
     - Series count is capped. Metrics are usually fed from user-influenced values
       (command names, server labels); an unbounded registry is a memory leak with extra
       steps, and one bad label would grow forever.

   Nothing here throws: a metrics bug must never take down a moderation action. */
"use strict";

const MAX_SERIES = 5000;          // hard ceiling across all metrics, guards label explosions
const DEFAULT_BUCKETS = [5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000];   // ms

const labelKey = (labels) => {
  const keys = Object.keys(labels || {});
  if (!keys.length) return "";
  return keys.sort().map(k => `${k}=${String(labels[k])}`).join(",");
};

const escapeLabel = (v) => String(v).replace(/\\/g, "\\\\").replace(/\n/g, "\\n").replace(/"/g, '\\"');

function renderLabels(labels) {
  const keys = Object.keys(labels || {}).sort();
  if (!keys.length) return "";
  return `{${keys.map(k => `${k}="${escapeLabel(labels[k])}"`).join(",")}}`;
}

function createMetrics({ logger = null, prefix = "bot" } = {}) {
  /** name -> { type, help, series: Map<labelKey, {labels, value|buckets...}> } */
  const registry = new Map();
  let seriesCount = 0;
  let droppedSeries = 0;

  function metric(name, type, help) {
    let m = registry.get(name);
    if (!m) { m = { name, type, help: help || name, series: new Map() }; registry.set(name, m); }
    return m;
  }

  /* Returns the series for a label set, or null when the ceiling is hit. Callers treat
     null as "skip silently" - dropping a data point is always preferable to unbounded
     growth or a thrown error in a hot path. */
  function series(m, labels, init) {
    const key = labelKey(labels);
    let s = m.series.get(key);
    if (s) return s;
    if (seriesCount >= MAX_SERIES) {
      droppedSeries++;
      if (droppedSeries === 1 && logger) logger.warn("Metrics", `series ceiling (${MAX_SERIES}) reached - new label combinations are being dropped`);
      return null;
    }
    s = { labels: { ...(labels || {}) }, ...init };
    m.series.set(key, s);
    seriesCount++;
    return s;
  }

  const api = {
    /** Monotonically increasing count (requests, errors, commands run). */
    increment(name, labels = {}, by = 1, help) {
      try {
        const s = series(metric(name, "counter", help), labels, { value: 0 });
        if (s) s.value += by;
      } catch {}
    },
    /** Point-in-time value that can go up or down (memory, queue depth, active services). */
    gauge(name, value, labels = {}, help) {
      try {
        const s = series(metric(name, "gauge", help), labels, { value: 0 });
        if (s) s.value = value;
      } catch {}
    },
    /** A duration or size distribution. `value` is in milliseconds for timers. */
    observe(name, value, labels = {}, help, buckets = DEFAULT_BUCKETS) {
      try {
        const s = series(metric(name, "histogram", help), labels,
          { buckets: buckets.slice(), counts: new Array(buckets.length).fill(0), sum: 0, count: 0 });
        if (!s) return;
        s.sum += value; s.count++;
        // Cumulative: every bucket whose upper bound is >= value counts this observation.
        for (let i = 0; i < s.buckets.length; i++) if (value <= s.buckets[i]) s.counts[i]++;
      } catch {}
    },
    /** Time an async fn, recording duration and a success/failure outcome label. */
    async time(name, labels, fn, help) {
      const t0 = process.hrtime.bigint();
      try {
        const out = await fn();
        api.observe(name, Number(process.hrtime.bigint() - t0) / 1e6, { ...labels, outcome: "success" }, help);
        return out;
      } catch (err) {
        api.observe(name, Number(process.hrtime.bigint() - t0) / 1e6, { ...labels, outcome: "failure" }, help);
        throw err;
      }
    },
    /** Snapshot for /health and diagnostics - plain JSON, no Prometheus formatting. */
    snapshot() {
      const out = {};
      for (const [name, m] of registry) {
        out[name] = { type: m.type, series: [...m.series.values()].map(s => (
          m.type === "histogram"
            ? { labels: s.labels, count: s.count, sum: s.sum, avg: s.count ? s.sum / s.count : 0 }
            : { labels: s.labels, value: s.value }))
        };
      }
      return out;
    },
    /** Prometheus text exposition format (v0.0.4), suitable for a scrape endpoint. */
    render() {
      const lines = [];
      for (const [name, m] of registry) {
        const full = `${prefix}_${name}`;
        lines.push(`# HELP ${full} ${m.help}`);
        lines.push(`# TYPE ${full} ${m.type}`);
        for (const s of m.series.values()) {
          if (m.type === "histogram") {
            for (let i = 0; i < s.buckets.length; i++) {
              lines.push(`${full}_bucket${renderLabels({ ...s.labels, le: s.buckets[i] })} ${s.counts[i]}`);
            }
            lines.push(`${full}_bucket${renderLabels({ ...s.labels, le: "+Inf" })} ${s.count}`);
            lines.push(`${full}_sum${renderLabels(s.labels)} ${s.sum}`);
            lines.push(`${full}_count${renderLabels(s.labels)} ${s.count}`);
          } else {
            lines.push(`${full}${renderLabels(s.labels)} ${s.value}`);
          }
        }
      }
      return lines.join("\n") + "\n";
    },
    /** Test/maintenance helper - drops every series. */
    reset() { registry.clear(); seriesCount = 0; droppedSeries = 0; },
    stats() { return { metrics: registry.size, series: seriesCount, dropped: droppedSeries }; },
  };
  return api;
}

module.exports = { createMetrics, DEFAULT_BUCKETS, labelKey, renderLabels };
