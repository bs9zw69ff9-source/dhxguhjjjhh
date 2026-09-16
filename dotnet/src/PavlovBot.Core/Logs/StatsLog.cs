using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PavlovBot.Core.Logs;

/// <param name="Weapon">Null when the game recorded "None" - a fall, drowning, the world.</param>
/// <param name="At">From the line's own bracket stamp, not the clock when it was read.</param>
public sealed record StatsKill(string Killer, string Killed, string? Weapon, bool Headshot, DateTimeOffset At)
{
    /// <summary>Killer and killed are the same person.</summary>
    public bool Suicide => string.Equals(Killer, Killed, StringComparison.Ordinal);
}

/// <param name="State">"Starting", "Started", "Ended" - whatever the game writes.</param>
public sealed record StatsRoundState(string State, DateTimeOffset At);

/// <summary>
/// Reading Pavlov's Stats.log, which records kills as structured JSON.
/// </summary>
/// <remarks>
/// A BETTER SOURCE THAN Pavlov.log FOR THE SAME EVENT. The kill feed used to scrape
/// Pavlov.log with three independent regexes - one per field - and reassemble a record from
/// whichever fragments turned up, keyed by file, because the game splits the JSON across
/// lines there. That cost three things:
///
///   THE TIMESTAMP. A reassembled record has no line of its own, so kills were stamped with
///   the clock at the moment the poll noticed them - up to a poll interval late, and further
///   whenever a backlog was being caught up after a restart.
///
///   THE HEADSHOT FLAG. Pavlov.log's verbose kill output does not carry it. Stats.log does.
///
///   bVerboseLogging. The Pavlov.log route needs it on in Game.ini. Stats.log is written
///   regardless.
///
/// THE FORMAT. A header line, then a JSON object indented with tabs, then a closing brace in
/// the first column:
///
///     [2026.09.11-20.01.15] StatManagerLog: {
///         "KillData":
///         {
///             "Killer": "Holosight1",
///             ...
///         }
///     }
///
/// So the reader is a state machine over lines rather than a line-at-a-time matcher - this
/// is tailed incrementally and a block routinely arrives split across two polls.
///
/// THE BRACKET STAMP IS UTC. A RoundState block carries its own "Timestamp" field, and in
/// the sample this was written against the two differ by exactly four hours - the bracket
/// reading 19.45.16 against an inner 15.45.16, which is Eastern in September. Unreal writes
/// its log stamps in UTC, the inner one is local, and the bracket is the one used here
/// because it does not move twice a year.
/// </remarks>
public sealed class StatsLogReader
{
    /// <summary>
    /// A block bigger than this is treated as garbage and dropped.
    /// </summary>
    /// <remarks>
    /// A rotation landing mid-block, or a truncated file, leaves an opening line whose close
    /// never arrives. Without a cap the buffer grows for the life of the process, holding a
    /// whole log in memory to parse one kill. The real blocks are a few hundred bytes.
    /// </remarks>
    private const int MaxBlockBytes = 8 * 1024;

    private const string Marker = "] StatManagerLog: {";

    private readonly StringBuilder _block = new();
    private DateTimeOffset _blockAt;
    private bool _open;

    /// <summary>Blocks abandoned because a new one started before they closed.</summary>
    public int Discarded { get; private set; }

    /// <summary>
    /// Feed one line in. Returns the records it completed, which is zero or one.
    /// </summary>
    /// <remarks>
    /// Returning a list rather than a single record keeps the caller identical whether the
    /// line closed a block, opened one, or sat in the middle of one.
    /// </remarks>
    public IReadOnlyList<object> Read(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (Header(line) is { } at)
        {
            /* A new header while a block is open means the previous one never closed - a
               rotation or a truncated write. Dropping it is right: half a JSON object
               cannot be parsed, and carrying it forward would corrupt the next one too. */
            if (_open) Discarded++;

            _block.Clear();
            _block.Append('{');
            _blockAt = at;
            _open = true;
            return [];
        }

        if (!_open) return [];

        if (_block.Length > MaxBlockBytes)
        {
            Discarded++;
            _open = false;
            _block.Clear();
            return [];
        }

        // The closing brace of the wrapper sits in the first column; everything inside the
        // object is indented. That is what ends the block.
        if (line.Length > 0 && line[0] == '}')
        {
            _block.Append('}');
            _open = false;

            var json = _block.ToString();
            _block.Clear();
            return Parse(json, _blockAt) is { } record ? [record] : [];
        }

        _block.Append('\n').Append(line);
        return [];
    }

    /// <summary>The timestamp on a block-opening line, or null if this is not one.</summary>
    private static DateTimeOffset? Header(string line)
    {
        if (line.Length < 2 || line[0] != '[') return null;
        if (!line.EndsWith(Marker, StringComparison.Ordinal)) return null;

        var close = line.IndexOf(']', StringComparison.Ordinal);
        if (close < 0) return null;

        return Timestamp(line.AsSpan(1, close - 1));
    }

    /// <summary>"2026.09.11-20.01.15" as UTC. See the class remarks for why UTC.</summary>
    internal static DateTimeOffset? Timestamp(ReadOnlySpan<char> text) =>
        DateTimeOffset.TryParseExact(text, "yyyy.MM.dd-HH.mm.ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    /// <summary>
    /// One complete block. Unknown record types are ignored rather than reported.
    /// </summary>
    /// <remarks>
    /// The game writes several kinds into this file and will write more. Anything not
    /// recognised is skipped silently, because a log reader that complains about a record it
    /// was never asked to handle is noise on every round change.
    /// </remarks>
    private static object? Parse(string json, DateTimeOffset at)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (root.TryGetProperty("KillData", out var kill) && kill.ValueKind == JsonValueKind.Object)
            {
                var killer = Text(kill, "Killer");
                var killed = Text(kill, "Killed");

                // Both names are needed. A kill naming nobody cannot be reported as anything.
                if (killer is null || killed is null) return null;

                return new StatsKill(killer, killed, Weapon(Text(kill, "KilledBy")), Flag(kill, "Headshot"), at);
            }

            if (root.TryGetProperty("RoundState", out var round) && round.ValueKind == JsonValueKind.Object
                && Text(round, "State") is { } state)
            {
                return new StatsRoundState(state, at);
            }

            return null;
        }
        catch (JsonException)
        {
            /* A block that will not parse is dropped, not thrown. This runs inside log
               ingestion, and one malformed record must not cost the rest of the batch. */
            return null;
        }
    }

    /// <summary>"None" is the game's way of saying there was no weapon, so it becomes null.</summary>
    private static string? Weapon(string? raw) =>
        raw is null || raw.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : raw;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
