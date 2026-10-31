using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Network;

/// <summary>An address or network the owner trusts: never limited, logged as trusted, kept out of CrowdSec bans.</summary>
public sealed record TrustedEntry(string Network, string Comment, string AddedBy, DateTimeOffset AddedAt);

/// <summary>
/// What the owner changed from Discord, kept across restarts: the allow list, threshold
/// overrides and the logging mode.
/// </summary>
public sealed class AntiDdosState
{
    public List<TrustedEntry> Trusted { get; set; } = [];

    /// <summary>Thresholds set with <c>/antiddos threshold</c>, by name; anything absent is the <c>.env</c> value.</summary>
    public Dictionary<string, int> Overrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The mode an owner asked for, and when it lapses back to the configured one.</summary>
    public LogMode? Mode { get; set; }

    public DateTimeOffset? ModeUntil { get; set; }

    public string? ModeBy { get; set; }
}

/// <summary>One threshold an owner may change from Discord, with the range that keeps it safe.</summary>
/// <remarks>
/// THE FLOORS ARE THE POINT. A per-address limit below what a Pavlov client sends would block
/// players; an SSH limit below a person reconnecting would lock the owner out. The ranges stop a
/// typo from doing either.
/// </remarks>
public sealed record Threshold(string Name, int Min, int Max, string Description);

/// <summary>The thresholds, and how the overrides are applied over the configuration.</summary>
public static class Thresholds
{
    public static readonly IReadOnlyList<Threshold> All =
    [
        new("per_ip_pps", 200, 1_000_000, "During an attack: one address over this many packets/s to the game ports is blocked"),
        new("unknown_pps", 500, 10_000_000, "During an attack: the cap on all non-player traffic to the game ports, packets/s"),
        new("block_minutes", 1, 1440, "How long the firewall's own per-address block lasts"),
        new("suspect_udp_pps", 50, 1_000_000, "A non-player over this many UDP packets/s is logged and detected"),
        new("suspect_syn_pps", 5, 100_000, "An address opening TCP connections faster than this is logged"),
        new("ssh_new_per_minute", 6, 600, "New SSH connections a minute one address may open"),
        new("icmp_pps", 10, 1_000_000, "Pings a second from everybody together"),
        new("syn_per_minute", 20, 1_000_000, "New TCP connections a minute that count as a burst"),
        new("syn_block_pps", 10, 100_000, "New TCP connections a second (not SSH) before an address is blocked"),
        new("port_spread", 10, 65_535, "Distinct ports a minute that count as a scan"),
        new("escalate_after", 2, 60, "Detection minutes within the correlation window before an automatic ban"),
        new("ban_minutes", 5, 10_080, "The first automatic ban's length; repeats double it"),
        new("autoban", 0, 1, "1 lets the bot ban repeat offenders automatically, 0 only alerts"),
        new("attack_permaban", 0, 1, "1 bans an address detected during a flood at once and permanently, 0 treats it like any other"),
    ];

    public static Threshold? Find(string? name) => All.FirstOrDefault(t => t.Name == name?.Trim().ToLowerInvariant());

    /// <summary>The configuration with an owner's overrides on top. An override out of its range is ignored.</summary>
    public static AntiDdosOptions Apply(AntiDdosOptions options, ShieldLimits limits, IReadOnlyDictionary<string, int> overrides,
        out ShieldLimits applied)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(overrides);

        int Get(string name, int fallback) =>
            overrides.TryGetValue(name, out var v) && Find(name) is { } t && v >= t.Min && v <= t.Max ? v : fallback;

        applied = limits with
        {
            PerSourcePps = Get("per_ip_pps", limits.PerSourcePps),
            UnknownPps = Get("unknown_pps", limits.UnknownPps),
            BlockMinutes = Get("block_minutes", limits.BlockMinutes),
        };
        return options with
        {
            SuspectUdpPps = Get("suspect_udp_pps", options.SuspectUdpPps),
            SuspectSynPerSecond = Get("suspect_syn_pps", options.SuspectSynPerSecond),
            SshNewPerMinute = Get("ssh_new_per_minute", options.SshNewPerMinute),
            IcmpEchoPps = Get("icmp_pps", options.IcmpEchoPps),
            SynPerMinute = Get("syn_per_minute", options.SynPerMinute),
            SynBlockPps = Get("syn_block_pps", options.SynBlockPps),
            PortSpread = Get("port_spread", options.PortSpread),
            EscalateAfter = Get("escalate_after", options.EscalateAfter),
            BanMinutes = Get("ban_minutes", options.BanMinutes),
            AutoBan = Get("autoban", options.AutoBan ? 1 : 0) == 1,
            AttackPermaban = Get("attack_permaban", options.AttackPermaban ? 1 : 0) == 1,
        };
    }
}

