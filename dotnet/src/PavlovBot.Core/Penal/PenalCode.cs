using System.Collections.Frozen;
using System.Globalization;

namespace PavlovBot.Core.Penal;

/// <summary>Severity of a charge.</summary>
public enum ChargeClass { Infraction, Misdemeanor, Felony, MisdemeanorOrFelony }

/// <summary>How bail is decided for a charge.</summary>
public enum BailRule
{
    /// <summary>A figure in dollars, scaled by the server's bail rate.</summary>
    Fixed,

    /// <summary>
    /// NOT BAILABLE. The sentence is served; money does not shorten it.
    /// </summary>
    /// <remarks>
    /// This is what "N/A" means in the bail column of the penal code sheet, and getting it
    /// wrong is expensive in both directions. An earlier reading treated it as "no figure
    /// has been chosen yet, the officer decides" - which would have let somebody buy their
    /// way out of manslaughter for whatever the officer typed. It is the opposite: these are
    /// the charges you cannot buy your way out of at all.
    /// </remarks>
    None,

    /// <summary>Set by whatever crime this charge attaches to - see PC 700.</summary>
    Associated,
}

/// <param name="Code">e.g. "PC 104", "VC 604".</param>
/// <param name="JailMinutes">0 for an infraction that carries no jail, or a ranging sentence.</param>
/// <param name="Bail">Dollars. Null whenever <paramref name="Rule"/> is not <see cref="BailRule.Fixed"/>.</param>
/// <param name="SentenceRanges">The sentence depends on the associated crime - see PC 707.</param>
public sealed record Charge(
    string Code,
    string Name,
    ChargeClass Class,
    int JailMinutes,
    int? Bail,
    BailRule Rule = BailRule.Fixed,
    bool SentenceRanges = false)
{
    /// <summary>The hundreds series this charge belongs to: "PC 104" -> 100.</summary>
    public int Section => PenalCode.SectionOf(Code);

    /// <summary>Bail at the current rate, rounded to the dollar. Null when there is no figure.</summary>
    public int? BailAt(double rate = 1.0) =>
        Bail is null ? null : (int)Math.Round(Bail.Value * rate, MidpointRounding.AwayFromZero);

    /// <summary>False when no amount of money gets this player out early.</summary>
    public bool Bailable => Rule != BailRule.None;
}

/// <summary>
/// The penal code: 72 charges across eight sections, plus the booking maths.
/// </summary>
/// <remarks>
/// Pure data and arithmetic, transcribed from the penal code spreadsheet.
///
/// TWO INDEPENDENT AXES, which the previous shape conflated into one "special" field:
/// what the SENTENCE is, and how BAIL is decided. PC 707 is the case that proves they are
/// separate - its sentence ranges with the associated crime AND it carries no bail, which a
/// single field cannot say.
///
/// `rate` is the admin-tunable bail multiplier behind /bail increase|decrease. It is applied
/// and rounded PER CHARGE, not to the total, so a booking's bail always equals the sum of
/// the figures shown for its individual charges. Rounding the total instead would produce a
/// receipt whose lines do not add up.
/// </remarks>
public static class PenalCode
{
    public static readonly FrozenDictionary<int, string> SectionTitles =
        new Dictionary<int, string>
        {
            [100] = "Public Order Offenses",
            [200] = "Crimes Against Persons",
            [300] = "Property Crimes",
            [400] = "Weapons Offenses",
            [500] = "Crimes Against Government",
            [600] = "Vehicle Code",
            [700] = "Organized Crime",
            [800] = "Controlled Substances",
        }.ToFrozenDictionary();

    private const BailRule NoBail = BailRule.None;

