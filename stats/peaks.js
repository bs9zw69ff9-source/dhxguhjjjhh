/* ---------------- stats/peaks: peak concurrent players (all-time + daily) ----------------
   Pure reducer for peak-player tracking. Given the current per-server player counts
   and the previously stored stats, it returns the updated stats plus whether anything
   changed. Tracks two kinds of peak, each per server and combined across all servers:

     • all-time — monotonic highs that never reset
     • daily    — highs for the current day (`date`), reset when the day rolls over

   `date` is an opaque day key (e.g. "2026-07-19" in the bot's timezone) supplied by
   the caller; pass null to skip daily tracking. Dependency-free so it unit-tests
   without any db/Discord state. */

function reducePeaks(counts, current = {}, now = Date.now(), date = null) {
  const servers  = Object.keys(counts || {});
  const combined = servers.reduce((sum, k) => sum + (Number(counts[k]) || 0), 0);
  const stats = { ...current, perServer: { ...(current.perServer || {}) } };
  let changed = false;

  // ---- all-time peaks (never reset) ----
  for (const s of servers) {
    const cur = Number(counts[s]) || 0;
    if (cur > (stats.perServer[s]?.peak || 0)) { stats.perServer[s] = { peak: cur, peakAt: now }; changed = true; }
  }
  if (combined > (stats.combined?.peak || 0)) { stats.combined = { peak: combined, peakAt: now }; changed = true; }

  // ---- daily peaks (reset when `date` changes) ----
  if (date != null) {
    const rolledOver = !current.daily || current.daily.date !== date;
    const daily = rolledOver
      ? { date, perServer: {}, combined: { peak: 0, peakAt: now } }
      : { ...current.daily, perServer: { ...(current.daily.perServer || {}) } };
    if (rolledOver) changed = true;   // persist the day's reset even at 0 players
    for (const s of servers) {
      const cur = Number(counts[s]) || 0;
      if (cur > (daily.perServer[s]?.peak || 0)) { daily.perServer[s] = { peak: cur, peakAt: now }; changed = true; }
    }
    if (combined > (daily.combined?.peak || 0)) { daily.combined = { peak: combined, peakAt: now }; changed = true; }
    stats.daily = daily;
  }

  if (changed) stats.updatedAt = now;
  return { changed, stats };
}

/* Combined daily peak for `date`, or 0 if the stored daily bucket is for another day
   (i.e. today hasn't been recorded yet). Safe for display without a write. */
function dailyPeak(stats, date) {
  return stats?.daily?.date === date ? (stats.daily.combined?.peak || 0) : 0;
}

module.exports = { reducePeaks, dailyPeak };
