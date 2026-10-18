using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Host.Discord;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>COMMANDS_DISABLED=whitelist playtime: one subcommand off, the rest of the command kept.</summary>
public class SubcommandFilterTests
{
    private sealed class FakeCommand(string name, params string[] subcommands) : ISlashCommand
    {
        public string Name => name;

        public bool Ephemeral => true;

        public ApplicationCommandProperties Build()
        {
            var builder = new SlashCommandBuilder().WithName(name).WithDescription("d");
            foreach (var sub in subcommands)
            {
                builder.AddOption(new SlashCommandOptionBuilder()
                    .WithName(sub).WithDescription("d").WithType(ApplicationCommandOptionType.SubCommand));
            }
            return builder.Build();
        }

        public Task HandleAsync(SocketSlashCommand command, CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly string[] Whitelist = ["add", "setrank", "remove", "list", "subclasses", "playtime", "wipe"];

    private static IReadOnlyList<ISlashCommand> Apply(out IReadOnlyList<string> unknown, params string[] entries) =>
        SubcommandFilter.Apply(
            [new FakeCommand("whitelist", Whitelist), new FakeCommand("arrest"), new FakeCommand("stats")],
            DisabledCommandList.Parse(entries), out unknown);

    private static IReadOnlyList<string> Registered(ISlashCommand command) =>
        [.. ((SlashCommandProperties)command.Build()).Options.Value.Select(o => o.Name)];

    [Fact]
    public void TwoSubcommandsGoAndTheRestOfTheCommandStays()
    {
        var kept = Apply(out var unknown, "whitelist playtime", "whitelist setrank");

        var whitelist = Assert.Single(kept, c => c.Name == "whitelist");
        Assert.Equal(["add", "remove", "list", "subclasses", "wipe"], Registered(whitelist));
        Assert.Empty(unknown);
    }

    [Fact]
    public void AWholeCommandStillGoesTheOldWay()
    {
        var kept = Apply(out _, "arrest");

        Assert.DoesNotContain(kept, c => c.Name == "arrest");
        Assert.Contains(kept, c => c.Name == "whitelist");
    }

    [Fact]
    public void BothKindsMix()
    {
        var kept = Apply(out var unknown, "stats", "whitelist playtime", "arrest");

        Assert.Equal(["whitelist"], kept.Select(c => c.Name));
        Assert.DoesNotContain("playtime", Registered(kept[0]));
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData("WHITELIST Playtime")]
    [InlineData("whitelist   playtime")]
    public void SpellingAndSpacingDoNotMatter(string entry)
    {
        var kept = Apply(out var unknown, entry);

        Assert.DoesNotContain("playtime", Registered(kept.Single(c => c.Name == "whitelist")));
        Assert.Empty(unknown);
    }

    [Fact]
    public void ACommandWithNothingLeftIsDroppedWhole()
    {
        var kept = Apply(out _, [.. Whitelist.Select(s => $"whitelist {s}")]);

        Assert.DoesNotContain(kept, c => c.Name == "whitelist");
    }

    [Fact]
    public void ATypoIsReportedNotIgnored()
    {
        // The failure is silent otherwise: the subcommand stays, and the setting looks ignored.
        Apply(out var unknown, "whitelist playtim", "whitlist add", "arest");

        Assert.Equal(["arest", "whitelist playtim", "whitlist add"], unknown.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnUntouchedCommandIsNotWrapped()
    {
        var kept = Apply(out _, "whitelist playtime");

        Assert.IsType<FakeCommand>(kept.Single(c => c.Name == "stats"));
    }

    [Fact]
    public void TheWrapperKeepsTheCommandsOwnSettings()
    {
        var whitelist = Apply(out _, "whitelist playtime").Single(c => c.Name == "whitelist");

        Assert.Equal("whitelist", whitelist.Name);
        Assert.True(whitelist.Ephemeral);
    }

    [Fact]
    public void TheRealWhitelistHasTheSubcommandsThisIsAbout()
    {
        /* The .env example tells the police bot to switch off "whitelist playtime" and
           "whitelist setrank". If either is ever renamed, that line becomes a typo the bot
           only warns about - so the names are pinned against the real command. */
        var store = new PavlovBot.Core.Data.SerializedStore(new MemoryBackend(), new PavlovBot.Host.Storage.SystemTextJsonCodec());
        var whitelist = new PavlovBot.Host.Discord.Commands.WhitelistCommand(
            new PavlovBot.Host.Factions.RosterService(null, NullLogger<PavlovBot.Host.Factions.RosterService>.Instance),
            new PavlovBot.Host.Factions.FactionMembers(store),
            new Access(store, [], []),
            new Boards(store, new PavlovBot.Host.Rcon.RconRegistry(new PavlovBot.Host.Configuration.BotOptions
            {
                DiscordToken = "t",
                Servers = [],
                Monitoring = new PavlovBot.Host.Configuration.MonitoringOptions(null, "127.0.0.1", null),
                DataDirectory = Path.GetTempPath(),
            }, new PavlovBot.Host.Observability.MetricsRegistry(), NullLogger<PavlovBot.Host.Rcon.RconRegistry>.Instance)),
            new PavlovBot.Host.Moderation.AuditLog(store),
            NullLogger<PavlovBot.Host.Discord.Commands.WhitelistCommand>.Instance);

        var offered = SubcommandFilter.SubcommandsOf(whitelist);

        Assert.Contains("playtime", offered);
        Assert.Contains("setrank", offered);
        Assert.Contains("add", offered);
        Assert.Contains("remove", offered);
        Assert.Contains("list", offered);
    }
}
