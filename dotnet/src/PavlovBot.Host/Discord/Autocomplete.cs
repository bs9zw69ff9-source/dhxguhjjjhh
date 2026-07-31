using PavlovBot.Core.Data;
using PavlovBot.Host.Logs;
using PavlovBot.Host.Rcon;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Discord;

/// <summary>
/// Suggestions for the player-name fields.
/// </summary>
/// <remarks>
/// Almost every command takes a player name, and Pavlov names are long, case-sensitive and
/// full of characters that are awkward to type. Without this, moderators retype them by
/// hand and a typo produces a ban on somebody who does not exist - which looks exactly like
/// a ban that worked.
///
/// THE ORDER IS THE FEATURE. Online players first, then anyone recently seen, then everyone
/// the bot has ever recorded. The name a moderator wants is nearly always somebody who is
/// on the server right now, and burying them under an alphabetical list of two thousand
/// historical names makes the field worse than useless.
///
/// Discord allows at most 25 choices and gives roughly three seconds to answer, so this
/// only ever reads what is already in memory or in one dataset - never RCON.
/// </remarks>
public sealed class PlayerAutocomplete(RconRegistry rcon, IpTrackingService tracking, SerializedStore store)
{
    private const int MaxChoices = 25;

    /// <param name="typed">What the user has entered so far. Empty is normal - the field
    /// is suggested before anything is typed.</param>
    public IReadOnlyList<(string Name, string Value)> Suggest(string typed)
    {
        var query = (typed ?? "").Trim();

        var online = rcon.AllOnlinePlayers();
        var onlineSet = online.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recent = store.Read(Datasets.LastSeen, new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .Where(n => !onlineSet.Contains(n));

        var known = tracking.KnownNames().Where(n => !onlineSet.Contains(n));

        var ordered = online.Select(n => (Name: n, Tag: "online"))
            .Concat(recent.Select(n => (Name: n, Tag: "recent")))
            .Concat(known.Select(n => (Name: n, Tag: "")))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

        if (query.Length > 0)
        {
            /* Prefix matches before substring ones. Typing "al" should offer "Alice" before
               "Marshal" - a substring-only filter buries the obvious answer. */
            ordered = ordered
                .Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Tag == "online" ? 0 : p.Tag == "recent" ? 1 : 2);
        }

        var choices = ordered.Take(MaxChoices)
            .Select(p => (Name: Label(p.Name, p.Tag), Value: p.Name))
            .ToList();

        /* Offer what they typed as a literal choice when nothing matched. The bot cannot
           know every name - a player who has never connected while it was running is
           invisible to it - and refusing to let a moderator type one would make the command
           unusable exactly when it is needed. */
        if (choices.Count == 0 && query.Length > 0)
            choices.Add(($"{query} (manual entry)", query));

        return choices;
    }

    private static string Label(string name, string tag)
    {
        // Discord caps a choice label at 100 characters.
        var label = tag.Length > 0 ? $"{name} ({tag})" : name;
        return label.Length > 100 ? label[..100] : label;
    }
}
