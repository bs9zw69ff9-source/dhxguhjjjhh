using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PavlovBot.Core.Network;

/// <summary>Something that happened, for the event log: a detection, a ban, a decision, a change.</summary>
/// <param name="Category">What kind: <c>flood-start</c>, <c>ban</c>, <c>crowdsec-decision</c>, <c>mode</c>, ...</param>
/// <param name="Severity"><c>info</c>, <c>warning</c> or <c>critical</c>.</param>
/// <param name="Source">The address it is about, when it is about one.</param>
/// <param name="Action">What was done: <c>alerted</c>, <c>banned</c>, <c>unbanned</c>, <c>held</c>, ...</param>
/// <param name="Actor">Who did it: a Discord user, <c>auto</c>, <c>crowdsec</c>.</param>
/// <param name="Incident">The incident it belongs to, so a flood's events can be pulled together.</param>
public sealed record AntiDdosEvent(
    DateTimeOffset Time,
    string Category,
    string Severity,
    string? Source = null,
    string? Action = null,
    string? Reason = null,
    DateTimeOffset? Expires = null,
    string? Actor = null,
    string? Incident = null,
    IReadOnlyDictionary<string, string>? Detail = null);

/// <summary>A window's totals for the traffic log.</summary>
public sealed record TrafficWindow(
    DateTimeOffset Time,
    TimeSpan Window,
    string Interface,
    long RxPackets,
    long RxBytes,
    long PeakPps,
    IReadOnlyDictionary<string, Counter> Counters,
    int Sources,
    int SourcesLogged,
    bool AccountingSaturated,
    LogMode Mode,
    bool Engaged,
    string Storage)
{
    public double Pps => RxPackets / Math.Max(1, Window.TotalSeconds);

    public double Mbps => RxBytes * 8 / 1_000_000.0 / Math.Max(1, Window.TotalSeconds);
}

/// <summary>Which of the three record kinds a line is. They live in separate files and never mix.</summary>
public enum RecordKind
{
    /// <summary>One packet the kernel logged (ulogd2): dropped, suspicious or a sample. Never every packet.</summary>
    Packet,

    /// <summary>One source's totals over a window, from the kernel's counters.</summary>
    Source,

    /// <summary>The whole interface's totals over a window, and every rule counter.</summary>
    Traffic,

    /// <summary>A detection, ban, decision, mode change or administrative action.</summary>
    Event,
}

/// <summary>Any log line, read back into the fields a query filters on.</summary>
/// <param name="Action">Packets: <c>dropped</c> or <c>passed</c> (this table's verdict - ufw may still drop it later). Events: their action.</param>
/// <param name="Category">The drop reason, suspicion, sample or event category.</param>
/// <param name="Ports">Every destination port the record covers (a source record has several).</param>
public sealed record LogRecord(
    RecordKind Kind,
    DateTimeOffset Time,
    string? Source,
    string? Destination,
    int? SourcePort,
    int? DestinationPort,
    string? Protocol,
    long? Length,
    string? Action,
    string? Category,
    string? Flags,
    string? InInterface,
    string? OutInterface,
    long Packets,
    long Bytes,
    IReadOnlyList<int> Ports,
    string Line);

/// <summary>
/// The anti-DDoS logs' line formats: JSON Lines, one object per line, UTC timestamps.
/// </summary>
/// <remarks>
/// WRITTEN BY HAND WITH <see cref="Utf8JsonWriter"/>, not a serializer, so Core stays AOT-safe and
/// the field names are exactly what the documentation promises. Every bot-written line carries
/// <c>"v": 1</c> and its <c>"type"</c>; packet lines are ulogd2's own JSON, read as they are.
/// </remarks>
public static class AntiDdosRecords
{
    public const int Version = 1;

