using PavlovBot.Host.Observability;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// The last few command failures, retrievable by the id the caller was shown.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. A failed command answers "the error has been logged (`ada985e1`)", and
/// that id could only ever be redeemed from a shell on the box while the log still held it.
/// Three ids were reported in one evening and not one stack trace was recovered, so each
/// failure was guessed at rather than read.
/// </remarks>
public class RecentErrorsTests
{
    private static Exception Thrown(string message)
    {
        // A real throw, so there is a genuine stack trace to keep. `new Exception(...)` has
        // a null one, which would make every assertion here vacuous.
        try { throw new InvalidOperationException(message); }
        catch (InvalidOperationException ex) { return ex; }
    }

    [Fact]
    public void AnErrorIsFoundByTheIdTheCallerWasShown()
    {
        var errors = new RecentErrors();
        errors.Record("ada985e1", "/counts", 42UL, Thrown("boom"));

        var found = errors.Find("ada985e1");

        Assert.NotNull(found);
        Assert.Equal("/counts", found!.Operation);
        Assert.Equal("InvalidOperationException", found.ExceptionType);
        Assert.Equal("boom", found.Message);
        Assert.Contains("RecentErrorsTests", found.Trace, StringComparison.Ordinal);
    }

    /// <summary>The id is read off a screen and typed back in.</summary>
    /// <remarks>
    /// Rejecting it over capitalisation would waste the one chance to look it up before it
    /// ages out, which is the whole window this class provides.
    /// </remarks>
    [Theory]
    [InlineData("ADA985E1")]
    [InlineData("  ada985e1  ")]
    public void TheLookupIsForgivingAboutHowTheIdWasTyped(string typed)
    {
        var errors = new RecentErrors();
        errors.Record("ada985e1", "/counts", 42UL, Thrown("boom"));

        Assert.NotNull(errors.Find(typed));
    }

    [Fact]
    public void AnUnknownIdIsNullRatherThanAnError()
    {
        var errors = new RecentErrors();
        errors.Record("aaaaaaaa", "/counts", 42UL, Thrown("boom"));

        Assert.Null(errors.Find("bbbbbbbb"));
        Assert.Null(errors.Find(null));
        Assert.Null(errors.Find("   "));
    }

    [Fact]
    public void TheNewestAreListedFirst()
    {
        // The one somebody is looking at is the one that just happened.
        var errors = new RecentErrors();
        errors.Record("first", "/a", 1UL, Thrown("1"));
        errors.Record("second", "/b", 1UL, Thrown("2"));

        Assert.Equal("second", errors.All()[0].CorrelationId);
    }

    /// <summary>
    /// The buffer is bounded, and it is the OLDEST that go.
    /// </summary>
    /// <remarks>
    /// Unbounded would be a slow leak holding stack traces forever, and dropping the newest
    /// would defeat the point - the failure being investigated is always the most recent one.
    /// </remarks>
    [Fact]
    public void OldErrorsAgeOutAndRecentOnesSurvive()
    {
        var errors = new RecentErrors();

        for (var i = 0; i < RecentErrors.Capacity + 10; i++)
            errors.Record($"id{i}", "/a", 1UL, Thrown($"{i}"));

        Assert.Equal(RecentErrors.Capacity, errors.All().Count);
        Assert.Null(errors.Find("id0"));
        Assert.NotNull(errors.Find($"id{RecentErrors.Capacity + 9}"));
    }

    /// <summary>Concurrent failures do not corrupt the buffer or breach the cap.</summary>
    /// <remarks>
    /// Commands run in parallel, so several can throw at once - and this is a debugging aid,
    /// which makes it the worst possible place for a race that only shows up when things are
    /// already going wrong.
    /// </remarks>
    [Fact]
    public async Task ConcurrentRecordingStaysWithinTheCap()
    {
        var errors = new RecentErrors();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            Task.Run(() => errors.Record($"id{i}", "/a", 1UL, Thrown($"{i}")))));

        Assert.Equal(RecentErrors.Capacity, errors.All().Count);
    }
}
