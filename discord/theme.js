/* ---------------- discord/theme: Fallout: New Vegas visual system ----------------
   Extracted from index.js. Holds the colour palette, flavour quotes, glyphs, the
   embed "brand" stamp, and every shared embed builder (success/error/denied/…).
   The only outside coupling is Discord's EmbedBuilder plus the bot's avatar/version
   for the brand footer — injected as a lazy client accessor + buildId so this loads
   before the Discord client exists and resolves the avatar at send time.

   Usage:
     const theme = require("./discord/theme")({ getClient: () => client, buildId: BUILD_ID });
     const { brand, successEmbed, NV, GLYPH, randomQuote, ... } = theme;
*/
const { EmbedBuilder } = require("discord.js");

module.exports = function createTheme({ getClient, buildId }) {
  // ---- fallout: new vegas theme ----
  const NV = {
    AMBER:       0xFFB000,
    GOLD:        0xD4A017,
    IRRAD_GREEN: 0x39FF14,
    NCR_TAN:     0xC8A96E,
    LEGION_RED:  0x8B0000,
    RUST_RED:    0xC0392B,
    DEAD_GREY:   0x4A4A4A,
    BLUE_VATS:   0x1B4F8A,
    DEEP_BLACK:  0x0D0D0D,
  };

  /* Ban / IP embed accents — Fallout: New Vegas palette. */
  const CLIN = {
    red:   0x8B0000,   // LEGION_RED  - ban / block / active
    green: 0x39FF14,   // IRRAD_GREEN - cleared / lifted / no bans
    grey:  0xFFB000,   // AMBER       - neutral info (lists, checks, connection log)
  };

  const QUOTES = {
    ban:     [
      '"You\'re banned from the Lucky 38. Mr. House\'s orders."',
      '"You\'ve made an enemy of the Mojave. Enjoy the wasteland."',
      '"Even in the wasteland, there are rules. You broke them."',
      '"The Securitrons don\'t forgive. Neither do we."',
      '"The game was rigged from the start — and you just lost."',
      '"Should\'ve learned to use your head instead of swinging it. Now you\'re exiled."',
      '"Out into the Divide with you. Don\'t look back."',
      '"The Courier always rings twice. You won\'t ring again."',
      '"Patrolling the Mojave almost makes you wish for a ban this clean."',
      '"The King is dead, and so is your access. Thank you. Thank you very much."',
    ],
    unban:   [
      '"Every soul deserves a second chance in the Mojave. Don\'t waste yours."',
      '"The gates of the Strip open once more. Don\'t make us regret it."',
      '"Exile lifted. Welcome back to New Vegas — try not to shoot anyone."',
      '"Begin again. The Mojave forgives, this once."',
      '"Your slate\'s wiped cleaner than a Vault-Tec ad. Walk the line."',
      '"Mr. House has reconsidered. Don\'t squander his mercy."',
    ],
    warn:    [
      '"Consider this a warning, friend. We\'re watching."',
      '"The Strip has eyes everywhere. Don\'t test us again."',
      '"One more strike and the Securitrons handle it personally."',
      '"Toe the line, courier — the NCR keeps ledgers, and so do we."',
      '"That\'s one mark on your Pip-Boy. Collect enough and you\'re Legion bait."',
      '"We\'ve got your number, and it\'s climbing. Slow down."',
    ],
    caps:    [
      '"War never changes. But caps? Caps are forever."',
      '"The House always collects. Today, it pays."',
      '"A courier without caps is just a wanderer."',
      '"In the Mojave, caps are the only truth that matters."',
      '"Bottle caps: the only currency the Brotherhood can\'t confiscate."',
    ],
    system:  [
      '"All systems nominal. Securitron network active."',
      '"Maintenance cycle complete. The Strip never sleeps."',
      '"Mr. House is watching. Always watching."',
      '"RobCo terminals online. Vault door sealed."',
      '"Reticulating splines across the Mojave wasteland..."',
    ],
    wages:   [
      '"The House always pays its debts — eventually."',
      '"Caps distributed. The economy of the Mojave endures."',
      '"A fair day\'s work for a fair day\'s pay. Even in the apocalypse."',
      '"Payday on the Strip. Don\'t spend it all at the Atomic Wrangler."',
    ],
    announce: [
      '"Attention all couriers on the Strip..."',
      '"Message from the Mojave Authority..."',
      '"Broadcast from the Lucky 38..."',
      '"This is Mr. New Vegas, and boy, do I have news for you..."',
      '"Radio New Vegas, cutting through the static..."',
    ],
    faction: [
      '"Allegiances in the Mojave are written in blood and caps."',
      '"Every faction needs soldiers. Every soldier needs orders."',
      '"The wasteland belongs to those who organise."',
      '"Rank is earned. Loyalty is proven."',
      '"NCR, Legion, or House — pick your banner and bleed for it."',
    ],
    kick:    [
      '"Get out. Don\'t make us ask twice."',
      '"Shown the door, courier. Mind the radroaches on your way out."',
      '"You\'re not welcome at the Tops tonight. Beat it."',
      '"Ejected. Take a walk down the Long 15 and cool off."',
    ],
    connect: [
      '"A courier strides into the Mojave."',
      '"Boots on the Strip. The Securitrons log every arrival."',
      '"Another wanderer steps off the Long 15."',
      '"Vault door opens. Someone\'s come to play."',
    ],
    autoban: [
      '"A barred courier tried to slip back into the Mojave. Denied."',
      '"The Securitrons remember every face. Yours wasn\'t welcome."',
      '"Ban evasion detected. The House does not tolerate cheats."',
      '"Nice try. The Mojave has a long memory and a longer reach."',
    ],
    casino:  [
      '"The house always wins. Eventually."',
      '"Mr. House built the Tops on losing streaks just like yours."',
      '"Fortune favors the bold — and the House favors the odds."',
      '"Every chip on this table has a story. Most of them end badly."',
      '"Luck is just another word for a spin no one\'s rigged yet."',
    ],
  };
  const randomQuote = (cat) => {
    const pool = QUOTES[cat] ?? QUOTES.system;
    return pool[Math.floor(Math.random() * pool.length)];
  };

  // ---- embed builders ----
  const DIVIDER = "────────────────────────────";
  const RULE    = "╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌";
  const BRAND_NAME = "Mojave Authority";
  // One tasteful, monochrome glyph set — status accents on titles, no cartoon emoji.
  const GLYPH = { ok: "✓", bad: "✕", warn: "⚠", deny: "⊘", info: "▸", dot: "•", up: "●", down: "○", caps: "◈", rank: "◆" };

  // ---- visual system  (consistent branding across every embed) ----
  function brandIcon() { try { return getClient()?.user?.displayAvatarURL?.({ size: 128 }) ?? null; } catch { return null; } }

  /** Stamp an embed with the bot's identity: author header (+ avatar),
      timestamp, thumbnail, and a subtle version footer unless one is set. */
  function brand(embed, { thumb = false, footer } = {}) {
    const icon = brandIcon();
    embed.setAuthor(icon ? { name: BRAND_NAME, iconURL: icon } : { name: BRAND_NAME });
    if (thumb && icon) embed.setThumbnail(icon);
    const f = footer ? (typeof footer === "string" ? { text: footer } : footer) : { text: `${BRAND_NAME} · ${buildId}` };
    if (icon && !f.iconURL) f.iconURL = icon;
    embed.setFooter(f);
    embed.setTimestamp();
    clampEmbed(embed);
    return embed;
  }
  // Keep any embed inside Discord's hard API limits so a long field can never
  // reject the whole message (title 256, desc 4096, field name 256/value 1024).
  function clampEmbed(embed) {
    try {
      const d = embed.data;
      if (d.title) embed.setTitle(String(d.title).slice(0, 256));
      if (d.description) embed.setDescription(String(d.description).slice(0, 4096));
      if (Array.isArray(d.fields)) {
        for (const f of d.fields) {
          if (f.name  && f.name.length  > 256)  f.name  = f.name.slice(0, 253)  + "…";
          if (f.value && f.value.length > 1024) f.value = f.value.slice(0, 1021) + "…";
        }
      }
    } catch {}
    return embed;
  }

  /** Fine-grained progress bar — smooth 1/8-cell fill, e.g. ██████▍░░░░░ */
  const _BAR_FRAC = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"];
  function bar(value, max, width = 12) {
    const ratio  = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
    // Work in 1/8-cell units and carry a rounded-up fraction into a full block,
    // otherwise a fraction that rounds to 8/8 renders as EMPTY and a higher value
    // can draw a shorter bar than a lower one.
    const eighths = Math.round(ratio * width * 8);
    const full    = Math.floor(eighths / 8);
    const frac    = _BAR_FRAC[eighths % 8] || "";
    const used    = full + (frac ? 1 : 0);
    return "█".repeat(full) + frac + "░".repeat(Math.max(0, width - used));
  }
  /** Labeled meter: `██████▍░░░░░  n/max (p%)` — for dashboards and rosters. */
  function meter(value, max, width = 12) {
    const pct = max > 0 ? Math.round((value / max) * 100) : 0;
    return `\`${bar(value, max, width)}\`  **${value}/${max}** *(${pct}%)*`;
  }
  const pip = (ok) => (ok ? GLYPH.up : GLYPH.down);

  // Fixed-width cell for lining up columns inside a monospace code block.
  const cell = (v, w) => { const s = String(v); return s.length > w ? s.slice(0, w - 1) + "…" : s.padEnd(w); };

  /* A blockquote-styled hero line used at the top of feature embeds. */
  function hero(quoteText) { return `> *${quoteText}*`; }

  /* Ban / IP embeds: stamp them with the full Mojave Authority branding (author
     header, avatar, timestamp) + an optional footer — same look as everything else. */
  function clinical(embed, footer) {
    return brand(embed, footer ? { footer } : {});
  }

  // ---- embed builders ----
  function successEmbed(title, description) {
    return brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN)
      .setTitle(`${GLYPH.ok}  ${title}`)
      .setDescription(String(description)));
  }
  function errorEmbed(title, description) {
    return brand(new EmbedBuilder().setColor(NV.RUST_RED)
      .setTitle(`${GLYPH.bad}  ${title}`)
      .setDescription(String(description)),
      { footer: { text: "Incident logged · Securitron network active" } });
  }
  function warningEmbed(title, description) {
    return brand(new EmbedBuilder().setColor(NV.AMBER)
      .setTitle(`${GLYPH.warn}  ${title}`)
      .setDescription(String(description)));
  }
  /* Shared "you can't run this" card — one look for every access gate. */
  function deniedEmbed(title, description, footer = "Unauthorized access attempt logged") {
    return brand(new EmbedBuilder().setColor(NV.LEGION_RED)
      .setTitle(`${GLYPH.deny}  ${title}`)
      .setDescription(String(description)),
      { footer: { text: footer } });
  }
  function adminOnlyEmbed()  { return deniedEmbed("Administrators Only", "This command is restricted to **Administrators**."); }
  function ownerOnlyEmbed()  { return deniedEmbed("Owner Only", "This command is restricted to the **bot owner**."); }
  function modOnlyEmbed()    { return deniedEmbed("Moderators Only", "This command requires the **Moderator** role.", "Access restricted"); }
  function factionLeaderOnlyEmbed()   { return deniedEmbed("Faction Leaders Only", "Requires the **Faction Leader** role (or Moderator).", "Access restricted"); }
  function factionLeaderStrictEmbed() { return deniedEmbed("Faction Leaders Only", "Rank changes require the **Faction Leader** role specifically.", "Access restricted"); }
  function blacklistedEmbed(entry) {
    const reason = entry?.reason ? `\n\n**Reason:** ${entry.reason}` : "";
    return deniedEmbed("Access Revoked", `You've been **blacklisted** from this bot — every command is unavailable to you.${reason}`,
      "Contact an administrator if you believe this is a mistake");
  }
  function emptyIdEmbed() {
    return warningEmbed("Courier ID Required",
      "Enter a valid **Courier ID** or username.\n-# Start typing in the player field — autocomplete surfaces anyone online, and manual IDs work for offline players.");
  }
  function rateLimitEmbed() {
    return warningEmbed("Slow Down", "You're issuing commands too quickly — wait a moment and try again.");
  }

  return {
    NV, CLIN, QUOTES, randomQuote,
    DIVIDER, RULE, BRAND_NAME, GLYPH,
    brandIcon, brand, clampEmbed,
    bar, meter, pip, cell, hero, clinical,
    successEmbed, errorEmbed, warningEmbed, deniedEmbed,
    adminOnlyEmbed, ownerOnlyEmbed, modOnlyEmbed,
    factionLeaderOnlyEmbed, factionLeaderStrictEmbed,
    blacklistedEmbed, emptyIdEmbed, rateLimitEmbed,
  };
};
