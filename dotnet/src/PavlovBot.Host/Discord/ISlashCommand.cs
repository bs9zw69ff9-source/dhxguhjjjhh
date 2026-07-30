using Discord;
using Discord.WebSocket;

namespace PavlovBot.Host.Discord;

/// <summary>
/// One slash command: its shape and its behaviour, together.
/// </summary>
/// <remarks>
/// The Node bot keeps the command DEFINITIONS in one file and the HANDLERS in another,
/// keyed by name. That split is the source of a recurring class of bug - a renamed command
/// that still has a handler under the old key, or an option added to the definition that
/// the handler never reads - and nothing catches it until someone runs the command.
///
/// Here the two live on one type, so a command that is registered necessarily has a
/// handler, and the registrar cannot get them out of step.
/// </remarks>
public interface ISlashCommand
{
    /// <summary>Command name, without the leading slash.</summary>
    string Name { get; }

    /// <summary>The shape Discord registers.</summary>
    ApplicationCommandProperties Build();

    /// <summary>
    /// Run it. The dispatcher has already deferred the reply, so implementations respond
    /// with <c>ModifyOriginalResponseAsync</c> or <c>FollowupAsync</c>.
    /// </summary>
    Task HandleAsync(SocketSlashCommand command, CancellationToken ct);

    /// <summary>
    /// Whether the reply is only visible to the person who ran it. Defaults to false; set
    /// it on anything whose output is a secret (a PIN) or noise in a public channel.
    /// </summary>
    bool Ephemeral => false;
}
