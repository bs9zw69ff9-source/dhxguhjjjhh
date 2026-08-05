using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PavlovBot.Tests;

/// <summary>
/// A stand-in for Pavlov's RCON listener, speaking the real handshake: password prompt,
/// MD5 challenge, "Authenticated=1", then JSON replies.
///
/// It counts CONNECTIONS separately from COMMANDS, because that distinction is the whole
/// point of the persistent-session work - a client that opens a socket per command looks
/// identical from the command count alone.
/// </summary>
internal sealed class FakeRconServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _connections;
    private readonly List<string> _commands = [];
    private readonly Lock _sync = new();

    public string Password { get; }
    public int Connections => Volatile.Read(ref _connections);
    public IReadOnlyList<string> Commands { get { lock (_sync) return [.. _commands]; } }

    /// <summary>Set to close the socket immediately after the next command, simulating a drop.</summary>
    public bool DropAfterNextCommand { get; set; }

    /// <summary>Set to reject the password, so auth failure can be asserted.</summary>
    public bool RejectAuth { get; set; }

    /// <summary>Artificial delay before replying, for concurrency tests.</summary>
    public TimeSpan ReplyDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Answer every command as if it were this verb, modelling a server that misattributes.
    /// </summary>
    /// <remarks>
    /// Not hypothetical: production returned a ServerInfo document to a BanList and a
    /// RefreshList document to the same BanList half an hour later. Whatever produces that,
    /// the bot has to survive it, so it has to be reproducible here.
    /// </remarks>
    public string? AnswerEverythingAs { get; set; }

    /// <summary>Reply without a Command field, which some Pavlov replies genuinely do.</summary>
    public bool OmitCommandField { get; set; }

    /// <summary>Answer every command with "Successful": false, as a refusal does.</summary>
    public bool RefuseEverything { get; set; }

    /// <summary>Reply without a Successful field at all - unknown, not failed.</summary>
    public bool OmitSuccessfulField { get; set; }

    /// <summary>Answer with bare text rather than JSON, which some builds do on failure.</summary>
    public string? PlainTextReply { get; set; }

    /// <summary>
    /// Verbs the server ACCEPTS AND NEVER ANSWERS.
    /// </summary>
    /// <remarks>
    /// A hung game thread, which is the failure that matters most here: the connection stays
    /// up and healthy-looking, the command is consumed, and no reply ever comes. Dropping the
    /// socket instead would be caught by the IOException path and prove nothing.
    /// </remarks>
    public HashSet<string> Swallow { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeRconServer(string password = "secret")
    {
        Password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private static string Md5Hex(string s) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buf = new byte[4096];

                await stream.WriteAsync(Encoding.UTF8.GetBytes("Password: "), _cts.Token);

                var n = await stream.ReadAsync(buf, _cts.Token);
                var supplied = Encoding.UTF8.GetString(buf, 0, n).Trim();
                var ok = !RejectAuth && supplied == Md5Hex(Password);
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"Authenticated={(ok ? 1 : 0)}\r\n"), _cts.Token);
                if (!ok) return;

                while (!_cts.IsCancellationRequested)
                {
                    n = await stream.ReadAsync(buf, _cts.Token);
                    if (n == 0) return;
                    var command = Encoding.UTF8.GetString(buf, 0, n).Trim();
                    lock (_sync) _commands.Add(command);

                    if (ReplyDelay > TimeSpan.Zero) await Task.Delay(ReplyDelay, _cts.Token);
                    if (Swallow.Contains(command.Split(' ')[0])) continue;

                    var answering = AnswerEverythingAs ?? command.Split(' ')[0];
                    var outcome = RefuseEverything ? "false" : "true";
                    string reply;
                    if (PlainTextReply is { } plain) reply = plain + "\r\n";
                    else if (OmitCommandField) reply = $"{{\"Successful\":{outcome},\"PlayerList\":[]}}\r\n";
                    else if (OmitSuccessfulField) reply = $"{{\"Command\":\"{answering}\",\"PlayerList\":[]}}\r\n";
                    else reply = $"{{\"Command\":\"{answering}\",\"Successful\":{outcome},\"PlayerList\":[]}}\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), _cts.Token);

                    if (DropAfterNextCommand) { DropAfterNextCommand = false; return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
