using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Network;

/// <summary>The kernel's counters at one moment, as the bot last read them.</summary>
public sealed record Reading(DateTimeOffset Time, string Interface, (long Packets, long Bytes)? Link, LinkHealth? Health, Accounting Accounting);

/// <summary>
/// The last reading, kept on disk, so time the bot was not running is recorded instead of lost.
/// </summary>
/// <remarks>
/// WHY. The kernel keeps counting every source while the bot is down - crashed under a flood,
/// restarted, being deployed - and a counter only grows. Without the last reading, the bot's first
/// reading after it comes back can only be a baseline, and everything that arrived while it was
/// gone is thrown away: exactly the minutes an attack that took the bot down would have filled.
/// With it, the difference is one record covering the whole gap.
///
/// WHAT IS EXACT AND WHAT IS NOT. The interface's own received count is exact across any gap. The
/// per-source entries are not: the kernel forgets a source three minutes after its last packet, so
/// a source that went quiet more than three minutes before the bot came back is missing from the
/// gap record. Per-source totals for a gap are therefore a lower bound; the capture service's ring
/// (anti-ddos-capture.service) is what holds every packet's header for that time.
///
/// SMALL, AND WRITTEN WHOLE. Compact arrays, one atomic write a minute, 0600. Even a spoofed flood
/// that fills the kernel's sets is a few megabytes.
/// </remarks>
public sealed class ReadingStore(string path, ILogger<ReadingStore> logger)
{
    public string Path => path;

    public void Save(Reading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        try
        {
            var buffer = new ArrayBufferWriter<byte>(4096);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteNumber("v", 1);
                w.WriteString("ts", reading.Time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                w.WriteString("iface", reading.Interface);
                if (reading.Link is { } link)
                {
                    w.WriteNumber("rx_packets", link.Packets);
                    w.WriteNumber("rx_bytes", link.Bytes);
                }
                if (reading.Health is { } h)
                {
                    w.WriteStartArray("health");
                    foreach (var n in new[] { h.NicDropped, h.NicMissed, h.NicFifo, h.NicOver, h.SoftnetDropped, h.ConntrackDrops, h.ConntrackCount, h.ConntrackMax })
                        w.WriteNumberValue(n);
                    w.WriteStringValue(h.OperState);
                    w.WriteEndArray();
                }
                Flows(w, "flows", reading.Accounting.Flows);
                Flows(w, "syn", reading.Accounting.Syn);
                Flows(w, "other", reading.Accounting.Other);
                w.WriteStartArray("dropped");
                foreach (var (source, c) in reading.Accounting.Dropped)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(source);
                    w.WriteNumberValue(c.Packets);
                    w.WriteNumberValue(c.Bytes);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, buffer.WrittenSpan.ToArray());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save the last reading to {Path}; a gap while the bot is down would go unrecorded", path);
        }
    }

    private static void Flows(Utf8JsonWriter w, string name, IReadOnlyDictionary<FlowKey, Counter> flows)
    {
        w.WriteStartArray(name);
        foreach (var (key, c) in flows)
        {
            w.WriteStartArray();
            w.WriteStringValue(key.Source);
            w.WriteStringValue(key.Protocol);
            w.WriteNumberValue(key.Port);
            w.WriteNumberValue(c.Packets);
            w.WriteNumberValue(c.Bytes);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    /// <summary>The saved reading, or null when there is none or it cannot be read.</summary>
    public Reading? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            if (!DateTimeOffset.TryParse(r.GetProperty("ts").GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                return null;

            (long, long)? link = r.TryGetProperty("rx_packets", out var p) && r.TryGetProperty("rx_bytes", out var b)
                ? (p.GetInt64(), b.GetInt64())
                : null;
            LinkHealth? health = null;
            if (r.TryGetProperty("health", out var h) && h.ValueKind == JsonValueKind.Array && h.GetArrayLength() == 9)
            {
                var n = h.EnumerateArray().Take(8).Select(e => e.GetInt64()).ToArray();
                health = new LinkHealth(n[0], n[1], n[2], n[3], n[4], n[5], n[6], n[7], h[8].GetString() ?? "unknown");
            }

            var dropped = new Dictionary<string, Counter>(StringComparer.Ordinal);
            if (r.TryGetProperty("dropped", out var d))
                foreach (var e in d.EnumerateArray()) dropped[e[0].GetString()!] = new Counter(e[1].GetInt64(), e[2].GetInt64());

            return new Reading(time, r.GetProperty("iface").GetString() ?? "", link, health,
                new Accounting(ReadFlows(r, "flows"), ReadFlows(r, "syn"), ReadFlows(r, "other"), dropped));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
                                       or KeyNotFoundException or IndexOutOfRangeException or FormatException)
        {
            logger.LogWarning(ex, "The saved reading {Path} is unreadable; this start has no gap to record", path);
            return null;
        }
    }

    private static Dictionary<FlowKey, Counter> ReadFlows(JsonElement r, string name)
    {
        var flows = new Dictionary<FlowKey, Counter>();
        if (!r.TryGetProperty(name, out var array)) return flows;
        foreach (var e in array.EnumerateArray())
            flows[new FlowKey(e[0].GetString()!, e[1].GetString()!, e[2].GetInt32())] = new Counter(e[3].GetInt64(), e[4].GetInt64());
        return flows;
    }

    /// <summary>For logs: how long ago a reading was.</summary>
    public static string Ago(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:0.#} h")
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0} min");
}
