using PavlovBot.Host.Logs;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Confirming a command ran by seeing it in Pavlov.log, so a grant is not reported failed
/// just because the RCON reply was unreadable.
/// </summary>
public class RconConfirmationsTests
{
    private const string Command = "GiveMenu Bob 0010010000000000101000000000000 10001000000000";

    [Fact]
    public void ACommandSeenAtOrAfterTheSendCounts()
    {
        var clock = new TestClock();
        var sut = new RconConfirmations(clock);

        var sentAt = clock.GetUtcNow();
        sut.Note(Command);   // seen at the same instant

        Assert.True(sut.ConfirmedSince(Command, sentAt));
    }

    [Fact]
    public void ACommandSeenBEFORETheSendDoesNotCount()
    {
        // An earlier identical grant left a log line; this send must not be confirmed by it.
        var clock = new TestClock();
        var sut = new RconConfirmations(clock);

        sut.Note(Command);                       // an old occurrence
        clock.Advance(TimeSpan.FromSeconds(30));
        var sentAt = clock.GetUtcNow();          // we send later

        Assert.False(sut.ConfirmedSince(Command, sentAt));
    }

    [Fact]
    public void AnUnseenCommandIsNotConfirmed()
    {
        var sut = new RconConfirmations(new TestClock());
        Assert.False(sut.ConfirmedSince(Command, new TestClock().GetUtcNow()));
    }

    [Fact]
    public void SpacingDifferencesDoNotCauseAFalseMiss()
    {
        // The bit code carries an internal space, and the log may render runs of whitespace
        // differently than the bot sent them.
        var clock = new TestClock();
        var sut = new RconConfirmations(clock);
        var sentAt = clock.GetUtcNow();

        sut.Note("GiveMenu Bob 0010010000000000101000000000000   10001000000000");   // extra spaces
        Assert.True(sut.ConfirmedSince(Command, sentAt));
    }

    [Fact]
    public async Task ConfirmedAsyncReturnsTrueOnceTheLineArrives()
    {
        // Real clock: the wait polls on a real timer. Note it mid-wait, as the tail would.
        var sut = new RconConfirmations();
        var sentAt = DateTimeOffset.UtcNow;

        var waiting = sut.ConfirmedAsync(Command, sentAt, TimeSpan.FromSeconds(2));
        _ = Task.Run(async () => { await Task.Delay(150); sut.Note(Command); });

        Assert.True(await waiting);
    }

    [Fact]
    public async Task ConfirmedAsyncGivesUpWhenTheLineNeverArrives()
    {
        var sut = new RconConfirmations();

        Assert.False(await sut.ConfirmedAsync(Command, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(200)));
    }
}
