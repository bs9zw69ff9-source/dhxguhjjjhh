using Discord;

namespace PavlovBot.Host.Discord;

/// <summary>One Discord server the bot is in, and an invite to it if one could be made.</summary>
/// <param name="GuildName">Its name at the time of asking.</param>
/// <param name="MemberCount">How many members it has, as the gateway last saw it.</param>
/// <param name="Url">The invite, or null when one could not be created.</param>
/// <param name="Problem">Why there is no invite. Null when there is one.</param>
public sealed record GuildInvite(
    ulong GuildId,
    string GuildName,
    int MemberCount,
    string? Url,
    string? Problem);

/// <summary>
/// Reading the guild list and sending a direct message.
/// </summary>
/// <remarks>
/// A narrow contract over the gateway, for the same reason as <see cref="IAutoPostTarget"/> and
/// <see cref="IChannelRenamer"/>: everything worth testing about the command that uses this - how
/// the list is rendered, what happens when an invite cannot be made - lives behind it and tests
/// without a connection.
/// </remarks>
public interface IGuildDirectory
{
    /// <summary>
    /// Every guild the bot is in, each with a fresh invite where one can be created.
    /// </summary>
    /// <remarks>
    /// A guild the bot cannot make an invite in is still LISTED, with the reason - it is in that
    /// server whether or not it may invite anyone to it, and silently dropping it would understate
    /// where the bot actually is.
    /// </remarks>
    Task<IReadOnlyList<GuildInvite>> InviteToEveryGuildAsync(TimeSpan maxAge, int maxUses, CancellationToken ct);

    /// <summary>Send one embed to a user's DMs. False when it could not be delivered.</summary>
    Task<bool> SendDirectMessageAsync(ulong userId, Embed embed, CancellationToken ct);
}
