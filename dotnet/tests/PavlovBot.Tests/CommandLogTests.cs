using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>COMMAND_LOG_CHANNEL: what each slash command attempt becomes.</summary>
public class CommandLogTests
{
    private sealed record Option(
        string Name,
        object? Value,
        ApplicationCommandOptionType Type,
        IReadOnlyCollection<IApplicationCommandInteractionDataOption> Options) : IApplicationCommandInteractionDataOption
    {
        public static Option Leaf(string name, object? value, ApplicationCommandOptionType type = ApplicationCommandOptionType.String) =>
            new(name, value, type, []);

        public static Option Sub(string name, params IApplicationCommandInteractionDataOption[] children) =>
            new(name, null, ApplicationCommandOptionType.SubCommand, children);

        public static Option Group(string name, params IApplicationCommandInteractionDataOption[] children) =>
            new(name, null, ApplicationCommandOptionType.SubCommandGroup, children);
    }

    private sealed class FakeTarget : IAutoPostTarget
    {
        public List<(ulong Channel, Embed Embed)> Sent { get; } = [];
        public bool Throw { get; set; }

        public Task<AutoPostEdit> EditAsync(ulong channelId, ulong messageId, Embed embed, MessageComponent? components, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ulong?> SendAsync(ulong channelId, Embed embed, MessageComponent? components, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException("channel gone");
            Sent.Add((channelId, embed));
            return Task.FromResult<ulong?>(1);
        }
    }

    private static CommandInvocation Purge(string line = "/purge count:6", ulong? channel = 42, string? guild = "Nuclear Roleplay") =>
        new(7, "fki6", "https://cdn.example/a.png", "purge", line, channel, guild, DateTimeOffset.UnixEpoch);

    [Fact]
    public void LeafOptionsAreKeyColonValue()
    {
        Assert.Equal("/purge count:6",
            CommandLog.Describe("purge", [Option.Leaf("count", 6L, ApplicationCommandOptionType.Integer)]));
    }

    [Fact]
    public void NoOptionsIsJustTheName() => Assert.Equal("/health", CommandLog.Describe("health", null));

    [Fact]
    public void SubCommandsAndGroupsAreWords()
    {
        var options = new[]
        {
            Option.Group("faction",
                Option.Sub("set",
                    Option.Leaf("player", "Bob"),
                    Option.Leaf("rank", "Sergeant"))),
        };

        Assert.Equal("/config faction set player:Bob rank:Sergeant", CommandLog.Describe("config", options));
    }

    [Fact]
    public void NumbersUseTheInvariantCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Equal("/pay amount:1.5",
                CommandLog.Describe("pay", [Option.Leaf("amount", 1.5d, ApplicationCommandOptionType.Number)]));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EmbedNamesTheCallerTheCommandAndTheChannel()
    {
        var embed = CommandLog.Build(Purge());

        Assert.Equal("fki6", embed.Author?.Name);
        Assert.Contains("<@7> used `/purge` command in <#42>", embed.Description, StringComparison.Ordinal);
        Assert.Contains("/purge count:6", embed.Description, StringComparison.Ordinal);
        Assert.Contains("Nuclear Roleplay", embed.Footer?.Text, StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.UnixEpoch, embed.Timestamp);
    }

    [Fact]
    public void TypedMarkdownCannotFormatTheLog()
    {
        var embed = CommandLog.Build(Purge("/say text:**everyone** `x`"));

        Assert.DoesNotContain("**everyone**", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void UnicodeIsKeptAsTyped()
    {
        var embed = CommandLog.Build(Purge("/say text:caf\u00e9 \u2622", guild: "Nuclear Roleplay\u2122"));

        Assert.Contains("caf\u00e9 \u2622", embed.Description, StringComparison.Ordinal);
        Assert.Contains("Nuclear Roleplay\u2122", embed.Footer?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AWallOfTextIsCapped()
    {
        var embed = CommandLog.Build(Purge("/say text:" + new string('a', 5000)));

        Assert.True(embed.Description.Length < CommandLog.MaxLineLength + 200);
        Assert.EndsWith("…", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostsToTheConfiguredChannel()
    {
        var target = new FakeTarget();
        var log = new CommandLog(target, 1553558500309729330, NullLogger<CommandLog>.Instance);

        await log.PostAsync(Purge());

        var (channel, _) = Assert.Single(target.Sent);
        Assert.Equal(1553558500309729330UL, channel);
    }

    [Fact]
    public async Task ADeliveryFailureNeverReachesTheCommand()
    {
        var log = new CommandLog(new FakeTarget { Throw = true }, 1, NullLogger<CommandLog>.Instance);

        await log.PostAsync(Purge());   // must not throw
    }
}
