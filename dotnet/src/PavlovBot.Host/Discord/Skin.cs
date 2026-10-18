using Discord;
using PavlovBot.Core.Text;

namespace PavlovBot.Host.Discord;

/// <summary>
/// The colours, one per MEANING. Every embed picks a colour by what it means, never by hue.
/// </summary>
/// <remarks>
/// The names keep their meanings across skins and only the values move: green is cleared,
/// red is a ban, amber is a warning, in every skin. A reader learns that bar before they
/// learn the words, so a skin that reshuffled the meanings would cost the one thing a
/// palette is for.
/// </remarks>
public sealed record Palette(
    Color Green,
    Color Amber,
    Color Gold,
    Color Blue,
    Color Sky,
    Color BanRed,
    Color ErrorRed,
    Color Grey,
    Color Purple,
    Color Pink,
    Color Teal,
    Color Orange,
    Color ServerUp,
    Color ServerDown)
{
    /// <summary>Every entry by name, for the check that no two meanings share a colour.</summary>
    public IReadOnlyDictionary<string, Color> Entries => new Dictionary<string, Color>
    {
        [nameof(Green)] = Green,
        [nameof(Amber)] = Amber,
        [nameof(Gold)] = Gold,
        [nameof(Blue)] = Blue,
        [nameof(Sky)] = Sky,
        [nameof(BanRed)] = BanRed,
        [nameof(ErrorRed)] = ErrorRed,
        [nameof(Grey)] = Grey,
        [nameof(Purple)] = Purple,
        [nameof(Pink)] = Pink,
        [nameof(Teal)] = Teal,
        [nameof(Orange)] = Orange,
        [nameof(ServerUp)] = ServerUp,
        [nameof(ServerDown)] = ServerDown,
    };
}

/// <summary>
/// The themed words one skin says out loud. Read through <see cref="Lore"/>, never directly.
/// </summary>
/// <remarks>
/// WHAT BELONGS HERE: what a thing is CALLED - titles, captions, glyphs, the currency. What
/// does not: anything a person has to act on and anything a machine reads. Command names,
/// option names, error causes, stored data and log lines are the same in every skin.
///
/// EVERY MEMBER IS REQUIRED, so a skin that forgets a phrase does not compile rather than
/// rendering an empty title on a live server.
/// </remarks>
public sealed record Vocabulary
{
    // ---- glyphs ----

    /// <summary>Currency.</summary>
    public required string MoneyGlyph { get; init; }

    /// <summary>The skin's mark: the footer stamp, the profile card, the verify panel.</summary>
    public required string Mark { get; init; }

    /// <summary>A posted warrant.</summary>
    public required string WarrantGlyph { get; init; }

    /// <summary>Doing time.</summary>
    public required string JailGlyph { get; init; }

    /// <summary>Rank, service, keeping the peace.</summary>
    public required string StaffGlyph { get; init; }

    // ---- nouns ----

    /// <summary>Where all of this happens, as it reads mid-sentence: "the Mojave".</summary>
    public required string World { get; init; }

    public required Currency Money { get; init; }

    /// <summary>What a balance is labelled: a profile field, a table column.</summary>
    public required string MoneyLabel { get; init; }

    public required string Person { get; init; }
    public required string People { get; init; }

    /// <summary>The caption under the player board, for one person and for several.</summary>
    public required string RoamingOne { get; init; }
    public required string RoamingMany { get; init; }

    /// <summary>"out in the Mojave" after a head count, and the title when there is no count.</summary>
    public required string Whereabouts { get; init; }
    public required string WhereaboutsTitle { get; init; }

    /// <summary>A server section with nobody on it.</summary>
    public required string EmptyServer { get; init; }

    /// <summary>A warrant, singular and plural, as it reads mid-sentence.</summary>
    public required string Warrant { get; init; }
    public required string Warrants { get; init; }

    // ---- boards ----

    public required string RichestTitle { get; init; }

    /// <summary>The heading over the cash table. <c>{0}</c> is how many rows it holds.</summary>
    public required string RichestHeading { get; init; }

    public required string LedgersUnreadable { get; init; }
    public required string NoLedgers { get; init; }
    public required string MostWantedTitle { get; init; }
    public required string NoArrests { get; init; }
    public required string WarrantBoardTitle { get; init; }
    public required string NoWarrantsTitle { get; init; }
    public required string NobodyWanted { get; init; }
    public required string StaffBoardTitle { get; init; }

    // ---- moderation ----

    public required string WarrantIssued { get; init; }
    public required string BannedForGood { get; init; }
    public required string BannedForAWhile { get; init; }
    public required string Banned { get; init; }
    public required string BannedByTheServer { get; init; }
    public required string BanServed { get; init; }
    public required string BanLifted { get; init; }

    /// <summary>After the player's name, once a ban is lifted: "may reconnect."</summary>
    public required string MayReturn { get; init; }

