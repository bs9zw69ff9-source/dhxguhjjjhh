using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;
using PavlovBot.Host.Servers;

namespace PavlovBot.Host.Network;

/// <summary>
/// tshark (Wireshark's command-line form) for the length of an attack: one copy streams fields
/// for the breakdown, one writes a capped pcap, and afterwards the pcap gets Wireshark's
/// statistics.
/// </summary>
/// <remarks>
/// TSHARK, SO NOBODY NEEDS A DESKTOP. The pcap stays on the server and the bot runs the
/// analysis Wireshark's Statistics menu would - protocol hierarchy, endpoints, conversations -
/// and posts it, so the whole investigation happens in Discord.
///
/// ALMOST EVERY DISSECTOR IS SWITCHED OFF. tshark cannot drop root, and Wireshark's protocol
/// dissectors are a large body of C parsing whatever an attacker sends; only frame, Ethernet,
/// IP, UDP, TCP and ICMP are left on (see <see cref="Protocols"/>). That is every field the
/// breakdown reads, a small fraction of the code an attacker's packet can reach, and much less
/// CPU per packet when there are fifty thousand of them a second. <c>-s 128</c> keeps headers
/// only.
///
/// NO SHELL. Every call is an argv array; the interface is allow-listed before it gets here and
/// the filter, fields and protocols are constants.
///
/// STOPPED WITH SIGTERM, NOT KILLED, because tshark prints its statistics on the way out -
/// including how many packets were dropped because it fell behind, which tells the report
/// whether its breakdown is a census or a sample.
/// </remarks>
public sealed partial class TsharkCapture : IAttackCapture
{
    /// <summary>Inbound IPv4 only: what arrives, not what the servers send back. A capture (BPF) filter.</summary>
    internal const string Filter = "inbound and ip";

    /// <summary>The only dissectors tshark runs. Everything the breakdown reads lives in these.</summary>
    internal const string Protocols = "frame,eth,sll,ip,udp,tcp,icmp";

    /// <summary>The most packets one pcap keeps. 200,000 at 128 bytes is about 30 MB.</summary>
    public const int PcapPacketLimit = 200_000;

    /// <summary>Rows kept per endpoint or conversation table in the statistics; tshark lists them busiest first.</summary>
    internal const int StatisticsRows = 25;

    private const int SnapLength = 128;
    private const int Sigterm = 15;

    private static readonly TimeSpan AnalyseTimeout = TimeSpan.FromMinutes(2);

    private readonly Process _stream;
    private readonly Process? _pcap;
    private readonly Task _reader;
    private readonly ILogger _logger;

    private TsharkCapture(Process stream, Process? pcap, Task reader, ILogger logger)
    {
        _stream = stream;
        _pcap = pcap;
        _reader = reader;
        _logger = logger;
    }

    /// <summary>Where the pcap went, or null when none is being written.</summary>
    public string? PcapPath { get; private init; }

    private static IEnumerable<string> Dissectors() => ["--disable-all-protocols", "--enable-protocol", Protocols];

    /// <summary>The stream copy: one line of fields per packet.</summary>
    internal static IReadOnlyList<string> StreamArgv(string iface) =>
    [
        "-i", iface, "-n", "-l", "-s", SnapLength.ToString(CultureInfo.InvariantCulture), "-B", "64", "-f", Filter,
        .. Dissectors(),
        "-T", "fields", "-E", "separator=,", "-E", "occurrence=f",
        .. CapturedPacket.TsharkFields.SelectMany(field => new[] { "-e", field }),
    ];

    /// <summary>The pcap copy. Writing a file, tshark does not dissect at all.</summary>
    internal static IReadOnlyList<string> PcapArgv(string iface, string path) =>
    [
        "-i", iface, "-n", "-s", SnapLength.ToString(CultureInfo.InvariantCulture), "-B", "64", "-f", Filter,
        "-c", PcapPacketLimit.ToString(CultureInfo.InvariantCulture), "-w", path,
    ];

    /// <summary>Wireshark's Statistics menu, in one pass over a pcap.</summary>
    internal static IReadOnlyList<string> StatisticsArgv(string path) =>
        ["-r", path, "-n", "-q", .. Dissectors(), "-z", "io,phs", "-z", "endpoints,ip", "-z", "conv,ip", "-z", "conv,udp"];

