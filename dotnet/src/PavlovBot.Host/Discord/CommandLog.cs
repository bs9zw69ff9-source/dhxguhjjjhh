using System.Globalization;
using System.Text;
using Discord;
using Microsoft.Extensions.Logging;
using PavlovBot.Core.Text;

namespace PavlovBot.Host.Discord;

/// <summary>One slash command as it was typed, captured the moment it arrived.</summary>
/// <param name="UserId">The caller.</param>
/// <param name="UserName">The caller's account name, for the author line.</param>
/// <param name="AvatarUrl">The caller's avatar, or null for the default.</param>
/// <param name="Command">The top-level command name, without the slash.</param>
/// <param name="Line">The full invocation, e.g. <c>/purge count:6</c>.</param>
/// <param name="ChannelId">Where it was run, or null outside a channel.</param>
/// <param name="GuildName">The server it was run in, or null for a DM.</param>
/// <param name="At">When it arrived.</param>
public sealed record CommandInvocation(
    ulong UserId,
    string UserName,
    string? AvatarUrl,
    string Command,
    string Line,
    ulong? ChannelId,
    string? GuildName,
    DateTimeOffset At);

/// <summary>
/// Every slash command anyone runs, posted to <c>COMMAND_LOG_CHANNEL</c>.
/// </summary>
/// <remarks>
/// NOT THE STAFF LOG. That one records what an action DID (who was banned, for what) and
/// only once it succeeded. This records what was TYPED, by whom and where, and it records it
/// on arrival - before the bar list, the permission checks or the handler have had a say -
/// so a refused, failed or timed-out command is logged exactly like one that worked. The
/// attempt is the thing worth seeing.
///
/// Posting is a REST call, so the gateway hands it to the dispatcher instead of awaiting it
/// in front of the three-second acknowledgement.
/// </remarks>
public sealed class CommandLog(IAutoPostTarget target, ulong channelId, ILogger<CommandLog> logger)
{
    /// <summary>
    /// Long enough for any real invocation; short enough that a pasted wall of text in a
    /// string option cannot push the embed past Discord's limits.
    /// </summary>
    internal const int MaxLineLength = 1000;

    private const string Hammer = "🔨";

    public async Task PostAsync(CommandInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        try
        {
            await target.SendAsync(channelId, Build(invocation), components: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never rethrown: a log line that could not be delivered must not touch the command.
            logger.LogWarning(ex, "Could not post /{Command} to the command log channel {Channel}",
                invocation.Command, channelId);
        }
    }

    /// <summary>
    /// The invocation as the user typed it: <c>/name sub key:value key:value</c>.
    /// </summary>
    /// <remarks>
    /// Sub-command groups and sub-commands are WORDS, the way the picker shows them; only
    /// leaf options are <c>key:value</c>. Users, roles and channels render by name rather
    /// than as mentions, because this line goes into plain text where a mention would ping.
    /// </remarks>
    internal static string Describe(string name, IEnumerable<IApplicationCommandInteractionDataOption>? options)
    {
        var line = new StringBuilder("/").Append(name);
        Append(line, options);
        return line.ToString();
    }

    private static void Append(StringBuilder line, IEnumerable<IApplicationCommandInteractionDataOption>? options)
    {
        if (options is null) return;

        foreach (var option in options)
        {
            if (option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup)
            {
                line.Append(' ').Append(option.Name);
                Append(line, option.Options);
                continue;
            }

            line.Append(' ').Append(option.Name).Append(':').Append(Render(option.Value));
        }
    }

    internal static string Render(object? value) => value switch
    {
        null => "",
        IUser user => $"@{user.Username}",
        IRole role => $"@{role.Name}",
        IChannel channel => $"#{channel.Name}",
        IAttachment file => file.Filename,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// The embed one invocation becomes.
    /// </summary>
    /// <remarks>
    /// PURE, so the shape is pinned by a test. The line is the one part a user controls end
    /// to end, so it is escaped and capped; the author and channel are ids Discord renders.
    /// </remarks>
    internal static Embed Build(CommandInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var line = invocation.Line.Length > MaxLineLength
            ? string.Concat(invocation.Line.AsSpan(0, MaxLineLength), "…")
            : invocation.Line;

        var where = invocation.ChannelId is { } channel ? $" in <#{channel}>" : " in a DM";

        var embed = new EmbedBuilder()
            .WithColor(Theme.Blue)
            .WithAuthor(invocation.UserName, invocation.AvatarUrl)
            .WithDescription(
                $"{Hammer} <@{invocation.UserId}> used `/{Sanitize.Code(invocation.Command)}` command{where}\n" +
                Escape(line));

        // Footers and author names are not markdown, so the server name goes in as it is.
        embed.Brand(invocation.GuildName is { Length: > 0 } guild ? guild : "Direct message");

        /* AFTER Brand, which clears the timestamp. It renders in each viewer's own time zone
           ("Today at 20:28"), so the footer only needs the server. */
        embed.Timestamp = invocation.At;
        return embed.Build();
    }

    /// <summary>
    /// Typed text made inert in a description: markdown escaped, line breaks flattened.
    /// </summary>
    /// <remarks>
    /// NOT <see cref="Sanitize.Markdown"/>. That one is built on the RCON sanitiser, which
    /// strips everything outside printable ASCII and stops at 200 characters - right for a
    /// broadcast, wrong for a record of what somebody actually typed.
    /// </remarks>
    internal static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\' or '*' or '_' or '~' or '`' or '|' or '[' or ']' or '>' or '#':
                    escaped.Append('\\').Append(c);
                    break;
                case '\r' or '\n':
                    escaped.Append(' ');
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }
}
