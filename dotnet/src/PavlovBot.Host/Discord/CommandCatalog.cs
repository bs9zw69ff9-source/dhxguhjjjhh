namespace PavlovBot.Host.Discord;

/// <param name="Description">The one-line description shown in Discord's picker and in /help.</param>
public sealed record CommandSummary(string Name, string Description);

/// <summary>
/// What commands exist, for anything that needs to describe them.
/// </summary>
/// <remarks>
/// This type exists to break a dependency cycle rather than because the data needed a home:
/// <c>/help</c> lists every command, so injecting the command collection into it makes it
/// depend on itself, and the container refuses to build.
///
/// A service locator would also have broken the cycle - resolve the collection at handle
/// time instead of construction time - but it would hide the dependency instead of removing
/// it. Populating a catalog once, at registration, keeps the graph acyclic AND honest.
/// </remarks>
public sealed class CommandCatalog
{
    private IReadOnlyList<CommandSummary> _commands = [];

    /// <summary>Called once by the gateway, which holds every command already.</summary>
    public void Populate(IEnumerable<CommandSummary> commands) =>
        _commands = commands.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<CommandSummary> Commands => _commands;
}
