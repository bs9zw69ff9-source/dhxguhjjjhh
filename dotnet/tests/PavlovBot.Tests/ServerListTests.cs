using PavlovBot.Core.Servers;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Reading the Pavlov master server's list, for <c>/server lookup</c>.
/// </summary>
/// <remarks>
/// Tested against a REAL CAPTURE of the endpoint rather than against a hand-written sample.
/// The list is Vankrupt's, not ours, and the shape a parser is written against is whatever
/// its author believed on the day - which is exactly the belief that goes stale silently.
/// </remarks>
public class ServerListTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pavlov-servers.json"));

    private static IReadOnlyList<ServerListing> Live() => ServerList.Parse(Fixture());

    [Fact]
    public void TheRealResponseParses()
    {
        var servers = Live();

        Assert.NotEmpty(servers);
        Assert.All(servers, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.Ip));
            Assert.True(s.Port > 0);
            Assert.True(s.MaxPlayers > 0);
            Assert.True(s.Players >= 0);
            Assert.NotNull(s.Updated);
        });
    }

    [Fact]
    public void EveryFieldOnTheWireIsCarriedThrough()
    {
        /* The point of the command is "all related info", so a field silently dropped in
           parsing is a field the card can never show. */
        var server = Live().First(s => s.Ip == "15.235.60.33" && s.Port == 7005);

        Assert.Equal("[24/7] BLOOD AND IRON OFFICIAL", server.Name);
        Assert.Equal(24, server.Players);
        Assert.Equal(24, server.MaxPlayers);
        Assert.Equal("UGC5062697", server.MapId);
        Assert.Equal("BLOOD AND IRON", server.MapLabel);
        Assert.Equal("CUSTOM", server.GameMode);
        Assert.Equal("1.0.27", server.Version);
        Assert.True(server.Secured);
        Assert.False(server.PasswordProtected);
        Assert.True(server.Full);
        Assert.True(server.IsWorkshopMap);
    }

    [Fact]
    public void TheKeyIsAddressAndPort_NotTheName()
    {
        /* Names are server-operator controlled, are not unique, and can exceed Discord's
           100-character option-value limit. Keying the dropdown on a name would collapse two
           servers called "Pavlov Server" into one option that opens the wrong card. */
        var server = Live().First();

        Assert.Equal($"{server.Ip}:{server.Port}", server.Key);
        Assert.True(server.Key.Length <= 100);
        Assert.All(Live(), s => Assert.True(s.Key.Length <= 100));
    }

    [Fact]
    public void MalformedInputIsEmpty_NotAnException()
    {
        // The master server is third-party. A bad response must degrade to an empty
        // browser, never take the command down.
        Assert.Empty(ServerList.Parse(""));
        Assert.Empty(ServerList.Parse("not json at all"));
        Assert.Empty(ServerList.Parse("""{"result":"Success"}"""));
        Assert.Empty(ServerList.Parse("""{"servers":"not an array"}"""));
        Assert.Empty(ServerList.Parse("[]"));
    }

    [Fact]
    public void AnUnreadableRowIsSkippedAndTheRestSurvive()
    {
        /* One odd row must not cost the whole list. A server with no name or no address
           cannot be shown or connected to, so it is the row that goes. */
        var servers = ServerList.Parse("""
            {"servers":[
              {"name":"Good","ip":"1.2.3.4","port":7777,"slots":3,"maxSlots":24},
              {"name":"","ip":"1.2.3.5","port":7777},
              {"ip":"1.2.3.6","port":7777},
              {"name":"No address","port":7777},
              "not even an object"
            ]}
            """);

        Assert.Single(servers);
        Assert.Equal("Good", servers[0].Name);
        Assert.Equal(3, servers[0].Players);
    }

    [Fact]
    public void MissingFieldsFallBackRatherThanThrow()
    {
        var server = ServerList.Parse("""{"servers":[{"name":"Bare","ip":"1.2.3.4"}]}""")[0];

        Assert.Equal(0, server.Port);
        Assert.Equal(0, server.MaxPlayers);
        Assert.Equal("", server.Version);
        Assert.False(server.PasswordProtected);
        Assert.False(server.Secured);
        Assert.Null(server.Updated);
        Assert.False(server.Full);          // 0/0 is not full - it is unknown
    }

    [Fact]
    public void AMapWithNoLabelFallsBackToItsId()
    {
        // The map field must say SOMETHING; a blank one reads as a parsing failure.
        var server = ServerList.Parse("""{"servers":[{"name":"X","ip":"1.2.3.4","mapId":"UGC123"}]}""")[0];

        Assert.Equal("UGC123", server.MapLabel);
    }

    // ---- ordering and filtering ----

    [Fact]
    public void TheBusiestServersComeFirst()
    {
        // Ten times as many empty servers as populated ones; an unsorted first page would
        // be entirely empty servers.
        var ordered = ServerList.Ordered(Live());

        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i - 1].Players >= ordered[i].Players);
    }

    [Fact]
    public void OrderingIsStableByNameWithinAPlayerCount()
    {
        var ordered = ServerList.Ordered([
            new ServerListing("Zulu", "1.1.1.1", 1, 5, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
            new ServerListing("alpha", "1.1.1.2", 1, 5, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
        ]);

        Assert.Equal("alpha", ordered[0].Name);
    }

    [Fact]
    public void TwoRowsSharingAnAddressCollapseToOne()
    {
        /* Discord rejects a whole message when a select carries two options with the same
           value, so a duplicate upstream would present as a browser that shows nothing at
           all - with nothing anywhere saying why. The busier row is the one kept. */
        var ordered = ServerList.Ordered([
            new ServerListing("Quiet copy", "1.1.1.1", 7777, 0, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
            new ServerListing("Busy copy", "1.1.1.1", 7777, 9, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
            new ServerListing("Different port", "1.1.1.1", 7778, 0, 24, "m", "m", "TDM", "TDM", "1", false, true, null),
        ]);

        Assert.Equal(2, ordered.Count);
        Assert.Equal("Busy copy", ordered[0].Name);
        Assert.Equal(ordered.Count, ordered.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void SearchMatchesNameMapModeAndAddress()
    {
        var live = Live();

        Assert.NotEmpty(ServerList.Matching(live, "BLOOD"));
        Assert.NotEmpty(ServerList.Matching(live, "blood"));            // case insensitive
        Assert.NotEmpty(ServerList.Matching(live, "15.235.60.33"));     // by address
        Assert.NotEmpty(ServerList.Matching(live, "CUSTOM"));           // by mode
        Assert.Empty(ServerList.Matching(live, "definitely-not-a-server-name"));
    }

    [Fact]
    public void AnEmptySearchMatchesEverything()
    {
        // Clearing the search box should show the list again, not an empty one.
        var live = Live();

        Assert.Equal(live.Count, ServerList.Matching(live, null).Count);
        Assert.Equal(live.Count, ServerList.Matching(live, "").Count);
        Assert.Equal(live.Count, ServerList.Matching(live, "   ").Count);
    }

    [Fact]
    public void SearchingByMapFindsTheServersRunningIt()
    {
        // "who else is running my map" is one of the two questions this command exists for.
        var found = ServerList.Matching(Live(), "Jailbreak");

        Assert.All(found, s =>
            Assert.True(
                s.Name.Contains("Jailbreak", StringComparison.OrdinalIgnoreCase) ||
                s.MapLabel.Contains("Jailbreak", StringComparison.OrdinalIgnoreCase)));
    }
}
