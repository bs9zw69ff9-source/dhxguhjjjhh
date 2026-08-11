using Discord;
using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Data;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Discord.Commands;
using PavlovBot.Host.Events;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Command definitions, checked against Discord's rules before they are sent.
/// </summary>
/// <remarks>
/// THE OUTAGE THIS COMES FROM. Registration is a bulk overwrite and Discord's rejection is
/// ATOMIC: one malformed command means none of the bot's sixty register, the previously
/// registered set stays exactly as it was, and nothing visible says why. The new binary is up,
/// the build stamp is right, /health is green, and the commands are simply absent - identical
/// to a deploy that never ran.
///
/// It happened for real: /eventlog declared the subcommands "player" and "staff" twice each,
/// because three were written by hand and six more were generated from an enum that contained
/// two of the same names. Two lines, invisible in review, and it took every command in the bot
/// off the picker for nine hours.
///
/// <see cref="TheEventLogCommandHasNoDuplicateSubcommands"/> is the regression test. The rest
/// pin the validator, and the validator is what turns this class of mistake from an outage
/// into one missing command and a loud log line.
/// </remarks>
public class SlashCommandValidationTests
{
    private static SlashCommandOptionBuilder Sub(string name) =>
        new SlashCommandOptionBuilder()
            .WithName(name).WithDescription("a subcommand")
            .WithType(ApplicationCommandOptionType.SubCommand);

    private static ApplicationCommandProperties Command(string name, params SlashCommandOptionBuilder[] options)
    {
        var builder = new SlashCommandBuilder().WithName(name).WithDescription("a command");
        foreach (var option in options) builder.AddOption(option);
        return builder.Build();
    }

    // ---- the regression ----

    /// <summary>
    /// The exact bug: /eventlog must not declare a subcommand name twice.
    /// </summary>
    /// <remarks>
    /// Built for real rather than reconstructed, so it stays true as the command changes. It
    /// generates one subcommand per EventCategory alongside its hand-written ones, and adding
    /// a category called "recent" would break it again - this is what would catch that.
    /// </remarks>
    [Fact]
    public void TheEventLogCommandHasNoDuplicateSubcommands()
    {
        var store = new SerializedStore(new MemoryBackend(), new SystemTextJsonCodec());
        var command = new EventLogCommand(new NullEventStore(), new Access(store, [], []));

        var problems = SlashCommandValidation.Problems(command.Build());

        Assert.Empty(problems);
    }

    // ---- the validator ----

    /// <summary>
    /// TWO SUBCOMMANDS WITH ONE NAME. Legal to build, fatal to register.
    /// </summary>
    [Fact]
    public void DuplicateSubcommandNamesAreRejected()
    {
        var problems = SlashCommandValidation.Problems(Command("thing", Sub("player"), Sub("staff"), Sub("player")));

        var problem = Assert.Single(problems);
        Assert.Contains("player", problem.Problem, StringComparison.Ordinal);
        Assert.Contains("unique", problem.Problem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A collision nested inside a subcommand is caught too.
    /// </summary>
    /// <remarks>
    /// The check has to recurse. A top-level-only version would have missed the real bug
    /// entirely, since that collision was between subcommands rather than commands.
    /// </remarks>
    [Fact]
    public void DuplicatesNestedInsideASubcommandAreCaught()
    {
        var nested = Sub("group")
            .AddOption(new SlashCommandOptionBuilder().WithName("who").WithDescription("x")
                .WithType(ApplicationCommandOptionType.String))
            .AddOption(new SlashCommandOptionBuilder().WithName("who").WithDescription("y")
                .WithType(ApplicationCommandOptionType.String));

        Assert.NotEmpty(SlashCommandValidation.Problems(Command("thing", nested)));
    }

    /// <summary>
    /// WHAT Discord.Net ALREADY CATCHES, and why that is not enough.
    /// </summary>
    /// <remarks>
    /// The builder throws on an over-long description and on an uppercase name, at BUILD time.
    /// Those failures are loud and land during startup, which is fine - nobody ships them.
    ///
    /// It does NOT check that sibling names are unique. That one builds cleanly, passes every
    /// local check, and is rejected only by Discord, atomically, taking every other command
    /// with it. That gap is the entire reason this validator exists, and this test records
    /// the division of labour so nobody later deletes the validator as redundant.
    /// </remarks>
    [Fact]
    public void TheBuilderCatchesLengthAndCaseButNotDuplicates()
    {
        Assert.Throws<ArgumentException>(() =>
            new SlashCommandBuilder().WithName("thing").WithDescription(new string('x', 101)).Build());

        // Thrown at Build(), not at WithName - the option builder itself is permissive and
        // the check lives in ApplicationCommandOptionProperties. Worth pinning: it means a
        // bad name surfaces when the command is assembled, which is still startup.
        Assert.Throws<FormatException>(() => Command("thing", Sub("Player")));

        // Duplicates build perfectly happily. This is the one that reaches Discord.
        var duplicated = Command("thing", Sub("player"), Sub("player"));
        Assert.NotEmpty(SlashCommandValidation.Problems(duplicated));
    }

    [Fact]
    public void AWellFormedCommandHasNoProblems()
    {
        Assert.Empty(SlashCommandValidation.Problems(Command("thing", Sub("one"), Sub("two"))));
    }

    // ---- partitioning ----

    /// <summary>
    /// ONE BAD COMMAND COSTS ONE COMMAND, not all of them.
    /// </summary>
    /// <remarks>
    /// The whole point. Refusing to register anything because one command is broken would
    /// reproduce the outage exactly, just with a better log line.
    /// </remarks>
    [Fact]
    public void ABadCommandIsDroppedAndTheRestSurvive()
    {
        var (valid, rejected) = SlashCommandValidation.Partition(
        [
            Command("good"),
            Command("bad", Sub("dupe"), Sub("dupe")),
            Command("alsogood"),
        ]);

        Assert.Equal(2, valid.Count);
        Assert.Single(rejected);
        Assert.Equal("bad", rejected[0].Command);
    }

    /// <summary>
    /// Two commands claiming one name are BOTH dropped.
    /// </summary>
    /// <remarks>
    /// No single command can see this collision, so it is checked across the payload. Both
    /// copies go rather than an arbitrary one being kept: keeping whichever the container
    /// happened to resolve first is not a decision anybody made, and it would differ between
    /// runs.
    /// </remarks>
    [Fact]
    public void TwoCommandsWithOneNameAreBothDropped()
    {
        var (valid, rejected) = SlashCommandValidation.Partition(
        [
            Command("stats"),
            Command("stats"),
            Command("other"),
        ]);

        Assert.Single(valid);
        Assert.Equal("other", Assert.IsType<SlashCommandProperties>(valid[0]).Name.Value);
        Assert.Contains(rejected, r => r.Command == "stats");
    }

    [Fact]
    public void AnEntirelyValidPayloadPassesThroughUntouched()
    {
        var (valid, rejected) = SlashCommandValidation.Partition([Command("a"), Command("b"), Command("c")]);

        Assert.Equal(3, valid.Count);
        Assert.Empty(rejected);
    }
}
