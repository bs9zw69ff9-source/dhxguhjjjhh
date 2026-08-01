using System.Text.Json;
using System.Text.Json.Serialization;
using PavlovBot.Host.Discord.Commands;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Arrests in the Node bot's shape, in both directions.
/// </summary>
/// <remarks>
/// THE TWO BOTS SHARE THE <c>arrests</c> DATASET AND DISAGREED ABOUT EVERY FIELD NAME:
///
///   Node   { playerId, charges: [{code, name, ...}], minutes, bail, by,          at }
///   C#     { player,   codes:   ["PC 100"],          jailMinutes, bail, arrestedBy, at }
///
/// Only <c>bail</c> and <c>at</c> lined up. Worse than a rename: <c>charges</c> is an array
/// of OBJECTS where <c>codes</c> expects strings, so deserialising Node's data threw, the
/// store returned its fallback, and the most-wanted board saw an empty registry - which,
/// combined with the board returning null on empty, meant it never posted at all and the
/// Node bot's old board sat frozen in the channel.
///
/// So this reads BOTH and WRITES NODE'S. Writing the older shape is what keeps a rollback
/// possible: the Node board reads <c>a.minutes</c>, and a C#-shaped record would give it
/// NaN and rank every player at zero.
/// </remarks>
public sealed class ArrestConverter : JsonConverter<Arrest>
{
    public override Arrest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return new Arrest("", [], 0, 0, "", default);

        return new Arrest(
            Player: Text(root, "player") ?? Text(root, "playerId") ?? "",
            Codes: Codes(root),
            JailMinutes: Int(root, "jailMinutes") ?? Int(root, "minutes") ?? 0,
            Bail: Int(root, "bail") ?? 0,
            ArrestedBy: Text(root, "arrestedBy") ?? Text(root, "by") ?? "",
            At: When(root));
    }

    /// <summary>
    /// The charge codes, from either a list of strings or Node's list of charge objects.
    /// </summary>
    private static IReadOnlyList<string> Codes(JsonElement root)
    {
        foreach (var name in new[] { "codes", "charges" })
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) continue;

            var codes = new List<string>();
            foreach (var entry in value.EnumerateArray())
            {
                var code = entry.ValueKind switch
                {
                    JsonValueKind.String => entry.GetString(),
                    // Node stores the whole charge; the code is the part that identifies it.
                    JsonValueKind.Object => Text(entry, "code"),
                    _ => null,
                };
                if (code is { Length: > 0 }) codes.Add(code);
            }
            return codes;
        }

        return [];
    }

    private static DateTimeOffset When(JsonElement root)
    {
        if (!root.TryGetProperty("at", out var at)) return default;

        return at.ValueKind switch
        {
            // Node writes Date.now(); an earlier C# build wrote ISO.
            JsonValueKind.Number => DateTimeOffset.FromUnixTimeMilliseconds(
                at.TryGetInt64(out var ms) ? ms : (long)at.GetDouble()),
            JsonValueKind.String => DateTimeOffset.TryParse(at.GetString(), out var parsed) ? parsed : default,
            _ => default,
        };
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    public override void Write(Utf8JsonWriter writer, Arrest value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        /* NODE'S FIELD NAMES, deliberately. Its most-wanted board reads a.minutes and its
           booking history reads a.playerId; writing our own names would leave a rollback
           ranking every player at zero jail time. */
        writer.WriteString("playerId", value.Player);
        writer.WriteNumber("minutes", value.JailMinutes);
        writer.WriteNumber("bail", value.Bail);
        writer.WriteString("by", value.ArrestedBy);
        writer.WriteNumber("at", value.At.ToUnixTimeMilliseconds());

        /* Charge OBJECTS, because that is what Node's own displays iterate. Only the code
           is carried - the rest is derivable from the penal code, and a stale copy of a
           charge's name or bail figure is a second source of truth that can disagree. */
        writer.WritePropertyName("charges");
        writer.WriteStartArray();
        foreach (var code in value.Codes)
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }
}
