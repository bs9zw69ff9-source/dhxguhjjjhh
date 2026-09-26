using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Discord;

/// <summary>
/// Runs interaction handlers OFF the Discord gateway's event loop, and keeps track of them.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. Discord.Net awaits every event handler inline in the loop that reads the
/// gateway socket. A slash command awaited there - /rotatemap restarting three servers, a slow
/// RCON sweep - stopped the bot reading Discord for its whole duration: every other user's
/// interaction queued behind it and missed its three-second acknowledgement window, and past the
/// heartbeat interval (about 41s) Discord.Net declared the connection dead and reconnected.
///
/// So the event handler only hands the work to the thread pool and returns. The tasks are
/// TRACKED rather than fire-and-forget: a fault is logged instead of going unobserved, and
/// shutdown can wait for commands in flight to finish instead of cutting them off mid-write.
/// </remarks>
internal sealed class InteractionDispatcher(ILogger logger)
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    /// <summary>Handlers currently running.</summary>
    public int InFlight => _running.Count;

    /// <summary>
    /// Start <paramref name="work"/> on the thread pool and return at once.
    /// </summary>
    /// <returns>The tracked task, for tests; the gateway does not wait on it.</returns>
    public Task Run(string what, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var task = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Handlers report their own failures to the caller; this is only reached by
                // something that escaped that, and it must not become an unobserved exception.
                logger.LogError(ex, "{What} failed outside its own error handling", what);
            }
        });

        _running.TryAdd(task, 0);
        _ = task.ContinueWith(
            static (done, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(done, out _),
            _running, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return task;
    }

    /// <summary>
    /// Wait for every handler in flight, up to <paramref name="timeout"/>.
    /// </summary>
    /// <returns>True when they all finished in time.</returns>
    public async Task<bool> DrainAsync(TimeSpan timeout)
    {
        var pending = _running.Keys.ToArray();
        if (pending.Length == 0) return true;

        var all = Task.WhenAll(pending);
        return await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false) == all;
    }
}
