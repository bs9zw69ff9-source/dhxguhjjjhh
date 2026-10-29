namespace PavlovBot.Host.Network;

/// <summary>Free and total space on the filesystem holding a path.</summary>
public static class DiskSpace
{
    /// <summary>Null when the path's filesystem cannot be read; the storage guard then treats space as unknown, not as full.</summary>
    public static (long Free, long Total)? Read(string path)
    {
        try
        {
            // The deepest existing ancestor: the folder itself may not have been created yet.
            var probe = new DirectoryInfo(path);
            while (probe is { Exists: false }) probe = probe.Parent;
            if (probe is null) return null;

            var drive = new DriveInfo(probe.FullName);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
