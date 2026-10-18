using Discord;
using Discord.WebSocket;

namespace PavlovBot.Host.Discord;

/// <summary>
/// <c>COMMANDS_DISABLED</c>, split into whole commands and single subcommands.
/// </summary>
/// <remarks>
/// A SUBCOMMAND IS "command space subcommand": <c>whitelist playtime</c>. One bot can then run
/// <c>/whitelist add</c> and <c>/whitelist remove</c> without <c>/whitelist setrank</c>, which a
/// whole-command list could not express - it was all of /whitelist or none of it.
/// </remarks>
public sealed record DisabledCommandList(
    IReadOnlySet<string> Commands,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Subcommands)
{
    public static DisabledCommandList Parse(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subcommands = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            // Runs of spaces collapse, so "whitelist  playtime" is not a different entry.
            var words = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (words.Length)
            {
                case 1:
                    commands.Add(words[0]);
                    break;
                case 2:
                    if (!subcommands.TryGetValue(words[0], out var set))
                        subcommands[words[0]] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(words[1]);
                    break;
                // Nothing else is a valid entry; the gateway reports it as matching no command.
            }
        }

        return new DisabledCommandList(commands,
            subcommands.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A command with some of its subcommands switched off: not registered, and refused if run.
/// </summary>
/// <remarks>
/// A WRAPPER, applied where the gateway builds its command table, so registration, dispatch and
/// the /help catalogue all see the same filtered command - the same rule as a whole disabled
/// command. Refusing at dispatch as well as dropping the option matters for the same reason it
/// does there: Discord's picker can keep a removed subcommand for a while after a restart, and
/// running it from that stale entry must not reach the handler.
/// </remarks>
internal sealed class SubcommandFilter(ISlashCommand inner, IReadOnlySet<string> disabled) : ISlashCommand
{
    public string Name => inner.Name;

    public bool Ephemeral => inner.Ephemeral;

    public ApplicationCommandProperties Build()
    {
        var built = inner.Build();
        if (built is SlashCommandProperties { Options.IsSpecified: true } slash)
            slash.Options = slash.Options.Value.Where(o => !IsDisabled(o)).ToList();
        return built;
    }

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sub = command.Data.Options.FirstOrDefault(o =>
            o.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup);

        if (sub is not null && disabled.Contains(sub.Name))
        {
            await command.ModifyOriginalResponseAsync(m =>
            {
                m.Embed = Theme.Denied("Not available",
                    $"`/{Name} {sub.Name}` is switched off on this bot.").Build();
                m.AllowedMentions = AllowedMentions.None;
            }).ConfigureAwait(false);
            return;
        }

        await inner.HandleAsync(command, ct).ConfigureAwait(false);
    }

    private bool IsDisabled(ApplicationCommandOptionProperties option) =>
        option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup &&
        disabled.Contains(option.Name);

    /// <summary>The subcommand and group names a command offers, for matching entries against.</summary>
    internal static IReadOnlySet<string> SubcommandsOf(ISlashCommand command) =>
        (command.Build() as SlashCommandProperties)?.Options is { IsSpecified: true } options
            ? options.Value
                .Where(o => o.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup)
                .Select(o => o.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every command that survives the list, filtered where it names subcommands.
    /// </summary>
    /// <param name="unknown">Entries that match no command or no subcommand: typos, said out loud.</param>
    /// <remarks>
    /// A COMMAND WITH EVERY SUBCOMMAND OFF IS DROPPED WHOLE. Registering it would put a
    /// <c>/whitelist</c> in the picker that offers nothing to pick.
    /// </remarks>
    internal static IReadOnlyList<ISlashCommand> Apply(
        IReadOnlyList<ISlashCommand> all, DisabledCommandList list, out IReadOnlyList<string> unknown)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(list);

        var misses = list.Commands
            .Where(name => !all.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var kept = new List<ISlashCommand>();
        foreach (var command in all)
        {
            if (list.Commands.Contains(command.Name)) continue;

            if (!list.Subcommands.TryGetValue(command.Name, out var off))
            {
                kept.Add(command);
                continue;
            }

            var offered = SubcommandsOf(command);
            misses.AddRange(off.Where(s => !offered.Contains(s)).Select(s => $"{command.Name} {s}"));

            if (offered.Count > 0 && offered.All(off.Contains)) continue;
            kept.Add(new SubcommandFilter(command, off));
        }

        misses.AddRange(list.Subcommands.Keys
            .Where(name => !all.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(name => list.Subcommands[name].Select(s => $"{name} {s}")));

        unknown = [.. misses.Distinct(StringComparer.OrdinalIgnoreCase)];
        return kept;
    }
}
