using PavlovBot.Core.Factions;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Factions;
using Xunit;

namespace PavlovBot.Tests;

/// <summary><c>/whitelist list</c> and <c>/whitelist subclasses</c>: what each one shows, and in what order.</summary>
public class WhitelistListTests
{
    private static readonly FactionDefinition Gwpd = FactionRegistry.Default.Get("GWPD")!;

    private static Membership At(string player, string rank) => new(Gwpd, player, rank);

    private static IReadOnlyDictionary<string, string> Held(params (string Player, string Subclass)[] pairs) =>
        pairs.ToDictionary(p => p.Player, p => p.Subclass, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheRosterIsHighestRankFirstWithOneMemberPerLine()
    {
        var low = Gwpd.Order[0];
        var high = Gwpd.Order[^1];
        var roster = new[] { At("Zed", low), At("Amy", low), At("Boss", high) };

        var lines = WhitelistCommand.RosterLines(Gwpd, roster, Held(("Amy", "Detective")));

        Assert.Equal($"**{high}** · 1", lines[0]);
        Assert.Contains("`Boss`", lines[1], StringComparison.Ordinal);
        var lowHeader = lines.ToList().IndexOf($"**{low}** · 2");
        Assert.True(lowHeader > 1);
        Assert.Equal("• `Amy` · *Detective*", lines[lowHeader + 1]);   // alphabetical, sub-class beside the name
        Assert.Equal("• `Zed`", lines[lowHeader + 2]);
    }

    [Fact]
    public void AnEmptyRosterSaysSo()
    {
        Assert.Equal(["*Nobody is on this roster.*"], WhitelistCommand.RosterLines(Gwpd, [], Held()));
    }

    [Fact]
    public void EverySubclassIsListedWithItsHoldersAndTheirRanks()
    {
        var low = Gwpd.Order[0];
        var high = Gwpd.Order[^1];
        var roster = new[] { At("Amy", low), At("Boss", high) };

        var lines = WhitelistCommand.SubclassLines(Gwpd, roster, Held(("Amy", "Detective"), ("Boss", "Detective")));

        var detective = lines.ToList().IndexOf("**Detective** · 2");
        Assert.True(detective >= 0);
        Assert.Equal($"• `Boss` · {high}", lines[detective + 1]);   // senior holder first
        Assert.Equal($"• `Amy` · {low}", lines[detective + 2]);

        // A sub-class nobody holds is still shown, so the list doubles as what exists.
        foreach (var other in Gwpd.Subclasses.Keys.Where(k => k != "Detective"))
            Assert.Contains($"**{other}** · 0", lines);
    }

    [Fact]
    public void AFactionWithoutSubclassesSaysSo()
    {
        var plain = FactionRegistry.Default.All.Values.FirstOrDefault(f => f.Subclasses.Count == 0);
        if (plain is null) return;   // every faction in this set has sub-classes

        Assert.Contains("has no sub-classes", Assert.Single(WhitelistCommand.SubclassLines(plain, [], Held())),
            StringComparison.Ordinal);
    }
}