    public required string Kicked { get; init; }

    // ---- everything else ----

    public required string MoneyMoved { get; init; }
    public required string HelpTitle { get; init; }
    public required string ConnectTitle { get; init; }

    /// <summary>Between the player and the server on the connect card: "just connected on".</summary>
    public required string Arrived { get; init; }

    public required string ConnectLog { get; init; }
    public required string VerifyTitle { get; init; }

    /// <summary>The verify panel's middle paragraph: who reviews, and what approval opens.</summary>
    public required string VerifyReview { get; init; }

    /// <summary>The verify panel's last line, on alts.</summary>
    public required string VerifyAlts { get; init; }

    public required string Verified { get; init; }
}

/// <summary>One whole look: the words, the colours, and the name stamped on every embed.</summary>
/// <param name="Name">What <c>THEME</c> is set to to choose it.</param>
/// <param name="Brand">The footer name when <c>BOT_NAME</c> is not set.</param>
public sealed record Skin(string Name, string Brand, Vocabulary Words, Palette Colors);

/// <summary>
/// The skins this bot ships, chosen per instance by <c>THEME</c>.
/// </summary>
/// <remarks>
/// ONE BINARY RUNS TWO SERVERS. The Fallout bot and the police bot are the same build with a
/// different <c>.env</c>, so the look has to be configuration, the same way the factions
/// (<c>FACTION_SET</c>) and the footer name (<c>BOT_NAME</c>) already are. Forking the repo to
/// change forty strings would mean every fix landing twice.
///
/// FALLOUT IS THE DEFAULT and is exactly what the bot said before skins existed, so an
/// instance with no <c>THEME</c> line notices nothing.
/// </remarks>
public static class Skins
{
    /// <summary>The Mojave: caps, bounties, exile. In its Christmas colours.</summary>
    public static Skin Fallout { get; } = new("fallout", "Mojave Authority",
        new Vocabulary
        {
            MoneyGlyph = "🧢",
            Mark = "☢️",
            WarrantGlyph = "📜",
            JailGlyph = "⛓️",
            StaffGlyph = "🎖️",

            World = "the Mojave",
            Money = Currency.Caps,
            MoneyLabel = "Caps",
            Person = "wastelander",
            People = "wastelanders",
            RoamingOne = "courier roaming the Mojave right now.",
            RoamingMany = "couriers roaming the Mojave right now.",
            Whereabouts = "out in the Mojave",
            WhereaboutsTitle = "Out in the Mojave",
            EmptyServer = "*nobody out there*",
            Warrant = "bounty",
            Warrants = "bounties",

            RichestTitle = "Deepest pockets in the Mojave",
            RichestHeading = "The {0} richest in the wasteland",
            LedgersUnreadable = "The caps ledgers are unreadable.",
            NoLedgers = "Nobody has a cap to their name yet.",
            MostWantedTitle = "Most wanted in the Mojave",
            NoArrests = "Nobody has done time yet.",
            WarrantBoardTitle = "Open bounties",
            NoWarrantsTitle = "No open bounties",
            NobodyWanted = "Nobody is wanted in the Mojave right now.",
            StaffBoardTitle = "Keeping the peace",

            WarrantIssued = "Bounty posted",
            BannedForGood = "Exiled from the Mojave",
            BannedForAWhile = "Run out of the Mojave",
            Banned = "Exiled",
            BannedByTheServer = "Exiled by the server",
            BanServed = "Sentence served",
            BanLifted = "Exile lifted",
            MayReturn = "may walk back in.",
            Kicked = "Thrown out",

            MoneyMoved = "Caps moved",
            HelpTitle = "Terminal — available commands",
            ConnectTitle = "Dossier",
            Arrived = "just walked into",
            ConnectLog = "Arrival log",
            VerifyTitle = "Check in at the gate",
            VerifyReview = "A staff member looks over every arrival. Once you are cleared, the rest of the Mojave opens up.",
            VerifyAlts = "One account per person — alts do not get through.",
            Verified = "Cleared at the gate",
        },
        /* THE CHRISTMAS SKIN (holly green, red, snow white, gold) over the Pip-Boy meanings:
           green is cleared, red is a ban, amber is a warning. */
        new Palette(
            Green: new Color(0x1F, 0x8A, 0x4C),       // holly green: success, online, cleared
            Amber: new Color(0xDD, 0xA1, 0x1E),       // gold: warnings
            Gold: new Color(0xF1, 0xDA, 0x94),        // champagne gold: caps, economy
            Blue: new Color(0xF2, 0xEF, 0xE6),        // snow white: information
            Sky: new Color(0xB5, 0xD9, 0xCD),         // frost: neutral accent
            BanRed: new Color(0xD9, 0x30, 0x3F),      // Christmas red: bans, blocks, denied
            ErrorRed: new Color(0xE8, 0x70, 0x7A),    // cranberry: errors, softer than a ban
            Grey: new Color(0x8B, 0x94, 0x90),        // winter grey: disabled, void
            Purple: new Color(0xA6, 0x3A, 0x5A),      // mulled wine
            Pink: new Color(0xF4, 0xAE, 0xB6),        // blush
            Teal: new Color(0x2E, 0x9C, 0x86),        // spruce
            Orange: new Color(0xAD, 0x73, 0x26),      // antique gold
            ServerUp: new Color(0x2E, 0x94, 0x55),    // the /serverinfo card, online
            ServerDown: new Color(0xA3, 0x26, 0x36))); // the /serverinfo card, offline

