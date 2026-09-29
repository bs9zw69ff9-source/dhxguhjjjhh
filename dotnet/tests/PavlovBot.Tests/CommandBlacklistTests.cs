using System.Reflection;
using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Core.Security;
using PavlovBot.Host.Configuration;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Observability;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Barring a Discord user from every bot command.
/// </summary>
/// <remarks>
/// THE BUG: <c>/configure</c> wrote the bar list and listed it back, and nothing ever read it.
/// A barred user kept every command their roles allowed, and <c>BLACKLIST_IDS</c> in .env was not
/// read at all. These pin the list's rules and, more to the point, that an interaction from a
/// barred user is actually refused.
/// </remarks>
public sealed class CommandBlacklistTests : IDisposable
{
    private const ulong Barred = 678362059905171471;
    private const ulong Stranger = 555000000000000001;
    private const ulong Owner = 444000000000000001;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-bar-" + Guid.NewGuid().ToString("N"));
    private readonly SerializedStore _store;

    public CommandBlacklistTests()
    {
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
    }

    private CommandBlacklist List(params ulong[] configured) =>
        new(_store, id => id == Owner, configured);

    private List<string> Stored(string key) => _store.Read(key, new List<string>());

    // ---- the list's rules ----

    [Fact]
    public async Task ABarFromConfigureBarsThem()
    {
        var bars = List();

        var result = await bars.BarAsync(Barred);

        Assert.Equal(BarOutcome.Barred, result.Outcome);
        Assert.True(bars.IsBarred(Barred));
        Assert.False(bars.IsBarred(Stranger));

        // The Node bot's format - an ARRAY of id strings - so the data outlives either bot.
        Assert.Equal(["678362059905171471"], Stored(Datasets.UserBlacklist));
    }

    [Fact]
    public void BlacklistIdsInEnvBarThem()
    {
        Assert.True(List(Barred).IsBarred(Barred));
    }

