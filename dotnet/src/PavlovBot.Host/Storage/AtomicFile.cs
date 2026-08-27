namespace PavlovBot.Host.Storage;

/// <summary>
/// Writing a whole file so a reader never sees it half-written.
/// </summary>
/// <remarks>
/// A temp file and a rename, the same shape <see cref="WhitelistFile"/> has always used and for
/// the same reason: a direct write that dies part-way - a crash, a full disk - leaves a
/// TRUNCATED file, and for the files this writes (a whitelist the game reads live, the bot's own
/// <c>.env</c>) a truncation is worse than no write at all. A rename either happens or it does
/// not; there is no in-between state on disk. Extracted so the config files a new server needs
/// and the whitelist share one implementation rather than two copies that can drift.
/// </remarks>
public static class AtomicFile
{
    /// <summary>Write <paramref name="contents"/> to <paramref name="path"/> atomically.</summary>
    public static async Task WriteAsync(string path, string contents, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // A sibling temp file, so the rename is on one filesystem and therefore atomic (a rename
        // across mounts is a copy, which is exactly the non-atomic thing this avoids).
        var temp = $"{path}.bot.tmp";
        await File.WriteAllTextAsync(temp, contents, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