    public static readonly IReadOnlyList<Charge> Charges =
    [
        // ---- Public Order Offenses ----
        new("PC 100", "Disorderly Conduct", ChargeClass.Misdemeanor, 2, 10),
        new("PC 101", "Disturbing the Peace", ChargeClass.Infraction, 1, 15),
        new("PC 102", "Public Intoxication", ChargeClass.Infraction, 1, 15),
        new("PC 103", "Loitering", ChargeClass.Infraction, 1, 30),
        new("PC 104", "Trespassing", ChargeClass.Misdemeanor, 2, 50),
        new("PC 105", "Unlawful Assembly", ChargeClass.Misdemeanor, 3, 50),
        new("PC 106", "Non-Peaceful Riot", ChargeClass.Felony, 7, null, NoBail),
        new("PC 107", "Failure to Obey a Lawful Order", ChargeClass.Misdemeanor, 3, 75),

        // ---- Crimes Against Persons ----
        new("PC 200", "Assault", ChargeClass.Misdemeanor, 4, 75),
        new("PC 201", "Aggravated Assault", ChargeClass.Felony, 5, 250),
        new("PC 202", "Battery", ChargeClass.Misdemeanor, 4, 100),
        new("PC 203", "Aggravated Battery", ChargeClass.Felony, 5, 250),
        new("PC 204", "Menacing", ChargeClass.Misdemeanor, 4, 150),
        new("PC 205", "Aggravated Menacing", ChargeClass.Felony, 6, 500),
        new("PC 206", "Brandishing a Weapon", ChargeClass.Misdemeanor, 3, 150),
        new("PC 207", "Kidnapping", ChargeClass.Felony, 5, 700),
        new("PC 208", "Fraud", ChargeClass.Misdemeanor, 3, null, NoBail),
        new("PC 209", "Attempted Murder", ChargeClass.Felony, 7, 75),
        new("PC 210", "Homicide", ChargeClass.Felony, 8, 250),
        new("PC 211", "Mass Homicide", ChargeClass.Felony, 10, null, NoBail),

        // ---- Property Crimes ----
        new("PC 300", "Petty Theft (<$500)", ChargeClass.Misdemeanor, 3, 400),
        new("PC 301", "Grand Larceny (>$500)", ChargeClass.Felony, 5, 500),
        new("PC 302", "Burglary", ChargeClass.Felony, 6, 100),
        new("PC 303", "Robbery", ChargeClass.Felony, 5, 75),
        new("PC 304", "Armed Robbery", ChargeClass.Felony, 7, 75),
        new("PC 305", "Possession of Stolen Property", ChargeClass.Misdemeanor, 4, 150),
        new("PC 306", "Criminal Mischief", ChargeClass.Misdemeanor, 3, 450),
        new("PC 307", "Vandalism", ChargeClass.Misdemeanor, 3, 300),
        new("PC 308", "Attempted Burglary", ChargeClass.Misdemeanor, 4, 150),
        new("PC 309", "Extortion", ChargeClass.Felony, 6, 475),
        new("PC 310", "Grand Theft Auto", ChargeClass.Felony, 7, null, NoBail),

        // ---- Weapons Offenses ----
        new("PC 400", "Unlawful Discharge of a Firearm", ChargeClass.Misdemeanor, 4, 450),
        new("PC 401", "Carrying a Concealed Weapon Without Permit", ChargeClass.Misdemeanor, 5, 150),
        new("PC 402", "Possession of Illegal Automatic Weapons", ChargeClass.Felony, 6, 100),
        new("PC 403", "Possession of Explosives", ChargeClass.Felony, 8, 500),
        new("PC 404", "Illegal Sale of Firearms", ChargeClass.Felony, 6, 75),
        new("PC 405", "Brandishing of a Firearm", ChargeClass.Misdemeanor, 3, null, NoBail),

        // ---- Crimes Against Government ----
        new("PC 500", "Resisting or Evading Arrest", ChargeClass.Misdemeanor, 3, 300),
        new("PC 501", "Obstruction of Justice", ChargeClass.Misdemeanor, 4, 250),
        new("PC 502", "Escape from Custody", ChargeClass.Felony, 5, 75),
        new("PC 503", "False Report", ChargeClass.Misdemeanor, 3, 10),
        new("PC 504", "Impersonating a Police Officer", ChargeClass.Felony, 6, 100),
        new("PC 505", "Bribery of a Public Official", ChargeClass.Felony, 5, 150),
        new("PC 506", "Perjury", ChargeClass.Felony, 6, 325),
        new("PC 507", "Contempt of Court", ChargeClass.Misdemeanor, 3, 10),
        new("PC 508", "Malfeasance", ChargeClass.Felony, 5, null, NoBail),

        // ---- Vehicle Code ----
        // An infraction with no jail is still a real charge: the fine is the whole penalty.
        new("VC 600", "Speeding", ChargeClass.Infraction, 0, 50),
        new("VC 601", "Reckless Driving", ChargeClass.Misdemeanor, 3, 5),
        new("VC 602", "Driving While Intoxicated", ChargeClass.Misdemeanor, 4, 350),
        new("VC 603", "Hit and Run", ChargeClass.Felony, 6, 550),
        new("VC 604", "Failure to Yield", ChargeClass.Infraction, 0, 75),
        new("VC 605", "Evading and Eluding Police", ChargeClass.Felony, 4, 200),
        new("VC 606", "Driving Without a License", ChargeClass.Misdemeanor, 2, 600),
        new("VC 607", "Illegal Parking", ChargeClass.Infraction, 0, 400),
        new("VC 608", "Vehicular Assault", ChargeClass.Felony, 5, 400),
        new("VC 609", "Vehicular Manslaughter", ChargeClass.Felony, 8, 150),
        new("VC 610", "Following an Emergency Vehicle", ChargeClass.Misdemeanor, 3, null, NoBail),
        new("VC 611", "Failure to Maintain Lane", ChargeClass.Infraction, 0, null, NoBail),

        // ---- Organized Crime ----
        new("PC 700", "Illegal Gambling", ChargeClass.Misdemeanor, 3, null, BailRule.Associated),
        new("PC 701", "Bootlegging", ChargeClass.Felony, 5, null, NoBail),
        new("PC 702", "Racketeering", ChargeClass.Felony, 7, null, NoBail),
        new("PC 703", "Loan Sharking", ChargeClass.Felony, 6, null, NoBail),
        new("PC 704", "Smuggling", ChargeClass.Felony, 6, null, NoBail),
        new("PC 705", "Operating an Illegal Business", ChargeClass.Misdemeanor, 4, null, NoBail),
        new("PC 706", "Criminal Conspiracy", ChargeClass.Felony, 6, null, NoBail),
        /* The charge both axes exist for: the sentence RANGES with whatever it attaches to,
           and there is no bail either way. */
        new("PC 707", "Aiding and Abetting", ChargeClass.MisdemeanorOrFelony, 0, null, NoBail, SentenceRanges: true),

        // ---- Controlled Substances ----
        // Every charge here carries a real sentence and no bail at all.
        new("PC 801", "Possession of Controlled Substance", ChargeClass.Misdemeanor, 3, null, NoBail),
        new("PC 802", "Possession of Narcotics with Intent to Distribute", ChargeClass.Felony, 5, null, NoBail),
        new("PC 803", "Sale of Illegal Narcotics", ChargeClass.Felony, 6, null, NoBail),
        new("PC 804", "Manufacturing Illegal Narcotics", ChargeClass.Felony, 8, null, NoBail),
        new("PC 805", "Illegal Distribution of Prescription Drugs", ChargeClass.Felony, 3, null, NoBail),
        new("PC 806", "Possession of Drug Paraphernalia", ChargeClass.Misdemeanor, 4, null, NoBail),
    ];