    [Fact]
    public async Task AnOwnerIsNeverBarred()
    {
        /* Every other gate lets an owner through; a bar that could catch one would lock the
           owner out of their own bot. Not even .env or a hand-edited list can do it. */
        await _store.WriteAsync(Datasets.UserBlacklist, new List<string> { Owner.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var bars = List(Owner);

        Assert.False(bars.IsBarred(Owner));
        Assert.Equal(BarOutcome.Owner, (await bars.BarAsync(Owner)).Outcome);
        Assert.Empty(bars.Entries());
    }

    [Fact]
    public async Task UnbarringAnEnvBarRecordsAnOverrideThatSurvivesARestart()
    {
        var bars = List(Barred);

        var result = await bars.UnbarAsync(Barred);

        Assert.Equal(BarOutcome.Unbarred, result.Outcome);
        Assert.False(bars.IsBarred(Barred));

        // A restart re-reads the same .env - the un-bar has to outrank it.
        Assert.False(List(Barred).IsBarred(Barred));
        Assert.Contains("678362059905171471", Stored(Datasets.UserUnbarred));
    }

    [Fact]
    public async Task UnbarringAConfigureBarLeavesNoOverrideBehind()
    {
        /* The override outlives everything. Written for a /configure bar, it would cancel a
           BLACKLIST_IDS bar somebody adds for this person later, with nothing to show why. */
        var bars = List();
        await bars.BarAsync(Barred);

        await bars.UnbarAsync(Barred);

        Assert.False(bars.IsBarred(Barred));
        Assert.Empty(Stored(Datasets.UserBlacklist));
        Assert.Empty(Stored(Datasets.UserUnbarred));
        Assert.True(List(Barred).IsBarred(Barred));
    }

    [Fact]
    public async Task BarringAgainClearsTheOverride()
    {
        // The override outranks the list, so leaving it would report "barred" and bar nobody.
        var bars = List(Barred);
        await bars.UnbarAsync(Barred);

        var result = await bars.BarAsync(Barred);

        Assert.Equal(BarOutcome.Barred, result.Outcome);
        Assert.True(bars.IsBarred(Barred));
        Assert.Empty(Stored(Datasets.UserUnbarred));
    }

    [Fact]
    public async Task ABarFromConfigureOutlivesTheEnvEntry()
    {
        // Barred in both places; tidying .env later must not quietly lift the /configure bar.
        await List(Barred).BarAsync(Barred);

        Assert.True(List().IsBarred(Barred));
    }

    [Fact]
    public async Task UnbarringSomebodyNotBarredChangesNothing()
    {
        var result = await List().UnbarAsync(Stranger);

        Assert.Equal(BarOutcome.NotBarred, result.Outcome);
        Assert.Null(_store.ReadRaw(Datasets.UserUnbarred));
        Assert.Null(_store.ReadRaw(Datasets.UserBlacklist));
    }

    [Fact]
    public async Task BarringTwiceSaysSo()
    {
        var bars = List();
        await bars.BarAsync(Barred);

        Assert.Equal(BarOutcome.AlreadyBarred, (await bars.BarAsync(Barred)).Outcome);
        Assert.Single(Stored(Datasets.UserBlacklist));
    }

    [Fact]
    public async Task TheListShowsBothSourcesOnce()
    {
        var bars = List(Barred, Stranger);
        await bars.BarAsync(Barred);

        var entries = bars.Entries();

        Assert.Equal(2, entries.Count);
        Assert.False(entries.Single(e => e.Id == "678362059905171471").FromEnvironment);
        Assert.True(entries.Single(e => e.Id == "555000000000000001").FromEnvironment);
    }

    // ---- the old flag object under the same key ----

    private Task SeedOldFlagObjectAsync() =>
        _store.WriteAsync(Datasets.UserBlacklist, new IpTrackingService.StoredFlags(["203.0.113.9"], [], [], []));

    [Fact]
    public async Task TheOldFlagObjectBarsNobodyAndIsNotReportedAsUnreadable()
    {
        /* Builds before 2026-07-31 kept address flags under this key. The check runs on every
           interaction, so treating that object as a corrupt list would log an unreadable
           dataset from the hot path and block every later write to it. */
        await SeedOldFlagObjectAsync();

        Assert.False(List().IsBarred(Barred));
        Assert.DoesNotContain(Datasets.UserBlacklist, _store.Unreadable);
    }

    [Fact]
    public async Task TheOldFlagObjectIsReplacedOnceTheFlagsLiveElsewhere()
    {
        await SeedOldFlagObjectAsync();
        await _store.WriteAsync(Datasets.IpFlags, new IpTrackingService.StoredFlags(["203.0.113.9"], [], [], []));

        var result = await List().BarAsync(Barred);

        Assert.Equal(BarOutcome.Barred, result.Outcome);
        Assert.Equal(["678362059905171471"], Stored(Datasets.UserBlacklist));
    }

    [Fact]
    public async Task TheOldFlagObjectIsKeptWhileItIsTheOnlyCopyOfTheFlags()
    {
        // Overwriting it here would unflag every banned address that has not been moved yet.
        await SeedOldFlagObjectAsync();
        var before = _store.ReadRaw(Datasets.UserBlacklist);

        var result = await List().BarAsync(Barred);

        Assert.Equal(BarOutcome.Failed, result.Outcome);
        Assert.Contains("/configure", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, _store.ReadRaw(Datasets.UserBlacklist));
    }

    // ---- the refusal at dispatch ----

    [Fact]
    public async Task ABarredUsersCommandIsRefusedPrivatelyAndNothingRuns()
    {
        var bars = List();
        await bars.BarAsync(Barred);
        var metrics = new MetricsRegistry();
        var interaction = FakeInteraction.For<IDiscordInteraction>(Barred);

        var refused = await DiscordGateway.RefuseIfBarredAsync(
            bars, interaction, "/permban", NullLogger.Instance, metrics);

        Assert.True(refused);
        var reply = Assert.Single(FakeInteraction.Replies(interaction));
        Assert.True(reply.Ephemeral);
        Assert.Contains("Blacklisted", reply.Embed?.Title, StringComparison.Ordinal);
        Assert.Contains("bot_interactions_barred_total 1", metrics.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyoneElseIsLetThroughUntouched()
    {
        var bars = List();
        await bars.BarAsync(Barred);
        var interaction = FakeInteraction.For<IDiscordInteraction>(Stranger);

        var refused = await DiscordGateway.RefuseIfBarredAsync(
            bars, interaction, "/permban", NullLogger.Instance, new MetricsRegistry());

        Assert.False(refused);
        Assert.Empty(FakeInteraction.Replies(interaction));
    }

    [Fact]
    public async Task AnOwnerOnTheListIsLetThrough()
    {
        var interaction = FakeInteraction.For<IDiscordInteraction>(Owner);

        Assert.False(await DiscordGateway.RefuseIfBarredAsync(
            List(Owner), interaction, "/configure", NullLogger.Instance, new MetricsRegistry()));
    }

    [Fact]
    public async Task ABarredUserGetsNoAutocompleteSuggestions()
    {
        var interaction = FakeInteraction.For<IAutocompleteInteraction>(Barred);

        var refused = await DiscordGateway.RefuseIfBarredAsync(
            List(Barred), interaction, "autocomplete", NullLogger.Instance, new MetricsRegistry());

        Assert.True(refused);
        Assert.Equal(0, Assert.Single(FakeInteraction.Suggestions(interaction)));
    }

    [Fact]
    public async Task AFailedReplyStillRefuses()
    {
        // Failing to SAY it was refused is no reason to run the command after all.
        var interaction = FakeInteraction.For<IDiscordInteraction>(Barred, replyFails: true);

        Assert.True(await DiscordGateway.RefuseIfBarredAsync(
            List(Barred), interaction, "/permban", NullLogger.Instance, new MetricsRegistry()));
    }

    // ---- the pieces around it ----

    [Fact]
    public void OwnershipByIdAgreesWithOwnershipByUser()
    {
        var access = new Access(_store, [Owner], [Stranger + 1]);

        foreach (var id in new[] { Owner, Stranger + 1, OwnerGuard.SuperOwnerId, Stranger, Barred })
            Assert.Equal(access.IsOwner(new FakeUser(id)), access.IsOwner(id));
    }

    [Theory]
    [InlineData("678362059905171471 555000000000000001")]
    [InlineData("678362059905171471,555000000000000001")]
    [InlineData(" 678362059905171471 ,\t555000000000000001 ")]
    public void BlacklistIdsReadsTheNodeBotsSeparators(string value)
    {
        // Spaces as well as commas, as the Node bot split it - "1 2" must not be one bad id.
        var features = FeatureOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("BLACKLIST_IDS", value)])
            .Build());

        Assert.Equal([Barred, Stranger], features.BarredUserIds);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// A stand-in for a Discord interaction: a user, and a record of what was sent back.
/// </summary>
/// <remarks>
/// A PROXY rather than a class, because the interaction interfaces run to dozens of members and
/// the refusal touches three. Anything else it is asked for throws, so a refusal that starts
/// doing more than replying fails these tests instead of passing them by accident.
/// </remarks>
public class FakeInteraction : DispatchProxy
{
    public sealed record Reply(bool Ephemeral, Embed? Embed);

    private IUser _user = null!;
    private bool _replyFails;
    private readonly List<Reply> _replies = [];
    private readonly List<int> _suggestions = [];

    public static T For<T>(ulong userId, bool replyFails = false) where T : class, IDiscordInteraction
    {
        var proxy = Create<T, FakeInteraction>();
        var fake = (FakeInteraction)(object)proxy;
        fake._user = new FakeUser(userId);
        fake._replyFails = replyFails;
        return proxy;
    }

    public static IReadOnlyList<Reply> Replies(object interaction) => ((FakeInteraction)interaction)._replies;

    /// <summary>How many suggestions each autocomplete answer carried.</summary>
    public static IReadOnlyList<int> Suggestions(object interaction) => ((FakeInteraction)interaction)._suggestions;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];

        if (targetMethod.Name == "get_User") return _user;

        if (targetMethod.Name == "RespondAsync")
        {
            if (_replyFails) return Task.FromException(new InvalidOperationException("Discord is down"));

            if (args.Length > 0 && args[0] is IEnumerable<AutocompleteResult> results)
                _suggestions.Add(results.Count());
            else
                _replies.Add(new Reply(Ephemeral: args[3] is true, Embed: args[6] as Embed));

            return Task.CompletedTask;
        }

        throw new NotSupportedException($"The refusal should not need {targetMethod.Name}");
    }
}
