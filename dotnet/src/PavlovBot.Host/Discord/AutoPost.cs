using System.Collections.Concurrent;
using Discord;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord;

/// <summary>Fetches a channel and the message this bot previously posted in it.</summary>
/// <remarks>
/// An interface so the update-in-place logic - the part that had the bug - tests without a
/// gateway connection.
/// </remarks>
public interface IAutoPostTarget
{
    /// <summary>Edit an existing message. False when it is gone.</summary>
    /// <param name="components">Buttons to keep on the message. Null leaves it plain.</param>
    Task<bool> TryEditAsync(ulong channelId, ulong messageId, Embed embed, MessageComponent? components, CancellationToken ct);

    /// <summary>Post a new message and return its id, or null when the channel is unusable.</summary>
    Task<ulong?> SendAsync(ulong channelId, Embed embed, MessageComponent? components, CancellationToken ct);
}

/// <summary>
/// A board that lives as ONE message and is edited in place.
/// </summary>
/// <remarks>
/// Editing rather than re-posting is the whole point. A leaderboard that deletes and
/// re-sends loses its position in the channel, loses its reactions, and pings anyone who
/// pinned it - and for the minutes between the delete and the send, it is simply gone.
///
/// TWO OVERLAPPING CALLS USED TO BOTH SEND. The stored message id was read, found empty,
/// and written by both, so two boards appeared and whichever id landed second orphaned the
/// other - which then got purged, and the board "reset". Serialising per key is the fix:
/// the second call sees the id the first one stored and edits it.
/// </remarks>
public sealed class AutoPost
{
    private readonly IAutoPostTarget _target;
    private readonly SerializedStore _store;
    private readonly ILogger<AutoPost> _logger;

    /// <summary>One chain per board, so unrelated boards never wait on each other.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public AutoPost(IAutoPostTarget target, SerializedStore store, ILogger<AutoPost> logger)
    {
        _target = target;
        _store = store;
        _logger = logger;
    }

    private Dictionary<string, ulong> State() =>
        _store.Read(Datasets.AutopostState, new Dictionary<string, ulong>(StringComparer.Ordinal));

    /// <param name="key">Which board. Also the storage key for its message id.</param>
    /// <param name="build">
    /// Builds the embed. Returning null skips this cycle entirely - used when there is
    /// nothing to show yet, which must NOT clear the board that is already there.
    /// </param>
    /// <param name="components">
    /// Buttons to carry on the board. Re-applied on every EDIT, not just the first post:
    /// omitting them on an edit strips them, so a restart would leave the verification panel
    /// looking correct with no button on it.
    /// </param>
    public async Task PostAsync(string key, ulong? channelId, Func<Task<Embed?>> build,
        CancellationToken ct = default, MessageComponent? components = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(build);
        if (channelId is not { } channel) return;

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Embed? embed;
            try
            {
                embed = await build().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed build leaves the EXISTING board alone. Clearing it because this
                // cycle could not compute is strictly worse than showing stale numbers.
                _logger.LogWarning(ex, "{Key} board build failed - leaving the existing post alone", key);
                return;
            }

            if (embed is null) return;

            var existing = State().GetValueOrDefault(key);
            if (existing != 0 &&
                await _target.TryEditAsync(channel, existing, embed, components, ct).ConfigureAwait(false))
            {
                return;
            }

            // Either there was no message, or it has been deleted. Post a fresh one.
            var posted = await _target.SendAsync(channel, embed, components, ct).ConfigureAwait(false);
            if (posted is null)
            {
                _logger.LogWarning("{Key} board could not be posted to channel {Channel}", key, channel);
                return;
            }

            await _store.UpdateAsync(Datasets.AutopostState,
                new Dictionary<string, ulong>(StringComparer.Ordinal),
                state => { state[key] = posted.Value; return state; }, ct).ConfigureAwait(false);

            if (existing != 0)
                _logger.LogInformation("{Key} board was missing and has been re-posted", key);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forget a board's message id, so the next post creates a new one.</summary>
    public Task ForgetAsync(string key, CancellationToken ct = default) =>
        _store.UpdateAsync(Datasets.AutopostState,
            new Dictionary<string, ulong>(StringComparer.Ordinal),
            state => { state.Remove(key); return state; }, ct);
}
