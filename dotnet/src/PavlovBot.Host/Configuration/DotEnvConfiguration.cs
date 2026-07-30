using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace PavlovBot.Host.Configuration;

/// <summary>
/// Reads the same <c>.env</c> file the Node bot reads, as an <see cref="IConfiguration"/> source.
/// </summary>
/// <remarks>
/// The point of this class is that the two bots must read ONE file identically during the
/// migration. A subtle difference in parsing - a truncated password, a quote kept as part
/// of a token - would present as an authentication failure with no obvious cause, so this
/// matches dotenv 16's grammar rather than inventing a cleaner one:
///
///   - `export ` prefixes are allowed and ignored
///   - `KEY=value` and `KEY: value` both bind
///   - single, double and backtick quotes are stripped; escapes are expanded only inside
///     double quotes, which is what dotenv does
///   - an UNQUOTED value ends at the first `#`. That is dotenv's inline-comment rule and
///     it means a value containing a hash MUST be quoted. Diverging here to be "safer"
///     would make the same file mean different things to the two bots, which is worse.
/// </remarks>
public sealed partial class DotEnvConfigurationProvider : ConfigurationProvider
{
    private readonly string _path;
    private readonly bool _optional;

    public DotEnvConfigurationProvider(string path, bool optional)
    {
        _path = path;
        _optional = optional;
    }

    public override void Load()
    {
        if (!File.Exists(_path))
        {
            if (!_optional) throw new FileNotFoundException($".env file not found: {_path}", _path);
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        Data = Parse(File.ReadAllText(_path));
    }

    // Mirrors dotenv 16's LINE regex. Kept as one expression rather than hand-rolled
    // scanning so the correspondence is checkable against the source it came from.
    [GeneratedRegex(
        @"^\s*(?:export\s+)?([\w.-]+)(?:\s*=\s*?|:\s+?)(\s*'(?:\\'|[^'])*'|\s*""(?:\\""|[^""])*""|\s*`(?:\\`|[^`])*`|[^#\r\n]+)?\s*(?:#.*)?$",
        RegexOptions.Multiline)]
    private static partial Regex LineRegex { get; }

    /// <summary>Parse the contents of a .env file. Exposed for tests.</summary>
    public static Dictionary<string, string?> Parse(string contents)
    {
        // OrdinalIgnoreCase to match IConfiguration's own key comparison, so a .env key and
        // an environment variable of differing case are one setting rather than two.
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in LineRegex.Matches(contents.Replace("\r\n", "\n", StringComparison.Ordinal)))
        {
            var key = match.Groups[1].Value;
            if (key.Length == 0) continue;

            var raw = match.Groups[2].Success ? match.Groups[2].Value.Trim() : "";
            data[key] = Unquote(raw);
        }
        return data;
    }

    private static string Unquote(string raw)
    {
        if (raw.Length < 2) return raw;

        var first = raw[0];
        if ((first is '"' or '\'' or '`') && raw[^1] == first)
        {
            var inner = raw[1..^1];
            /* ONLY \n and \r, and only inside double quotes. dotenv does not unescape \"
               - a backslash-quote stays a literal backslash-quote - and the differential
               harness caught this port doing it, which would have silently changed the
               value of any token containing one. Single and backtick quotes are literal. */
            return first == '"'
                ? inner.Replace("\\n", "\n", StringComparison.Ordinal)
                       .Replace("\\r", "\r", StringComparison.Ordinal)
                : inner;
        }
        return raw;
    }
}

/// <summary>Registers a .env file as a configuration source.</summary>
public sealed class DotEnvConfigurationSource : IConfigurationSource
{
    public required string Path { get; init; }
    public bool Optional { get; init; } = true;

    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new DotEnvConfigurationProvider(Path, Optional);
}

public static class DotEnvConfigurationExtensions
{
    /// <summary>
    /// Add a .env file. Register it BEFORE <c>AddEnvironmentVariables</c>: a real
    /// environment variable must win, which is both what dotenv does and what makes a
    /// container override work.
    /// </summary>
    public static IConfigurationBuilder AddDotEnvFile(
        this IConfigurationBuilder builder, string path, bool optional = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new DotEnvConfigurationSource { Path = path, Optional = optional });
    }
}
