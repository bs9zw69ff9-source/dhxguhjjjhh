/* ---------------- discord/textify: reply rendering (text for short, embed for long) ----------------
   Every reply in this codebase is BUILT as an EmbedBuilder (that keeps ~250 call
   sites untouched). At send time each payload is sized: SHORT one-line results
   (a ban confirmation, an access denial) are flattened to a clean plain-text
   message and the embed dropped; LONG or multi-part results (the help menu, a
   player dossier, per-server status, paginated lists) keep their embed - so they
   read well and paginator buttons attach to an embed, not a wall of text. Either
   way nothing pings. This module is pure (no ctx) - it owns the renderer
   (embedToText), the size decision (isLongPayload), the chunker (textifyChunks),
   the one-shot converter (textify, for DMs/panel posts), and the per-interaction
   patch. Unit-tested directly in test/textify.test.js. */

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
/* Decide whether a payload should stay as a proper embed instead of being flattened
   to a plain-text reply. Short, one-line results (a ban confirmation, an access
   denial) read best as clean text; long or structured results (the help menu, a
   player dossier, per-server status, any paginated list) read far better as an
   embed - and keeping them as embeds also means paginator buttons attach to an
   embed, not a wall of text. */
function isLongPayload(embeds) {
  if (!Array.isArray(embeds) || !embeds.length) return false;
  if (embeds.length > 1) return true;                 // multi-embed (e.g. /serverinfo)
  const txt = embeds.map(embedToText).join("\n");
  return txt.length > 900 || txt.split("\n").length > 8;
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
// One-message form for interaction replies / edits / DMs. SHORT results render to a
// clean plain-text message and the embed is dropped; LONG or structured results keep
// their embed(s). Components (buttons/selects) pass through untouched either way, and
// nothing pings (allowedMentions parse []).
function textify(payload) {
  if (!payload || typeof payload !== "object") return payload;
  const { keepEmbeds, ...rest } = payload;   // legacy flag - conversion decided by size now
  if (!Array.isArray(rest.embeds) || !rest.embeds.length) return rest;
  if (isLongPayload(rest.embeds)) {
    if (!rest.allowedMentions) rest.allowedMentions = { parse: [] };
    return rest;                             // keep the embed(s) for long/structured output
  }
  const { first } = textifyChunks(rest);
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
      if (!rest.allowedMentions) rest.allowedMentions = { parse: [] };   // never ping from a reply
      // Long / multi-part output stays as a proper embed; short output flattens to text.
      if (isLongPayload(rest.embeds)) return orig(rest, ...args);
      const { first } = textifyChunks(rest);
      if (!first.allowedMentions) first.allowedMentions = { parse: [] };
      return orig(first, ...args);
    };
  }
}

module.exports = { embedToText, textifyChunks, textify, patchInteractionOutput, isLongPayload };
