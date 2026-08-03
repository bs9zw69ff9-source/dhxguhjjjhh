using Microsoft.Extensions.Logging;

namespace PavlovBot.Host.Observability;

/// <summary>
/// The context every log line inside one operation should carry.
/// </summary>
/// <param name="Operation">The command, component or service tick this is part of.</param>
/// <param name="CorrelationId">
/// Ties every line from one operation together. Eight hex characters - long enough that two
/// concurrent operations will not collide in a log somebody is reading, short enough to be
/// grepped by eye.
/// </param>
/// <param name="GuildId">Discord guild, when the operation came from an interaction.</param>
/// <param name="UserId">
/// The Discord user ID, NOT the username. A username can be changed by the person who owns
/// it, so an audit trail keyed on one is a trail that can be rewritten from the outside.
/// </param>
/// <param name="Server">The Pavlov server this concerns, where the operation has one.</param>
public readonly record struct OperationContext(
    string Operation,
    string CorrelationId,
    ulong? GuildId = null,
    ulong? UserId = null,
    string? Server = null);

/// <summary>
/// Correlation context for logs, attached once per operation rather than per statement.
/// </summary>
/// <remarks>
/// WHY A SCOPE RATHER THAN MORE ARGUMENTS. There are ~211 logging statements in this bot.
/// Threading a correlation id, a guild, a user and a command name through every one of them
/// would be 211 edits, would be wrong in the places somebody forgot, and would make each
/// statement harder to read than the thing it describes. A scope is set once at the entry
/// point and every line logged underneath it - including from services several layers down
/// that know nothing about Discord - carries the context automatically.
///
/// THE ENTRY POINTS ARE THE ONLY PLACES THAT KNOW. A slash command knows its guild and its
/// caller; <c>BanService</c>, three layers below, does not and should not have to. That is
/// the whole argument for ambient context here, and it is also the reason not to overuse it:
/// anything a service genuinely needs should be a parameter, not something fished out of a
/// scope.
///
/// IT ONLY RENDERS IF THE PROVIDER ASKS FOR IT. Scopes are invisible unless
/// <c>IncludeScopes</c> is set on the logging provider - which it was not, so a scope added
/// without that change would be correct, invisible, and indistinguishable from broken. See
/// the console configuration in Program.
/// </remarks>
public static class OperationLog
{
    /// <summary>
    /// A short id for one operation.
    /// </summary>
    /// <remarks>
    /// From a Guid rather than a counter: a counter would have to be shared state, and two
    /// operations starting in the same instant on different threads is the normal case here,
    /// not the exotic one.
    /// </remarks>
    public static string NewCorrelationId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Open a logging scope for one operation. Dispose ends it.
    /// </summary>
    /// <remarks>
    /// Null-valued fields are OMITTED rather than logged as "null". A tick has no guild and
    /// no user, and a line reading <c>guild=null user=null</c> is three quarters noise on
    /// every background log the bot writes.
    /// </remarks>
    public static IDisposable? Begin(this ILogger logger, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var state = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["op"] = context.Operation,
            ["corr"] = context.CorrelationId,
        };

        if (context.GuildId is { } guild) state["guild"] = guild;
        if (context.UserId is { } user) state["user"] = user;
        if (context.Server is { Length: > 0 } server) state["server"] = server;

        return logger.BeginScope(state);
    }

    /// <summary>Open a scope for a Discord interaction: command, guild, user, correlation.</summary>
    public static IDisposable? BeginInteraction(
        this ILogger logger, string operation, ulong? guildId, ulong userId, out string correlationId)
    {
        correlationId = NewCorrelationId();
        return logger.Begin(new OperationContext(operation, correlationId, guildId, userId));
    }

    /// <summary>Open a scope for one run of a background service.</summary>
    public static IDisposable? BeginTick(this ILogger logger, string service) =>
        logger.Begin(new OperationContext(service, NewCorrelationId()));
}
