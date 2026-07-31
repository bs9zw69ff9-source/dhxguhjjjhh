using Discord;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The visual system: one palette, one set of glyphs, one stamp.
/// </summary>
/// <remarks>
/// Every embed the bot sends routes its colour through here, so this table is the single
/// source of truth for the whole UI. Colours keep their MEANING - green is success, red is
/// a ban, amber is a warning - which is what lets somebody read a feed at a glance without
/// reading the words.
///
/// The clamping in <see cref="Brand"/> is not cosmetic. Discord rejects an embed that
/// exceeds any of its limits, and it rejects the WHOLE MESSAGE - so one long ban reason
/// silently costs you the entire audit entry. Truncating is always better than not sending.
/// </remarks>
public static class Theme
{
    // ---- palette ----
    public static readonly Color Amber = new(0xFB, 0xBF, 0x24);        // warnings, neutral highlight
    public static readonly Color Gold = new(0xFA, 0xCC, 0x15);         // economy, balances
    public static readonly Color Green = new(0x4A, 0xDE, 0x80);        // success, online, cleared
    public static readonly Color Sky = new(0x38, 0xBD, 0xF8);          // neutral accent
    public static readonly Color BanRed = new(0xF4, 0x3E, 0x5E);       // bans, blocks, denied
    public static readonly Color ErrorRed = new(0xFB, 0x71, 0x85);     // errors, softer than a ban
    public static readonly Color Grey = new(0x94, 0xA3, 0xB8);         // disabled, void
    public static readonly Color Blue = new(0x60, 0xA5, 0xFA);         // information
    public static readonly Color Purple = new(0xA7, 0x8B, 0xFA);
    public static readonly Color Pink = new(0xF4, 0x72, 0xB6);
    public static readonly Color Teal = new(0x2D, 0xD4, 0xBF);
    public static readonly Color Orange = new(0xFB, 0x92, 0x3C);

    // ---- glyphs ----
    public const string Ok = "✅";
    public const string Bad = "❌";
    public const string Warn = "⚠️";
    public const string Deny = "🚫";
    public const string Info = "ℹ️";
    public const string Dot = "•";
    public const string Up = "🟢";
    public const string Down = "🔴";
    public const string Money = "💰";
    public const string Rank = "🏅";

    public const string Divider = "────────────────────────────";

    /// <summary>The name stamped on branded embeds. Set BOT_NAME to skin the bot per server.</summary>
    public static string BrandName { get; set; } = "Server Authority";

    // Discord's hard limits. Exceeding any one of them rejects the entire message.
    private const int MaxTitle = 256;
    private const int MaxDescription = 4096;
    private const int MaxFieldName = 256;
    private const int MaxFieldValue = 1024;
    private const int MaxFields = 25;

    /// <summary>
    /// Apply the house style and clamp everything to Discord's limits.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal: colour bar, title, body. No author header, no thumbnail, no
    /// default footer, no timestamp - the clean look the help menu established. Any
    /// timestamp a call site set is stripped so every embed in the bot looks the same.
    /// </remarks>
    public static EmbedBuilder Brand(this EmbedBuilder embed, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(embed);

        if (footer is { Length: > 0 }) embed.WithFooter(Truncate(footer, 2048));
        embed.Timestamp = null;

        if (embed.Title is { Length: > MaxTitle }) embed.Title = Truncate(embed.Title, MaxTitle);
        if (embed.Description is { Length: > MaxDescription }) embed.Description = Truncate(embed.Description, MaxDescription);

        if (embed.Fields.Count > MaxFields) embed.Fields.RemoveRange(MaxFields, embed.Fields.Count - MaxFields);
        foreach (var field in embed.Fields)
        {
            if (field.Name is { Length: > MaxFieldName }) field.Name = Truncate(field.Name, MaxFieldName);
            if (field.Value?.ToString() is { Length: > MaxFieldValue } value) field.Value = Truncate(value, MaxFieldValue);
        }
        return embed;
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 3)] + "...";

    private static EmbedBuilder Base(Color color, string title, string? description) =>
        new EmbedBuilder().WithColor(color).WithTitle(title).WithDescription(description ?? "").Brand();

    public static EmbedBuilder Success(string title, string? description = null) => Base(Green, $"{Ok} {title}", description);
    public static EmbedBuilder Failure(string title, string? description = null) => Base(ErrorRed, $"{Bad} {title}", description);
    public static EmbedBuilder Denied(string title, string? description = null) => Base(BanRed, $"{Deny} {title}", description);
    public static EmbedBuilder Warning(string title, string? description = null) => Base(Amber, $"{Warn} {title}", description);
    public static EmbedBuilder Notice(string title, string? description = null) => Base(Blue, $"{Info} {title}", description);

    /// <summary>A ban or other punishment. Its own colour so it stands out in a busy feed.</summary>
    public static EmbedBuilder Punishment(string title, string? description = null) => Base(BanRed, title, description);

    /// <summary>
    /// Split a long list into embed-sized pages.
    /// </summary>
    /// <remarks>
    /// Joining first and slicing the string would cut a line in half. Accumulating whole
    /// lines means a 400-name roster pages cleanly instead of ending a page mid-name.
    /// </remarks>
    public static IReadOnlyList<string> Paginate(IEnumerable<string> lines, int limit = MaxDescription)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var pages = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var piece = line.Length > limit ? Truncate(line, limit) : line;
            if (current.Length > 0 && current.Length + piece.Length + 1 > limit)
            {
                pages.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append('\n');
            current.Append(piece);
        }

        if (current.Length > 0) pages.Add(current.ToString());
        return pages.Count > 0 ? pages : [""];
    }

    /// <summary>A progress bar for a leaderboard row.</summary>
    public static string Bar(double value, double max, int width = 12)
    {
        if (max <= 0 || width <= 0) return new string('░', Math.Max(0, width));
        var filled = (int)Math.Round(Math.Clamp(value / max, 0, 1) * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>Discord's relative timestamp, which auto-localises per viewer.</summary>
    /// <remarks>
    /// Preferred over any fixed zone WHEN THE READER IS A PERSON IN DISCORD - each of them
    /// sees it in their own. Fixed Eastern is for logs and files, where there is no viewer
    /// to localise for.
    /// </remarks>
    public static string Relative(DateTimeOffset at) => $"<t:{at.ToUnixTimeSeconds()}:R>";

    public static string Absolute(DateTimeOffset at) => $"<t:{at.ToUnixTimeSeconds()}:f>";
}