/// <summary>The state file: read once, written whole and atomically on every change.</summary>
public sealed class AntiDdosStateStore(string path, ILogger<AntiDdosStateStore> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private AntiDdosState? _state;

    public string Path => path;

    /// <summary>A copy of the state, so a caller can never change it without <see cref="Update"/>.</summary>
    public AntiDdosState Read()
    {
        lock (_gate) return Clone(Load());
    }

    /// <summary>Change the state and save it. False, with the reason logged, when it could not be saved.</summary>
    public bool Update(Action<AntiDdosState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var next = Clone(Load());
            change(next);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                AtomicFile.Write(path, JsonSerializer.Serialize(next, Json));
                _state = next;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Could not save the anti-DDoS state to {Path}; the change was not made", path);
                return false;
            }
        }
    }

    private AntiDdosState Load()
    {
        if (_state is not null) return _state;
        try
        {
            _state = File.Exists(path) ? JsonSerializer.Deserialize<AntiDdosState>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file must not take the firewall down: start from the configuration, and say so loudly.
            logger.LogError(ex, "Could not read the anti-DDoS state {Path}; starting from the .env settings alone", path);
            _state = new AntiDdosState();
        }
        return _state;
    }

    private static AntiDdosState Clone(AntiDdosState s) => new()
    {
        Trusted = [.. s.Trusted],
        Overrides = new Dictionary<string, int>(s.Overrides, StringComparer.Ordinal),
        Mode = s.Mode,
        ModeUntil = s.ModeUntil,
        ModeBy = s.ModeBy,
    };
}

/// <summary>
/// Where the anti-DDoS logs live, and the files that make up each one.
/// </summary>
/// <remarks>
/// ONE FILE PER KIND, ROTATED BY LOGROTATE: <c>packets/packets.jsonl</c> (ulogd2, one line per
/// logged packet), <c>sources.jsonl</c> (one line per source per minute), <c>traffic.jsonl</c> (one
/// line per minute) and <c>events.jsonl</c>. logrotate renames them to <c>name-YYYYMMDD-HHMMSS</c>
/// and gzips the older ones; the bot opens a file per write, so a rename needs no signal to it.
/// </remarks>
public sealed class AntiDdosPaths(string root)
{
    public string Root { get; } = root;

    public string Packets => System.IO.Path.Combine(Root, "packets", "packets.jsonl");

    public string Sources => System.IO.Path.Combine(Root, "sources.jsonl");

    public string Traffic => System.IO.Path.Combine(Root, "traffic.jsonl");

    public string Events => System.IO.Path.Combine(Root, "events.jsonl");

    public string Capture => System.IO.Path.Combine(Root, "capture");

    public string Exports => System.IO.Path.Combine(Root, "exports");

