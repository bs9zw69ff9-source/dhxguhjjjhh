const { test } = require("node:test");
const assert = require("node:assert");
const u = require("../utils");

test("sanitizeId strips labels, junk chars, and caps length", () => {
  assert.equal(u.sanitizeId("Player_One (manual entry)"), "Player_One");
  assert.equal(u.sanitizeId("Name [S1+S2]"), "Name");
  assert.equal(u.sanitizeId("bad name!!"), "badname");           // spaces impossible in names
  assert.equal(u.sanitizeId("a".repeat(99)).length, 64);
  assert.equal(u.sanitizeId(null), "");
});

test("sanitizeMessage flattens control chars and caps at 200", () => {
  assert.equal(u.sanitizeMessage("a\r\nb\tc"), "a  b c");   // \r and \n each become a space
  assert.equal(u.sanitizeMessage("x".repeat(300)).length, 200);
  assert.equal(u.sanitizeMessage("emoji🎰gone"), "emojigone");
});

test("md5 matches a known digest", () => {
  assert.equal(u.md5("password"), "5f4dcc3b5aa765d61d8327deb882cf99");
});

test("parseDuration handles units, bare minutes, and rejects junk", () => {
  assert.equal(u.parseDuration("30s"), 30_000);
  assert.equal(u.parseDuration("10m"), 600_000);
  assert.equal(u.parseDuration("2h"), 7_200_000);
  assert.equal(u.parseDuration("1d"), 86_400_000);
  assert.equal(u.parseDuration("15"), 900_000);                  // bare number = minutes
  assert.equal(u.parseDuration("0"), null);
  assert.equal(u.parseDuration("abc"), null);
});

test("formatTimeLeft renders d/h/m and expiry", () => {
  const now = Date.now();
  assert.equal(u.formatTimeLeft(now - 1000), "expired");
  assert.match(u.formatTimeLeft(now + 90_000), /^1m$/);
  assert.match(u.formatTimeLeft(now + 3 * 3_600_000 + 60_000), /^3h /);
  assert.match(u.formatTimeLeft(now + 2 * 86_400_000 + 3_600_000), /^2d /);
});

test("parseClockTime normalizes 12h/24h forms", () => {
  assert.equal(u.parseClockTime("18:30"), "18:30");
  assert.equal(u.parseClockTime("6:30pm"), "18:30");
  assert.equal(u.parseClockTime("3pm"), "15:00");
  assert.equal(u.parseClockTime("12am"), "00:00");
  assert.equal(u.parseClockTime("12pm"), "12:00");
  assert.equal(u.parseClockTime("25:00"), null);
  assert.equal(u.parseClockTime("13pm"), null);
});

test("easternClock returns YYYY-MM-DD and HH:MM shapes", () => {
  const { date, hm } = u.easternClock(new Date("2026-07-14T12:00:00Z"));
  assert.match(date, /^\d{4}-\d{2}-\d{2}$/);
  assert.match(hm, /^\d{2}:\d{2}$/);
});

test("redactPrivateInfo scrubs IPv4/IPv6/long ids, keeps normal text", () => {
  assert.equal(u.redactPrivateInfo("block 86.166.107.200 now"), "block [ip redacted] now");
  assert.ok(!/db8|8329/.test(u.redactPrivateInfo("from 2001:db8::ff00:42:8329")));
  assert.ok(!/fe80/.test(u.redactPrivateInfo("link fe80:: local")));
  assert.ok(!/0002f0a3/.test(u.redactPrivateInfo("id 0002f0a3b96e4c5d8899aabbccddeeff")));
  const clean = "refactor: split handler · v3.2.2 at 18:30 e24ecfe https://github.com/x";
  assert.equal(u.redactPrivateInfo(clean), clean);
});