    /// <summary>The time format of every bot-written line: UTC, to the second.</summary>
    public static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static string Source(DateTimeOffset time, TimeSpan window, SourceWindow s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Write(w =>
        {
            Head(w, "source", time);
            w.WriteNumber("window_s", (int)window.TotalSeconds);
            w.WriteString("src", s.Source);
            w.WriteNumber("packets", s.Packets);
            w.WriteNumber("bytes", s.Bytes);
            w.WriteNumber("pps", Math.Round(s.Packets / Math.Max(1, window.TotalSeconds), 1));
            w.WriteNumber("udp", s.Udp);
            w.WriteNumber("tcp", s.Tcp);
            w.WriteNumber("other", s.Other);
            w.WriteNumber("syn", s.Syn);
            w.WriteNumber("dropped", s.Dropped);
            w.WriteNumber("distinct_ports", s.DistinctPorts);
            w.WriteStartArray("top_ports");
            foreach (var p in s.TopPorts)
            {
                w.WriteStartObject();
                w.WriteString("proto", p.Protocol);
                w.WriteNumber("port", p.Port);
                w.WriteNumber("packets", p.Packets);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteBoolean("player", s.Player);
            w.WriteBoolean("trusted", s.Trusted);
        });
    }

    public static string Traffic(TrafficWindow t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return Write(w =>
        {
            Head(w, "traffic", t.Time);
            w.WriteNumber("window_s", (int)t.Window.TotalSeconds);
            w.WriteString("iface", t.Interface);
            w.WriteNumber("rx_packets", t.RxPackets);
            w.WriteNumber("rx_bytes", t.RxBytes);
            w.WriteNumber("pps", Math.Round(t.Pps, 1));
            w.WriteNumber("mbps", Math.Round(t.Mbps, 3));
            w.WriteNumber("peak_pps", t.PeakPps);
            w.WriteStartObject("counters");
            foreach (var (name, c) in t.Counters.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(name);
                w.WriteNumber("packets", c.Packets);
                w.WriteNumber("bytes", c.Bytes);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteNumber("sources", t.Sources);
            w.WriteNumber("sources_logged", t.SourcesLogged);
            w.WriteBoolean("accounting_saturated", t.AccountingSaturated);
            w.WriteString("mode", t.Mode.ToString().ToLowerInvariant());
            w.WriteBoolean("engaged", t.Engaged);
            w.WriteString("storage", t.Storage);
        });
    }

    public static string Event(AntiDdosEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Write(w =>
        {
            Head(w, "event", e.Time);
            w.WriteString("category", e.Category);
            w.WriteString("severity", e.Severity);
            if (e.Source is not null) w.WriteString("src", e.Source);
            if (e.Action is not null) w.WriteString("action", e.Action);
            if (e.Reason is not null) w.WriteString("reason", e.Reason);
            if (e.Expires is { } expires) w.WriteString("expires", Stamp(expires));
            if (e.Actor is not null) w.WriteString("actor", e.Actor);
            if (e.Incident is not null) w.WriteString("incident", e.Incident);
            if (e.Detail is { Count: > 0 } detail)
            {
                w.WriteStartObject("detail");
                foreach (var (key, value) in detail.OrderBy(d => d.Key, StringComparer.Ordinal)) w.WriteString(key, value);
                w.WriteEndObject();
            }
        });
    }

    private static void Head(Utf8JsonWriter w, string type, DateTimeOffset time)
    {
        w.WriteNumber("v", Version);
        w.WriteString("type", type);
        w.WriteString("ts", Stamp(time));
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        // Relaxed escaping: these are log lines read with grep and jq, never embedded in HTML,
        // and "udp-rate+syn-burst" should read as that, not as "udp-rate\u002Bsyn-burst".
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ---- reading ----

    /// <summary>
    /// Any line of any of the logs, or null when it is not one: a ulogd2 packet record, or a
    /// bot-written source, traffic or event record.
    /// </summary>
    public static LogRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            return r.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? ParseOurs(r, type.GetString()!, line)
                : ParsePacket(r, line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A ulogd2 JSON record. Its own <c>timestamp</c> is local time without a zone, so the time is
    /// taken from <c>oob.time.sec</c>/<c>usec</c>, which is UTC epoch.
    /// </summary>
    private static LogRecord? ParsePacket(JsonElement r, string line)
    {
        if (!r.TryGetProperty("oob.time.sec", out var sec) || !sec.TryGetInt64(out var seconds)) return null;
        var micro = r.TryGetProperty("oob.time.usec", out var usec) && usec.TryGetInt64(out var u) ? u : 0;
        var time = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(micro * 10);

        var prefix = Str(r, "oob.prefix") ?? "";
        var (action, category) = FromPrefix(prefix);
        var proto = Int(r, "ip.protocol") ?? Int(r, "ip6.nexthdr");
        var protocol = proto switch { 6 => "tcp", 17 => "udp", 1 => "icmp", 58 => "ipv6-icmp", null => null, var p => p.Value.ToString(CultureInfo.InvariantCulture) };

        string? flags = null;
        if (protocol == "tcp")
        {
            var set = new[] { "syn", "ack", "fin", "rst", "psh", "urg" }.Where(f => Int(r, "tcp." + f) == 1).ToList();
            flags = set.Count == 0 ? "" : string.Join(",", set);
        }

        var dport = Int(r, "dest_port");
        return new LogRecord(
            RecordKind.Packet, time, Str(r, "src_ip"), Str(r, "dest_ip"), Int(r, "src_port"), dport, protocol,
            Long(r, "raw.pktlen") ?? Long(r, "ip.totlen"), action, category, flags, Empty(Str(r, "oob.in")), Empty(Str(r, "oob.out")),
            1, Long(r, "raw.pktlen") ?? 0, dport is { } p0 ? [p0] : [], line);
    }

    /// <summary><c>pbd drop blocked </c> → (dropped, blocked). Another table's prefix keeps its text as the category.</summary>
    public static (string? Action, string? Category) FromPrefix(string prefix)
    {
        var parts = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != FloodShieldRules.Prefix) return (null, prefix.Trim());
        return (parts[1] == "drop" ? "dropped" : "passed", parts[1] == "drop" ? parts[2] : $"{parts[1]}:{parts[2]}");
    }

    private static LogRecord? ParseOurs(JsonElement r, string type, string line)
    {
        if (Str(r, "ts") is not { } ts ||
            !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            return null;
        }

        switch (type)
        {
            case "source":
                var ports = new List<int>();
                string? proto = null;
                if (r.TryGetProperty("top_ports", out var top) && top.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in top.EnumerateArray())
                    {
                        if (Int(p, "port") is { } port) ports.Add(port);
                        proto ??= Str(p, "proto");
                    }
                }
                return new LogRecord(RecordKind.Source, time, Str(r, "src"), null, null, ports.Count > 0 ? ports[0] : null, proto,
                    null, (Long(r, "dropped") ?? 0) > 0 ? "dropped" : "passed", null, null, null, null,
                    Long(r, "packets") ?? 0, Long(r, "bytes") ?? 0, ports, line);
            case "traffic":
                return new LogRecord(RecordKind.Traffic, time, null, null, null, null, null, null, null, Str(r, "mode"), null,
                    Str(r, "iface"), null, Long(r, "rx_packets") ?? 0, Long(r, "rx_bytes") ?? 0, [], line);
            case "event":
                return new LogRecord(RecordKind.Event, time, Str(r, "src"), null, null, null, null, null, Str(r, "action"),
                    Str(r, "category"), null, null, null, 0, 0, [], line);
            default:
                return null;
        }
    }

    private static string? Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static long? Long(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static string? Empty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
