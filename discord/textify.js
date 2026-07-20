/* ---------------- discord/textify: plain-text reply rendering ----------------
   Every reply in this codebase is BUILT as an EmbedBuilder (that keeps ~250 call
   sites untouched), but at send time it is rendered to short human-readable text
   and the embed is dropped. This module is pure (no ctx) - it owns the renderer
   (embedToText), the message chunker (textifyChunks), the one-shot converter
   (textify, used for DMs/panel posts), and the per-interaction patch that
   converts reply/editReply/followUp/update payloads and sends overflow as
   follow-up messages. Unit-tested directly in test/textify.test.js. */

function embedToText(e) {
  let d; try { d = typeof e?.toJSON === "function" ? e.toJSON() : e; } catch { d = e; }
  if (!d || typeof d !== "object") return "";
  // Strip embed-era decoration that reads as clutter in a plain message: divider /
  // rule lines (▓▒░, ----, ───, ====, ▔▔▔ ...) and stacked blank lines. Code-fence
  const decor = /^[\s>*_`~|·▓▒░─━▔═▬⎯=—–-]+$/;
  const tidy  = (s) => String(s).split("\n")
    .filter(l => l.trim().startsWith("```") || !decor.test(l))
    .join("\n").replace(/\n{2,}/g, "\n").trim();
  // Human reply style: when there's a description, the reply is just the leading
  // emoji from the title plus the sentence itself, like "✅ Banned chupavr for 2d".
  // The title text only shows when there's no description to speak for itself.
  const title = String(d.title ?? "").trim();
  const desc  = tidy(d.description ?? "");
  const emoji = (title.match(/^([^\p{L}\p{N}*_`]+)\s/u)?.[1] ?? "").trim();
  const parts = [];
  if (desc) parts.push(emoji ? `${emoji} ${desc}` : desc);
  else if (title) parts.push(title);
  for (const f of d.fields ?? []) {
    const name = String(f.name ?? "").trim();
    const val  = tidy(f.value ?? "");
    if (!name && !val) continue;
    parts.push(val.includes("\n") ? `**${name}**\n${val}` : `**${name}:** ${val}`);
  }
  return parts.filter(Boolean).join("\n");
}
// payload {content?, embeds?, ...} -> { first: payload-without-embeds, extra: [overflow strings] }
// Messages cap at 2000 chars (embeds allowed ~6000), so long output splits by line.
function textifyChunks(payload) {
  const text = [payload.content, ...(payload.embeds ?? []).map(embedToText)].filter(Boolean).join("\n\n");
  const chunks = [];
  let cur = "";
  for (let line of String(text).split("\n")) {
    while (line.length > 1900) { chunks.push(line.slice(0, 1900)); line = line.slice(1900); }
    if (cur && cur.length + 1 + line.length > 1900) { chunks.push(cur); cur = line; }
    else cur = cur ? `${cur}\n${line}` : line;
  }
  if (cur) chunks.push(cur);
  const { embeds, ...rest } = payload;
  return { first: { ...rest, content: chunks[0] || "​" }, extra: chunks.slice(1) };
}
// One-message form for interaction replies / edits / DMs: render every embed to a
// short plain-text message (the clean reply style) and drop the embed. Components
// (buttons/selects) pass through untouched. Overflow past one message is truncated
// with a marker - command replies are expected to be short.
function textify(payload) {
  if (!payload || typeof payload !== "object") return payload;
  const { keepEmbeds, ...rest } = payload;   // legacy flag - conversion applies regardless
  if (!Array.isArray(rest.embeds) || !rest.embeds.length) return rest;
  const { first, extra } = textifyChunks(rest);
  if (extra.length) first.content = `${first.content.slice(0, 1880)}\n-# ...output shortened`;
  // Replies are plain text now, so a raw <@id> in the content would ping. Command
  // replies should never ping anyone - mentions still render, they just don't notify.
  if (!first.allowedMentions) first.allowedMentions = { parse: [] };
  return first;
}
function patchInteractionOutput(interaction) {
  for (const m of ["reply", "editReply", "followUp", "update"]) {
    const orig = typeof interaction[m] === "function" ? interaction[m].bind(interaction) : null;
    if (!orig) continue;
    interaction[m] = async (payload, ...args) => {
      if (!payload || typeof payload !== "object" || !Array.isArray(payload.embeds) || !payload.embeds.length) {
        return orig(payload, ...args);
      }
      const { keepEmbeds, ...rest } = payload;
      const { first, extra } = textifyChunks(rest);
      if (!first.allowedMentions) first.allowedMentions = { parse: [] };   // never ping from a reply
      // update() edits one message in place - no follow-ups possible there.
      if (m === "update" && extra.length) first.content = `${first.content.slice(0, 1880)}\n-# ...output shortened`;
      const res = await orig(first, ...args);
      if (m !== "update") {
        for (const c of extra) { try { await interaction.followUp({ content: c, flags: first.flags, allowedMentions: { parse: [] } }); } catch { break; } }
      }
      return res;
    };
  }
}

module.exports = { embedToText, textifyChunks, textify, patchInteractionOutput };