    /// <summary>
    /// The city: dollars, warrants, bans, and the GWPD on every footer.
    /// </summary>
    /// <remarks>
    /// THE WORDING IS THE POLICE BOT'S OWN, restored from before the Mojave reskin (PR #111)
    /// replaced it - "Active warrants", "Ban lifted", "$1,250" - and the colours are the ones
    /// it wore before the Pip-Boy palette (PR #130). It is what that server looked like, not
    /// a new design.
    /// </remarks>
    public static Skin Police { get; } = new("police", "GWPD",
        new Vocabulary
        {
            MoneyGlyph = "💰",
            Mark = "🚔",
            WarrantGlyph = "🚨",
            JailGlyph = "🔒",
            StaffGlyph = "👮",

            World = "the city",
            Money = Currency.Dollars,
            MoneyLabel = "Balance",
            Person = "player",
            People = "players",
            RoamingOne = "player on the streets right now.",
            RoamingMany = "players on the streets right now.",
            Whereabouts = "online",
            WhereaboutsTitle = "Online",
            EmptyServer = "*nobody*",
            Warrant = "warrant",
            Warrants = "warrants",

            RichestTitle = "Richest players",
            RichestHeading = "Top {0} richest players",
            LedgersUnreadable = "Economy records are unreadable.",
            NoLedgers = "No ledgers on file yet. Balances appear here once players have money.",
            MostWantedTitle = "Most wanted",
            NoArrests = "No arrests on record yet.",
            WarrantBoardTitle = "Active warrants",
            NoWarrantsTitle = "No active warrants",
            NobodyWanted = "Nobody is currently wanted.",
            StaffBoardTitle = "Staff activity",

            WarrantIssued = "Warrant issued",
            BannedForGood = "Permanently banned",
            BannedForAWhile = "Banned",
            Banned = "Banned",
            BannedByTheServer = "Banned by the server",
            BanServed = "Ban served",
            BanLifted = "Ban lifted",
            MayReturn = "may reconnect.",
            Kicked = "Kicked",

            MoneyMoved = "Balance updated",
            HelpTitle = "Commands",
            ConnectTitle = "Player information",
            Arrived = "just connected on",
            ConnectLog = "Connection log",
            VerifyTitle = "Verify to unlock the server",
            VerifyReview = "A staff member reviews each request. Once approved you get access to the rest of the server.",
            VerifyAlts = "One account per person — alts cannot be verified.",
            Verified = "Verified",
        },
        new Palette(
            Green: new Color(0x4A, 0xDE, 0x80),       // success, online, cleared
            Amber: new Color(0xFB, 0xBF, 0x24),       // warnings
            Gold: new Color(0xFA, 0xCC, 0x15),        // economy, balances
            Blue: new Color(0x60, 0xA5, 0xFA),        // information
            Sky: new Color(0x38, 0xBD, 0xF8),         // neutral accent
            BanRed: new Color(0xF4, 0x3E, 0x5E),      // bans, blocks, denied
            ErrorRed: new Color(0xFB, 0x71, 0x85),    // errors, softer than a ban
            Grey: new Color(0x94, 0xA3, 0xB8),        // disabled, void
            Purple: new Color(0xA7, 0x8B, 0xFA),
            Pink: new Color(0xF4, 0x72, 0xB6),
            Teal: new Color(0x2D, 0xD4, 0xBF),
            Orange: new Color(0xFB, 0x92, 0x3C),
            ServerUp: new Color(0x3F, 0xA2, 0x5F),
            ServerDown: new Color(0x9E, 0x3B, 0x2F)));

    public static IReadOnlyList<Skin> All { get; } = [Fallout, Police];

    /// <summary>Every name <c>THEME</c> accepts, for the error when it matches none.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name)];

    /// <summary>
    /// A skin by name, or null when the name is not one.
    /// </summary>
    /// <remarks>
    /// NULL RATHER THAN A FALLBACK, the same rule <c>FACTION_SET</c> follows: a typo is refused
    /// at startup instead of quietly dressing the police bot in the Mojave.
    /// </remarks>
    public static Skin? Named(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
}
