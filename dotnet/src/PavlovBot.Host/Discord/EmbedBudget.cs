using Discord;

namespace PavlovBot.Host.Discord;

/// <summary>
/// Adding fields to an embed that Discord cannot reject.
/// </summary>
/// <remarks>
/// <c>EmbedBuilder.Build()</c> VALIDATES EAGERLY AND THROWS: <c>ArgumentException</c> on a
/// field that is empty or over 1024 characters, <c>InvalidOperationException</c> when the
/// whole embed exceeds 6000. In this bot embeds are built inline in a log poll or a
/// background tick, so a throw does not cost the embed - it costs whatever loop was building
/// it. See <see cref="Logs.FeedBridge"/> for the version of that which also cost the tailer's
/// offset.
///
/// FIELDS ARE CHARGED IN THE ORDER THEY ARE ADDED. The caller decides what survives a tight
/// budget by deciding what to add first, and a field that does not fit is DROPPED rather than
/// half-written. <see cref="Add"/> reports which happened, and <see cref="Room"/> answers it
/// in advance, so a caller with something better to do than silently lose rows - saying how
/// many it could not show, say - can.
///
/// Extracted from <see cref="ConnectCard"/>, which is still the biggest user. The player
/// board needs the identical guarantee over the identical limits, and two copies of a cap is
/// how one of them ends up wrong.
/// </remarks>
internal sealed class EmbedBudget
{
    /// <summary>Discord's per-field cap, enforced by <c>EmbedBuilder.Build()</c>.</summary>
    public const int FieldLimit = 1024;

    /// <summary>
    /// Discord's total is 6000. Staying under it leaves room for the title, description and
    /// branded footer, which are not counted as fields but do count towards the total.
    /// </summary>
    public const int TotalBudget = 5400;

    /// <summary>Discord's title cap is 256.</summary>
    public const int TitleLimit = 200;

    /// <summary>What a branded footer costs, for callers seeding the total.</summary>
    public const int FooterAllowance = 80;

    /// <summary>Under this there is no room left for a field worth reading.</summary>
    private const int Minimum = 8;

    private readonly EmbedBuilder _embed;
    private int _used;

    /// <param name="seeded">
    /// What the title, description and footer already cost. They are not fields, so
    /// <see cref="Add"/> never charges them, but Discord counts them towards the same 6000.
    /// </param>
    public EmbedBudget(EmbedBuilder embed, int seeded)
    {
        ArgumentNullException.ThrowIfNull(embed);

        _embed = embed;
        _used = Math.Max(0, seeded);
    }

    /// <summary>How much a field under this label may still spend.</summary>
    public int Room(string label) =>
        Math.Min(FieldLimit, TotalBudget - _used - (label?.Length ?? 0));

    /// <summary>
    /// Add a field, truncated to whatever is left.
    /// </summary>
    /// <param name="placeholder">
    /// What stands in for a blank value. An empty field is an <c>ArgumentException</c> out of
    /// <c>Build()</c> rather than a blank line, so something has to go there - and a caller
    /// whose value sanitizes away to nothing would otherwise take the whole embed down.
    /// </param>
    /// <returns>False when there was no room and the field was dropped.</returns>
    public bool Add(string label, string? value, bool inline = false, string placeholder = "unknown")
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        var text = string.IsNullOrWhiteSpace(value) ? placeholder : value;

        var room = Room(label);
        if (room < Minimum) return false;

        if (text.Length > room) text = Truncate(text, room);

        _embed.AddField(label, text, inline);
        _used += label.Length + text.Length;
        return true;
    }

    /// <summary>Clip to a hard limit, with an ellipsis so the clipping is visible.</summary>
    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
    }
}
