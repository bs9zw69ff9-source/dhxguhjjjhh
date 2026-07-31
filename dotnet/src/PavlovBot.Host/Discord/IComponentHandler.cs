using Discord.WebSocket;

namespace PavlovBot.Host.Discord;

/// <summary>
/// Handles a button press, select-menu choice or modal submission.
/// </summary>
/// <remarks>
/// Dispatched by CUSTOM ID PREFIX, not by exact match, because a component's custom id is
/// the only state Discord carries back. Everything the handler needs about which thing was
/// clicked has to be encoded there - "page:donator:3" - so an exact-match table would need
/// an entry per page number.
///
/// The prefix is therefore a namespace, and <see cref="ComponentId"/> owns the encoding so
/// that a handler never has to parse a raw string itself. Prefixes must not be prefixes of
/// one another; the dispatcher picks the LONGEST match to make an accidental overlap
/// resolve predictably rather than by registration order.
/// </remarks>
public interface IComponentHandler
{
    /// <summary>Custom id prefix this handler owns, without the separator.</summary>
    string Prefix { get; }

    /// <summary>
    /// Handle the interaction. It has NOT been acknowledged yet.
    /// </summary>
    /// <remarks>
    /// Acknowledgement is left to the handler because the right kind depends on what it
    /// does: a page turn edits the existing message (DeferAsync + ModifyOriginal), whereas
    /// opening a modal must respond with the modal and cannot be deferred at all - Discord
    /// rejects a modal on an already-acknowledged interaction.
    /// </remarks>
    Task HandleAsync(SocketInteraction interaction, ComponentId id, CancellationToken ct);
}

/// <summary>
/// A component's custom id: a prefix plus its arguments.
/// </summary>
/// <remarks>
/// Discord caps a custom id at 100 characters and never validates the contents, so this
/// keeps the encoding in one place and refuses to build one that would be silently
/// truncated - a truncated id does not error, it just routes to the wrong handler or to
/// none at all, months later, when a player name happens to be long.
/// </remarks>
public sealed record ComponentId(string Prefix, IReadOnlyList<string> Arguments)
{
    public const char Separator = ':';
    public const int MaxLength = 100;

    /// <summary>Argument at <paramref name="index"/>, or null when absent.</summary>
    public string? Argument(int index) => index >= 0 && index < Arguments.Count ? Arguments[index] : null;

    public int ArgumentAsInt(int index, int fallback = 0) =>
        int.TryParse(Argument(index), out var value) ? value : fallback;

    public static string Encode(string prefix, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(arguments);

        if (prefix.Contains(Separator, StringComparison.Ordinal))
            throw new ArgumentException($"A prefix may not contain '{Separator}'.", nameof(prefix));

        var encoded = arguments.Length == 0
            ? prefix
            : prefix + Separator + string.Join(Separator, arguments);

        /* Truncating would produce an id that still LOOKS valid and routes somewhere wrong.
           Callers encoding user-controlled text must shorten it themselves - deliberately,
           where they know what is safe to lose. */
        if (encoded.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Custom id '{prefix}' is {encoded.Length} characters; Discord's limit is {MaxLength}.",
                nameof(arguments));
        }

        return encoded;
    }

    public static ComponentId Parse(string customId)
    {
        ArgumentNullException.ThrowIfNull(customId);

        var parts = customId.Split(Separator);
        return new ComponentId(parts[0], parts.Skip(1).ToArray());
    }
}
