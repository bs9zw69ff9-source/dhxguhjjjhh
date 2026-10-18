using System.Globalization;

namespace PavlovBot.Core.Text;

/// <summary>
/// How a server writes an amount of money: "1,250 caps" or "$1,250".
/// </summary>
/// <remarks>
/// THE ONE MONEY FORMATTER, and it lives in Core so there can be one. The bail label is
/// built in Core and every balance in Host, and Core cannot see Host - so for as long as the
/// format lived in Host's vocabulary, Core kept a private copy of it and a test pinned the
/// two together. A second copy is how a booking ends up quoting bail in dollars on the same
/// screen as a balance in caps.
///
/// INVARIANT CULTURE, deliberately: the separator must not depend on the locale of whichever
/// machine happens to be running the bot.
/// </remarks>
/// <param name="Prefix">Written before the figure, e.g. <c>$</c>.</param>
/// <param name="Suffix">Written after the figure, e.g. <c> caps</c>.</param>
public sealed record Currency(string Prefix, string Suffix)
{
    /// <summary>Bottle caps: "1,250 caps".</summary>
    public static Currency Caps { get; } = new("", " caps");

    /// <summary>Dollars: "$1,250".</summary>
    public static Currency Dollars { get; } = new("$", "");

    public string Format(long amount) =>
        Prefix + amount.ToString("N0", CultureInfo.InvariantCulture) + Suffix;
}