    private static readonly FrozenDictionary<string, Charge> ByCode =
        Charges.ToFrozenDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>Hundreds series from a code: "PC 104" -> 100, "VC 604" -> 600.</summary>
    public static int SectionOf(string code)
    {
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture) / 100 * 100;
    }

    public static Charge? Get(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var c) ? c : null;

    /// <summary>Charges grouped by section, in code order.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<Charge>> Sections { get; } =
        Charges.GroupBy(c => c.Section)
               .OrderBy(g => g.Key)
               .ToDictionary(g => g.Key, g => (IReadOnlyList<Charge>)[.. g]);

    /// <summary>Section number, title and charge count - the section picker.</summary>
    public static IReadOnlyList<(int Number, string Title, int Count)> SectionList() =>
        [.. Sections.OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key,
                SectionTitles.TryGetValue(kv.Key, out var t) ? t : $"Section {kv.Key}",
                kv.Value.Count))];

    /// <summary>
    /// Total jail and bail for a set of charge codes.
    /// </summary>
    /// <param name="capMinutes">
    /// The most jail a single booking can carry, however many charges are stacked. Zero or
    /// less means no cap.
    /// </param>
    /// <remarks>
    /// THE CAP APPLIES TO JAIL AND NOT TO BAIL, deliberately. Stacking eight charges should
    /// cost eight charges' worth of money - that is the deterrent - but it should not put
    /// somebody in a cell for half an hour, because a sentence nobody will sit through is
    /// one they disconnect through instead.
    ///
    /// ONE NON-BAILABLE CHARGE MAKES THE WHOLE BOOKING NON-BAILABLE. Anything else defeats
    /// the point of the rule: a player charged with manslaughter and a parking ticket would
    /// otherwise pay the parking ticket and walk out of the manslaughter.
    /// </remarks>
    public static Booking Book(IEnumerable<string> codes, double rate = 1.0, int capMinutes = 0)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var matched = new List<Charge>();
        var minutes = 0;
        var bail = 0;
        var bailable = true;
        var associated = false;
        var ranges = false;

        foreach (var code in codes)
        {
            var charge = Get(code);
            if (charge is null) continue;   // an unknown code is skipped, never guessed at
            matched.Add(charge);
            minutes += charge.JailMinutes;
            // Rounded per charge so the total always equals the sum of the displayed lines.
            if (charge.BailAt(rate) is { } b) bail += b;
            if (charge.Rule == BailRule.None) bailable = false;
            if (charge.Rule == BailRule.Associated) associated = true;
            if (charge.SentenceRanges) ranges = true;
        }

        /* Reported, not just applied. A booking that says "35 min" when the player will
           serve 15 is a receipt that lies, and the officer needs to know the cap is what
           decided the sentence rather than the charges. */
        var capped = capMinutes > 0 && minutes > capMinutes;
        if (capped) minutes = capMinutes;

        return new Booking(matched, minutes, bail, bailable, associated, ranges, capped, capped ? capMinutes : null);
    }
}

