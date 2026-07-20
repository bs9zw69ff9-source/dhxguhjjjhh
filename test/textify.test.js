/* Reply rendering - tests the real discord/textify module. Short one-line results
   flatten to a clean plain-text message; long or multi-part results keep their
   embed. Covers: the renderer, the short/long decision, and the interaction patch. */
const { test } = require("node:test");
const assert = require("node:assert");
const { embedToText, textify, textifyChunks, patchInteractionOutput, isLongPayload } = require("../discord/textify");

test("reply is just the title emoji plus the sentence (human style)", () => {
  const out = embedToText({ title: "✅ Success", description: "Tempbanned **chupavr** for **2d** (Reason: `vdm`)." });
  assert.equal(out, "✅ Tempbanned **chupavr** for **2d** (Reason: `vdm`).");
});

test("title without an emoji is dropped when a description exists", () => {
  const out = embedToText({ title: "Temp Ban Active", description: "line one\nline two" });
  assert.equal(out, "line one\nline two");
});

test("title-only embeds still show the title", () => {
  assert.equal(embedToText({ title: "✅ Done" }), "✅ Done");
});

test("fields render compactly; footers and rules are dropped", () => {
  const out = embedToText({
    title: "T", description: "d\n────────────\nx",
    fields: [{ name: "Server", value: "Server 1" }],
    footer: { text: "should not appear" },
  });
  assert.ok(out.includes("**Server:** Server 1"));
  assert.ok(!out.includes("should not appear"));
  assert.ok(!out.includes("────"));
});

test("textify converts an embed payload to content and drops the embeds", () => {
  const p = textify({ embeds: [{ title: "✅ Done", description: "ok" }], flags: 64, keepEmbeds: true });
  assert.equal(p.embeds, undefined);
  assert.equal(p.flags, 64);
  assert.equal(p.content, "✅ ok");
});

test("isLongPayload: short one-liners flatten, long/multi-part stay embeds", () => {
  assert.equal(isLongPayload([{ title: "✅ Done", description: "ok" }]), false);
  assert.equal(isLongPayload([{ description: "line 1\nline 2\nline 3" }]), false);
  // >8 lines -> embed
  assert.equal(isLongPayload([{ description: Array.from({ length: 12 }, (_, i) => `l${i}`).join("\n") }]), true);
  // >900 chars -> embed
  assert.equal(isLongPayload([{ description: "x".repeat(950) }]), true);
  // multiple embeds -> embed (e.g. /serverinfo)
  assert.equal(isLongPayload([{ description: "a" }, { description: "b" }]), true);
});

test("short replies flatten to plain text (embed dropped)", async () => {
  const sent = [];
  const interaction = { reply: async (p) => { sent.push(p); } };
  patchInteractionOutput(interaction);
  await interaction.reply({ embeds: [{ title: "✅ Done", description: "ok" }], flags: 64 });
  assert.equal(sent[0].embeds, undefined);
  assert.equal(sent[0].content, "✅ ok");
  assert.deepEqual(sent[0].allowedMentions, { parse: [] });
});

test("long / multi-embed replies keep their embeds", async () => {
  const sent = [];
  const interaction = { reply: async (p) => { sent.push(p); } };
  patchInteractionOutput(interaction);
  const long = Array.from({ length: 20 }, (_, i) => `line ${i}`).join("\n");
  await interaction.reply({ embeds: [{ title: "Big", description: long }], flags: 64 });
  assert.ok(Array.isArray(sent[0].embeds) && sent[0].embeds.length === 1, "embed kept, not flattened");
  assert.equal(sent[0].content, undefined);
  assert.deepEqual(sent[0].allowedMentions, { parse: [] });
});

test("components pass through and long payloads never ping", async () => {
  const sent = [];
  const interaction = { reply: async (p) => { sent.push(p); } };
  patchInteractionOutput(interaction);
  await interaction.reply({ embeds: [{ description: "x".repeat(950) }], components: [{ type: 1 }], flags: 64 });
  assert.ok(sent[0].embeds, "embed kept");
  assert.deepEqual(sent[0].components, [{ type: 1 }], "paginator components preserved");
  assert.deepEqual(sent[0].allowedMentions, { parse: [] });
});

test("payloads without embeds pass through untouched", async () => {
  const sent = [];
  const interaction = { reply: async (p) => { sent.push(p); } };
  patchInteractionOutput(interaction);
  await interaction.reply({ content: "plain", flags: 64 });
  assert.deepEqual(sent[0], { content: "plain", flags: 64 });
});
