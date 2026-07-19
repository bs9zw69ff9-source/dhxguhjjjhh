/* Plain-text reply rendering (the clean no-embed reply style).
   index.js can't be require()d without booting the bot, so the pure rendering
   functions are extracted from its source between stable markers — the same
   trick the wiring guard uses. Covers: compact "emoji **Title** — description"
   rendering, chunking, and the interaction patch converting embeds to content. */
const { test } = require("node:test");
const assert = require("node:assert");
const fs = require("node:fs");
const path = require("node:path");

const src = fs.readFileSync(path.join(__dirname, "..", "index.js"), "utf8");
const start = src.indexOf("function embedToText");
const end   = src.indexOf("function logAction(");
assert.ok(start > 0 && end > start, "extraction markers present in index.js");
const { embedToText, textify, textifyChunks, patchInteractionOutput } =
  new Function(`${src.slice(start, end)}; return { embedToText, textify, textifyChunks, patchInteractionOutput };`)();

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

test("patched reply sends converted content and follows up overflow chunks", async () => {
  const sent = [];
  const interaction = {
    reply: async (payload) => { sent.push(["reply", payload]); return "r"; },
    followUp: async (payload) => { sent.push(["followUp", payload]); return "f"; },
  };
  patchInteractionOutput(interaction);
  const long = Array.from({ length: 120 }, (_, i) => `line ${i} ${"x".repeat(30)}`).join("\n");
  await interaction.reply({ embeds: [{ title: "Big", description: long }], flags: 64 });
  assert.equal(sent[0][0], "reply");
  assert.equal(sent[0][1].embeds, undefined);
  assert.ok(sent[0][1].content.length <= 1900);
  assert.ok(sent.length > 1, "overflow went out as follow-ups");
  assert.ok(sent.slice(1).every(([m]) => m === "followUp"));
});

test("payloads without embeds pass through untouched", async () => {
  const sent = [];
  const interaction = { reply: async (p) => { sent.push(p); } };
  patchInteractionOutput(interaction);
  await interaction.reply({ content: "plain", flags: 64 });
  assert.deepEqual(sent[0], { content: "plain", flags: 64 });
});
