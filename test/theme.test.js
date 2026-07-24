const { test } = require("node:test");
const assert = require("node:assert");
// index.js runs with validators disabled (embeds are clamped at send time by
// clampEmbed) — mirror that here or oversized setTitle() throws at build.
const { disableValidators } = require("discord.js");
disableValidators();
const createTheme = require("../discord/theme");

const theme = () => createTheme({ getClient: () => null, buildId: "v-test" });

test("BRAND_NAME defaults neutral and respects BOT_NAME", () => {
  delete process.env.BOT_NAME;
  assert.equal(theme().BRAND_NAME, "Server Authority");
  process.env.BOT_NAME = "LSPD Command";
  assert.equal(theme().BRAND_NAME, "LSPD Command");
  delete process.env.BOT_NAME;
});

test("brand is minimal: no author, no default footer, timestamps stripped", () => {
  const t = theme();
  const { EmbedBuilder } = require("discord.js");
  const e = t.brand(new EmbedBuilder().setTitle("x").setTimestamp());
  assert.equal(e.data.author, undefined, "no author header");
  assert.equal(e.data.footer, undefined, "no default footer");
  assert.equal(e.data.timestamp, undefined, "timestamp stripped for the clean look");
  // an explicitly passed footer (informative note) is kept as plain text
  const f = t.brand(new EmbedBuilder().setTitle("y"), { footer: "note" });
  assert.equal(f.data.footer.text, "note");
});

test("hero is a plain intro line (no blockquote styling)", () => {
  const t = theme();
  assert.equal(t.hero("Welcome"), "Welcome");
});

test("clampEmbed enforces Discord hard limits", () => {
  const t = theme();
  const { EmbedBuilder } = require("discord.js");
  // Builder setters validate length even with validators disabled, so inject the
  // oversized payload directly — same shape clampEmbed sees from real callers.
  const e = new EmbedBuilder();
  e.data.title = "T".repeat(400);
  e.data.description = "d".repeat(5000);
  e.data.fields = [{ name: "n".repeat(300), value: "v".repeat(1100) }];
  t.clampEmbed(e);
  assert.ok(e.data.title.length <= 256);
  assert.ok(e.data.description.length <= 4096);
  assert.ok(e.data.fields[0].name.length <= 256);
  assert.ok(e.data.fields[0].value.length <= 1024);
});

test("embed builders carry the right glyph accents", () => {
  const t = theme();
  assert.ok(t.successEmbed("Done", "x").data.title.startsWith(t.GLYPH.ok));
  assert.ok(t.errorEmbed("Bad", "x").data.title.startsWith(t.GLYPH.bad));
  assert.ok(t.warningEmbed("Careful", "x").data.title.startsWith(t.GLYPH.warn));
  assert.ok(t.deniedEmbed("No", "x").data.title.startsWith(t.GLYPH.deny));
});

test("bar renders monotonic fills at fixed width", () => {
  const t = theme();
  assert.equal(t.bar(0, 10).length, 12);
  assert.equal(t.bar(10, 10), "█".repeat(12));
  assert.equal(t.bar(15, 10), "█".repeat(12));            // clamped
  assert.equal(t.bar(5, 0), "░".repeat(12));              // zero max
  // a higher value must never draw a shorter bar (the 1/8-cell carry bug)
  let prev = -1;
  for (let v = 0; v <= 10; v++) {
    const filled = [...t.bar(v, 10)].filter(c => c !== "░").length;
    assert.ok(filled >= prev);
    prev = filled;
  }
});

test("no flavour quote system is exported (professional tone)", () => {
  const t = theme();
  assert.equal(t.QUOTES, undefined);
  assert.equal(t.randomQuote, undefined);
});
