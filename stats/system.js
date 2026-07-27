/* ---------------- stats/system: process + host resource usage ----------------
   Backs /stats. Reports what the PROCESS is using (rss / heap / cpu), what the HOST
   has (cores, load, RAM, disk), and - the useful part - which of the bot's own stored
   datasets are the biggest, so "what is eating memory" has a concrete answer.

   The per-part breakdown is measured, not guessed: every dataset the bot keeps lives
   as a row in the shared SQLite kv table (index.js datasets AND the ipBans registry),
   and those rows are exactly what the in-memory read cache holds. LENGTH() is computed
   SQL-side so sizing the whole store costs one cheap query rather than serialising
   large objects in JS.

   Usage: require("./stats/system")({ db, baseDir }). */
const os   = require("os");
const fs   = require("fs");
const path = require("path");

module.exports = function createSystemStats({ db, baseDir }) {

  const MB = 1024 * 1024;
  const bytes = (n) => n >= 1024 * MB ? `${(n / (1024 * MB)).toFixed(2)} GB`
                     : n >= MB        ? `${(n / MB).toFixed(1)} MB`
                     : n >= 1024      ? `${(n / 1024).toFixed(0)} KB`
                     :                  `${n} B`;

  /** What this Node process is holding. */
  function processMemory() {
    const m = process.memoryUsage();
    return {
      rss: m.rss,                       // total resident - the number `top` shows
      heapUsed: m.heapUsed,             // live JS objects
      heapTotal: m.heapTotal,           // heap reserved by V8
      external: m.external,             // C++ objects bound to JS (buffers, sqlite)
      arrayBuffers: m.arrayBuffers ?? 0,
      heapPct: m.heapTotal ? Math.round((m.heapUsed / m.heapTotal) * 100) : 0,
    };
  }

  /* Process CPU as a percentage of ONE core, sampled over a short window. cpuUsage()
     returns cumulative microseconds, so two reads are required - a single read would
     report the average since boot, which is not what "current usage" means. */
  async function processCpuPercent(sampleMs = 200) {
    const t0 = process.hrtime.bigint();
    const c0 = process.cpuUsage();
    await new Promise(r => setTimeout(r, sampleMs));
    const c1 = process.cpuUsage(c0);                       // delta since c0
    const elapsedUs = Number(process.hrtime.bigint() - t0) / 1000;
    const usedUs = c1.user + c1.system;
    const oneCore = elapsedUs > 0 ? (usedUs / elapsedUs) * 100 : 0;
    const cores = os.cpus().length || 1;
    return {
      ofOneCore: Math.round(oneCore * 10) / 10,            // can exceed 100 (multi-threaded)
      ofAllCores: Math.round((oneCore / cores) * 10) / 10,
      userMs: Math.round(c1.user / 1000),
      systemMs: Math.round(c1.system / 1000),
    };
  }

  /** Host hardware + OS. */
  function systemInfo() {
    const cpus = os.cpus() || [];
    const total = os.totalmem(), free = os.freemem();
    return {
      cpuModel: (cpus[0]?.model || "unknown").replace(/\s+/g, " ").trim(),
      cores: cpus.length,
      cpuSpeedMhz: cpus[0]?.speed ?? 0,
      loadAvg: os.loadavg().map(n => Math.round(n * 100) / 100),   // 1/5/15 min - 0 on Windows
      memTotal: total,
      memFree: free,
      memUsed: total - free,
      memPct: total ? Math.round(((total - free) / total) * 100) : 0,
      platform: `${os.platform()} ${os.arch()}`,
      osUptime: os.uptime() * 1000,
      nodeVersion: process.version,
    };
  }

  /* Every stored dataset, largest first. This is the "which part of the bot is using
     the most memory" answer: these rows are what the read cache holds in the heap. */
  function datasetSizes(limit = 8) {
    let rows = [];
    try { rows = db.prepare("SELECT file, LENGTH(data) AS bytes FROM kv ORDER BY bytes DESC").all(); }
    catch { return { rows: [], total: 0, count: 0 }; }
    const total = rows.reduce((s, r) => s + (r.bytes || 0), 0);
    return {
      rows: rows.slice(0, limit).map(r => ({
        // keys are stored as "./thing.json" (index.js) or "thing.json" (ipBans)
        name: String(r.file).replace(/^\.\//, "").replace(/\.json$/, ""),
        bytes: r.bytes || 0,
        pct: total ? Math.round(((r.bytes || 0) / total) * 100) : 0,
      })),
      total, count: rows.length,
    };
  }

  /** On-disk size of the SQLite database plus its WAL sidecar. */
  function dbFileSize() {
    let n = 0;
    for (const f of ["bot.db", "bot.db-wal", "bot.db-shm"]) {
      try { n += fs.statSync(path.join(baseDir, f)).size; } catch {}
    }
    return n;
  }

  return { bytes, processMemory, processCpuPercent, systemInfo, datasetSizes, dbFileSize };
};