/// <summary>The result of booking a set of charges.</summary>
/// <param name="Bailable">False when any charge on the booking cannot be bailed out of.</param>
/// <param name="AssociatedBail">Bail depends on the crime a charge attaches to.</param>
/// <param name="Ranges">The sentence depends on the crime a charge attaches to.</param>
/// <param name="Capped">True when the stacked total was clamped by the sentence cap.</param>
/// <param name="CapMinutes">The cap that applied, for the message that explains the clamp.</param>
public sealed record Booking(
    IReadOnlyList<Charge> Charges,
    int JailMinutes,
    int Bail,
    bool Bailable = true,
    bool AssociatedBail = false,
    bool Ranges = false,
    bool Capped = false,
    int? CapMinutes = null)
{
    /// <summary>"9 min", "9 min (capped)", "2 min + ranges with the associated charge".</summary>
    public string SentenceLabel()
    {
        var parts = new List<string>(2);
        if (JailMinutes > 0) parts.Add($"{JailMinutes} min" + (Capped ? " (capped)" : ""));
        if (Ranges) parts.Add("ranges with the associated charge");
        return parts.Count > 0 ? string.Join(" + ", parts) : "No jail time";
    }

    /// <summary>"$250", "No bail - the sentence must be served", "Based on the associated charge".</summary>
    public string BailLabel()
    {
        /* Checked FIRST, and it wins outright. A booking that shows a dollar figure the
           player cannot actually pay is worse than showing none: they queue up at the
           station with the money and nobody can tell them why it will not work. */
        if (!Bailable) return "No bail - the sentence must be served";

        var amount = "$" + Bail.ToString("N0", CultureInfo.GetCultureInfo("en-US"));

        if (!AssociatedBail) return amount;

        return Bail > 0 ? $"{amount} + based on the associated charge" : "Based on the associated charge";
    }
}
