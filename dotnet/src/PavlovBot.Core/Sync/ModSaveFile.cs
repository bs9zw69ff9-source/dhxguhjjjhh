using System.Globalization;

namespace PavlovBot.Core.Sync;

/// <summary>
/// One player's <c>modsave/&lt;player&gt;.txt</c>, in either of the two shapes a mod writes.
/// </summary>
/// <remarks>
/// TWO SHAPES, AND THE BOT ONLY KNEW ONE. The Fallout mod writes a bare number. The police
/// server's mod writes key and value on alternating lines:
/// <code>
/// Money
/// 0
/// DriversLiscense
/// false
/// FirearmLiscense
/// false
/// </code>
/// Every reader parsed the whole file as one integer, so on the keyed shape a balance read as
/// "no ledger", and a write replaced the file with a bare number - taking the licences, and
/// anything else the mod keeps there, with it.
///
/// AN EDIT CHANGES ONE VALUE AND NOTHING ELSE. Keys the bot does not know, their order, their
/// spelling ("Liscense" is the mod's, and the game looks it up that way), the line endings and
/// the trailing newline all come back out as they went in. The game owns this file; the bot
/// is a guest in it.
///
/// Null from <see cref="Parse"/> means "not a shape this understands", and every writer treats
/// that as a refusal. Overwriting a file you cannot read is how the licences were lost.
/// </remarks>
public sealed class ModSaveFile
{
    /// <summary>The key the balance is filed under in the keyed shape.</summary>
    public const string MoneyKey = "Money";

    private readonly long? _bare;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _pairs;
    private readonly string _newline;
    private readonly bool _trailingNewline;

    private ModSaveFile(long? bare, IReadOnlyList<KeyValuePair<string, string>> pairs, string newline, bool trailingNewline)
    {
        _bare = bare;
        _pairs = pairs;
        _newline = newline;
        _trailingNewline = trailingNewline;
    }

    /// <summary>The Fallout shape: the whole file is one number.</summary>
    public bool IsBare => _bare is not null;

    /// <summary>
    /// The file, or null when it is neither shape.
    /// </summary>
    /// <remarks>
    /// The keyed shape must pair up exactly - an odd line count is a file the game is half way
    /// through writing, or one that is not a save at all, and either way it is not edited.
    /// </remarks>
    public static ModSaveFile? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        if (long.TryParse(content.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bare))
            return new ModSaveFile(bare, [], "\n", trailingNewline: false);

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var trailing = lines[^1].Length == 0;
        if (trailing) lines.RemoveAt(lines.Count - 1);

        if (lines.Count == 0 || lines.Count % 2 != 0) return null;

        var pairs = new List<KeyValuePair<string, string>>(lines.Count / 2);
        for (var i = 0; i < lines.Count; i += 2)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) return null;
            pairs.Add(new(lines[i], lines[i + 1]));
        }

        return new ModSaveFile(null, pairs, newline, trailing);
    }

    /// <summary>The balance, or null when the file has none or it is not a number.</summary>
    public long? Money =>
        _bare ?? (Get(MoneyKey) is { } text &&
                  long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null);

    /// <summary>A value by key, ignoring case and surrounding space; null when absent.</summary>
    public string? Get(string key)
    {
        var index = IndexOf(key);
        return index < 0 ? null : _pairs[index].Value;
    }

    /// <summary>Whether a value reads as <c>true</c>. False when it is anything else or absent.</summary>
    public bool IsTrue(string key) =>
        string.Equals(Get(key)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same file with one value replaced, or null when the key is not in it.
    /// </summary>
    /// <remarks>
    /// NEVER ADDS A KEY. A key the mod did not write is one it may not read, or may read as
    /// something else; inventing it is the same mistake as inventing a ledger.
    /// </remarks>
    public ModSaveFile? With(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
            throw new ArgumentException("A value must fit on one line, or it would shift every key after it.", nameof(value));

        var index = IndexOf(key);
        if (index < 0) return null;

        var pairs = _pairs.ToList();
        pairs[index] = new(pairs[index].Key, value);
        return new ModSaveFile(null, pairs, _newline, _trailingNewline);
    }

    /// <summary>The same file with a new balance, or null when it has nowhere to put one.</summary>
    public ModSaveFile? WithMoney(long balance) =>
        IsBare
            ? new ModSaveFile(balance, [], _newline, _trailingNewline)
            : With(MoneyKey, balance.ToString(CultureInfo.InvariantCulture));

    /// <summary>The file's text, as it goes back to disk.</summary>
    public override string ToString()
    {
        if (_bare is { } bare) return bare.ToString(CultureInfo.InvariantCulture);

        var body = string.Join(_newline, _pairs.SelectMany(p => new[] { p.Key, p.Value }));
        return _trailingNewline ? body + _newline : body;
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < _pairs.Count; i++)
        {
            if (string.Equals(_pairs[i].Key.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
