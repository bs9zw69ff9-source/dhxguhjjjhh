using System.Globalization;
using System.Text.Json;

namespace PavlovBot.Core.Servers;

/// <summary>One server as the Pavlov master server describes it.</summary>
/// <param name="Players">How many are on right now. The API calls this "slots".</param>
/// <param name="MaxPlayers">Capacity. The API calls this "maxSlots".</param>
/// <param name="MapId">The workshop id, e.g. "UGC3480960", or a stock map name.</param>
/// <param name="Secured">Anti-cheat enforced.</param>
/// <param name="Updated">When the server last checked in. Null when unparseable.</param>
public sealed record ServerListing(
    string Name,
    string Ip,
    int Port,
    int Players,
    int MaxPlayers,
    string MapId,
    string MapLabel,
    string GameMode,
    string GameModeLabel,
    string Version,
    bool PasswordProtected,
    bool Secured,
    DateTimeOffset? Updated)
{
    /// <summary>Stable identity, and short enough to ride in a Discord component value.</summary>
    public string Key => $"{Ip}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public bool Full => MaxPlayers > 0 && Players >= MaxPlayers;

    /// <summary>True when the map is a workshop upload rather than a stock one.</summary>
    public bool IsWorkshopMap => MapId.StartsWith("UGC", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reading the master server's list.
/// </summary>
/// <remarks>
/// PURE, and separate from anything that makes a request, so the shape of the response is
/// testable against a captured body rather than against whatever the live list happens to
/// contain today. The list is third-party and can change under us; a parser that throws on
/// one odd row would take the whole browser down, so a row that cannot be read is SKIPPED
/// and the rest are returned.
/// </remarks>
public static class ServerList
{
    /// <summary>
    /// Parse a master server response. Returns an empty list rather than throwing.
    /// </summary>
    public static IReadOnlyList<ServerListing> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<ServerListing> Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("servers", out var servers) ||
            servers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var listings = new List<ServerListing>(servers.GetArrayLength());

        foreach (var element in servers.EnumerateArray())
        {
            if (ReadOne(element) is { } listing) listings.Add(listing);
        }

        return listings;
    }

    private static ServerListing? ReadOne(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var name = Text(element, "name");
        var ip = Text(element, "ip");

        // A row with no name or no address identifies nothing and cannot be connected to.
        if (name.Length == 0 || ip.Length == 0) return null;

        var mode = Text(element, "gameMode");
        var modeLabel = Text(element, "gameModeLabel");
        var mapId = Text(element, "mapId");
        var mapLabel = Text(element, "mapLabel");

        return new ServerListing(
            Name: name,
            Ip: ip,
            Port: Number(element, "port"),
            Players: Number(element, "slots"),
            MaxPlayers: Number(element, "maxSlots"),
            MapId: mapId,
            // The label is what a person recognises; the id is the fallback because a
            // workshop map with no label still needs SOMETHING in the map field.
            MapLabel: mapLabel.Length > 0 ? mapLabel : mapId,
            GameMode: mode,
            GameModeLabel: modeLabel.Length > 0 ? modeLabel : mode,
            Version: Text(element, "version"),
            PasswordProtected: Flag(element, "bPasswordProtected"),
            Secured: Flag(element, "bSecured"),
            Updated: Timestamp(element, "updated"));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Timestamp(JsonElement element, string name)
    {
        var text = Text(element, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The browse order: busiest first, then by name.
    /// </summary>
    /// <remarks>
    /// Population descending because an empty server is almost never what somebody opening a
    /// browser is looking for, and there are ten times as many of them. Full servers stay in
    /// place rather than sinking - "full" is information, not a reason to hide.
    ///
    /// DEDUPLICATED BY ADDRESS, which is not paranoia about a list that happens to be clean
    /// today. Two rows sharing an address would become two select options with the same
    /// value, and Discord rejects the entire message for that - so a duplicate upstream
    /// would present as a browser that silently shows nothing, with nothing anywhere saying
    /// why. The listing kept is the busier one, since ordering has already run.
    /// </remarks>
    public static IReadOnlyList<ServerListing> Ordered(IEnumerable<ServerListing> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return [.. servers
            .OrderByDescending(s => s.Players)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(s => seen.Add(s.Key))];
    }

    /// <summary>
    /// Servers whose name, address or map matches <paramref name="query"/>.
    /// </summary>
    /// <remarks>
    /// The address is searchable because "which of these is 104.128.55.206" is a question
    /// staff actually ask, and the map because "who is running Jailbreak" is the other one.
    /// An empty query matches everything rather than nothing - a cleared search box should
    /// show the list again, not an empty one.
    /// </remarks>
    public static IReadOnlyList<ServerListing> Matching(IEnumerable<ServerListing> servers, string? query)
    {
        ArgumentNullException.ThrowIfNull(servers);

        if (string.IsNullOrWhiteSpace(query)) return [.. servers];

        var needle = query.Trim();

        return [.. servers.Where(s =>
            s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            s.MapLabel.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            s.GameModeLabel.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            s.Key.Contains(needle, StringComparison.OrdinalIgnoreCase))];
    }
}
