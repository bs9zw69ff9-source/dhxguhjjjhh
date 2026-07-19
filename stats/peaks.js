/* ---------------- stats/peaks: all-time peak concurrent players ----------------
   Pure reducer for peak-player tracking. Given the current per-server player counts
   and the previously stored stats, it returns the updated stats plus whether anything
   changed. Peaks are monotonic (all-time highs) — per server and combined across all
   active servers. Dependency-free so it unit-tests without any db/Discord state. */

function reducePeaks(counts, current = {}, now = Date.now()) {
  const servers  = Object.keys(counts || {});
  const combined = servers.reduce((sum, k) => sum + (Number(counts[k]) || 0), 0);
  const stats = { ...current, perServer: { ...(current.perServer || {}) } };
  let changed = false;

  for (const s of servers) {
    const cur = Number(counts[s]) || 0;
    if (cur > (stats.perServer[s]?.peak || 0)) { stats.perServer[s] = { peak: cur, peakAt: now }; changed = true; }
  }
  if (combined > (stats.combined?.peak || 0)) { stats.combined = { peak: combined, peakAt: now }; changed = true; }
  if (changed) stats.updatedAt = now;

  return { changed, stats };
}

module.exports = { reducePeaks };
