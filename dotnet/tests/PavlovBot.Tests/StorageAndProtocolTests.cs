using PavlovBot.Core.Data;
using PavlovBot.Host.Storage;
using PavlovBot.Rcon.Protocol;
using Xunit;

namespace PavlovBot.Tests;

public class FileKeyValueBackendTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pavlovbot-tests-" + Guid.NewGuid().ToString("N"));

    private FileKeyValueBackend Backend() => new(_directory);

    [Fact]
    public void ARoundTripPreservesTheValue()
    {
        var backend = Backend();
        backend.Write("bans", "{\"a\":1}");
        Assert.Equal("{\"a\":1}", backend.Read("bans"));
    }

    [Fact]
    public void AnAbsentDatasetReadsAsNull_NotAnException()
    {
        Assert.Null(Backend().Read("never-written"));
    }

    [Fact]
    public void AWriteOverAnExistingDatasetLeavesNoTempFileBehind()
    {
        /* The write is temp-then-replace so a crash mid-write cannot leave half a ban list
           in place. The leftover temp file would be harmless but it would also mean the
           replace never happened. */
        var backend = Backend();
        backend.Write("bans", "first");
        backend.Write("bans", "second");

        Assert.Equal("second", backend.Read("bans"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/path")]
    [InlineData("..")]
    public void KeysThatCouldEscapeTheDataDirectoryAreRefused(string key)
    {
        /* Rejected rather than sanitised: a silently-renamed dataset reads back as empty,
           which is indistinguishable from data loss. */
        Assert.Throws<ArgumentException>(() => Backend().Write(key, "x"));
    }

    [Fact]
    public async Task TheStoreSerialisesConcurrentUpdatesToOneDataset()
    {
        /* Every mutation the bot makes is read-modify-write. Run two concurrently against
           one dataset and one silently overwrites the other - a ban that "didn't take",
           with no error anywhere. */
        var store = new SerializedStore(Backend(), new SystemTextJsonCodec());

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            store.UpdateAsync<List<int>>("counter", [], current => [.. current, 1])));

        Assert.Equal(50, store.Read<List<int>>("counter", []).Count);
    }

    [Fact]
    public async Task ACorruptDatasetReadsAsTheFallbackRatherThanThrowing()
    {
        // One bad character must not take down every command that touches bans.
        var backend = Backend();
        backend.Write("bans", "{ this is not json");

        var store = new SerializedStore(backend, new SystemTextJsonCodec());
        Assert.Empty(store.Read<List<string>>("bans", []));

        // And an update over the corrupt file still writes a good one.
        var result = await store.UpdateAsync<List<string>>("bans", [], current => [.. current, "player"]);
        Assert.True(result.Ok);
        Assert.Equal(["player"], store.Read<List<string>>("bans", []));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

/// <summary>
/// Pavlov's replies are inconsistent about property names and are not always JSON. Every
/// case here was observed against a real server.
/// </summary>
public class RconProtocolTests
{
    private static System.Text.Json.JsonElement Root(string json)
    {
        Assert.True(RconReply.TryParse(json, out var document));
        return document!.RootElement.Clone();
    }

    [Fact]
    public void ANonJsonReplyIsRejectedRatherThanThrowing()
    {
        // Failures come back as bare text. A parser that throws turns a transient server
        // hiccup into a stack trace.
        Assert.False(RconReply.TryParse("Password: ", out _));
        Assert.False(RconReply.TryParse("", out _));
        Assert.False(RconReply.TryParse(null, out _));
    }

    [Fact]
    public void PlayerNamesAreReadUnderEverySpellingPavlovUses()
    {
        var root = Root("""
            {"PlayerList":[
              {"Username":"Alice","UniqueId":"1"},
              {"username":"Bob","uniqueId":"2"},
              {"PlayerName":"Carol","UniqueID":"3"},
              {"Name":"Dave","Id":"4"}
            ],"Successful":true}
            """);

        var players = RefreshList.Players(root);
        Assert.Equal(["Alice", "Bob", "Carol", "Dave"], players.Select(p => p.Name));
        Assert.Equal(["1", "2", "3", "4"], players.Select(p => p.UniqueId));
    }

    [Fact]
    public void AnEntryWithNeitherNameNorIdIsDropped()
    {
        var root = Root("""{"PlayerList":[{"Username":"  "},{"Username":"Real"}],"Successful":true}""");
        Assert.Equal(["Real"], RefreshList.Names(root));
    }

    [Fact]
    public void AMissingOrMisshapenPlayerListIsEmpty_NotAnException()
    {
        Assert.Empty(RefreshList.Players(Root("""{"Successful":true}""")));
        Assert.Empty(RefreshList.Players(Root("""{"PlayerList":"nope"}""")));
    }

    [Fact]
    public void SuccessfulIsTriState()
    {
        /* "Did not say" is not "said no". Collapsing the two is the single most common
           source of bugs in this codebase - it is what makes a hiccup look like an empty
           server. */
        Assert.True(RconReply.Successful(Root("""{"Successful":true}""")));
        Assert.False(RconReply.Successful(Root("""{"Successful":false}""")));
        Assert.Null(RconReply.Successful(Root("""{"PlayerList":[]}""")));
    }

    [Fact]
    public void ServerInfoIsReadFromItsNestedKey()
    {
        /* Pavlov nests the fields under `ServerInfo`. Reading them top-level is why the
           Node bot showed *Unknown* for map, mode and capacity for months: the reply
           parsed fine, it just had nothing at the level being read. */
        var info = ServerInfoReply.Read(Root("""
            {"ServerInfo":{"ServerName":"NYC RP","MapLabel":"Datacenter","GameMode":"SND","MaxPlayers":"32"},"Successful":true}
            """));

        Assert.Equal("NYC RP", info.ServerName);
        Assert.Equal("Datacenter", info.MapLabel);
        Assert.Equal("SND", info.GameMode);
        Assert.Equal(32, info.MaxPlayers);   // delivered as a string, read as a number
    }

    [Fact]
    public void AFlatServerInfoIsAlsoAccepted()
    {
        var info = ServerInfoReply.Read(Root("""{"ServerName":"Flat","MaxPlayers":24}"""));
        Assert.Equal("Flat", info.ServerName);
        Assert.Equal(24, info.MaxPlayers);
    }

    [Fact]
    public void AbsentFieldsAreNull_NeverInvented()
    {
        /* MaxPlayers has no sensible default at this layer. The Node bot defaulted it to
           24 in the parser, which meant a 40-slot server rendered "30/24 players" and
           nobody could tell where the 24 came from. */
        var info = ServerInfoReply.Read(Root("""{"ServerInfo":{}}"""));
        Assert.Null(info.ServerName);
        Assert.Null(info.MapLabel);
        Assert.Null(info.MaxPlayers);
    }
}
