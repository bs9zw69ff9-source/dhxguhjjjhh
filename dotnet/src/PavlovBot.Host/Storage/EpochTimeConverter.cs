using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Timestamps as JavaScript milliseconds, in both directions.
/// </summary>
/// <remarks>
/// THE TWO BOTS SHARE ONE DATABASE, and this is what makes that survivable.
///
/// The Node bot writes every timestamp as <c>Date.now()</c> - a number of milliseconds.
/// System.Text.Json's default <see cref="DateTimeOffset"/> handling expects ISO 8601 and
/// THROWS on a number. That exception is caught by <c>SerializedStore.Read</c>, which
/// returns the caller's fallback - so the ban list would read as EMPTY, and the first
/// command that wrote a ban would persist that empty list plus one entry, silently wiping
/// every existing ban on the server.
///
/// So this reads BOTH forms and WRITES the Node form:
///
///   READING accepts a number (Node's, and anything already stored) or an ISO string
///   (written by a build before this converter existed).
///
///   WRITING emits a number, always. If C# wrote ISO strings, the Node bot could no longer
///   read its own data back - <c>Number("2026-07-31T…")</c> is NaN, its ban validator
///   rejects the record as corrupt, and the ban vanishes. Writing the older format is what
///   keeps a rollback possible.
/// </remarks>
public sealed class EpochMillisecondsConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadValue(ref reader) ?? default;

    internal static DateTimeOffset? ReadValue(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // Node's Date.now(). Doubles because JSON has no integer type and a
                // fractional millisecond is not worth rejecting a whole dataset over.
                return reader.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : DateTimeOffset.FromUnixTimeMilliseconds((long)reader.GetDouble());

            case JsonTokenType.String:
            {
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;

                // ISO first - that is what an earlier build of this bot wrote.
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    return parsed;
                }

                // A number that arrived quoted. Some JSON writers do this for large integers.
                return long.TryParse(text, CultureInfo.InvariantCulture, out var quoted)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(quoted)
                    : null;
            }

            case JsonTokenType.Null:
                return null;

            default:
                /* Anything else is not a timestamp - an object or an array where a time
                   should be. SKIP it, which consumes the whole value: returning null
                   without skipping leaves the reader parked mid-token, the deserialiser
                   throws on the next field, and the WHOLE dataset fails. That is precisely
                   the empty-ban-list failure this converter exists to prevent, so getting
                   it wrong here would have reintroduced the bug through the back door. */
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
    }
}

/// <summary>The nullable form. A missing or unreadable timestamp stays null, never epoch zero.</summary>
/// <remarks>
/// The distinction is load-bearing throughout the bot: a ban with a null <c>Expires</c> and
/// <c>Permanent = false</c> is INVALID and is skipped, whereas one with <c>Expires</c> at
/// epoch zero is "expired in 1970" and would be lifted on the next sweep. Collapsing null
/// into a default would quietly unban people.
/// </remarks>
public sealed class NullableEpochMillisecondsConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        EpochMillisecondsConverter.ReadValue(ref reader);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is { } instant) writer.WriteNumberValue(instant.ToUnixTimeMilliseconds());
        else writer.WriteNullValue();
    }
}
