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

    /// <summary>
    /// Ask the OS to probe the peer while the session is idle.
    /// </summary>
    /// <remarks>
    /// Without this, a server that CRASHED or a link that dropped leaves a socket that still
    /// reads as Connected - TCP has no way to know until something is sent. The bot would
    /// then believe it had a live session and only discover otherwise on the next command,
    /// which is a failed sweep and a gap in the feeds rather than a reconnect.
    ///
    /// Best effort: these options are not supported everywhere, and a connection without
    /// them still works exactly as before - the command-level retry is the backstop.
    /// </remarks>
    private static void KeepAlive(TcpClient tcp)
    {
        try
        {
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // Probe after 30s idle, then every 10s, and give up after 3 failures - so a dead
            // peer is detected in about a minute rather than at the next command.
            tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            tcp.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or ArgumentException)
        {
            // Nothing to do about it, and nothing depends on it.
        }
    }

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
            KeepAlive(tcp);

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
    /// Tear down the session so the next command starts a fresh one.
    /// </summary>
    /// <remarks>
    /// For a fault the TRANSPORT cannot see. A reply that names another command arrives as a
    /// complete, well-formed, successful exchange - nothing here can tell it is wrong, so
    /// nothing here drops the socket. The client can tell, and if the server's replies are
    /// offset then every later command on this session is wrong too. Reconnecting is the only
    /// way to resynchronise, and it costs one handshake against reading wrong data
    /// indefinitely.
    ///
    /// Takes the gate, so it cannot dispose the stream under an exchange in flight.
    /// </remarks>
    internal async Task ResetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await DisposeSocketAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Send one command and return the raw reply. Serialised: callers queue behind each
    /// other rather than corrupting the stream.
    /// </summary>
    public async Task<string> SendAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        /* Whether this exchange finished cleanly. Anything else means the socket holds an
           unknown number of unread bytes - see the finally. */
        var settled = false;
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
                var reply = await ExchangeAsync(command, ct).ConfigureAwait(false);
                settled = true;
                return reply;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                /* The session died mid-command - a server restart, a map change, an idle
                   reap. Rebuild once and retry: from the caller's side this is a transient
                   blip, not a failure worth surfacing. */
                await ConnectAsync(ct).ConfigureAwait(false);
                var reply = await ExchangeAsync(command, ct).ConfigureAwait(false);
                settled = true;
                return reply;
            }
        }
        finally
        {
            _lastUsed = DateTimeOffset.UtcNow;

            /* AN UNSETTLED EXCHANGE POISONS THE CONNECTION, so it is dropped rather than
               handed to the next caller.

               THE CASE THIS EXISTS FOR IS THE COMMAND TIMEOUT. RconClient wraps every send
               in a linked CancellationTokenSource with CancelAfter(CommandTimeout). When it
               fires mid-exchange the command has ALREADY BEEN WRITTEN and its reply has not
               been read - and OperationCanceledException is not IOException, so the retry
               above did not catch it and the socket stayed open with a reply nobody
               consumed. The next command then wrote, read, and got the PREVIOUS command's
               answer. Every command after that was off by one, permanently, until the idle
               recycle five minutes later.

               That is silent and it is not a crash: RefreshList returns a well-formed
               roster belonging to an older request, ServerInfo returns the reply to a Kick.
               The class remarks call a shared stream out as "the sort of bug that looks
               like the bot occasionally reports the wrong player" - the gate prevents it
               between concurrent callers, and this prevents it between successive ones.

               Reconnecting costs a TCP handshake and an MD5 auth on a path that is already
               failing. Reading the wrong reply costs correctness, everywhere, silently. */
            if (!settled) await DisposeSocketAsync().ConfigureAwait(false);

            _gate.Release();
        }
    }

    private async Task<string> ExchangeAsync(string command, CancellationToken ct)
    {
        var stream = _stream ?? throw new RconException("not connected");
        await WriteAsync(stream, command + "\n", ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        /* A DECODER, NOT Encoding.GetString PER CHUNK, and this is a correctness fix rather
           than a tidy-up. TCP splits wherever it likes, including through the middle of a
           multi-byte character - and a player name is exactly where those live. Decoding each
           chunk independently turned the halves into replacement characters, so a name with
           an accent or an emoji in it came back mangled whenever the split landed inside it:
           intermittent, invisible in a code read, and impossible to reproduce on demand
           because it depends on where the network chose to break. A Decoder holds the partial
           sequence over to the next call. */
        var decoder = Encoding.UTF8.GetDecoder();

        // Sized from the BUFFER, not from 4096: Rent may hand back a larger array, the read
        // above uses all of it, and UTF-8 never decodes to more chars than it had bytes.
        var chars = ArrayPool<char>.Shared.Rent(buffer.Length);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("connection closed while awaiting a reply");

                var decoded = decoder.GetChars(buffer, 0, read, chars, 0);
                sb.Append(chars, 0, decoded);

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
            ArrayPool<char>.Shared.Return(chars);
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
        /* TAKE THE GATE FIRST. Disposing the stream out from under an in-flight exchange
           makes its read throw ObjectDisposedException, and disposing the semaphore while a
           caller is inside SendAsync makes its Release() throw too - during shutdown, from a
           finally, which is where an exception is least welcome and least visible.

           Bounded rather than unconditional: a wedged exchange must not stop the process
           from exiting, and the socket is being torn down either way. */
        var held = await _gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            await DisposeSocketAsync().ConfigureAwait(false);
        }
        finally
        {
            if (held) _gate.Release();
            _gate.Dispose();
        }
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

/// <summary>
/// The server answered a different command than the one that was sent.
/// </summary>
/// <remarks>
/// Its own type so the retry loop can tell it from an ordinary transport error, and so the
/// message names both verbs: "asked X, got Y" is the whole diagnosis, and burying it in a
/// generic failure is how this went unnoticed while moderators read the wrong data.
/// </remarks>
public sealed class MismatchedReplyException : RconException
{
    public MismatchedReplyException(string asked, string answered)
        : base($"sent \"{asked}\" but the server answered \"{answered}\" - the reply belongs to another command") =>
        (Asked, Answered) = (asked, answered);

    public string Asked { get; }
    public string Answered { get; }
}

/// <summary>
/// The server understood the command and refused it.
/// </summary>
/// <remarks>
/// NOT A TRANSPORT FAILURE, and the distinction is the point. A refused Ban and an
/// unreachable server both leave the player unbanned, but one needs a different argument and
/// the other needs the server looked at. Both used to be invisible: the reply was discarded,
/// so "Successful": false was counted as success and logged as applied.
/// </remarks>
public sealed class RconRejectedException : RconException
{
    public RconRejectedException(string command, string reply)
        : base($"the server refused \"{command}\": {reply}") => (Command, Reply) = (command, reply);

    public string Command { get; }
    public string Reply { get; }
}

/// <summary>The server rejected the password. Never worth retrying.</summary>
public sealed class RconAuthException : RconException
{
    public RconAuthException(string message) : base(message) { }
}
