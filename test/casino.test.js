const { test } = require("node:test");
const assert = require("node:assert");
const g = require("../casino/games");

test("freshDeck: 52 unique cards, 4 suits x 13 ranks", () => {
  const deck = g.freshDeck();
  assert.equal(deck.length, 52);
  assert.equal(new Set(deck.map(c => c.rank + c.suit)).size, 52);
});

test("handValue: ace soft/hard math", () => {
  assert.deepEqual(g.handValue([{ rank: "A" }, { rank: "K" }]), { total: 21, soft: true });
  assert.deepEqual(g.handValue([{ rank: "A" }, { rank: "A" }, { rank: "9" }]), { total: 21, soft: true });
  assert.equal(g.handValue([{ rank: "A" }, { rank: "9" }, { rank: "9" }]).total, 19);   // ace demotes
  assert.equal(g.handValue([{ rank: "K" }, { rank: "Q" }, { rank: "5" }]).total, 25);   // bust
});

test("isBlackjack: only a 2-card 21", () => {
  assert.ok(g.isBlackjack([{ rank: "A" }, { rank: "J" }]));
  assert.ok(!g.isBlackjack([{ rank: "7" }, { rank: "7" }, { rank: "7" }]));
});

test("slots: reels weighted, triple-match pays, mixed pays 0", () => {
  for (let i = 0; i < 200; i++) {
    const { reels, mult } = g.spinSlots();
    assert.equal(reels.length, 3);
    const triple = reels[0].key === reels[1].key && reels[1].key === reels[2].key;
    assert.equal(mult > 0, triple);
  }
  assert.equal(g.SLOT_SYMBOLS.reduce((s, x) => s + x.weight, 0), 100);
});

test("roulette: straight number pays 36x, colors match the wheel", () => {
  for (let i = 0; i < 100; i++) {
    const r = g.spinRoulette(null, 17);
    assert.ok(r.landed >= 0 && r.landed <= 36);
    assert.equal(r.mult, 36);
    assert.equal(r.win, r.landed === 17);
    assert.equal(r.color, g.rouletteColor(r.landed));
  }
  assert.equal(g.rouletteColor(0), "green");
  assert.equal(g.rouletteColor(1), "red");
  assert.equal(g.rouletteColor(2), "black");
});

test("roulette outside bets: zero loses every outside space", () => {
  for (const def of Object.values(g.ROULETTE_SPACES)) assert.equal(def.hit(0), false);
});

test("formatHand hides the dealer hole card", () => {
  const shown = g.formatHand([{ rank: "A", suit: "♠" }, { rank: "K", suit: "♥" }], 1);
  assert.ok(shown.includes("A"));
  assert.ok(shown.includes("??"));
  assert.ok(!shown.includes("K "));
});

test("russian roulette multipliers rise monotonically", () => {
  const m = g.RUSSIAN_ROULETTE_MULTS;
  for (let i = 1; i < m.length; i++) assert.ok(m[i] > m[i - 1]);
});
