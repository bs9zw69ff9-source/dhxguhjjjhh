namespace PavlovBot.Host.Servers;

/// <summary>What is keeping this bot process alive, and so what can restart it from inside.</summary>
public enum SupervisorKind
{
    /// <summary>Nothing we recognise: restarting ourselves would just kill the bot.</summary>
    None,
    Pm2,
    Systemd,
}

/// <summary>
/// The process manager running the bot, detected from the environment it was started with.
/// </summary>
/// <remarks>
/// The bot used to assume pm2 and nothing else, so once the live bot moved to a systemd unit,
/// <c>/provisionserver</c> and <c>/deleteserver</c> both finished with "restart the bot yourself",
/// and until somebody did, the bot kept its old server list: a second <c>/deleteserver</c> was
/// refused because the server just deleted still counted as the top one.
///
/// PM2 IS CHECKED FIRST. pm2's own daemon is often started from a systemd unit (pm2-root), so a
/// pm2-managed bot can sit in a systemd cgroup too. Restarting that unit would bounce every pm2
/// app, not just this one; <c>pm_id</c> only exists inside a pm2 app, so it settles it.
/// </remarks>
public sealed record BotSupervisor(SupervisorKind Kind, string Name)
{
    public static readonly BotSupervisor Unmanaged = new(SupervisorKind.None, "");

    /// <summary>What the bot tells people when it restarts itself, e.g. "systemd unit `pavlov-bot`".</summary>
    public string Describe() => Kind switch
    {
        SupervisorKind.Pm2 => $"pm2 app `{Name}`",
        SupervisorKind.Systemd => $"systemd unit `{Name}`",
        _ => "no process manager",
    };

    /// <summary>
    /// The argv that restarts the bot. <c>--no-block</c> for systemd because stopping the unit
    /// kills its whole cgroup, systemctl included; the job is queued in PID 1 either way, so there
    /// is nothing to wait for and no point being killed mid-wait.
    /// </summary>
    public (string File, string[] Args)? RestartCommand() => Kind switch
    {
        SupervisorKind.Pm2 => ("pm2", ["restart", Name]),
        SupervisorKind.Systemd => ("systemctl", ["restart", "--no-block", Name]),
        _ => null,
    };

    /// <summary>Detect the supervisor of this process.</summary>
    public static BotSupervisor Current()
    {
        string? cgroup = null;
        try
        {
            if (File.Exists("/proc/self/cgroup")) cgroup = File.ReadAllText("/proc/self/cgroup");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable means "not provably systemd": the caller reports the manual step.
        }

        return Detect(Environment.GetEnvironmentVariable, cgroup);
    }

    /// <summary>The detection itself, over an injected environment and cgroup file, for tests.</summary>
    internal static BotSupervisor Detect(Func<string, string?> env, string? cgroup)
    {
        if (!string.IsNullOrEmpty(env("pm_id")))
        {
            var app = env("PAVLOV_PM2_APP") ?? env("name") ?? "pavlov-bot-cs";
            return new BotSupervisor(SupervisorKind.Pm2, app);
        }

        // INVOCATION_ID is set by systemd for every service it starts; without it a ".service"
        // cgroup is somebody's shell inside a unit, not the unit's own main process.
        if (string.IsNullOrEmpty(env("INVOCATION_ID"))) return Unmanaged;

        var unit = env("PAVLOV_SYSTEMD_UNIT") ?? UnitFromCgroup(cgroup);
        return unit is not null && unit.EndsWith(".service", StringComparison.Ordinal) &&
               ServiceControl.IsPlausibleUnitName(unit)
            ? new BotSupervisor(SupervisorKind.Systemd, unit[..^".service".Length])
            : Unmanaged;
    }

    /// <summary>
    /// The service from <c>/proc/self/cgroup</c>: the last path segment on the unified (v2) line
    /// <c>0::/system.slice/pavlov-bot.service</c>, or v1's <c>name=systemd</c> line.
    /// </summary>
    internal static string? UnitFromCgroup(string? cgroup)
    {
        if (string.IsNullOrEmpty(cgroup)) return null;

        foreach (var line in cgroup.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(':', 3);
            if (parts.Length != 3 || !(parts[1].Length == 0 || parts[1] == "name=systemd")) continue;

            var last = parts[2][(parts[2].LastIndexOf('/') + 1)..];
            if (last.EndsWith(".service", StringComparison.Ordinal)) return last;
        }

        return null;
    }
}
