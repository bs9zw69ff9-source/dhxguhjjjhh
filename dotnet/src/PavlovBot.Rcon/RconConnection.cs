using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PavlovBot.Rcon;

/// <summary>
/// One authenticated TCP session to a Pavlov RCON server.
/// </summary>
/// <remarks>
/// The protocol, per the Pavlov RCON reference:
///   1. server sends "Password: "
///   2. client sends the lowercase hex MD5 of the password
///   3. server replies "Authenticated=1\r\n" (or =0)
///   4. commands are plain UTF-8 lines; replies are JSON terminated by CRLF
///
/// RCON is strictly request/response over a single stream, so commands MUST be
/// serialised. Two concurrent writers would interleave and each would read the other's
/// reply - the sort of bug that looks like "the bot occasionally reports the wrong
/// player". The semaphore is what makes a shared connection safe.
///
/// MD5 is not a security decision here: it is what the protocol specifies, and the
/// password is being sent to a game server on a LAN or over an operator's own link.
/// </remarks>
internal sealed class RconConnection : IAsyncDisposable
{
    private readonly RconOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private DateTimeOffset _lastUsed = DateTimeOffset.UtcNow;

    public RconConnection(RconOptions options) => _options = options;

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;

    /// <summary>True when the session has sat unused long enough to be worth recycling.</summary>
    public bool IsStale => DateTimeOffset.UtcNow - _lastUsed > _options.IdleTimeout;

    private static string Md5Hex(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await DisposeSocketAsync().ConfigureAwait(false);

        var tcp = new TcpClient { NoDelay = true };   // a 3-byte command must not wait on Nagle
        try
        {
            await tcp.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
            var stream = tcp.GetStream();

            // The server speaks first with the password prompt.
            var prompt = await ReadSomeAsync(stream, ct).ConfigureAwait(false);
            if (!prompt.Contains("Password", StringComparison.OrdinalIgnoreCase))
                throw new RconException($"expected a password prompt from {_options.Host}:{_options.Port}, got \"{Truncate(prompt)}\"");

            await WriteAsync(stream, Md5Hex(_options.Password), ct).ConfigureAwait(false);

            var auth = await ReadSomeAsync(stream, ct).ConfigureAwait(false);
            if (!auth.Contains("Authenticated=1", StringComparison.Ordinal))
                throw new RconAuthException($"{_options.Name} rejected the RCON password");

            _tcp = tcp;
            _stream = stream;
            _lastUsed = DateTimeOffset.UtcNow;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Send one command and return the raw reply. Serialised: callers queue behind each
    /// other rather than corrupting the stream.
    /// </summary>
    public async Task<string> SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected || IsStale)
                await ConnectAsync(ct).ConfigureAwait(false);

            /* Spacing is applied INSIDE the gate, so it paces the shared connection rather
               than each caller independently - which is the only way it actually limits the
               rate the server sees. */
            var since = DateTimeOffset.UtcNow - _lastUsed;
            if (since < _options.CommandSpacing)
                await Task.Delay(_options.CommandSpacing - since, ct).ConfigureAwait(false);

            try
            {
                return await ExchangeAsync(command, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                /* The session died mid-command - a server restart, a map change, an idle
                   reap. Rebuild once and retry: from the caller's side this is a transient
                   blip, not a failure worth surfacing. */
                await ConnectAsync(ct).ConfigureAwait(false);
                return await ExchangeAsync(command, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lastUsed = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    private async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        var stream = _stream ?? throw new RconException("not connected");
        await WriteAsync(stream, command + "\n", ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("connection closed while awaiting a reply");
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));

                /* Settle as soon as the buffer is a complete JSON document rather than
                   waiting for a close or a timeout. A truncated document does not parse, so
                   this can only fire on a whole reply. Commands that answer with something
                   other than JSON fall through to the CRLF check below. */
                var text = sb.ToString();
                if (LooksLikeJson(text) && IsCompleteJson(text)) return text;
                if (text.EndsWith("\r\n", StringComparison.Ordinal) && !LooksLikeJson(text)) return text;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.AsSpan().TrimStart();
        return t.Length > 0 && (t[0] == '{' || t[0] == '[');
    }

    private static bool IsCompleteJson(string s)
    {
        try
        {
            using var _ = JsonDocument.Parse(s);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadSomeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            return read == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, read);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string Truncate(string s) =>
        s.Length <= 60 ? s : string.Concat(s.AsSpan(0, 60), "...");

    private async ValueTask DisposeSocketAsync()
    {
        if (_stream is not null) await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSocketAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    // Kept for parity with the invariant culture used elsewhere in formatting.
    internal static string Fmt(int n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>An RCON exchange failed.</summary>
public class RconException : Exception
{
    public RconException(string message) : base(message) { }
    public RconException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The server rejected the password. Never worth retrying.</summary>
public sealed class RconAuthException : RconException
{
    public RconAuthException(string message) : base(message) { }
}
