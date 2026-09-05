using System.Text;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Replies that arrive in more than one TCP chunk.
/// </summary>
/// <remarks>
/// ASSERTED AGAINST THE WIRE, not the format string. The client decoded every chunk on its
/// own with Encoding.UTF8.GetString, so a multi-byte character split across two reads became
/// two replacement characters - and a player name is exactly where those live. Intermittent,
/// invisible in a code read, and unreproducible on demand against a real server because it
/// depends on where the network chose to break. FakeRconServer chooses the break here.
/// </remarks>
public class RconChunkedReplyTests
{
    private static RconOptions Options(FakeRconServer server) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Password = server.Password,
        Name = "server1",
        CommandTimeout = TimeSpan.FromSeconds(5),
        CommandSpacing = TimeSpan.Zero,
        ReadCacheDuration = TimeSpan.Zero,
    };

    /// <summary>A reply carrying a name whose characters are not one byte each.</summary>
    private const string Reply =
        "{\"Command\":\"RefreshList\",\"Successful\":true,\"PlayerList\":[{\"Username\":\"Réçkeré\"}]}\r\n";

    [Fact]
    public async Task AReplySplitThroughTheMiddleOfACharacterStillDecodes()
    {
        /* THE REGRESSION. The split lands inside the two bytes of "é", so decoding the first
           chunk alone yields a replacement character and loses the second half entirely. */
        var bytes = Encoding.UTF8.GetBytes(Reply);
        var accent = Reply.IndexOf('é', StringComparison.Ordinal);
        var bytesBeforeAccent = Encoding.UTF8.GetByteCount(Reply[..accent]);

        await using var server = new FakeRconServer { ExactReply = Reply, SplitReplyAfterBytes = bytesBeforeAccent + 1 };
        await using var client = new RconClient(Options(server));

        var reply = await client.SendAsync("RefreshList");

        Assert.Equal(Reply.TrimEnd('\r', '\n'), reply.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('�', reply);
        Assert.Contains("Réçkeré", reply, StringComparison.Ordinal);
        Assert.True(bytes.Length > Reply.Length, "the fixture must contain multi-byte characters");
    }

    [Fact]
    public async Task AReplySplitOnACharacterBoundaryIsUnaffected()
    {
        // The control: chunking on its own was never the problem, only chunking mid-character.
        await using var server = new FakeRconServer { ExactReply = Reply, SplitReplyAfterBytes = 10 };
        await using var client = new RconClient(Options(server));

        var reply = await client.SendAsync("RefreshList");

        Assert.Equal(Reply.TrimEnd('\r', '\n'), reply.TrimEnd('\r', '\n'));
        Assert.DoesNotContain('�', reply);
    }

    [Fact]
    public async Task ASplitJsonReplyIsNotSettledUntilItIsComplete()
    {
        /* The other half of the same loop: the first chunk is not valid JSON, so the client
           must keep reading rather than handing back a truncated document. */
        await using var server = new FakeRconServer { ExactReply = Reply, SplitReplyAfterBytes = 20 };
        await using var client = new RconClient(Options(server));

        var reply = await client.SendAsync("RefreshList");

        Assert.EndsWith("}", reply.TrimEnd('\r', '\n'), StringComparison.Ordinal);
    }
}
