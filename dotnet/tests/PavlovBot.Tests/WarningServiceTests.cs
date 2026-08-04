using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Warnings, and the one rule that turns three of them into a day's ban.
/// </summary>
/// <remarks>
/// The escalation is a COUNTER THAT BANS PEOPLE, so the tests that matter are the ones about
/// when it must not fire: below the threshold, past it, and against warnings old enough to
/// have decayed.
/// </remarks>
public class WarningServiceTests
{
    private readonly TestClock _time = new();
    private readonly SerializedStore _store = new(new MemoryBackend(), new SystemTextJsonCodec());
    private readonly WarningService _warnings;

    public WarningServiceTests()
    {
        var audit = new AuditLog(_store);
        _warnings = new WarningService(_store, audit, NullLogger<WarningService>.Instance, _time);
    }

    private Task<WarningOutcome> Warn(string reason = "spawn killing") =>
        _warnings.WarnAsync("Alice", reason, "Mod", CancellationToken.None);

    // ---- the threshold ----

    [Fact]
    public async Task TheThirdWarningEscalatesAndTheFirstTwoDoNot()
    {
        Assert.False((await Warn()).Escalated);
        Assert.False((await Warn()).Escalated);

        var third = await Warn();

        Assert.True(third.Escalated);
        Assert.Equal(3, third.Active);
    }

    [Fact]
    public async Task AFourthWarningDoesNotEscalateAgain()
    {
        /* THE ONE THAT WOULD BUILD A PERMANENT BAN A DAY AT A TIME. Firing on `active >=
           threshold` re-bans on every warning past the third, so a player at the threshold is
           banned again every time anybody warns them. It fires on the CROSSING. */
        for (var i = 0; i < 3; i++) await Warn();

        Assert.False((await Warn()).Escalated);
        Assert.False((await Warn()).Escalated);
    }

    [Fact]
    public void TheBanIsOneDay()
    {
        // Pinned because it is the whole policy, and a typo here is a month-long ban.
        Assert.Equal(TimeSpan.FromDays(1), WarningService.EscalationBan);
        Assert.Equal(3, WarningService.EscalationThreshold);
    }

    // ---- decay ----

    [Fact]
    public async Task AWarningPastTheDecayWindowDoesNotCountTowardTheBan()
    {
        /* A player who earned three warnings across two years is not the player who earned
           three in a night, and a counter that only goes up eventually bans everyone who has
           ever played. */
        await Warn("ancient");
        _time.Advance(WarningService.DecayWindow + TimeSpan.FromDays(1));

        await Warn();
        var third = await Warn();

        Assert.False(third.Escalated);
        Assert.Equal(2, third.Active);
    }

    [Fact]
    public async Task ADecayedWarningStaysOnRecord()
    {
        // Decay is about the DECISION, not the evidence. Staff still need to read it.
        await Warn("ancient");
        _time.Advance(WarningService.DecayWindow + TimeSpan.FromDays(1));

        Assert.Single(_warnings.For("Alice"));
        Assert.Empty(_warnings.ActiveFor("Alice"));

        // A fresh warning counts one active and two on record - the decayed one is still there.
        var next = await Warn();
        Assert.Equal(1, next.Active);
        Assert.Equal(2, next.Total);
    }

    [Fact]
    public async Task WarningsJustInsideTheWindowStillCount()
    {
        await Warn();
        await Warn();
        _time.Advance(WarningService.DecayWindow - TimeSpan.FromHours(1));

        Assert.True((await Warn()).Escalated);
    }

    // ---- concurrency ----

    [Fact]
    public async Task TwoModeratorsWarningAtOnceCannotBothSeeTwoActive()
    {
        /* The count is computed from the list AS WRITTEN, inside the same update, so the
           third warning escalates exactly once no matter who issued it or when. */
        await Warn();

        var both = await Task.WhenAll(
            _warnings.WarnAsync("Alice", "a", "ModA"),
            _warnings.WarnAsync("Alice", "b", "ModB"));

        Assert.Equal(3, _warnings.ActiveFor("Alice").Count);
        Assert.Single(both, o => o.Escalated);
    }

    // ---- editing the record ----

    [Fact]
    public async Task OneWarningCanBeRemovedById()
    {
        var first = await Warn("wrong player");
        await Warn();

        Assert.True(await _warnings.RemoveAsync("Alice", first.Warning.Id));
        Assert.Single(_warnings.For("Alice"));
        Assert.DoesNotContain(_warnings.For("Alice"), w => w.Id == first.Warning.Id);
    }

    [Fact]
    public async Task RemovingAWarningDropsThePlayerBelowTheThreshold()
    {
        // The point of removal: an appeal succeeds and the next warning must not re-ban.
        await Warn();
        var mistake = await Warn("wrong player");

        await _warnings.RemoveAsync("Alice", mistake.Warning.Id);

        Assert.False((await Warn()).Escalated);
    }

    [Fact]
    public async Task RemovingAnUnknownIdIsRefusedRatherThanSilentlyIgnored()
    {
        await Warn();
        Assert.False(await _warnings.RemoveAsync("Alice", "nope99"));
        Assert.Single(_warnings.For("Alice"));
    }

    [Fact]
    public async Task ClearingRemovesEverythingAndReportsHowMany()
    {
        await Warn();
        await Warn();

        Assert.Equal(2, await _warnings.ClearAsync("Alice", "Admin"));
        Assert.Empty(_warnings.For("Alice"));
        Assert.Equal(0, await _warnings.ClearAsync("Alice", "Admin"));
    }

    // ---- reporting ----

    [Fact]
    public async Task StandingsRankByActiveWarnings()
    {
        await _warnings.WarnAsync("Alice", "a", "Mod");
        await _warnings.WarnAsync("Bob", "b", "Mod");
        await _warnings.WarnAsync("Bob", "c", "Mod");

        var standings = _warnings.Standings();

        Assert.Equal("Bob", standings[0].Player);
        Assert.Equal(2, standings[0].Active);
        Assert.Equal("Alice", standings[1].Player);
    }

    [Fact]
    public async Task StandingsOmitPlayersWhoseWarningsHaveAllDecayed()
    {
        await Warn();
        _time.Advance(WarningService.DecayWindow + TimeSpan.FromDays(1));

        Assert.Empty(_warnings.Standings());
    }

    [Fact]
    public void WarningsUntilBanCountsDownAndThenStops()
    {
        Assert.Equal(3, WarningService.WarningsUntilBan(0));
        Assert.Equal(1, WarningService.WarningsUntilBan(2));
        Assert.Null(WarningService.WarningsUntilBan(3));
        Assert.Null(WarningService.WarningsUntilBan(9));
    }

    [Fact]
    public async Task AnUnknownPlayerHasACleanRecord()
    {
        Assert.Empty(_warnings.For("Nobody"));
        Assert.Empty(_warnings.ActiveFor("Nobody"));
        Assert.Equal(0, await _warnings.ClearAsync("Nobody", "Admin"));
    }
}
