const { test } = require("node:test");
const assert = require("node:assert");
const { reducePeaks, dailyPeak } = require("../stats/peaks");

test("records per-server and combined peaks from empty", () => {
  const { changed, stats } = reducePeaks({ server1: 5, server2: 3, server3: 0 }, {}, 1000);
  assert.equal(changed, true);
  assert.equal(stats.perServer.server1.peak, 5);
  assert.equal(stats.perServer.server2.peak, 3);
  assert.equal(stats.combined.peak, 8);
  assert.equal(stats.combined.peakAt, 1000);
});

test("empty rosters record nothing", () => {
  assert.equal(reducePeaks({ server1: 0, server2: 0 }, {}).changed, false);
});

test("peaks are monotonic — lower counts never lower a peak and don't rewrite", () => {
  const first = reducePeaks({ server1: 5, server2: 3 }, {}, 1).stats;
  const { changed, stats } = reducePeaks({ server1: 1, server2: 0 }, first, 2);
  assert.equal(changed, false);
  assert.equal(stats.perServer.server1.peak, 5);
  assert.equal(stats.combined.peak, 8);
});

test("a new per-server high updates only that server + combined if it also beats", () => {
  const base = reducePeaks({ server1: 5, server2: 3, server3: 0 }, {}, 1).stats; // combined 8
  const { changed, stats } = reducePeaks({ server1: 6, server2: 4, server3: 2 }, base, 2); // combined 12
  assert.equal(changed, true);
  assert.equal(stats.perServer.server1.peak, 6);
  assert.equal(stats.perServer.server3.peak, 2);
  assert.equal(stats.combined.peak, 12);
});

test("combined can beat even when no single server sets a new high", () => {
  // server1 peaked at 5 alone earlier; now 4+4 across two servers = 8 combined, a new combined high,
  // but neither server individually beats its own peak of 5/? — combined still updates.
  const base = reducePeaks({ server1: 5, server2: 0 }, {}, 1).stats; // s1 peak 5, combined 5
  const { changed, stats } = reducePeaks({ server1: 4, server2: 4 }, base, 2); // combined 8
  assert.equal(changed, true);
  assert.equal(stats.perServer.server1.peak, 5);   // unchanged
  assert.equal(stats.perServer.server2.peak, 4);   // new
  assert.equal(stats.combined.peak, 8);            // new combined high
});

test("does not mutate the input stats object", () => {
  const input = { perServer: { server1: { peak: 2, peakAt: 1 } }, combined: { peak: 2, peakAt: 1 } };
  reducePeaks({ server1: 9 }, input, 5);
  assert.equal(input.perServer.server1.peak, 2);   // original untouched
});

test("daily peak tracks the current day and resets on rollover", () => {
  // Day 1: peak 5 combined.
  let stats = reducePeaks({ server1: 5, server2: 0 }, {}, 1, "2026-07-19").stats;
  assert.equal(stats.daily.date, "2026-07-19");
  assert.equal(stats.daily.combined.peak, 5);
  assert.equal(stats.combined.peak, 5);            // all-time also 5

  // Same day, lower counts: daily unchanged, no write.
  let r = reducePeaks({ server1: 2, server2: 0 }, stats, 2, "2026-07-19");
  assert.equal(r.changed, false);
  assert.equal(r.stats.daily.combined.peak, 5);

  // Same day, new daily high 7.
  stats = reducePeaks({ server1: 4, server2: 3 }, stats, 3, "2026-07-19").stats;
  assert.equal(stats.daily.combined.peak, 7);

  // Next day: daily resets (even though all-time stays), and the rollover persists.
  r = reducePeaks({ server1: 1, server2: 0 }, stats, 4, "2026-07-20");
  assert.equal(r.changed, true, "day rollover must be written");
  assert.equal(r.stats.daily.date, "2026-07-20");
  assert.equal(r.stats.daily.combined.peak, 1);    // fresh daily peak
  assert.equal(r.stats.combined.peak, 7);          // all-time unchanged
});

test("daily tracking is skipped when no date is given (all-time only)", () => {
  const { stats } = reducePeaks({ server1: 5 }, {}, 1);
  assert.equal(stats.daily, undefined);
});

test("dailyPeak returns today's peak, or 0 for a stale/other day", () => {
  const stats = reducePeaks({ server1: 6 }, {}, 1, "2026-07-19").stats;
  assert.equal(dailyPeak(stats, "2026-07-19"), 6);
  assert.equal(dailyPeak(stats, "2026-07-20"), 0);  // yesterday's bucket → 0 today
  assert.equal(dailyPeak({}, "2026-07-19"), 0);
});
