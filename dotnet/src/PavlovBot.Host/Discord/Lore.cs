using System.Globalization;
using PavlovBot.Core.Text;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The themed words the bot says out loud, from whichever <see cref="Skin"/> this instance
/// runs.
/// </summary>
/// <remarks>
/// The bot talks like it belongs on the server it is on. That is a real requirement rather
/// than decoration - a bot that answers "Balance updated: $1,250" on a server whose currency
/// is caps is reading off a different game, and so is one quoting caps to a police server.
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
/// thirty command files until half the bot says caps and half says money. The words
/// themselves are data in <see cref="Skins"/>; this is the one door they come through.
/// </remarks>
public static class Lore
{
    private static Vocabulary Words => Theme.Skin.Words;

    // ---- glyphs ----

    /// <summary>Currency: a bottle cap, or a bag of money.</summary>
    public static string Caps => Words.MoneyGlyph;

    /// <summary>The skin's mark - brand stamps, the profile card, the bot's own voice.</summary>
    public static string Rads => Words.Mark;

    /// <summary>A posted warrant.</summary>
    public static string Bounty => Words.WarrantGlyph;

    /// <summary>Doing time.</summary>
    public static string Irons => Words.JailGlyph;

    /// <summary>Rank, service, keeping the peace.</summary>
    public static string Badge => Words.StaffGlyph;

    // ---- nouns ----

    /// <summary>Where all of this happens. Used in prose, never in a command name.</summary>
    public static string World => Words.World;

    /// <summary>How this server writes money.</summary>
    public static Currency Money => Words.Money;

    /// <summary>What a balance is labelled.</summary>
    public static string MoneyLabel => Words.MoneyLabel;

    /// <summary>A warrant, singular and plural, as it reads mid-sentence.</summary>
    public static string Warrant => Words.Warrant;
    public static string Warrants => Words.Warrants;

    /// <summary>The plural as a heading or a field name: "Bounties", "Warrants".</summary>
    public static string WarrantsHeading =>
        Words.Warrants.Length == 0 ? "" : char.ToUpperInvariant(Words.Warrants[0]) + Words.Warrants[1..];

    /// <summary>Every other themed phrase, by name.</summary>
    public static Vocabulary Say => Words;

    // ---- phrasing ----

    /// <summary>
    /// An amount of money, the way this server counts it: "1,250 caps", "$1,250".
    /// </summary>
    /// <remarks>
    /// THE ONE MONEY FORMATTER, shared with the bail label through <see cref="Currency"/>.
    /// Three private copies of a <c>$"${value:N0}"</c> helper had once grown across the
    /// economy, police and profile commands, which is how a bot ends up quoting bail in one
    /// currency on the same screen as a balance in another.
    /// </remarks>
    public static string Amount(long amount) => Money.Format(amount);

    /// <summary>"12 wastelanders", "1 player" - a head count that reads as prose.</summary>
    public static string Wastelanders(int count) =>
        count == 1 ? $"1 {Words.Person}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {Words.People}";

    /// <summary>The caption under the player board, by head count.</summary>
    public static string Roaming(int count) => count == 1 ? Words.RoamingOne : Words.RoamingMany;

    /// <summary>The heading over the cash table.</summary>
    public static string RichestHeading(int rows) =>
        string.Format(CultureInfo.InvariantCulture, Words.RichestHeading, rows);
}
