namespace PavlovBot.Host.Storage;

/// <summary>Creating directories inside a Pavlov install without taking them away from the game.</summary>
internal static class GameDirectories
{
    /// <summary>The install's <c>Pavlov/Saved/Config</c>: present on every real server.</summary>
    public static string ConfigPath(string installRoot) => Path.Combine(installRoot, "Pavlov", "Saved", "Config");

    /// <summary>Create every missing level, each owned like the nearest existing ancestor.</summary>
    /// <remarks>
    /// The bot runs as root; the game runs as steam. A root-owned ModSave is one the game's mods
    /// cannot write into, which fails with no message at all.
    /// </remarks>
    public static void CreateOwnedLikeParent(string directory)
    {
        var missing = new Stack<string>();
        var existing = Path.GetFullPath(directory).TrimEnd('/');
        while (!Directory.Exists(existing))
        {
            missing.Push(existing);
            existing = Path.GetDirectoryName(existing) ?? throw new IOException($"no existing parent for {directory}");
        }

        var owner = UnixFileOwnership.Get(existing);
        while (missing.TryPop(out var level))
        {
            Directory.CreateDirectory(level);
            if (owner is { } o) UnixFileOwnership.Set(level, o.Uid, o.Gid);
        }
    }
}
