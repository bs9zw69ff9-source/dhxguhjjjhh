/* ---------------- casino: game logic (pure — no I/O) ----------------
   Extracted from index.js. Every function here is deterministic given
   Math.random — no shared bot state, no Discord/RCON/db access — so it can be
   unit-tested in isolation. The theme-coupled result-embed builder stays in
   index.js (it needs brand/DIVIDER/randomQuote). */

// One icon per game, used in every embed title so the casino reads as one
// consistent system rather than six differently-styled commands.
const GAME_ICON = {
  slots: "🎰", coinflip: "🪙", blackjack: "🃏", roulette: "🎡", cockfight: "🐓", russianroulette: "🔫", jackpot: "🎉",
};

// The jackpot: every losing gamble across the casino feeds currentPot() instead of
// vanishing (see addToPot() near debitCaps/creditCaps). /jackpot lets anyone with at
// least this much bet their ENTIRE balance for a chance to win the whole thing.
const JACKPOT_MIN_BALANCE = 10_000;
const JACKPOT_WIN_CHANCE  = 0.15;

// Slots: 3 independent reels, triple-match-only. Weights sum to 100 -> ~54% RTP,
// ~1-in-8 chance of any win. Tune here (or move to casino_config.json later).
const SLOT_SYMBOLS = [
  { key: "Scrap",   emoji: "🔩", weight: 45, mult: 3  },
  { key: "Credits",    emoji: "💰", weight: 30, mult: 6  },
  { key: "Hazard",  emoji: "☢️", weight: 18, mult: 14 },
  { key: "Jackpot", emoji: "💎", weight: 7,  mult: 55 },
];
function spinSlotReel() {
  const total = SLOT_SYMBOLS.reduce((s, x) => s + x.weight, 0);
  let roll = Math.random() * total;
  for (const s of SLOT_SYMBOLS) { if ((roll -= s.weight) < 0) return s; }
  return SLOT_SYMBOLS[SLOT_SYMBOLS.length - 1];
}
function spinSlots() {
  const reels = [spinSlotReel(), spinSlotReel(), spinSlotReel()];
  const win = reels[0].key === reels[1].key && reels[1].key === reels[2].key ? reels[0] : null;
  return { reels, mult: win ? win.mult : 0 };
}

// Roulette: standard European single-zero wheel (0 is green, no outside-bet wins on 0).
const ROULETTE_RED = new Set([1,3,5,7,9,12,14,16,18,19,21,23,25,27,30,32,34,36]);
function rouletteColor(n) { return n === 0 ? "green" : ROULETTE_RED.has(n) ? "red" : "black"; }
const ROULETTE_COLOR_EMOJI = { red: "🔴", black: "⚫", green: "🟢" };
const ROULETTE_SPACES = {
  red:    { mult: 2, hit: (n) => rouletteColor(n) === "red" },
  black:  { mult: 2, hit: (n) => rouletteColor(n) === "black" },
  even:   { mult: 2, hit: (n) => n !== 0 && n % 2 === 0 },
  odd:    { mult: 2, hit: (n) => n !== 0 && n % 2 === 1 },
  low:    { mult: 2, hit: (n) => n >= 1 && n <= 18 },
  high:   { mult: 2, hit: (n) => n >= 19 && n <= 36 },
  "1st12":{ mult: 3, hit: (n) => n >= 1  && n <= 12 },
  "2nd12":{ mult: 3, hit: (n) => n >= 13 && n <= 24 },
  "3rd12":{ mult: 3, hit: (n) => n >= 25 && n <= 36 },
};
function spinRoulette(space, number) {
  const landed = Math.floor(Math.random() * 37);   // 0-36
  if (number !== null && number !== undefined) {
    return { landed, color: rouletteColor(landed), win: landed === number, mult: 36 };
  }
  const def = ROULETTE_SPACES[space];
  return { landed, color: rouletteColor(landed), win: def ? def.hit(landed) : false, mult: def?.mult ?? 0 };
}

// Blackjack: fresh 52-card shoe per hand (no persistent deck needed for one round).
// Suits are plain glyphs (no emoji variation selector) on purpose: formatHand()
// draws real ASCII card boxes in a monospace code block, and full-width emoji
// presentation would break the box alignment.
const CARD_RANKS = ["A","2","3","4","5","6","7","8","9","10","J","Q","K"];
const CARD_SUITS = ["♠","♥","♦","♣"];
function freshDeck() {
  const deck = [];
  for (const suit of CARD_SUITS) for (const rank of CARD_RANKS) deck.push({ rank, suit });
  for (let i = deck.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [deck[i], deck[j]] = [deck[j], deck[i]]; }
  return deck;
}
function cardValue(card) { return card.rank === "A" ? 11 : ["J","Q","K"].includes(card.rank) ? 10 : Number(card.rank); }
// Returns { total, soft } — soft = hand contains an ace still counted as 11.
function handValue(cards) {
  let total = cards.reduce((s, c) => s + cardValue(c), 0);
  let aces  = cards.filter(c => c.rank === "A").length;
  let soft  = aces > 0;
  while (total > 21 && aces > 0) { total -= 10; aces--; soft = aces > 0; }
  return { total, soft };
}
// Draws each card as a little ASCII box (rank top, suit below) in a code block, so
// a hand reads like a real spread of playing cards instead of plain "A♠ K♥" text.
// Cards from index `hiddenFrom` on are drawn face-down (the dealer's hole card).
function formatHand(cards, hiddenFrom = Infinity) {
  const face = (c, hidden) => hidden ? { rank: "??", suit: "▒▒" } : { rank: c.rank, suit: c.suit };
  const shown = cards.map((c, i) => face(c, i >= hiddenFrom));
  const top = shown.map(() => "┌────┐").join(" ");
  const rank = shown.map(c => `│${c.rank.padEnd(2)}  │`).join(" ");
  const suit = shown.map(c => `│ ${c.suit.padEnd(3)}│`).join(" ");
  const bot = shown.map(() => "└────┘").join(" ");
  return "```\n" + [top, rank, suit, bot].join("\n") + "\n```";
}
function isBlackjack(cards) { return cards.length === 2 && handValue(cards).total === 21; }

// Russian roulette: independent 1-in-6 death chance per pull, up to 6 pulls.
// Multiplier at pull k = 0.95 * 1.2^k -> ~95% RTP at EVERY stopping point, so no
// cash-out point has positive expected value (no "always stop at pull 1" exploit).
const RUSSIAN_ROULETTE_MULTS = [1.1, 1.4, 1.6, 2.0, 2.4, 2.8];

module.exports = {
  GAME_ICON, JACKPOT_MIN_BALANCE, JACKPOT_WIN_CHANCE,
  SLOT_SYMBOLS, spinSlotReel, spinSlots,
  ROULETTE_RED, rouletteColor, ROULETTE_COLOR_EMOJI, ROULETTE_SPACES, spinRoulette,
  CARD_RANKS, CARD_SUITS, freshDeck, cardValue, handValue, formatHand, isBlackjack,
  RUSSIAN_ROULETTE_MULTS,
};
