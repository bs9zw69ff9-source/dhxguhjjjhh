using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Interaction handlers run off the gateway loop, so one slow command cannot stall the bot.
/// </summary>
public class InteractionDispatcherTests
{
    [Fact]
    public async Task RunReturnsWhileTheWorkIsStillBlocked()
    {
        /* The bug this replaces: Discord.Net awaits the event handler inline in its socket loop,
           so a handler that blocked for a minute stopped the bot reading Discord for a minute. */
        var dispatcher = new InteractionDispatcher(NullLogger.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tracked = dispatcher.Run("slow", () => release.Task);

        Assert.False(tracked.IsCompleted);
        Assert.Equal(1, dispatcher.InFlight);

        release.SetResult();
        await tracked;
        Assert.True(SpinWait.SpinUntil(() => dispatcher.InFlight == 0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DrainWaitsForWorkInFlight()
    {
        var dispatcher = new InteractionDispatcher(NullLogger.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.Run("slow", () => release.Task);

        Assert.False(await dispatcher.DrainAsync(TimeSpan.FromMilliseconds(50)));

        release.SetResult();
        Assert.True(await dispatcher.DrainAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AFaultIsContainedAndLoggedNotThrown()
    {
        // An escaped exception must not become an unobserved task fault.
        var dispatcher = new InteractionDispatcher(NullLogger.Instance);

        var tracked = dispatcher.Run("broken", () => throw new InvalidOperationException("boom"));

        await tracked;   // does not throw
        Assert.True(tracked.IsCompletedSuccessfully);
    }
}