    /// <summary>
    /// Start both copies. Null, with the reason, when tshark cannot run here.
    /// </summary>
    /// <param name="onPacket">Called on the reader's thread for every packet.</param>
    public static (TsharkCapture? Capture, string? Problem) Start(
        string iface, string? pcapPath, Action<CapturedPacket> onPacket, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(onPacket);

        if (!LinkCounters.IsPlausibleInterface(iface)) return (null, $"\"{iface}\" is not a usable interface name");
        if (!Environment.IsPrivilegedProcess) return (null, "the bot is not running as root, so tshark cannot capture");

        Process stream;
        try
        {
            stream = Launch(StreamArgv(iface), readOutput: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, "tshark is not installed - run `/packettools install`");
        }

        Process? pcap = null;
        if (pcapPath is not null)
        {
            try
            {
                pcap = Launch(PcapArgv(iface, pcapPath), readOutput: false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not start the pcap copy of tshark; the summary will have no pcap");
            }
        }

        var capture = new TsharkCapture(stream, pcap, Task.Run(() => ReadAsync(stream, onPacket, logger)), logger)
        {
            PcapPath = pcap is null ? null : pcapPath,
        };
        logger.LogWarning("tshark capturing on {Interface}{Pcap}", iface, pcap is null ? "" : $", pcap to {pcapPath}");
        return (capture, null);
    }

    public Task<string?> StatisticsAsync(CancellationToken ct) =>
        PcapPath is { } path ? StatisticsAsync(path, _logger, ct) : Task.FromResult<string?>(null);

    /// <summary>Stop both, and return what tshark said about the capture.</summary>
    public async Task<CaptureHealth> StopAsync()
    {
        foreach (var process in new[] { _stream, _pcap })
        {
            if (process is null) continue;
            try
            {
                if (!process.HasExited) _ = Kill(process.Id, Sigterm);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var process in new[] { _stream, _pcap })
        {
            if (process is null) continue;
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("tshark did not stop on SIGTERM within 10s; killing it");
                process.Kill(entireProcessTree: true);
            }
        }

        await _reader.ConfigureAwait(false);

        var stderr = await _stream.StandardError.ReadToEndAsync().ConfigureAwait(false);
        return Health(stderr);
    }

    /// <summary>
    /// tshark's closing statistics: "N packets captured" and "M packets dropped from eth0".
    /// </summary>
    internal static CaptureHealth Health(string stderr)
    {
        long Find(Regex pattern) =>
            pattern.Match(stderr ?? "") is { Success: true } m &&
            long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : 0;

        return new CaptureHealth(null, Find(CapturedPattern()), Find(DroppedPattern()));
    }

    [GeneratedRegex(@"(\d+) packets? captured")]
    private static partial Regex CapturedPattern();

    [GeneratedRegex(@"(\d+) packets? dropped")]
    private static partial Regex DroppedPattern();

    /// <summary>
    /// Wireshark's statistics for a pcap, trimmed to fit a report. Null when tshark could not read it.
    /// </summary>
    public static async Task<string?> StatisticsAsync(string pcapPath, ILogger logger, CancellationToken ct)
    {
        if (!File.Exists(pcapPath)) return null;

        var run = await ProcessRunner.RunAsync("tshark", StatisticsArgv(pcapPath), AnalyseTimeout, logger, ct).ConfigureAwait(false);
        if (!run.Started || run.TimedOut || run.ExitCode != 0)
        {
            logger.LogWarning("tshark could not analyse {Pcap}: {Output}", pcapPath, run.TimedOut ? "timed out" : run.Stderr);
            return null;
        }

        return Trim(run.Stdout);
    }

    /// <summary>
    /// The statistics with each endpoint and conversation table cut to its busiest rows.
    /// </summary>
    /// <remarks>
    /// A spoofed flood comes from thousands of addresses, and an untrimmed endpoint table is a
    /// report nobody can read. tshark already lists those tables busiest first, so the first rows
    /// are the top talkers. The protocol hierarchy is short and kept whole.
    /// </remarks>
    internal static string Trim(string statistics, int rows = StatisticsRows)
    {
        var b = new StringBuilder();
        var kept = 0;
        var skipped = 0;

        void Flush()
        {
            if (skipped > 0) b.AppendLine(CultureInfo.InvariantCulture, $"  … {skipped:N0} more row(s)");
            kept = 0;
            skipped = 0;
        }

        foreach (var line in statistics.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("=====", StringComparison.Ordinal))
            {
                Flush();
                b.AppendLine(line);
                continue;
            }

            // A table row starts with an address; everything else is a title, filter or header.
            var isRow = line.Length > 0 && char.IsAsciiDigit(line[0]);
            if (!isRow)
            {
                b.AppendLine(line);
                continue;
            }

            if (kept < rows)
            {
                b.AppendLine(line);
                kept++;
            }
            else
            {
                skipped++;
            }
        }

        Flush();
        return b.ToString().TrimEnd();
    }

    private static Process Launch(IReadOnlyList<string> argv, bool readOutput)
    {
        var info = new ProcessStartInfo("tshark")
        {
            UseShellExecute = false,
            // The pcap copy prints nothing worth reading; its stderr stays out of the bot's log.
            RedirectStandardOutput = readOutput,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var arg in argv) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("tshark did not start");
    }

    private static async Task ReadAsync(Process stream, Action<CapturedPacket> onPacket, ILogger logger)
    {
        try
        {
            while (await stream.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (CapturedPacket.ParseFields(line) is { } packet) onPacket(packet);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogDebug(ex, "tshark's output closed");
        }
    }

    [DllImport("libc.so.6", SetLastError = true, EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_stream.HasExited || _pcap is { HasExited: false }) await StopAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Never started, or already reaped.
        }

        _stream.Dispose();
        _pcap?.Dispose();
    }
}
