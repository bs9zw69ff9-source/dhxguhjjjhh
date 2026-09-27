using PavlovBot.Host.Servers;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// <c>/deleteserver</c> keeps ModSave (rosters, ledgers) and the player lists; the next
/// <c>/provisionserver</c> into that slot puts them back.
/// </summary>
public sealed class PreservedPlayerDataTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 14, 31, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pavlovbot-preserve-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;

    public PreservedPlayerDataTests()
    {
        _install = Path.Combine(_root, "pavlovserver");
    }

    private string Config => Path.Combine(_install, "Pavlov", "Saved", "Config");
    private string Roster => Path.Combine(Config, "ModSave", "FactionRoles", "ncr_trooper.txt");

    private void SeedLiveInstall()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Roster)!);
        File.WriteAllText(Roster, "PlayerOne\nPlayerTwo\n");
        File.WriteAllText(Path.Combine(Config, "mods.txt"), "fki6\n");
        File.WriteAllText(Path.Combine(Config, "blacklist.txt"), "Cheater\n");
        Directory.CreateDirectory(Path.Combine(Config, "LinuxServer"));
        File.WriteAllText(Path.Combine(Config, "LinuxServer", "Game.ini"), "old settings");
    }

    /// <summary>What /provisionserver leaves: the directories, the three lists touched empty, no ModSave.</summary>
    private void FreshProvision()
    {
        Directory.CreateDirectory(Path.Combine(Config, "LinuxServer"));
        foreach (var file in new[] { "mods.txt", "whitelist.txt", "blacklist.txt" })
            File.WriteAllText(Path.Combine(Config, file), "");
    }

    [Fact]
    public void DeleteThenProvisionKeepsTheRostersAndLists()
    {
        SeedLiveInstall();

        var kept = PreservedPlayerData.Preserve(_install, Now);
        Directory.Delete(_install, recursive: true);   // the rm -rf
        FreshProvision();
        var restored = PreservedPlayerData.Restore(_install);

        Assert.Equal(["ModSave", "mods.txt", "blacklist.txt"], kept.Moved);
        Assert.Equal("PlayerOne\nPlayerTwo\n", File.ReadAllText(Roster));
        Assert.Equal("fki6\n", File.ReadAllText(Path.Combine(Config, "mods.txt")));
        Assert.Equal("Cheater\n", File.ReadAllText(Path.Combine(Config, "blacklist.txt")));
        Assert.Empty(restored.Skipped);

        // Emptied and cleaned up, so the next delete starts clean.
        Assert.False(Directory.Exists(PreservedPlayerData.PreservedDir(_install)));
    }

    [Fact]
    public void GameSettingsAreNotKept()
    {
        // The new slot's Game.ini carries its own ports; the old one must not come back over it.
        SeedLiveInstall();

        PreservedPlayerData.Preserve(_install, Now);

        Assert.False(File.Exists(Path.Combine(PreservedPlayerData.PreservedDir(_install), "LinuxServer", "Game.ini")));
        Assert.True(File.Exists(Path.Combine(Config, "LinuxServer", "Game.ini")));
    }

    [Fact]
    public void ThePreservedFolderIsNotMistakenForAnInstall()
    {
        SeedLiveInstall();
        PreservedPlayerData.Preserve(_install, Now);

        var preserved = PreservedPlayerData.PreservedDir(_install);
        Assert.StartsWith(Path.Combine(_root, "."), preserved, StringComparison.Ordinal);
        Assert.DoesNotContain(preserved, PavlovInstalls.Discover(firstBase: _install));
    }

    [Fact]
    public void RestoreNeverOverwritesRealData()
    {
        SeedLiveInstall();
        PreservedPlayerData.Preserve(_install, Now);

        // A provision that copied another install already has a populated ModSave.
        Directory.CreateDirectory(Path.GetDirectoryName(Roster)!);
        File.WriteAllText(Roster, "Copied\n");
        File.WriteAllText(Path.Combine(Config, "mods.txt"), "");

        var restored = PreservedPlayerData.Restore(_install);

        Assert.Equal("Copied\n", File.ReadAllText(Roster));
        Assert.Contains("mods.txt", restored.Moved);
        Assert.Single(restored.Skipped, s => s.StartsWith("ModSave", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(PreservedPlayerData.PreservedDir(_install), "ModSave")));
    }

    [Fact]
    public void ASecondDeleteDoesNotDestroyTheFirstOnesData()
    {
        SeedLiveInstall();
        PreservedPlayerData.Preserve(_install, Now);

        // Re-provisioned by hand (no restore), then deleted again with different data.
        Directory.CreateDirectory(Path.GetDirectoryName(Roster)!);
        File.WriteAllText(Roster, "Newer\n");
        PreservedPlayerData.Preserve(_install, Now);

        var preserved = PreservedPlayerData.PreservedDir(_install);
        Assert.Equal("Newer\n", File.ReadAllText(Path.Combine(preserved, "ModSave", "FactionRoles", "ncr_trooper.txt")));
        Assert.Equal("PlayerOne\nPlayerTwo\n",
            File.ReadAllText(Path.Combine($"{preserved}-20260927-143100", "ModSave", "FactionRoles", "ncr_trooper.txt")));
    }

    [Fact]
    public void AnInstallWithNothingToKeepLeavesNoFolderBehind()
    {
        Directory.CreateDirectory(Config);

        Assert.Empty(PreservedPlayerData.Preserve(_install, Now).Moved);
        Assert.False(Directory.Exists(PreservedPlayerData.PreservedDir(_install)));
        Assert.Empty(PreservedPlayerData.Restore(_install).Moved);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
