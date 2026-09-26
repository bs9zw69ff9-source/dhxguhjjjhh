using System.Runtime.InteropServices;

namespace PavlovBot.Host.Storage;

/// <summary>
/// Reads and sets a file's numeric owner, which .NET has no managed API for.
/// </summary>
/// <remarks>
/// WHY IT MATTERS. The game servers run as <c>steam</c>. A file the bot replaces by writing a
/// temp file and renaming it over the original takes the temp file's owner - the bot's user,
/// root today - and the game can then no longer write it. For a caps ledger that means in-game
/// earnings stop saving; for <c>blacklist.txt</c>, in-game bans stop persisting. Nothing fails
/// loudly: the game just cannot save.
///
/// <c>statx</c> rather than <c>stat</c> because its struct layout is the same on every
/// architecture (the kernel defines it explicitly), whereas <c>struct stat</c> differs between
/// x86-64 and arm64. glibc has exported <c>statx</c> since 2.28.
///
/// <c>libc.so.6</c> BY NAME, not <c>libc</c>: the bare name makes the runtime probe
/// <c>libc.so</c> first, which on a box with development headers is a linker script that cannot
/// be loaded. glibc's soname is fixed; on a non-glibc system this binds nothing, ownership is
/// not carried over, and the host warns about it once at startup.
/// </remarks>
internal static class UnixFileOwnership
{
    private const int AtFdCwd = -100;
    private const uint StatxBasicStats = 0x7ff;
    private const int StatxSize = 256;
    private const int UidOffset = 20;
    private const int GidOffset = 24;

    [DllImport("libc.so.6", SetLastError = true, EntryPoint = "statx")]
    private static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buffer);

    [DllImport("libc.so.6", SetLastError = true, EntryPoint = "chown")]
    private static extern int Chown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    /// <summary>Set once the native calls turn out to be unavailable, so they are not retried.</summary>
    private static volatile bool _unavailable;

    /// <summary>Whether the native calls failed to bind on this platform.</summary>
    public static bool Unavailable => _unavailable;

    /// <summary>The owner and group of <paramref name="path"/>, or null when they cannot be read.</summary>
    public static (uint Uid, uint Gid)? Get(string path)
    {
        if (_unavailable || OperatingSystem.IsWindows()) return null;

        var buffer = new byte[StatxSize];
        try
        {
            if (Statx(AtFdCwd, path, 0, StatxBasicStats, buffer) != 0) return null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _unavailable = true;
            return null;
        }

        return (BitConverter.ToUInt32(buffer, UidOffset), BitConverter.ToUInt32(buffer, GidOffset));
    }

    /// <summary>Passed as an id to <c>chown</c>, leaves that id unchanged.</summary>
    public const uint Unchanged = uint.MaxValue;

    /// <summary>Give <paramref name="path"/> this owner. False when refused or unavailable.</summary>
    public static bool Set(string path, uint uid, uint gid)
    {
        if (_unavailable || OperatingSystem.IsWindows()) return false;

        try
        {
            return Chown(path, uid, gid) == 0;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _unavailable = true;
            return false;
        }
    }
}
