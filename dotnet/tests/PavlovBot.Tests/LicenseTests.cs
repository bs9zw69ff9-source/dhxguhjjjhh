using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Economy;
using PavlovBot.Core.Licensing;
using PavlovBot.Core.Sync;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Economy;
using PavlovBot.Host.Rcon;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The keyed save file, read and edited without losing anything the bot does not own.
/// </summary>
public class ModSaveFileTests
{
    /// <summary>Exactly what the police server's mod writes.</summary>
    internal const string PoliceSave = "Money\n0\nDriversLiscense\nfalse\nFirearmLiscense\nfalse";

    [Fact]
    public void TheKeyedShapeIsRead()
    {
        var save = ModSaveFile.Parse("Money\n1250\nDriversLiscense\ntrue\nFirearmLiscense\nfalse\n")!;

        Assert.False(save.IsBare);
        Assert.Equal(1250, save.Money);
        Assert.True(save.IsTrue(Licenses.Drivers.Key));
        Assert.False(save.IsTrue(Licenses.Firearm.Key));
    }

    [Fact]
    public void TheBareShapeStillReads()
    {
        var save = ModSaveFile.Parse(" 500\n")!;

        Assert.True(save.IsBare);
        Assert.Equal(500, save.Money);
        Assert.Equal("750", save.WithMoney(750)!.ToString());
    }

    [Theory]
    [InlineData(PoliceSave)]
    [InlineData(PoliceSave + "\n")]
    [InlineData("Money\r\n0\r\nDriversLiscense\r\nfalse\r\n")]
    [InlineData("Money\n0\nSomethingElse\n  keep me  \nFirearmLiscense\nfalse\n")]
    public void AnUneditedFileComesBackByteForByte(string content) =>
        Assert.Equal(content, ModSaveFile.Parse(content)!.ToString());

    [Fact]
    public void AnEditChangesOneValueAndNothingElse()
    {
        const string before = "Money\r\n40\r\nDriversLiscense\r\ntrue\r\nUnknownKey\r\nx\r\nFirearmLiscense\r\ntrue\r\n";

        var after = ModSaveFile.Parse(before)!.With("firearmliscense", "false")!.ToString();

        Assert.Equal(before.Replace("FirearmLiscense\r\ntrue", "FirearmLiscense\r\nfalse", StringComparison.Ordinal), after);
    }

    [Fact]
    public void MoneyIsWrittenIntoItsOwnLine() =>
        Assert.Equal("Money\n900\nDriversLiscense\nfalse\nFirearmLiscense\nfalse",
            ModSaveFile.Parse(PoliceSave)!.WithMoney(900)!.ToString());

