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
    /* ---- the skin ----

       THE PALETTE IS THE SKIN'S, and the names below keep their meanings in every skin:
       green is cleared, red is a ban, amber is a warning. A reader learns that bar before
       they learn the words. The values live in <see cref="Skins"/>.

       CHOSEN ONCE, AT STARTUP, by THEME - before the gateway connects and before anything can
       build an embed. Nothing changes it afterwards; the field is volatile so a reader on any
       thread sees the skin startup chose rather than a stale default. */
    private static volatile Skin _skin = Skins.Fallout;

    /// <summary>The skin this instance runs. Fallout unless <c>THEME</c> says otherwise.</summary>
    public static Skin Skin => _skin;

    /// <summary>
    /// Dress the bot in <paramref name="skin"/>, and stamp its name unless BOT_NAME overrides it.
    /// </summary>
    /// <remarks>
    /// THE BRAND IS RESET WITH IT, because a police skin stamping "Mojave Authority" on every
    /// footer is the half-applied theme this exists to prevent. Program sets
    /// <see cref="BrandName"/> from BOT_NAME AFTER this, so an explicit name still wins.
    /// </remarks>
    public static void Use(Skin skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skin = skin;
        BrandName = skin.Brand;
    }

    public static Color Green => _skin.Colors.Green;        // success, online, cleared
    public static Color Amber => _skin.Colors.Amber;        // warnings
    public static Color Gold => _skin.Colors.Gold;          // economy
    public static Color Blue => _skin.Colors.Blue;          // information
    public static Color Sky => _skin.Colors.Sky;            // neutral accent
    public static Color BanRed => _skin.Colors.BanRed;      // bans, blocks, denied
    public static Color ErrorRed => _skin.Colors.ErrorRed;  // errors, softer than a ban
    public static Color Grey => _skin.Colors.Grey;          // disabled, void
    public static Color Purple => _skin.Colors.Purple;
    public static Color Pink => _skin.Colors.Pink;
    public static Color Teal => _skin.Colors.Teal;
    public static Color Orange => _skin.Colors.Orange;
    public static Color ServerUp => _skin.Colors.ServerUp;      // the /serverinfo card
    public static Color ServerDown => _skin.Colors.ServerDown;

    // ---- glyphs ----
    public const string Ok = "✅";
    public const string Bad = "❌";
    public const string Warn = "⚠️";
    public const string Deny = "🚫";
    public const string Info = "ℹ️";
    public const string Dot = "•";
    public const string Up = "🟢";
    public const string Down = "🔴";
    /// <summary>Money, in this skin's glyph - see <see cref="Lore"/>.</summary>
    public static string Money => Lore.Caps;
    public const string Rank = "🏅";

    /// <summary>A terminal rule, for the places that need a break rather than a heading.</summary>
    public const string Divider = "▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔▔";

    /// <summary>
    /// The name stamped on every branded embed. <c>BOT_NAME</c> skins the bot per server.
    /// </summary>
    /// <remarks>
    /// IT WAS NEVER READ. <c>.env.example</c> has documented BOT_NAME as "stamped on every
    /// embed" since the port, and nothing set this property or printed it - a skin that
    /// silently did nothing, which is the kind of port gap that only shows up when somebody
    /// sets the variable and waits for a change that never comes. <see cref="Brand"/> now
    /// stamps it, and Program wires the variable in.
    /// </remarks>
    public static string BrandName { get; set; } = Skins.Fallout.Brand;

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
    /// Deliberately minimal: colour bar, title, body, and the brand name in the footer. No
    /// author header, no thumbnail, no timestamp - the clean look the help menu established.
    /// Any timestamp a call site set is stripped so every embed in the bot looks the same.
    /// </remarks>
    /// <summary>
    /// What a footer says besides the brand: the whole text when it was set without one, the part
    /// after the brand when it was branded already, or null when there is nothing else.
    /// </summary>
    internal static string? FooterAfterBrand(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var mark = $"{Lore.Rads} {BrandName}";
        if (!text.StartsWith(mark, StringComparison.Ordinal)) return text;

        var rest = text[mark.Length..].TrimStart();
        if (rest.StartsWith(Dot, StringComparison.Ordinal)) rest = rest[Dot.Length..].TrimStart();
        return rest.Length > 0 ? rest : null;
    }

    public static EmbedBuilder Brand(this EmbedBuilder embed, string? footer = null)
    {
        ArgumentNullException.ThrowIfNull(embed);

        /* THE BRAND GOES ON EVERYTHING, which is what makes a wall of embeds read as one
           bot rather than thirty commands. The caller's footer keeps its place after it, so
           "Mojave Authority • Updated 14:02 Eastern" is the shape every board and card
           shares. Composed from the argument, never from the footer already set, so branding
           an embed twice cannot stack the name up. */
        /* THE MARK GOES ON EVERYTHING. One glyph in the footer is what makes a wall of embeds
           read as this server's bot rather than as a generic tool that happens to be in the
           channel - and the footer is the one place it can live without competing with a
           title somebody actually needs to read. */
        /* A FOOTER THE CALLER ALREADY SET IS KEPT, after the brand. Branding used to replace it,
           which silently threw away /health's "build <commit>" line - the one way to tell from
           Discord whether a deploy actually replaced the running process - and /monitor's
           legend. An explicit argument still wins. */
        footer ??= FooterAfterBrand(embed.Footer?.Text);

        var stamp = footer is { Length: > 0 }
            ? $"{Lore.Rads} {BrandName} {Dot} {footer}"
            : $"{Lore.Rads} {BrandName}";

        if (stamp is { Length: > 0 }) embed.WithFooter(Truncate(stamp, 2048));
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

    /// <summary>
    /// A progress bar for a leaderboard row.
    /// </summary>
    /// <remarks>
    /// BLOCK CHARACTERS RATHER THAN AN EMOJI RUN: they are one cell wide in every client, so
    /// a column of them lines up, and they are what a terminal would have drawn anyway.
    /// </remarks>
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
