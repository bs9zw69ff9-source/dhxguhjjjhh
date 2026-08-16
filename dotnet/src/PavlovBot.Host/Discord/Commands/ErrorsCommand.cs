using System.Globalization;
using Discord;
using Discord.WebSocket;
using PavlovBot.Core.Text;
using PavlovBot.Host.Observability;

namespace PavlovBot.Host.Discord.Commands;

/// <summary>
/// <c>/errors</c> - the last few command failures, by the id the caller was shown.
/// </summary>
/// <remarks>
/// THE PROBLEM THIS SOLVES. A failed command answers "the error has been logged
/// (`ada985e1`)" and, until this existed, that id could only be redeemed by somebody with a
/// shell on the box while the log still held it. So the id got reported and the stack trace
/// did not, and the fix was guessed at rather than read.
///
/// OWNER ONLY, AND ALWAYS EPHEMERAL. A stack trace names internal types, file paths and line
/// numbers. None of that is a credential, but none of it belongs in a channel either.
/// </remarks>
public sealed class ErrorsCommand(RecentErrors errors, Access access) : ISlashCommand
{
    /// <summary>Discord's hard limit on an embed field value.</summary>
    private const int FieldLimit = 1024;

    public string Name => "errors";

    /// <summary>Always ephemeral, whatever the caller does.</summary>
    public bool Ephemeral => true;

    public ApplicationCommandProperties Build() =>
        new SlashCommandBuilder()
            .WithName(Name)
            .WithDescription("Owner - Recent command failures, with the stack trace")
            .AddOption("id", ApplicationCommandOptionType.String,
                "The id from the failure message. Omit to list recent failures.", isRequired: false)
            .Build();

    public async Task HandleAsync(SocketSlashCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!access.Allows(RequiredAccess.Owner, command))
        {
            await Reply(command, Theme.Denied("Not allowed", access.Refusal(RequiredAccess.Owner, command)))
                .ConfigureAwait(false);
            return;
        }

        var id = command.Data.Options.FirstOrDefault(o => o.Name == "id")?.Value as string;

        await Reply(command, string.IsNullOrWhiteSpace(id) ? List() : Detail(id)).ConfigureAwait(false);
    }

    private EmbedBuilder List()
    {
        var all = errors.All();
        if (all.Count == 0)
        {
            return Theme.Success("No recent failures",
                "Nothing has thrown since the bot last started.\n\n" +
                "This list lives in memory, so a restart clears it. If you are chasing a failure " +
                "from before the last restart, reproduce it once and run this again.");
        }

        var lines = all.Select(e =>
            $"{Theme.Dot} `{e.CorrelationId}` **{Sanitize.Code(e.Operation)}** - {Sanitize.Code(e.ExceptionType)} " +
            $"<t:{e.At.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:R>");

        return Theme.Notice($"{all.Count} recent failure(s)", string.Join("\n", lines))
            .AddField("Next", "`/errors id:<the id>` for the message and stack trace.");
    }

    private EmbedBuilder Detail(string id)
    {
        if (errors.Find(id) is not { } error)
        {
            return Theme.Failure("No such error",
                $"Nothing recorded under `{Sanitize.Code(id)}`.\n\n" +
                $"Only the last {RecentErrors.Capacity} are kept, and a restart clears them. " +
                "Run `/errors` with no id to see what is still here.");
        }

        /* THE END OF THE TRACE, NOT THE START. The frames nearest the throw are the ones that
           name the failing line; the outer ones are the dispatcher and are identical for
           every failure in the bot. Taking the head would spend the whole field on them. */
        var frames = error.Trace.Split('\n');
        var trace = string.Join("\n", frames.TakeLast(14)).Trim();
        if (trace.Length > FieldLimit - 20) trace = trace[^(FieldLimit - 20)..];

        return Theme.Failure($"{error.Operation} - {error.ExceptionType}", Sanitize.Code(error.Message))
            .AddField("When", $"<t:{error.At.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:F>", inline: true)
            .AddField("Caller", $"<@{error.UserId}>", inline: true)
            .AddField("Trace (innermost frames)", $"```\n{trace}\n```");
    }

    private static Task Reply(SocketSlashCommand command, EmbedBuilder embed) =>
        command.ModifyOriginalResponseAsync(m =>
        {
            m.Embed = embed.Brand().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
}
