using Discord;

namespace PavlovBot.Host.Discord;

/// <param name="Command">Which command is malformed.</param>
/// <param name="Problem">What is wrong with it, in terms somebody can fix.</param>
public sealed record CommandProblem(string Command, string Problem);

/// <summary>
/// Checking command definitions against Discord's rules BEFORE they are sent.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS, and it is worth being blunt because the failure cost nine hours of
/// somebody's evening.
///
/// Registration is a BULK OVERWRITE: one request carrying every command. That is the right
/// call - it is one round trip instead of sixty, and it deletes commands that no longer exist
/// so a rename does not leave its old name in the picker forever. But it is also ATOMIC. One
/// malformed command means Discord rejects the WHOLE payload with a 400, so none of the sixty
/// register, the previously registered set stays exactly as it was, and Discord says nothing
/// a user can see.
///
/// The symptom is indistinguishable from a deploy that never ran: the new binary is up, the
/// build stamp is right, /health is green, and the new commands simply are not there. The one
/// that actually happened was /eventlog declaring subcommands "player" and "staff" twice each
/// - a two-line mistake that took down every command in the bot.
///
/// So the payload is checked first. A command that would be rejected is DROPPED AND NAMED,
/// and the remaining fifty-nine register. Losing one command is a bug; losing all of them
/// silently is an outage.
///
/// NOT A COMPLETE IMPLEMENTATION OF DISCORD'S RULES, and it does not try to be - Discord is
/// the authority and this would rot the moment they change something. It covers the mistakes
/// that are easy to make in a builder and impossible to see in a diff: duplicate names,
/// over-length text, too many options.
/// </remarks>
public static class SlashCommandValidation
{
    /// <summary>Discord's limit on a command or option description.</summary>
    public const int MaxDescription = 100;

    /// <summary>Discord's limit on a command or option name.</summary>
    public const int MaxName = 32;

    /// <summary>Options, subcommands or choices allowed on one parent.</summary>
    public const int MaxOptions = 25;

    /// <summary>Everything wrong with one command, or empty when it is fine.</summary>
    public static IReadOnlyList<CommandProblem> Problems(ApplicationCommandProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (properties is not SlashCommandProperties slash) return [];

        var name = slash.Name.IsSpecified ? slash.Name.Value : "(unnamed)";
        var problems = new List<CommandProblem>();

        void Problem(string text) => problems.Add(new CommandProblem(name, text));

        if (name.Length is 0 or > MaxName) Problem($"the name is {name.Length} characters; the limit is {MaxName}");
        if (name != name.ToLowerInvariant()) Problem("the name must be lowercase");

        if (slash.Description.IsSpecified && slash.Description.Value.Length > MaxDescription)
            Problem($"the description is {slash.Description.Value.Length} characters; the limit is {MaxDescription}");

        if (slash.Options.IsSpecified) Check(slash.Options.Value, name, Problem);

        return problems;
    }

    /// <summary>
    /// One level of options, then every level below it.
    /// </summary>
    /// <remarks>
    /// RECURSIVE, because the collision that caused the outage was between SUBCOMMANDS rather
    /// than top-level commands - checking only the top level would have missed it entirely.
    /// </remarks>
    private static void Check(IReadOnlyCollection<ApplicationCommandOptionProperties> options, string path, Action<string> problem)
    {
        if (options.Count > MaxOptions)
            problem($"{path} has {options.Count} options; the limit is {MaxOptions}");

        /* THE CHECK THAT WOULD HAVE CAUGHT IT. Two options with one name is legal to build,
           invisible in review, and fatal to the entire registration. */
        foreach (var duplicate in options
            .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            problem($"{path} declares \"{duplicate.Key}\" {duplicate.Count()} times - names must be unique among siblings");
        }

        foreach (var option in options)
        {
            var here = $"{path} {option.Name}";

            if (option.Name.Length is 0 or > MaxName)
                problem($"{here}: the name is {option.Name.Length} characters; the limit is {MaxName}");

            if (option.Name != option.Name.ToLowerInvariant())
                problem($"{here}: option names must be lowercase");

            if (option.Description is { Length: > MaxDescription })
                problem($"{here}: the description is {option.Description.Length} characters; the limit is {MaxDescription}");

            if (option.Choices is { Count: > MaxOptions })
                problem($"{here}: {option.Choices.Count} choices; the limit is {MaxOptions}");

            if (option.Options is { Count: > 0 }) Check(option.Options, here, problem);
        }
    }

    /// <summary>
    /// Split a payload into what may be sent and what must not be.
    /// </summary>
    /// <remarks>
    /// PARTITIONED RATHER THAN REFUSED WHOLESALE. Refusing to register anything because one
    /// command is broken reproduces the exact outage this exists to prevent, just with a
    /// better log line.
    /// </remarks>
    public static (IReadOnlyList<ApplicationCommandProperties> Valid, IReadOnlyList<CommandProblem> Rejected) Partition(
        IEnumerable<ApplicationCommandProperties> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var valid = new List<ApplicationCommandProperties>();
        var rejected = new List<CommandProblem>();

        foreach (var candidate in properties)
        {
            var problems = Problems(candidate);
            if (problems.Count == 0) valid.Add(candidate);
            else rejected.AddRange(problems);
        }

        /* DUPLICATE TOP-LEVEL NAMES are checked across the whole payload rather than per
           command, because no single command can see the collision. Two classes both claiming
           "/stats" builds cleanly and fails the same atomic way. */
        foreach (var duplicate in valid
            .OfType<SlashCommandProperties>()
            .Where(s => s.Name.IsSpecified)
            .GroupBy(s => s.Name.Value, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList())
        {
            rejected.Add(new CommandProblem(duplicate.Key,
                $"{duplicate.Count()} commands are named \"{duplicate.Key}\" - two classes are claiming it"));

            // Drop every copy: keeping an arbitrary one registers whichever the container
            // happened to resolve first, which is not a decision anybody made.
            valid.RemoveAll(v => v is SlashCommandProperties s && s.Name.IsSpecified &&
                string.Equals(s.Name.Value, duplicate.Key, StringComparison.OrdinalIgnoreCase));
        }

        return (valid, rejected);
    }
}
