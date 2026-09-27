namespace PavlovBot.Host.Servers;

/// <summary>
/// One <c>/provisionserver</c> or <c>/deleteserver</c> at a time, and none on a stale layout.
/// </summary>
/// <remarks>
/// Both commands plan from the configuration read at startup and finish by rewriting .env from
/// that plan. Two things went wrong with nothing in between:
///
///   TWO RUNS AT ONCE each wrote .env from their own snapshot, so the second silently undid the
///   first, and two provisions at once aimed at the same slot ran SteamCMD, the config writes and
///   the unit file into one directory side by side.
///
///   A PROVISION AFTER A PROVISION, before the bot restarted onto the first (a bot nothing
///   supervises, or a restart that did not take), still saw the old layout, picked the same slot
///   again and rebuilt over the server it had just made - new RCON password, new config.
///
/// So a run holds the gate, and a run that changed .env leaves it shut until the process
/// restarts and reads the new layout. Every such run ends by restarting the bot, the last
/// server's delete included, now that the bot starts with no servers configured.
/// </remarks>
public sealed class ServerLayoutGate
{
    private readonly Lock _lock = new();
    private string? _running;
    private bool _reloadPending;

    /// <summary>Claim the gate for <paramref name="what"/>, or say why not.</summary>
    /// <returns>Null when claimed; otherwise the reason to give the caller.</returns>
    public string? TryEnter(string what)
    {
        lock (_lock)
        {
            if (_reloadPending)
                return "the server layout already changed and the bot has not restarted onto it yet. " +
                       "Restart the bot and run this again.";
            if (_running is not null)
                return $"{_running} is still running. Wait for its checklist to finish.";

            _running = what;
            return null;
        }
    }

    /// <summary>Release the gate after a run.</summary>
    /// <param name="outcome">The run's result; null when it threw, which is treated as a possible change.</param>
    public void Exit(ProvisionOutcome? outcome)
    {
        lock (_lock)
        {
            _running = null;
            if (outcome is null || outcome.ChangedEnv) _reloadPending = true;
        }
    }

    /// <summary>Release the gate for a run that was refused before it touched anything.</summary>
    public void Abandon()
    {
        lock (_lock) _running = null;
    }
}