    public string File(RecordKind kind) => kind switch
    {
        RecordKind.Packet => Packets,
        RecordKind.Source => Sources,
        RecordKind.Traffic => Traffic,
        RecordKind.Event => Events,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>A kind's files, oldest first: rotated ones by the time in their name, then the live one.</summary>
    public IReadOnlyList<FileInfo> Files(RecordKind kind)
    {
        var live = new FileInfo(File(kind));
        var directory = live.Directory;
        if (directory is null || !directory.Exists) return [];

        var rotated = directory.EnumerateFiles(live.Name + "-*")
            .Where(f => !f.Name.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(f => f.Name.Replace(".gz", "", StringComparison.Ordinal), StringComparer.Ordinal)
            .ToList();
        if (live.Exists) rotated.Add(live);
        return rotated;
    }

    /// <summary>Every file under the log folder except the capture ring, which has its own budget.</summary>
    public IEnumerable<FileInfo> AllLogFiles()
    {
        var root = new DirectoryInfo(Root);
        if (!root.Exists) return [];
        return root.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !f.FullName.StartsWith(Capture + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}

/// <summary>
/// Appending the bot's own records, and reading any of them back.
/// </summary>
/// <remarks>
/// NEVER THROWS INTO THE CALLER. A disk that refuses a write costs that record, logged as a
/// warning - never the firewall update or the detection that produced it.
///
/// OPEN, APPEND, CLOSE, per batch. Nothing holds a log open, so logrotate's rename-and-recreate
/// works without signalling the bot, and a new file is created 0600.
/// </remarks>
public sealed class AntiDdosLog(AntiDdosPaths paths, ILogger<AntiDdosLog> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AntiDdosPaths Paths => paths;

    public async Task WriteAsync(RecordKind kind, IReadOnlyList<string> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0 || kind == RecordKind.Packet) return;

        var path = paths.File(kind);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(paths.Root);
            var fresh = !File.Exists(path);
            var text = new StringBuilder();
            foreach (var line in lines) text.Append(line).Append('\n');
            await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), ct).ConfigureAwait(false);
                // Events are few and they are the record: on disk before the next one, so a power
                // cut or a kernel panic mid-attack cannot take the last minutes with it.
                if (kind == RecordKind.Event) stream.Flush(flushToDisk: true);
            }
            if (fresh && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write {Count} {Kind} record(s) to {Path}", lines.Count, kind, path);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Record an event, and put it in the bot's own log at its severity.</summary>
    public Task EventAsync(AntiDdosEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        var level = e.Severity switch { "critical" => LogLevel.Error, "warning" => LogLevel.Warning, _ => LogLevel.Information };
        logger.Log(level, "ANTI-DDOS {Category} {Action} {Source}: {Reason} (by {Actor}, until {Expires}, incident {Incident})",
            e.Category, e.Action, e.Source, e.Reason, e.Actor, e.Expires, e.Incident);
        return WriteAsync(RecordKind.Event, [AntiDdosRecords.Event(e)], ct);
    }

    /// <summary>
    /// The records of these kinds between two times, oldest first.
    /// </summary>
    /// <remarks>
    /// ONLY THE FILES THAT CAN HOLD THE RANGE ARE OPENED. A rotated file last written before
    /// <paramref name="from"/> holds nothing after it, and records are appended in time order, so
    /// reading a file stops at the first record past <paramref name="to"/> - with a minute's slack,
    /// since packet and minute records can arrive slightly out of order.
    /// </remarks>
    public async IAsyncEnumerable<LogRecord> ReadAsync(
        IEnumerable<RecordKind> kinds, DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var slack = TimeSpan.FromMinutes(1);
        foreach (var kind in kinds)
        {
            foreach (var file in paths.Files(kind))
            {
                if (file.LastWriteTimeUtc < from.UtcDateTime - slack) continue;

                Stream? stream = null;
                try
                {
                    stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (file.Name.EndsWith(".gz", StringComparison.Ordinal)) stream = new GZipStream(stream, CompressionMode.Decompress);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not open {File}", file.FullName);
                    if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (true)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    {
                        // A gzip cut short by a full disk, or a file rotated away mid-read: keep what was read.
                        logger.LogWarning(ex, "Stopped reading {File} early", file.FullName);
                        break;
                    }
                    if (line is null) break;
                    if (AntiDdosRecords.Parse(line) is not { } record) continue;
                    if (record.Time > to + slack) break;
                    if (record.Time >= from && record.Time <= to) yield return record;
                }
            }
        }
    }

    /// <summary>The oldest record time and total size of a kind, for the retention report.</summary>
    public async Task<(DateTimeOffset? Oldest, long Bytes, int Files)> SummaryAsync(RecordKind kind, CancellationToken ct)
    {
        var files = paths.Files(kind);
        var bytes = files.Sum(f => f.Length);
        if (files.Count == 0) return (null, 0, 0);

        await foreach (var record in ReadAsync([kind], DateTimeOffset.MinValue.AddMinutes(2), DateTimeOffset.MaxValue.AddMinutes(-2), ct).ConfigureAwait(false))
            return (record.Time, bytes, files.Count);
        return (null, bytes, files.Count);
    }

    /// <summary>A stamp for file names: <c>20261029-173100</c>.</summary>
    public static string FileStamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
