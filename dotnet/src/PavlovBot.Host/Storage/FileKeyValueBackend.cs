using System.Text.Json;
using PavlovBot.Core.Data;

namespace PavlovBot.Host.Storage;

/// <summary>
/// One JSON file per dataset, written atomically.
/// </summary>
/// <remarks>
/// ATOMICITY IS THE WHOLE POINT. A plain write truncates the file first, so a crash or a
/// full disk mid-write leaves a valid-looking file containing half a ban list. Writing to a
/// temp file in the same directory and then replacing is atomic on every filesystem the bot
/// runs on: a reader sees either the old file or the new one, never a partial.
///
/// Keys are dataset names, not paths. Anything that could escape the data directory is
/// rejected rather than sanitised, because a silently-renamed dataset reads as empty and
/// looks exactly like data loss.
/// </remarks>
public sealed class FileKeyValueBackend : IKeyValueBackend
{
    private readonly string _directory;

    public FileKeyValueBackend(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            key.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"invalid dataset name \"{key}\"", nameof(key));
        }
        return Path.Combine(_directory, $"{key}.json");
    }

    public string? Read(string key)
    {
        var path = PathFor(key);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            // An unreadable dataset is "absent" to the caller, which SerializedStore turns
            // into the fallback. The alternative - throwing - takes down every command that
            // touches this dataset over a transient lock.
            return null;
        }
    }

    public void Write(string key, string json)
    {
        var path = PathFor(key);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
    }
}

/// <summary>
/// <see cref="IJsonCodec"/> over System.Text.Json.
/// </summary>
/// <remarks>
/// Lives in the host, not in Core, precisely so Core stays reflection-free and AOT-clean.
/// A deserialisation failure returns false rather than throwing: a single bad character in
/// a dataset must not take down every command that reads it, and the store turns a false
/// into the caller's fallback.
/// </remarks>
public sealed class SystemTextJsonCodec : IJsonCodec
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonCodec(JsonSerializerOptions? options = null) =>
        _options = options ?? new JsonSerializerOptions
        {
            // Indented because these files are read and hand-edited during incidents. The
            // size difference is irrelevant next to being able to diff them.
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _options);

    public bool TryDeserialize<T>(string json, out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, _options);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
