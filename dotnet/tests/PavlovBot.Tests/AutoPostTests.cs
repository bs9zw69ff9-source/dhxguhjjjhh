using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The bug this class was rewritten for: "leaderboards are being deleted then reset when
/// they should just keep updating."
/// </summary>
public class AutoPostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-autopost-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTarget _target = new();
    private readonly AutoPost _autopost;

    public AutoPostTests()
    {
        var store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _autopost = new AutoPost(_target, store, NullLogger<AutoPost>.Instance);
    }

    private sealed class FakeTarget : IAutoPostTarget
    {
        public int Sends;
        public int Edits;
        public HashSet<ulong> Deleted { get; } = [];
        public TaskCompletionSource? BlockSends { get; set; }
        private ulong _next = 100;

        public async Task<bool> TryEditAsync(ulong channelId, ulong messageId, Embed embed, CancellationToken ct)
        {
            await Task.Yield();
            if (Deleted.Contains(messageId)) return false;
            Edits++;
            return true;
        }

        public async Task<ulong?> SendAsync(ulong channelId, Embed embed, CancellationToken ct)
        {
            if (BlockSends is not null) await BlockSends.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Sends++;
            return _next++;
        }
    }

    private static Task<Embed?> Board() =>
        Task.FromResult<Embed?>(new EmbedBuilder().WithTitle("Leaderboard").WithDescription("rows").Build());

    [Fact]
    public async Task TheFirstPostSendsAndLaterOnesEdit()
    {
        /* A board that deletes and re-sends loses its position in the channel, loses its
           reactions, and is simply GONE for the minutes between. */
        await _autopost.PostAsync("leaderboard", 1, Board);
        await _autopost.PostAsync("leaderboard", 1, Board);
        await _autopost.PostAsync("leaderboard", 1, Board);

        Assert.Equal(1, _target.Sends);
        Assert.Equal(2, _target.Edits);
    }

    [Fact]
    public async Task TwoOverlappingCallsProduceOneBoard()
    {
        /* THE regression. Both calls read the stored id, both found it empty, and both
           sent - so two boards appeared, whichever id landed second orphaned the other,
           and the orphan got purged. The board "reset". */
        _target.BlockSends = new TaskCompletionSource();

        var first = _autopost.PostAsync("leaderboard", 1, Board);
        var second = _autopost.PostAsync("leaderboard", 1, Board);

        _target.BlockSends.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, _target.Sends);
        Assert.Equal(1, _target.Edits);   // the second call edited what the first posted
    }

    [Fact]
    public async Task ADeletedBoardIsRePostedRatherThanLostForever()
    {
        await _autopost.PostAsync("leaderboard", 1, Board);
        _target.Deleted.Add(100);

        await _autopost.PostAsync("leaderboard", 1, Board);
        Assert.Equal(2, _target.Sends);
    }

    [Fact]
    public async Task AFailedBuildLeavesTheExistingBoardAlone()
    {
        // Clearing it because this cycle could not compute is strictly worse than showing
        // stale numbers.
        await _autopost.PostAsync("leaderboard", 1, Board);

        await _autopost.PostAsync("leaderboard", 1, () => throw new InvalidOperationException("no data"));

        Assert.Equal(1, _target.Sends);
        Assert.Equal(0, _target.Edits);
    }

    [Fact]
    public async Task ANullEmbedSkipsTheCycleWithoutClearingTheBoard()
    {
        await _autopost.PostAsync("leaderboard", 1, Board);
        await _autopost.PostAsync("leaderboard", 1, () => Task.FromResult<Embed?>(null));

        Assert.Equal(1, _target.Sends);
        Assert.Equal(0, _target.Edits);
    }

    [Fact]
    public async Task AnUnconfiguredChannelIsANoOp()
    {
        await _autopost.PostAsync("leaderboard", null, Board);
        Assert.Equal(0, _target.Sends);
    }

    [Fact]
    public async Task DifferentBoardsDoNotWaitOnEachOther()
    {
        _target.BlockSends = new TaskCompletionSource();
        var slow = _autopost.PostAsync("leaderboard", 1, Board);

        _target.BlockSends.SetResult();
        await _autopost.PostAsync("arrests", 2, Board).WaitAsync(TimeSpan.FromSeconds(5));
        await slow;

        Assert.Equal(2, _target.Sends);
    }

    [Fact]
    public async Task ForgettingABoardMakesTheNextPostCreateAFreshOne()
    {
        await _autopost.PostAsync("leaderboard", 1, Board);
        await _autopost.ForgetAsync("leaderboard");
        await _autopost.PostAsync("leaderboard", 1, Board);

        Assert.Equal(2, _target.Sends);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
