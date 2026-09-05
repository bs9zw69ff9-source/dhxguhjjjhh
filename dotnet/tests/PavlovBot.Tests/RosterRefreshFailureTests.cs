using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Rcon;
using PavlovBot.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// What /players reports when the roster refresh fails, end to end from the wire.
/// </summary>
/// <remarks>
/// THE SYMPTOM THIS REPRODUCES: every server on the board reading "the server's reply to
/// RefreshList could not be parsed", with no way to tell whether the bot or the servers were
/// at fault. FakeRconServer has been able to answer with bare text since it was written and
/// nothing exercised it, so the one failure an operator actually hits had no coverage at all.
/// </remarks>
public class RosterRefreshFailureTests : IAsyncDisposable
{
    private readonly FakeRconServer _server = new();

    private RconRegistry Registry() => new(
        new BotOptions
        {
            DiscordToken = "t",
            Servers =
            [
                new RconOptions
                {
                    Name = "server1",
                    Host = "127.0.0.1",
                    Port = _server.Port,
                    Password = _server.Password,
                    CommandSpacing = TimeSpan.Zero,
                    ReadCacheDuration = TimeSpan.Zero,
                    CommandTimeout = TimeSpan.FromSeconds(5),
                    MaxAttempts = 1,
                },
            ],
            Monitoring = new MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = Path.GetTempPath(),
        },
        new MetricsRegistry(), NullLogger<RconRegistry>.Instance);

    [Fact]
    public async Task ANonJsonReplyIsReportedWithWhatTheServerActuallySaid()
    {
        /* THE FIX. "could not be parsed" names the one thing nobody needed telling; the
           reply itself is the entire diagnosis, and it was being discarded. */
        _server.PlainTextReply = "Error: unknown command RefreshList";
        var rcon = Registry();

        await rcon.RefreshRostersAsync(CancellationToken.None);

        var problem = rcon.RosterProblem("server1");
        Assert.NotNull(problem);
        Assert.Contains("Error: unknown command RefreshList", problem!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonReplyClearsTheProblemAndRecordsTheRoster()
    {
        // The control: a working refresh must not leave a problem behind.
        var rcon = Registry();

        await rcon.RefreshRostersAsync(CancellationToken.None);

        Assert.Null(rcon.RosterProblem("server1"));
        Assert.NotNull(rcon.Roster("server1"));
    }

    [Fact]
    public async Task ARefusalIsReportedAsARefusalRatherThanAParseFailure()
    {
        /* A server that answered "Successful": false is a different fault from one that
           answered something unparseable, and they need different fixes. */
        _server.RefuseEverything = true;
        var rcon = Registry();

        await rcon.RefreshRostersAsync(CancellationToken.None);

        Assert.Equal("the server refused RefreshList", rcon.RosterProblem("server1"));
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _server.DisposeAsync();
    }
}
