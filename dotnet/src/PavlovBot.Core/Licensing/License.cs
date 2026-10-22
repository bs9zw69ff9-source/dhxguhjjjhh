namespace PavlovBot.Core.Licensing;

/// <summary>One licence a player's save file can hold.</summary>
/// <param name="Key">The key in the save file, spelled exactly as the mod spells it.</param>
/// <param name="Label">What a person reads.</param>
/// <param name="Choice">The value of the command option that picks it.</param>
public sealed record License(string Key, string Label, string Choice);

/// <summary>The licences the police server's mod keeps in <c>modsave/&lt;player&gt;.txt</c>.</summary>
/// <remarks>
/// "LISCENSE" IS NOT A TYPO TO FIX. It is the key the mod writes and reads; a correctly
/// spelled key would be a new line the game never looks at, and the licence it was meant to
/// revoke would stay exactly where it was.
/// </remarks>
public static class Licenses
{
    public static License Drivers { get; } = new("DriversLiscense", "Driver's license", "drivers");

    public static License Firearm { get; } = new("FirearmLiscense", "Firearm license", "firearm");

    public static IReadOnlyList<License> All { get; } = [Drivers, Firearm];

    /// <summary>The option value that means every licence at once.</summary>
    public const string Every = "all";

    /// <summary>The licences an option value names, or empty when it names none.</summary>
    public static IReadOnlyList<License> Named(string? choice) =>
        string.Equals(choice?.Trim(), Every, StringComparison.OrdinalIgnoreCase)
            ? All
            : [.. All.Where(l => string.Equals(l.Choice, choice?.Trim(), StringComparison.OrdinalIgnoreCase))];
}
