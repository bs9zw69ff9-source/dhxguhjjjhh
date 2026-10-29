using System.Globalization;

namespace PavlovBot.Core.Network;

/// <summary>How full the disk and the logs are, worst first being the one acted on.</summary>
public enum StorageLevel
{
    Ok,

    /// <summary>Approaching a limit: the owner is told, nothing changes.</summary>
    Warning,

    /// <summary>At a limit: routine logging drops to the minimum and the always-on capture stops.</summary>
    Pressure,

    /// <summary>The disk itself is nearly full: packet logging stops entirely. Events are still written.</summary>
    Critical,
}

/// <summary>The storage limits (<c>ANTIDDOS_LOG_MAX_MB</c>, <c>ANTIDDOS_WARN_FREE_PERCENT</c>, ...).</summary>
/// <param name="MaxLogBytes">The most the log folder may hold, rotated files included; the capture ring has its own budget.</param>
/// <param name="WarnFreePercent">Free space on the log's filesystem below which the owner is warned.</param>
/// <param name="PressureFreePercent">Free space below which routine logging is cut back.</param>
/// <param name="ReserveBytes">Free space that is never used, whatever the percentages say: the OS, the servers, SSH.</param>
public sealed record StorageLimits(
    long MaxLogBytes = 2L * 1024 * 1024 * 1024,
    int WarnFreePercent = 20,
    int PressureFreePercent = 10,
    long ReserveBytes = 2L * 1024 * 1024 * 1024);

/// <summary>A storage reading and what it means.</summary>
public sealed record StorageVerdict(StorageLevel Level, string Reason, LogMode MostLogging, bool CaptureAllowed);

/// <summary>
/// Deciding, from free space and log size, how much logging the disk can afford.
/// </summary>
/// <remarks>
/// LOGGING YIELDS, FILTERING NEVER DOES. The verdict only ever lowers the log mode and stops the
/// optional capture; the drop rules are not touched by it. And events - bans, detections, admin
/// actions - are written at every level, because they are a few lines and the record that matters.
/// </remarks>
public static class StoragePolicy
{
    public static StorageVerdict Judge(long freeBytes, long totalBytes, long logBytes, StorageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var freePercent = totalBytes <= 0 ? 100 : freeBytes * 100.0 / totalBytes;
        var logPercent = limits.MaxLogBytes <= 0 ? 0 : logBytes * 100.0 / limits.MaxLogBytes;

        string Free() => string.Create(CultureInfo.InvariantCulture, $"{freePercent:0.0}% free ({AttackReport.Bytes(freeBytes)})");
        string Logs() => string.Create(CultureInfo.InvariantCulture, $"logs at {logPercent:0}% of their {AttackReport.Bytes(limits.MaxLogBytes)}");

        if (freeBytes < limits.ReserveBytes / 2)
            return new StorageVerdict(StorageLevel.Critical, $"disk nearly full: {Free()}, under half the {AttackReport.Bytes(limits.ReserveBytes)} reserve", LogMode.Off, false);
        if (freeBytes < limits.ReserveBytes || freePercent < limits.PressureFreePercent)
            return new StorageVerdict(StorageLevel.Pressure, $"low disk: {Free()}", LogMode.Minimal, false);
        if (logBytes >= limits.MaxLogBytes)
            return new StorageVerdict(StorageLevel.Pressure, $"{Logs()}; oldest rotated logs are being removed", LogMode.Minimal, false);
        if (freePercent < limits.WarnFreePercent || logPercent >= 80)
            return new StorageVerdict(StorageLevel.Warning, $"{Free()}, {Logs()}", LogMode.Incident, true);
        return new StorageVerdict(StorageLevel.Ok, $"{Free()}, {Logs()}", LogMode.Incident, true);
    }

    /// <summary>
    /// Which files to delete, oldest first, to bring the folder back under <paramref name="target"/>.
    /// Only rotated files: a file being written is never deleted, and events go last.
    /// </summary>
    public static IReadOnlyList<string> Evict(IEnumerable<(string Path, long Bytes, DateTimeOffset Modified, bool Live, bool Events)> files, long target)
    {
        ArgumentNullException.ThrowIfNull(files);
        var all = files.ToList();
        var total = all.Sum(f => f.Bytes);
        var evict = new List<string>();
        foreach (var f in all.Where(f => !f.Live).OrderBy(f => f.Events).ThenBy(f => f.Modified))
        {
            if (total <= target) break;
            evict.Add(f.Path);
            total -= f.Bytes;
        }
        return evict;
    }
}
