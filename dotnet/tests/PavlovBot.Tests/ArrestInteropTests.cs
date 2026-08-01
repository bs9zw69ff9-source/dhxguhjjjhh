using PavlovBot.Core.Data;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Arrest records, shared with the Node bot.
/// </summary>
/// <remarks>
/// The two bots wrote the same dataset with different field names for everything except
/// bail and at - and Node's `charges` is an array of OBJECTS where ours expected strings,
/// so reading its data threw, the store fell back to empty, and the most-wanted board saw
/// no arrests at all.
/// </remarks>
public class ArrestInteropTests
{
    private static readonly SystemTextJsonCodec Codec = new();

    private const string NodeShape = """
    {
      "playerId": "Evader",
      "charges": [
        { "code": "PC 100", "name": "Disorderly Conduct", "cls": "Misdemeanor", "min": 2, "bail": 25 },
        { "code": "PC 210", "name": "Assault", "cls": "Felony", "min": 15, "bail": 500 }
      ],
      "minutes": 17,
      "bail": 525,
      "by": "Dana",
      "at": 1785000000000
    }
    """;

    [Fact]
    public void TheNodeShapeIsRead()
    {
        Assert.True(Codec.TryDeserialize<Arrest>(NodeShape, out var arrest));

        Assert.NotNull(arrest);
        Assert.Equal("Evader", arrest!.Player);
        Assert.Equal(17, arrest.JailMinutes);
        Assert.Equal(525, arrest.Bail);
        Assert.Equal("Dana", arrest.ArrestedBy);
        Assert.Equal(["PC 100", "PC 210"], arrest.Codes);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1785000000000), arrest.At);
    }

    [Fact]
    public void WeWriteTheNodeShape()
    {
        /* The rollback path. Node's board reads a.minutes and a.playerId; writing our own
           field names would leave it ranking every player at zero jail time. */
        var json = Codec.Serialize(new Arrest("Evader", ["PC 100"], 17, 525, "Dana",
            DateTimeOffset.FromUnixTimeMilliseconds(1785000000000)));

        // Parsed, not substring-matched: the codec writes indented JSON, and a test that
        // depends on the whitespace would break on a formatting change that broke nothing.
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("Evader", root.GetProperty("playerId").GetString());
        Assert.Equal(17, root.GetProperty("minutes").GetInt32());
        Assert.Equal(525, root.GetProperty("bail").GetInt32());
        Assert.Equal("Dana", root.GetProperty("by").GetString());
        Assert.Equal(1785000000000, root.GetProperty("at").GetInt64());
        Assert.Equal("PC 100", root.GetProperty("charges")[0].GetProperty("code").GetString());

        // Our own names must NOT appear, or a rolled-back Node bot reads neither.
        Assert.False(root.TryGetProperty("jailMinutes", out _));
        Assert.False(root.TryGetProperty("arrestedBy", out _));
        Assert.False(root.TryGetProperty("codes", out _));
    }

    [Fact]
    public void ItRoundTripsThroughItsOwnFormat()
    {
        var original = new Arrest("Evader", ["PC 100", "PC 210"], 17, 525, "Dana",
            DateTimeOffset.FromUnixTimeMilliseconds(1785000000000));

        Assert.True(Codec.TryDeserialize<Arrest>(Codec.Serialize(original), out var again));

        /* Field by field, not record equality: a positional record compares its
           IReadOnlyList by REFERENCE, so two records with identical codes are unequal and
           the assertion would fail while the data was perfectly fine. */
        Assert.NotNull(again);
        Assert.Equal(original.Player, again!.Player);
        Assert.Equal(original.JailMinutes, again.JailMinutes);
        Assert.Equal(original.Bail, again.Bail);
        Assert.Equal(original.ArrestedBy, again.ArrestedBy);
        Assert.Equal(original.At, again.At);
        Assert.Equal(original.Codes, again.Codes);
    }

    [Fact]
    public void AListOfNodeArrestsSurvives()
    {
        // The real read path: a map of player -> arrests, which is what the board consumes.
        var json = $$"""{ "evader": [ {{NodeShape}} ] }""";

        Assert.True(Codec.TryDeserialize<Dictionary<string, List<Arrest>>>(json, out var all));

        Assert.NotNull(all);
        Assert.Single(all!["evader"]);
        Assert.Equal(17, all["evader"][0].JailMinutes);
    }

    [Fact]
    public void AMalformedEntryDoesNotTakeTheRegistryWithIt()
    {
        // A record with nothing recognisable reads as an empty arrest rather than throwing
        // and emptying the whole most-wanted board.
        Assert.True(Codec.TryDeserialize<Arrest>("""{ "nonsense": true }""", out var arrest));

        Assert.NotNull(arrest);
        Assert.Equal("", arrest!.Player);
        Assert.Equal(0, arrest.JailMinutes);
    }
}
