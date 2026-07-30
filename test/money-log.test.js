/* The balance watcher.

   Pavlov's economy mod rewrites a per-player text file and announces nothing, so this
   is the only way the bot learns money moved. Two properties matter most:

     - a restart must not announce everyone's balance as if they had just been paid
     - a truncated or unreadable ledger must never be reported as a huge fabricated
       debit, which is what treating it as 0 would do */
const { test } = require("node:test");
const assert = require("node:assert");
const fs = require("fs");
const os = require("os");
const path = require("path");

function makeWatcher({ posts = [] } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "modsave-"));
  const watcher = require("../economy/moneyLog.js")({
    fs, path,
    logger: { info() {}, warn() {}, error() {}, debug() {} },
    getModsavePath: () => dir,
    MODSAVE_SYNC_SKIP: /menuaccess|accessmanager|rconplus/i,
    postMoneyLog: (body) => posts.push(body),
  });
  const write = (player, amount) => fs.writeFileSync(path.join(dir, `${player}.txt`), String(amount));
  return { watcher, dir, posts, write };
}

test("the first scan records a baseline and reports nothing", () => {
  // Otherwise every restart would announce one line per player.
  const { watcher, posts, write } = makeWatcher();
  write("player1", 1000);
  write("player2", 2000);

  const changes = watcher.tick();

  assert.deepEqual(changes, []);
  assert.equal(posts.length, 0);
  assert.equal(watcher._state().tracked, 2);
});

test("a credit and a debit are reported in the requested format", () => {
  const { watcher, posts, write } = makeWatcher();
  write("player1", 1000);
  write("player2", 2000);
  watcher.tick();                    // baseline

  write("player1", 1400);            // +400
  write("player2", 1655);            // -345
  watcher.tick();

  assert.equal(posts.length, 1);
  const lines = posts[0].split("\n");
  assert.ok(lines.includes("+400 to player1"), posts[0]);
  assert.ok(lines.includes("-345 to player2"), posts[0]);
});

test("several movements in one tick go out as one message", () => {
  // One webhook post per tick, not one per player - three payouts is one message.
  const { watcher, posts, write } = makeWatcher();
  for (const p of ["a", "b", "c"]) write(p, 100);
  watcher.tick();

  for (const p of ["a", "b", "c"]) write(p, 250);
  watcher.tick();

  assert.equal(posts.length, 1);
  assert.equal(posts[0].split("\n").length, 3);
});

test("an unchanged balance reports nothing", () => {
  const { watcher, posts, write } = makeWatcher();
  write("player1", 500);
  watcher.tick();

  watcher.tick();
  watcher.tick();

  assert.equal(posts.length, 0);
});

test("a new player's first balance is reported as a credit from zero", () => {
  const { watcher, posts, write } = makeWatcher();
  write("existing", 100);
  watcher.tick();

  write("newcomer", 750);
  watcher.tick();

  assert.equal(posts.length, 1);
  assert.equal(posts[0], "+750 to newcomer");
});

test("a new EMPTY ledger is not a movement", () => {
  // The mod creates the file before the player earns anything.
  const { watcher, posts, write } = makeWatcher();
  write("existing", 100);
  watcher.tick();

  write("newcomer", 0);
  watcher.tick();

  assert.equal(posts.length, 0);
});

test("a deleted ledger is not reported as a loss", () => {
  /* Deleting a ledger is an admin action - /wipe, a manual clear. Reporting it as the
     player losing their money would be actively misleading. */
  const { watcher, posts, write, dir } = makeWatcher();
  write("player1", 900);
  watcher.tick();

  fs.unlinkSync(path.join(dir, "player1.txt"));
  watcher.tick();

  assert.equal(posts.length, 0);
});

test("an unparseable ledger is skipped, never read as zero", () => {
  /* The dangerous one. A truncated write read as 0 would announce "-5000 to player1"
     for someone whose money is perfectly fine. */
  const { watcher, posts, write, dir } = makeWatcher();
  write("player1", 5000);
  watcher.tick();

  fs.writeFileSync(path.join(dir, "player1.txt"), "");        // mid-write truncation
  watcher.tick();
  fs.writeFileSync(path.join(dir, "player1.txt"), "corrupt");
  watcher.tick();

  assert.equal(posts.length, 0);
  // ...and when the real value lands, it is compared against the last GOOD balance.
  write("player1", 5500);
  watcher.tick();
  assert.equal(posts[0], "+500 to player1");
});

test("a mass change collapses to a summary instead of hundreds of lines", () => {
  // A wipe would otherwise emit one line per player and bury anything real.
  const { watcher, posts, write } = makeWatcher();
  for (let i = 0; i < 30; i++) write("p" + i, 1000);
  watcher.tick();

  for (let i = 0; i < 30; i++) write("p" + i, 0);
  const changes = watcher.tick();

  assert.equal(changes.length, 30);
  assert.equal(posts.length, 1);
  assert.match(posts[0], /30 balances changed at once/);
  assert.match(posts[0], /0 up, 30 down/);
});

test("non-ledger files in ModSave are ignored", () => {
  const { watcher, posts, write, dir } = makeWatcher();
  write("player1", 100);
  watcher.tick();

  fs.writeFileSync(path.join(dir, "banlist.txt"), "someone\nReason: x");
  fs.writeFileSync(path.join(dir, "menuaccess.txt"), "1111");
  fs.writeFileSync(path.join(dir, "notes.md"), "hello");
  fs.mkdirSync(path.join(dir, "FactionRoles"));
  watcher.tick();

  assert.equal(posts.length, 0);
  assert.equal(watcher._state().tracked, 1);
});

test("player names containing spaces are handled", () => {
  // Ledger filenames match what the GAME writes, and Oculus names contain spaces.
  const { watcher, posts, write } = makeWatcher();
  write("Butter Life", 200);
  watcher.tick();

  write("Butter Life", 950);
  watcher.tick();

  assert.equal(posts[0], "+750 to Butter Life");
});

test("large amounts are formatted with separators", () => {
  const { watcher, posts, write } = makeWatcher();
  write("rich", 1_000_000);
  watcher.tick();
  write("rich", 2_500_000);
  watcher.tick();

  assert.equal(posts[0], "+1,500,000 to rich");
});

test("changes are reported biggest movement first", () => {
  const { watcher, posts, write } = makeWatcher();
  for (const p of ["small", "big", "medium"]) write(p, 1000);
  watcher.tick();

  write("small", 1010);
  write("big", 9000);
  write("medium", 1500);
  watcher.tick();

  const lines = posts[0].split("\n");
  assert.match(lines[0], /to big$/);
  assert.match(lines[1], /to medium$/);
  assert.match(lines[2], /to small$/);
});

test("with no MODSAVE_PATH the watcher is inert", () => {
  const posts = [];
  const watcher = require("../economy/moneyLog.js")({
    fs, path, logger: { info() {}, warn() {}, error() {}, debug() {} },
    getModsavePath: () => null, MODSAVE_SYNC_SKIP: /menuaccess/i,
    postMoneyLog: (b) => posts.push(b),
  });
  assert.deepEqual(watcher.tick(), []);
  assert.equal(posts.length, 0);
});
