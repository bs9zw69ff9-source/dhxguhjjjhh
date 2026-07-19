/* ---------------- discord/theme: visual system (RP-neutral) ----------------
   Extracted from index.js. Holds the colour palette, flavour quotes, glyphs, the
   embed "brand" stamp, and every shared embed builder (success/error/denied/...).
   Theme-neutral by design so the bot fits ANY RP server: the display name comes
   from BOT_NAME (env), and the quotes are generic RP-moderation flavour.
   The only outside coupling is Discord's EmbedBuilder plus the bot's avatar/version
   for the brand footer - injected as a lazy client accessor + buildId so this loads
   before the Discord client exists and resolves the avatar at send time.

   Usage:
     const theme = require("./discord/theme")({ getClient: () => client, buildId: BUILD_ID });
     const { brand, successEmbed, NV, GLYPH, randomQuote, ... } = theme;
*/
const { EmbedBuilder } = require("discord.js");

module.exports = function createTheme({ getClient, buildId }) {
  // ---- colour palette (exported as NV for backwards compatibility) ----
  // A bright, candy-vibrant set tuned to pop on Discord's dark theme while keeping
  // each colour's *meaning* (success=green, error/ban=red, warn=amber, info=blue...).
  // Every embed in the bot routes its setColor() through these keys, so this table
  // is the single source of truth for the whole UI.
  const NV = {
    AMBER:       0xFBBF24,   // amber-400  - warnings / neutral highlight
    GOLD:        0xFACC15,   // yellow-400 - economy / caps / winnings
    IRRAD_GREEN: 0x4ADE80,   // green-400  - success / online / cleared
    NCR_TAN:     0x38BDF8,   // sky-400    - neutral accent (bright + friendly)
    LEGION_RED:  0xF43F5E,   // rose-500   - bans / blocks / denied
    RUST_RED:    0xFB7185,   // rose-400   - errors (softer than a ban)
    DEAD_GREY:   0x94A3B8,   // slate-400  - disabled / void
    BLUE_VATS:   0x60A5FA,   // blue-400   - info
    DEEP_BLACK:  0x1E293B,   // slate-800  - deep background accent
    // sweet extra accents - reach for these to add variety (casino, factions, fun)
    PURPLE:      0xA78BFA,   // violet-400
    PINK:        0xF472B6,   // pink-400
    TEAL:        0x2DD4BF,   // teal-400
    CYAN:        0x22D3EE,   // cyan-400
    ORANGE:      0xFB923C,   // orange-400
  };

  /* Ban / IP embed accents. */
  const CLIN = {
    red:   0xF43F5E,   // rose-500   - ban / block / active
    green: 0x4ADE80,   // green-400  - cleared / lifted / no bans
    grey:  0xFBBF24,   // amber-400  - neutral info (lists, checks, connection log)
  };

  const QUOTES = {
    ban:     [
      '"Banned. The rules were posted; you walked past them."',
      '"You made your choices. This one was made for you."',
      '"Every server has a line. You crossed it."',
      '"Access revoked. Actions have consequences."',
      '"The door is closed, and it locks from our side."',
      '"You gambled on nobody watching. The house was watching."',
      '"Out you go. Take the lesson with you."',
      '"Your name goes in the book - the one nobody wants to be in."',
    ],
    unban:   [
      '"Everyone deserves a second chance. Don\'t waste yours."',
      '"The gates open once more. Don\'t make us regret it."',
      '"Exile lifted. Welcome back - play it straight this time."',
      '"Begin again. The slate is clean, this once."',
      '"Reinstated. Walk the line."',
      '"The ban is lifted. The memory isn\'t."',
    ],
    warn:    [
      '"Consider this a warning, friend. We\'re watching."',
      '"There are eyes everywhere. Don\'t test us again."',
      '"One more strike and it\'s handled personally."',
      '"Toe the line - we keep ledgers, and yours has a mark."',
      '"That\'s one on the record. Collect enough and you\'re done."',
      '"We\'ve got your number, and it\'s climbing. Slow down."',
    ],
    caps:    [
      '"Fortunes change. Ledgers remember."',
      '"The economy always collects. Today, it pays."',
      '"Wealth is just influence you can count."',
      '"Every balance tells a story."',
      '"Currency is the only rumor everyone believes."',
    ],
    system:  [
      '"All systems nominal. Monitoring active."',
      '"Maintenance cycle complete. The server never sleeps."',
      '"Always watching. Always logging."',
      '"Terminals online. Perimeter sealed."',
      '"Reticulating splines..."',
    ],
    wages:   [
      '"The house always pays its debts - eventually."',
      '"Wages distributed. The economy endures."',
      '"A fair day\'s work for a fair day\'s pay."',
      '"Payday. Don\'t spend it all in one place."',
    ],
    announce: [
      '"Attention all players..."',
      '"Message from the administration..."',
      '"Broadcast from headquarters..."',
      '"Now hear this - news from the top..."',
      '"Cutting through the static with an announcement..."',
    ],
    faction: [
      '"Allegiances are written in blood and coin."',
      '"Every faction needs soldiers. Every soldier needs orders."',
      '"The server belongs to those who organise."',
      '"Rank is earned. Loyalty is proven."',
      '"Pick your banner and stand by it."',
    ],
    kick:    [
      '"Get out. Don\'t make us ask twice."',
      '"Shown the door. Mind the step on your way out."',
      '"You\'re not welcome tonight. Beat it."',
      '"Ejected. Take a walk and cool off."',
    ],
    connect: [
      '"A new arrival steps in."',
      '"Boots on the ground. Every arrival is logged."',
      '"Another wanderer joins the fray."',
      '"The doors open. Someone\'s come to play."',
    ],
    autoban: [
      '"A barred player tried to slip back in. Denied."',
      '"We remember every face. Yours wasn\'t welcome."',
      '"Ban evasion detected. Cheats are not tolerated."',
      '"Nice try. Long memory, longer reach."',
    ],
    casino:  [
      '"The house always wins. Eventually."',
      '"Casinos are built on losing streaks just like yours."',
      '"Fortune favors the bold - and the house favors the odds."',
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
  // Display name stamped on every embed. Set BOT_NAME in .env to skin the bot
  // for your RP ("Mojave Authority", "LSPD Command", ...). Neutral default.
  const BRAND_NAME = process.env.BOT_NAME || "Server Authority";
  // Colourful status glyphs - a splash of colour on every title, status line and
  // list. `dot` stays a plain bullet (used as an inline separator in running text).
  const GLYPH = { ok: "✅", bad: "❌", warn: "⚠️", deny: "🚫", info: "ℹ️", dot: "•", up: "🟢", down: "🔴", caps: "💰", rank: "🏅" };

  // ---- visual system  (clean, minimal embeds - see the reference help-menu look) ----
  function brandIcon() { try { return getClient()?.user?.displayAvatarURL?.({ size: 128 }) ?? null; } catch { return null; } }

  /** Minimal stamp: no author header, no avatar/thumbnail, no default footer, no
      timestamp - just the colour bar, title, and body (the clean help-menu look).
      An explicitly passed footer (a short informative note) is kept as plain text;
      any timestamp a call site set is stripped so every embed stays uniform. */
  function brand(embed, { thumb = false, footer } = {}) {
    if (footer) embed.setFooter(typeof footer === "string" ? { text: footer } : footer);
    try { if (embed.data) delete embed.data.timestamp; } catch {}
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
          if (f.name  && f.name.length  > 256)  f.name  = f.name.slice(0, 253)  + "...";
          if (f.value && f.value.length > 1024) f.value = f.value.slice(0, 1021) + "...";
        }
      }
    } catch {}
    return embed;
  }

  /** Fine-grained progress bar - smooth 1/8-cell fill, e.g. ██████▍░░░░░ */
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
  /** Labeled meter: `██████▍░░░░░  n/max (p%)` - for dashboards and rosters. */
  function meter(value, max, width = 12) {
    const pct = max > 0 ? Math.round((value / max) * 100) : 0;
    return `\`${bar(value, max, width)}\`  **${value}/${max}** *(${pct}%)*`;
  }
  const pip = (ok) => (ok ? GLYPH.up : GLYPH.down);

  // Fixed-width cell for lining up columns inside a monospace code block.
  const cell = (v, w) => { const s = String(v); return s.length > w ? s.slice(0, w - 1) + "..." : s.padEnd(w); };

  /* Intro line at the top of feature embeds - plain text in the clean style
     (was a blockquote-italic "hero quote" in the old theme). */
  function hero(quoteText) { return String(quoteText); }

  /* Ban / IP embeds: stamp them with the full bot branding (author
     header, avatar, timestamp) + an optional footer - same look as everything else. */
  function clinical(embed, footer) {
    return brand(embed, footer ? { footer } : {});
  }

  // ---- embed builders ----
  function successEmbed(title, description) {
    return brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN)
      .setTitle(`${GLYPH.ok} ${title}`)
      .setDescription(String(description)));
  }
  function errorEmbed(title, description) {
    return brand(new EmbedBuilder().setColor(NV.RUST_RED)
      .setTitle(`${GLYPH.bad} ${title}`)
      .setDescription(String(description)));
  }
  function warningEmbed(title, description) {
    return brand(new EmbedBuilder().setColor(NV.AMBER)
      .setTitle(`${GLYPH.warn} ${title}`)
      .setDescription(String(description)));
  }
  /* Shared "you can't run this" card - one look for every access gate. */
  function deniedEmbed(title, description) {
    return brand(new EmbedBuilder().setColor(NV.LEGION_RED)
      .setTitle(`${GLYPH.deny} ${title}`)
      .setDescription(String(description)));
  }
  function adminOnlyEmbed()  { return deniedEmbed("Admins only", "Only admins can use this command."); }
  function ownerOnlyEmbed()  { return deniedEmbed("Owner only", "Only the bot owner can use this command."); }
  function modOnlyEmbed()    { return deniedEmbed("Mods only", "You need the mod role to use this command."); }
  function factionLeaderOnlyEmbed()   { return deniedEmbed("Faction leaders only", "You need the faction leader role (or mod) to use this."); }
  function factionLeaderStrictEmbed() { return deniedEmbed("Faction leaders only", "Only faction leaders can change ranks."); }
  function blacklistedEmbed(entry) {
    const reason = entry?.reason ? ` Reason: ${entry.reason}` : "";
    return deniedEmbed("Blacklisted", `You are blacklisted from this bot, so no commands will work for you.${reason}`);
  }
  function emptyIdEmbed() {
    return warningEmbed("Need a player name", "Type a player name. The field autocompletes anyone online, and typed names work for offline players too.");
  }
  function rateLimitEmbed() {
    return warningEmbed("Slow down", "Too many commands too fast. Wait a few seconds and try again.");
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