    [Fact]
    public void AKeyThatIsNotThereIsNeverInvented()
    {
        var save = ModSaveFile.Parse("Money\n0\n")!;

        Assert.Null(save.With(Licenses.Firearm.Key, "false"));
        Assert.Null(ModSaveFile.Parse("DriversLiscense\ntrue\n")!.WithMoney(5));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Money\n0\nDriversLiscense")]   // odd: the game is mid-write
    [InlineData("\n\nMoney\n0")]                 // a blank key
    [InlineData("some config text")]
    public void ANeitherShapeFileIsNotASave(string content) => Assert.Null(ModSaveFile.Parse(content));

    [Fact]
    public void AValueCannotSmuggleInANewLine() =>
        Assert.Throws<ArgumentException>(() => ModSaveFile.Parse(PoliceSave)!.With("Money", "1\nFirearmLiscense"));

    [Fact]
    public void TheSyncGuardsSeeKeyedMoney()
    {
        // A fresh zero-money keyed file must not be copied over one with money in it.
        Assert.True(LedgerFile.LooksLikeLedger(PoliceSave));
        Assert.True(LedgerFile.WouldWipeBalance(PoliceSave, "Money\n500\nDriversLiscense\ntrue\nFirearmLiscense\ntrue"));
        Assert.False(LedgerFile.WouldWipeBalance("Money\n10\nDriversLiscense\nfalse", "Money\n500\n"));
    }

    [Theory]
    [InlineData("drivers", new[] { "DriversLiscense" })]
    [InlineData("firearm", new[] { "FirearmLiscense" })]
    [InlineData("all", new[] { "DriversLiscense", "FirearmLiscense" })]
    [InlineData("ALL", new[] { "DriversLiscense", "FirearmLiscense" })]
    [InlineData("pilot", new string[0])]
    [InlineData(null, new string[0])]
    public void AChoiceNamesItsLicenses(string? choice, string[] keys) =>
        Assert.Equal(keys, Licenses.Named(choice).Select(l => l.Key));
}

/// <summary>
/// Licenses and money, written through the one store that knows when a save may be touched.
/// </summary>
public sealed class LicenseStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "licenses-" + Guid.NewGuid().ToString("N"));

    private sealed record FakeRoster(bool IsTrustworthy, IReadOnlyList<string> Online) : IOnlineRoster;

    public LicenseStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private LedgerFileStore Store(params string[] online) => new(_directory, new FakeRoster(true, online));

    private string PathOf(string player) => Path.Combine(_directory, player + ".txt");

    private void Seed(string player, string content) => File.WriteAllText(PathOf(player), content);

    private string Contents(string player) => File.ReadAllText(PathOf(player));

    [Fact]
    public async Task APayoutNoLongerWipesTheLicenses()
    {
        /* THE BUG UNDER THIS FEATURE. A write replaced the whole file with a bare number, so
           /givemoney on the police server took both licenses, and the mod's layout, with it. */
        Seed("Butter Life", "Money\n100\nDriversLiscense\ntrue\nFirearmLiscense\ntrue\n");

        var change = await new Ledger(Store()).CreditAsync("Butter Life", 50);

        Assert.True(change.Ok);
        Assert.Equal(100, change.Before);
        Assert.Equal("Money\n150\nDriversLiscense\ntrue\nFirearmLiscense\ntrue\n", Contents("Butter Life"));
    }

    [Fact]
    public void AKeyedBalanceIsRead()
    {
        Seed("Alice", "Money\n1250\nDriversLiscense\nfalse\nFirearmLiscense\nfalse");

        Assert.Equal(1250, Store().Read("Alice"));
    }

    [Fact]
    public void AFileInNeitherShapeIsRefusedNotOverwritten()
    {
        // It used to read as zero and be written over with the payout.
        Seed("Alice", "Money\n10\nDriversLiscense");
        var store = Store();

        Assert.False(store.Write("Alice", 60));
        Assert.Contains("shape", store.Refusal("Alice"), StringComparison.Ordinal);
        Assert.Equal("Money\n10\nDriversLiscense", Contents("Alice"));
    }

    [Fact]
    public void TheBareShapeIsStillWrittenAsItWas()
    {
        Seed("Alice", "500");

        Assert.True(Store().Write("Alice", 750));
        Assert.Equal("750", Contents("Alice"));
    }

    [Fact]
    public void OneLicenseGoesAndEverythingElseStays()
    {
        Seed("Alice", "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue\n");

        var result = Store().RemoveLicenses("Alice", [Licenses.Firearm]);

        Assert.Equal(LicenseOutcome.Removed, result.Outcome);
        Assert.Equal([Licenses.Firearm], result.Removed);
        Assert.Equal("Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\nfalse\n", Contents("Alice"));
    }

    [Fact]
    public void BothGoTogether()
    {
        Seed("Alice", "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue");

        var result = Store().RemoveLicenses("Alice", Licenses.All);

        Assert.Equal(Licenses.All, result.Removed);
        Assert.Equal("Money\n40\nDriversLiscense\nfalse\nFirearmLiscense\nfalse", Contents("Alice"));
    }

    [Fact]
    public void OnlyTheHeldOnesAreReported()
    {
        Seed("Alice", "Money\n40\nDriversLiscense\nfalse\nFirearmLiscense\ntrue");

        Assert.Equal([Licenses.Firearm], Store().RemoveLicenses("Alice", Licenses.All).Removed);
    }

    [Fact]
    public void NothingHeldIsNothingWritten()
    {
        Seed("Alice", ModSaveFileTests.PoliceSave);
        var before = File.GetLastWriteTimeUtc(PathOf("Alice"));

        var result = Store().RemoveLicenses("Alice", Licenses.All);

        Assert.Equal(LicenseOutcome.NoneHeld, result.Outcome);
        Assert.Equal(ModSaveFileTests.PoliceSave, Contents("Alice"));
        Assert.Equal(before, File.GetLastWriteTimeUtc(PathOf("Alice")));
    }

    [Fact]
    public void ABareSaveHasNoLicensesToTake()
    {
        Seed("Alice", "500");

        var result = Store().RemoveLicenses("Alice", Licenses.All);

        Assert.Equal(LicenseOutcome.NotInFile, result.Outcome);
        Assert.Equal("500", Contents("Alice"));
    }

    [Fact]
    public void AConnectedPlayerIsRefused()
    {
        /* The game rewrites the whole file from memory on its next save, so a revocation
           written now would come back - and the command would have said it worked. */
        Seed("Alice", "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue");

        var result = Store("alice").RemoveLicenses("Alice", Licenses.All);

        Assert.Equal(LicenseOutcome.Refused, result.Outcome);
        Assert.Contains("in game", result.Reason, StringComparison.Ordinal);
        Assert.Contains("FirearmLiscense\ntrue", Contents("Alice"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownRosterIsRefused()
    {
        Seed("Alice", "Money\n40\nDriversLiscense\ntrue\nFirearmLiscense\ntrue");
        var store = new LedgerFileStore(_directory, new FakeRoster(false, []));

        Assert.Equal(LicenseOutcome.Refused, store.RemoveLicenses("Alice", Licenses.All).Outcome);
        Assert.Contains("DriversLiscense\ntrue", Contents("Alice"), StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveIsNeverCreated()
    {
        var result = Store().RemoveLicenses("Nobody", Licenses.All);

        Assert.Equal(LicenseOutcome.Refused, result.Outcome);
        Assert.False(File.Exists(PathOf("Nobody")));
    }

    [Fact]
    public async Task PayoutsAndRevocationsDoNotUndoEachOther()
    {
        /* Two callers now edit one file. Each must re-read under the lock, or a payout that
           read before a revocation would write the license back. */
        Seed("Alice", "Money\n0\nDriversLiscense\ntrue\nFirearmLiscense\ntrue");
        var store = Store();
        var ledger = new Ledger(store);

        var payouts = Enumerable.Range(0, 50).Select(_ => Task.Run(() => ledger.CreditAsync("Alice", 1)));
        var revoke = Task.Run(() => store.RemoveLicenses("Alice", Licenses.All));
        await Task.WhenAll([.. payouts, revoke]);

        Assert.Equal("Money\n50\nDriversLiscense\nfalse\nFirearmLiscense\nfalse", Contents("Alice"));
    }

    [Fact]
    public void TheCashBoardCountsKeyedSaves()
    {
        Seed("Alice", "Money\n300\nDriversLiscense\nfalse\nFirearmLiscense\nfalse");
        Seed("Bob", "100");

        var store = new PavlovBot.Core.Data.SerializedStore(new MemoryBackend(), new PavlovBot.Host.Storage.SystemTextJsonCodec());
        var boards = new PavlovBot.Host.Discord.Boards(store, new RconRegistry(new PavlovBot.Host.Configuration.BotOptions
        {
            DiscordToken = "t",
            Servers = [],
            Monitoring = new PavlovBot.Host.Configuration.MonitoringOptions(null, "127.0.0.1", null),
            DataDirectory = _directory,
        }, new PavlovBot.Host.Observability.MetricsRegistry(), NullLogger<RconRegistry>.Instance), _directory);

        var cash = boards.ReadCash()!;

        Assert.Equal(400, cash.Total);
        Assert.Equal("Alice", cash.Top[0].Player);
    }
}

/// <summary>The command's shape, and which bot offers it.</summary>
public class LicenseCommandShapeTests
{
    [Fact]
    public void OnlyThePoliceWorldHasLicenses()
    {
        Assert.True(Skins.Police.Licenses);
        Assert.False(Skins.Fallout.Licenses);
    }

    [Fact]
    public void ItIsLicenseRemoveWithAChoiceOfLicense()
    {
        var store = new LedgerFileStore(null);
        var command = new LicenseCommand(store, new Access(
                new PavlovBot.Core.Data.SerializedStore(new MemoryBackend(), new PavlovBot.Host.Storage.SystemTextJsonCodec()), [], []),
            new PavlovBot.Host.Moderation.AuditLog(
                new PavlovBot.Core.Data.SerializedStore(new MemoryBackend(), new PavlovBot.Host.Storage.SystemTextJsonCodec())),
            NullLogger<LicenseCommand>.Instance);

        var built = (SlashCommandProperties)command.Build();
        var remove = Assert.Single(built.Options.Value);

        Assert.Equal("license", built.Name.Value);
        Assert.Equal("remove", remove.Name);
        Assert.Equal(["drivers", "firearm", "all"],
            remove.Options.Single(o => o.Name == "license").Choices.Select(c => c.Value.ToString()));
        Assert.Contains(SubcommandFilter.SubcommandsOf(command), s => s == "remove");
    }

    [Fact]
    public void ItLandsInThePoliceLog() =>
        Assert.True(ChannelStaffLog.IsPoliceAction(LicenseCommand.AuditAction));
}
