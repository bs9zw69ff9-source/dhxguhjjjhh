using System.Globalization;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The Mojave vocabulary: the themed words the bot says out loud, in one place.
/// </summary>
/// <remarks>
/// The server is a Fallout roleplay server and the bot talks like it belongs there. That is
/// a real requirement rather than decoration - a bot that answers "Balance updated: $1,250"
/// on a server whose currency is caps is reading off a different game.
///
/// WHAT BELONGS HERE: nouns, glyphs and flavour lines. What a thing is CALLED.
///
/// WHAT DOES NOT: anything a person has to act on, and anything a machine reads. Command
/// names, option names, error causes, stored data and log lines stay plain - somebody
/// reading "the roster file could not be written" at two in the morning is not helped by
/// being told the Followers have misplaced their ledger. The rule that keeps this honest is
/// that flavour goes in the TITLE and the caption; the facts underneath stay in plain words.
///
/// ONE PLACE, because the alternative is the theme drifting a synonym at a time across
/// thirty command files until half the bot says caps and half says money. Reskinning for a
/// different server is then this file plus <see cref="Theme.BrandName"/>.
/// </remarks>
public static class Lore
{
    // ---- glyphs ----

    /// <summary>Currency. Bottle caps, so the money glyph is a cap.</summary>
    public const string Caps = "🧢";

    /// <summary>The wasteland itself - brand marks, hazards, the bot's own voice.</summary>
    public const string Rads = "☢️";

    /// <summary>A posted bounty.</summary>
    public const string Bounty = "📜";

    /// <summary>Doing time.</summary>
    public const string Irons = "⛓️";

    /// <summary>Rank, service, keeping the peace.</summary>
    public const string Badge = "🎖️";

    // ---- nouns ----

    /// <summary>Where all of this happens. Used in prose, never in a command name.</summary>
    public const string World = "the Mojave";

    /// <summary>What the currency is called, singular-agnostic.</summary>
    public const string Currency = "caps";

    // ---- phrasing ----

    /// <summary>
    /// An amount of money, the way the wasteland counts it: "1,250 caps".
    /// </summary>
    /// <remarks>
    /// THE ONE MONEY FORMATTER. Three private copies of a <c>$"${value:N0}"</c> helper had
    /// already grown across the economy, police and profile commands, which is how a bot ends
    /// up quoting bail in dollars on the same screen as a balance in caps.
    ///
    /// INVARIANT CULTURE, deliberately: the separator must not depend on the locale of
    /// whichever machine happens to be running the bot.
    /// </remarks>
    public static string Amount(long caps) =>
        $"{caps.ToString("N0", CultureInfo.InvariantCulture)} {Currency}";

    /// <summary>"12 wastelanders", "1 wastelander" - a head count that reads as prose.</summary>
    public static string Wastelanders(int count) =>
        count == 1 ? "1 wastelander" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} wastelanders";

    /// <summary>The caption under the player board, by head count.</summary>
    public static string Roaming(int count) =>
        count == 1 ? "courier roaming the Mojave right now." : "couriers roaming the Mojave right now.";
}
