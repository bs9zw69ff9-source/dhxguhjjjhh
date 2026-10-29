using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Network;

namespace PavlovBot.Host.Network;

/// <summary>
/// tcpdump, run for the length of an attack: one copy streams headers for the breakdown, one
/// writes a capped pcap for Wireshark.
/// </summary>
/// <remarks>
/// TCPDUMP RATHER THAN A HAND-ROLLED RAW SOCKET. It is the tool everybody already trusts, uses
/// libpcap's ring buffer, and prints the same lines whoever reads them; and its pcap opens in
/// Wireshark as-is, which is the next thing anybody does with an attack.
///
/// HEADERS ONLY, AND NOT DECODED. <c>-s 128</c> keeps 128 bytes of each packet, which holds every
/// header the breakdown reads and none of the payload, and no <c>-v</c> keeps tcpdump's decoding
/// to the minimum - the less of an attacker's packet anything parses, the better. The stream copy
/// keeps tcpdump's default of dropping root after it opens the interface.
///
/// NO SHELL. Both run from argv arrays; the interface is allow-listed before it gets here and
/// the filter is a constant, so nothing typed anywhere reaches a command line.
///
/// STOPPED WITH SIGTERM, NOT KILLED, because tcpdump prints its capture statistics on the way
/// out - including how many packets the kernel dropped because it fell behind, which is what
/// tells the report whether its breakdown is a census or a sample.
/// </remarks>
public sealed partial class TcpdumpCapture : IAttackCapture
{
    /// <summary>Inbound IPv4 only: what arrives, not what the servers send back.</summary>
    private static readonly string[] Filter = ["inbound", "and", "ip"];

    /// <summary>The most packets one pcap keeps. 200,000 at 128 bytes is about 30 MB.</summary>
    public const int PcapPacketLimit = 200_000;

    private const int SnapLength = 128;
    private const int Sigterm = 15;

    private readonly Process _stream;
    private readonly Process? _pcap;
    private readonly Task _reader;
    private readonly ILogger _logger;
    private readonly List<string> _streamErrors = [];

    private TcpdumpCapture(Process stream, Process? pcap, Task reader, ILogger logger)
    {
        _stream = stream;
        _pcap = pcap;
        _reader = reader;
        _logger = logger;
    }

    /// <summary>Where the pcap went, or null when none is being written.</summary>
    public string? PcapPath { get; private init; }

    /// <summary>The stream copy's argv.</summary>
    internal static IReadOnlyList<string> StreamArgv(string iface) =>
        ["-i", iface, "-l", "-n", "-t", "-s", SnapLength.ToString(CultureInfo.InvariantCulture), "-B", "16384", .. Filter];

    /// <summary>
    /// The pcap copy's argv.
    /// </summary>
    /// <remarks>
    /// <c>-Z root</c> because tcpdump drops to its own user BEFORE it opens an output file, and
    /// that user cannot write the bot's data directory. This copy only writes what it reads; it
    /// prints nothing, so it decodes nothing.
    /// </remarks>
    internal static IReadOnlyList<string> PcapArgv(string iface, string path) =>
        ["-i", iface, "-n", "-s", SnapLength.ToString(CultureInfo.InvariantCulture), "-B", "16384",
         "-c", PcapPacketLimit.ToString(CultureInfo.InvariantCulture), "-Z", "root", "-w", path, .. Filter];

    /// <summary>
    /// Start both copies. Null, with the reason, when tcpdump cannot run here.
    /// </summary>
    /// <param name="onPacket">Called on the reader's thread for every packet line.</param>
    public static (TcpdumpCapture? Capture, string? Problem) Start(
        string iface, string? pcapPath, Action<CapturedPacket> onPacket, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(onPacket);

        if (!LinkCounters.IsPlausibleInterface(iface)) return (null, $"\"{iface}\" is not a usable interface name");
        if (!Environment.IsPrivilegedProcess) return (null, "the bot is not running as root, so tcpdump cannot capture");

        Process stream;
        try
        {
            stream = Launch(StreamArgv(iface), redirectOutput: true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, "tcpdump is not installed - run `/packettools install`");
        }

        Process? pcap = null;
        if (pcapPath is not null)
        {
            try
            {
                pcap = Launch(PcapArgv(iface, pcapPath), redirectOutput: false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not start the pcap copy of tcpdump; the summary will have no pcap");
            }
        }

        var capture = new TcpdumpCapture(stream, pcap, Task.Run(() => ReadAsync(stream, onPacket, logger)), logger)
        {
            PcapPath = pcap is null ? null : pcapPath,
        };
        logger.LogWarning("tcpdump capturing on {Interface}{Pcap}", iface, pcap is null ? "" : $", pcap to {pcapPath}");
        return (capture, null);
    }

    /// <summary>Stop both, and return what tcpdump said about the capture.</summary>
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

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var process in new[] { _stream, _pcap })
        {
            if (process is null) continue;
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("tcpdump did not stop on SIGTERM within 5s; killing it");
                process.Kill();
            }
        }

        await _reader.ConfigureAwait(false);

        var stderr = await _stream.StandardError.ReadToEndAsync().ConfigureAwait(false);
        return Health(stderr);
    }

    /// <summary>
    /// tcpdump's closing statistics: "N packets captured" and "M packets dropped by kernel".
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

    [GeneratedRegex(@"(\d+) packets? dropped by kernel")]
    private static partial Regex DroppedPattern();

    private static Process Launch(IReadOnlyList<string> argv, bool redirectOutput)
    {
        var info = new ProcessStartInfo("tcpdump")
        {
            UseShellExecute = false,
            // The pcap copy prints nothing to stdout; its two lines of stderr stay out of the bot's log.
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
            RedirectStandardInput = false,
        };
        foreach (var arg in argv) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("tcpdump did not start");
    }

    private static async Task ReadAsync(Process stream, Action<CapturedPacket> onPacket, ILogger logger)
    {
        try
        {
            while (await stream.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (CapturedPacket.Parse(line) is { } packet) onPacket(packet);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogDebug(ex, "tcpdump's output closed");
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
