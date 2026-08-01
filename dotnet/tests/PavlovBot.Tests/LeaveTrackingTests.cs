using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Leave lines, and where they come from.
/// </summary>
/// <remarks>
/// THE DISCONNECT LINE, not the RCON roster. The port derived leaves by diffing the roster
/// between sweeps, and on a live server that produced nothing at all: an unreachable server
/// and an empty one are indistinguishable from a roster diff, so the sweep declined to
/// conclude anything - correctly, and forever.
///
/// Pavlov's close line carries the address and the account id together. It is the same line
/// the address tracking already treats as a CONFIRMED pairing, and the same one the Node bot
/// has always posted leaves from.
/// </remarks>
public class LeaveTrackingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    // ---- the debounce ----

    [Fact]
    public void OneDisconnectIsReportedOnce()
    {
        /* A single disconnect writes several close lines milliseconds apart. Without the
           debounce, every player leaving posts three or four identical lines and as many
           connection cards - which is what "the feed is spamming" looks like from outside. */
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        Assert.True(FeedBridge.Report(seen, "76561198000000001", Now));
        Assert.False(FeedBridge.Report(seen, "76561198000000001", Now.AddMilliseconds(40)));
        Assert.False(FeedBridge.Report(seen, "76561198000000001", Now.AddSeconds(19)));
    }

    [Fact]
    public void ARejoinAndASecondDropAreBothReported()
    {
        // The failure mode on the other side: somebody with an unstable connection dropping
        // repeatedly is exactly who staff want to see, and silence would hide it.
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        Assert.True(FeedBridge.Report(seen, "abc", Now));
        Assert.True(FeedBridge.Report(seen, "abc", Now.AddSeconds(21)));
    }

    [Fact]
    public void TwoPlayersLeavingTogetherAreBothReported()
    {
        // The debounce is per ACCOUNT. A round ending drops everybody at once, and one line
        // for the whole server would be worse than none.
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        Assert.True(FeedBridge.Report(seen, "alice", Now));
        Assert.True(FeedBridge.Report(seen, "bob", Now));
    }

    // ---- the wording ----

    [Fact]
    public void TheLinesReadTheWayTheNodeBotsDid()
    {
        /* Character-for-character. This is the feed people read every day, and a line that
           suddenly reads "[stamp] LEAVE name" searches differently and drops which server
           the player was on. */
        Assert.EndsWith("Pkdestroy joined Server 1", FeedWebhooks.JoinLine("Pkdestroy", "Server 1", Now), StringComparison.Ordinal);
        Assert.EndsWith("Pkdestroy left Server 1", FeedWebhooks.LeaveLine("Pkdestroy", "Server 1", Now), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownServerIsNotGuessedAt()
    {
        // "Server 1" for what is actually server 2 sends staff to the wrong place.
        Assert.EndsWith("joined the server", FeedWebhooks.JoinLine("Alice", null, Now), StringComparison.Ordinal);
        Assert.EndsWith("left the server", FeedWebhooks.LeaveLine("Alice", "   ", Now), StringComparison.Ordinal);
    }

    [Fact]
    public void ALeaveLineCannotBeForgedByAPlayerName()
    {
        /* Names are attacker-controlled and the feed is plain text, so a newline would let a
           player forge an entire extra line - including somebody else leaving. */
        var line = FeedWebhooks.LeaveLine("Alice\n[2026-07-31 13:33:53] Admin left Server 1", "Server 1", Now);

        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
    }

    // ---- the server numbering ----

    [Fact]
    public void ServersAreNumberedByDiscoveryOrder()
    {
        string[] logs = ["/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log",
                         "/home/steam/pavlov-ttt/Pavlov/Saved/Logs/Pavlov.log"];

        Assert.Equal("Server 1", ServerLabels.Label(logs, logs[0]));
        // Numbered by ORDER, not by folder name - the second install is rarely "…server2".
        Assert.Equal("Server 2", ServerLabels.Label(logs, logs[1]));
    }

    [Fact]
    public void AnUnrecognisedLogIsNotNumbered()
    {
        Assert.Equal("the server", ServerLabels.Label([], "/somewhere/else/Pavlov.log"));
    }

    [Fact]
    public void TheLabelsAreLiveOnceAssigned()
    {
        var labels = new ServerLabels();
        Assert.Equal("the server", labels.Of("/a/Pavlov.log"));

        labels.Assign(["/a/Pavlov.log", "/b/Pavlov.log"]);

        Assert.Equal("Server 2", labels.Of("/b/Pavlov.log"));
    }
}
