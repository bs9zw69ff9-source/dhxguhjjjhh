using System.Text.Json;

namespace PavlovBot.Rcon.Protocol;

/// <summary>
/// Reads Pavlov's JSON replies.
/// </summary>
/// <remarks>
/// Deliberately <see cref="JsonDocument"/> rather than deserialising into records, for
/// two reasons that both come from the running bot:
///
///   PAVLOV IS INCONSISTENT ABOUT PROPERTY NAMES. The same field arrives as `Username`,
///   `username`, `PlayerName`, `name` or `Name` depending on the build and the command.
///   The Node bot coalesces across all five spellings at every call site; doing it once,
///   here, is the same behaviour with one place to fix.
///
///   A REPLY IS NOT ALWAYS JSON. Failures come back as bare text. A parser that throws on
///   that turns a transient server hiccup into a stack trace, so parsing returns false
///   instead and the caller decides.
///
/// JsonDocument keeps this project trim- and AOT-safe: no reflection, no serialiser
/// context to keep in step with the shapes.
/// </remarks>
public static class RconReply
{
    /// <summary>Parse a reply body. False when it is not JSON at all.</summary>
    public static bool TryParse(string? raw, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            document = JsonDocument.Parse(raw);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Look up a property, trying each candidate name case-insensitively.</summary>
    public static JsonElement? Property(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var direct)) return direct;

        // Fall back to a case-insensitive sweep. Only reached when none of the exact
        // spellings matched, so the cost is paid on an unknown shape, not the happy path.
        foreach (var property in element.EnumerateObject())
            foreach (var name in names)
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;

        return null;
    }

    /// <summary>A property as trimmed text, or null when absent or empty.</summary>
    public static string? Text(JsonElement element, params string[] names)
    {
        var value = Property(element, names);
        if (value is null) return null;
        var text = value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>A property as an integer, tolerating one delivered as a string.</summary>
    public static int? Int(JsonElement element, params string[] names)
    {
        var value = Property(element, names);
        return value?.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.Value.GetString(), out var n) => n,
            _ => null,
        };
    }

    /// <summary>
    /// Whether the server reported the command as successful. Null means it did not say -
    /// which is NOT the same as saying no, and callers must not collapse the two.
    /// </summary>
    public static bool? Successful(JsonElement root)
    {
        var value = Property(root, "Successful", "successful");
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}

/// <param name="Name">Display name. May be empty if the server only reported an id.</param>
/// <param name="UniqueId">
/// The id RCON actually targets. Ban, Kick and Unban take a UniqueId, NOT a display name -
/// passing the name silently does nothing, which is how a "ban" appears to succeed and the
/// player stays on the server.
/// </param>
public readonly record struct PavlovPlayer(string Name, string UniqueId);

/// <summary>The RefreshList roster.</summary>
public static class RefreshList
{
    /// <summary>Every player in a RefreshList reply. Empty when the reply is unusable.</summary>
    public static IReadOnlyList<PavlovPlayer> Players(JsonElement root)
    {
        var list = RconReply.Property(root, "PlayerList", "playerList");
        if (list?.ValueKind != JsonValueKind.Array) return [];

        var players = new List<PavlovPlayer>();
        foreach (var entry in list.Value.EnumerateArray())
        {
            var name = RconReply.Text(entry, "Username", "username", "PlayerName", "name", "Name") ?? "";
            var id = RconReply.Text(entry, "UniqueId", "uniqueId", "UniqueID", "Id", "id") ?? "";
            // An entry with neither is noise, not a player.
            if (name.Length > 0 || id.Length > 0) players.Add(new PavlovPlayer(name, id));
        }
        return players;
    }

    /// <summary>Just the non-empty display names, as the roster surfaces want them.</summary>
    public static IReadOnlyList<string> Names(JsonElement root) =>
        Players(root).Select(p => p.Name).Where(n => n.Length > 0).ToList();
}

/// <param name="MapLabel">Human-readable map, or null when the server did not report one.</param>
/// <param name="MaxPlayers">Server capacity, or null when unreported. Never defaulted here -
/// the caller decides what to show, and a made-up 24 in the data layer is a lie.</param>
public sealed record PavlovServerInfo(
    string? ServerName,
    string? MapLabel,
    string? GameMode,
    int? MaxPlayers);

/// <summary>The ServerInfo reply.</summary>
public static class ServerInfoReply
{
    /// <summary>
    /// Read a ServerInfo body.
    /// </summary>
    /// <remarks>
    /// Pavlov nests the fields under a `ServerInfo` key. Reading them top-level is what
    /// made the Node bot show *Unknown* for map, mode and capacity for months - the reply
    /// parsed fine, it just had nothing at the level being read. Both shapes are accepted.
    /// </remarks>
    public static PavlovServerInfo Read(JsonElement root)
    {
        var scope = RconReply.Property(root, "ServerInfo", "serverInfo") ?? root;
        return new PavlovServerInfo(
            ServerName: RconReply.Text(scope, "ServerName", "serverName"),
            MapLabel: RconReply.Text(scope, "MapLabel", "MapName", "mapLabel"),
            GameMode: RconReply.Text(scope, "GameMode", "gameMode"),
            /* Capacity is NOT a field of its own in Pavlov's reply - it is the denominator
               of PlayerCount, which is a STRING like "2/24". Reading only MaxPlayers meant
               every capacity in the bot came back null: /serverinfo showed a bare count and
               the player-count channels read "Server 1 2" instead of "Server 1: 2/24".
               MaxPlayers is still tried first, in case a build ever reports one. */
            MaxPlayers: RconReply.Int(scope, "MaxPlayers", "maxPlayers")
                        ?? Capacity(RconReply.Text(scope, "PlayerCount", "playerCount")));
    }

    /// <summary>
    /// The capacity out of a "2/24" player count.
    /// </summary>
    /// <remarks>
    /// PURE, because the shape is the fragile part: a build that reports a bare "2", or
    /// nothing at all, must yield null rather than a made-up number - a capacity the bot
    /// invented would show as a full server nobody can join.
    /// </remarks>
    internal static int? Capacity(string? playerCount)
    {
        if (playerCount is null) return null;

        var slash = playerCount.IndexOf('/');
        if (slash < 0) return null;

        return int.TryParse(playerCount[(slash + 1)..].Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var max) && max > 0
            ? max
            : null;
    }
}
