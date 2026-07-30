using System.Collections.Frozen;
using System.Globalization;

namespace PavlovBot.Core.Penal;

/// <summary>Severity of a charge.</summary>
public enum ChargeClass { Infraction, Misdemeanor, Felony, MisdemeanorOrFelony }

/// <summary>Charges whose sentence or bail is not a fixed figure.</summary>
public enum ChargeSpecial
{
    /// <summary>Death penalty: no jail timer, no bail.</summary>
    Execution,

    /// <summary>Jail and bail both depend on the associated crime.</summary>
    Variable,
}

/// <param name="Code">e.g. "PC 104", "VC 604".</param>
/// <param name="JailMinutes">0 for an infraction with no jail, an execution, or a variable charge.</param>
/// <param name="Bail">Dollars, or null when there is no set figure.</param>
public sealed record Charge(
    string Code,
    string Name,
    ChargeClass Class,
    int JailMinutes,
    int? Bail,
    ChargeSpecial? Special = null)
{
    /// <summary>The hundreds series this charge belongs to: "PC 104" -> 100.</summary>
    public int Section => PenalCode.SectionOf(Code);

    /// <summary>Bail at the current rate, rounded to the dollar. Null when there is no set figure.</summary>
    public int? BailAt(double rate = 1.0) =>
        Bail is null ? null : (int)Math.Round(Bail.Value * rate, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The penal code: 59 charges across seven sections, plus the booking maths.
/// </summary>
/// <remarks>
/// Pure data and arithmetic. The charge table was GENERATED from the running JS rather
/// than retyped - 59 rows of bail figures retyped by hand is 59 chances to fat-finger a
/// number that nobody would notice until a player was charged the wrong amount.
///
/// `rate` is the admin-tunable bail multiplier behind /bail increase|decrease. It is
/// applied and rounded PER CHARGE, not to the total, so a booking's bail always equals
/// the sum of the figures shown for its individual charges. Rounding the total instead
/// would produce a receipt whose lines do not add up.
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
        }.ToFrozenDictionary();

    public static readonly IReadOnlyList<Charge> Charges =
    [
        new("PC 100", "Disorderly Conduct", ChargeClass.Misdemeanor, 2, 25, null),
        new("PC 101", "Disturbing the Peace", ChargeClass.Infraction, 1, 10, null),
        new("PC 102", "Public Intoxication", ChargeClass.Infraction, 1, 15, null),
        new("PC 103", "Vagrancy", ChargeClass.Infraction, 1, 15, null),
        new("PC 104", "Trespassing", ChargeClass.Misdemeanor, 2, 30, null),
        new("PC 105", "Unlawful Assembly", ChargeClass.Misdemeanor, 3, 50, null),
        new("PC 106", "Failure to Obey a Lawful Order", ChargeClass.Misdemeanor, 3, 50, null),
        new("PC 200", "Assault", ChargeClass.Misdemeanor, 4, 75, null),
        new("PC 201", "Aggravated Assault", ChargeClass.Felony, 5, 200, null),
        new("PC 202", "Battery", ChargeClass.Misdemeanor, 4, 75, null),
        new("PC 203", "Aggravated Battery", ChargeClass.Felony, 5, 250, null),
        new("PC 204", "Menacing", ChargeClass.Misdemeanor, 4, 100, null),
        new("PC 205", "Aggravated Menacing", ChargeClass.Felony, 6, 250, null),
        new("PC 206", "Brandishing a Weapon", ChargeClass.Misdemeanor, 3, 150, null),
        new("PC 207", "Kidnapping", ChargeClass.Felony, 5, 500, null),
        new("PC 208", "False Imprisonment", ChargeClass.Misdemeanor, 5, 150, null),
        new("PC 209", "Attempted Murder", ChargeClass.Felony, 6, 700, null),
        new("PC 210", "Homicide", ChargeClass.Felony, 0, null, ChargeSpecial.Execution),
        new("PC 300", "Petty Theft", ChargeClass.Misdemeanor, 3, 75, null),
        new("PC 301", "Grand Larceny", ChargeClass.Felony, 5, 250, null),
        new("PC 302", "Burglary", ChargeClass.Felony, 6, 350, null),
        new("PC 303", "Robbery", ChargeClass.Felony, 6, 400, null),
        new("PC 304", "Armed Robbery", ChargeClass.Felony, 8, 500, null),
        new("PC 305", "Possession of Stolen Property", ChargeClass.Misdemeanor, 4, 100, null),
        new("PC 306", "Criminal Mischief", ChargeClass.Misdemeanor, 3, 75, null),
        new("PC 307", "Vandalism", ChargeClass.Misdemeanor, 3, 75, null),
        new("PC 308", "Attempted Burglary", ChargeClass.Misdemeanor, 4, 150, null),
        new("PC 309", "Extortion", ChargeClass.Felony, 8, 450, null),
        new("PC 400", "Unlawful Discharge of a Firearm", ChargeClass.Felony, 5, 300, null),
        new("PC 401", "Carrying a Concealed Weapon Without Permit", ChargeClass.Misdemeanor, 5, 150, null),
        new("PC 402", "Possession of Illegal Automatic Weapons", ChargeClass.Felony, 6, 475, null),
        new("PC 403", "Possession of Explosives", ChargeClass.Felony, 8, 700, null),
        new("PC 404", "Illegal Sale of Firearms", ChargeClass.Felony, 6, 450, null),
        new("PC 500", "Resisting Arrest", ChargeClass.Misdemeanor, 3, 150, null),
        new("PC 501", "Obstruction of Justice", ChargeClass.Misdemeanor, 4, 100, null),
        new("PC 502", "Escape from Custody", ChargeClass.Felony, 5, 500, null),
        new("PC 503", "False Report", ChargeClass.Misdemeanor, 3, 75, null),
        new("PC 504", "Impersonating a Police Officer", ChargeClass.Felony, 6, 300, null),
        new("PC 505", "Bribery of a Public Official", ChargeClass.Felony, 5, 300, null),
        new("PC 506", "Perjury", ChargeClass.Felony, 6, 250, null),
        new("PC 507", "Contempt of Court", ChargeClass.Misdemeanor, 3, 75, null),
        new("VC 600", "Speeding", ChargeClass.Infraction, 0, 10, null),
        new("VC 601", "Reckless Driving", ChargeClass.Misdemeanor, 3, 100, null),
        new("VC 602", "Driving While Intoxicated", ChargeClass.Misdemeanor, 4, 150, null),
        new("VC 603", "Hit and Run", ChargeClass.Felony, 6, 325, null),
        new("VC 604", "Failure to Yield", ChargeClass.Infraction, 0, 10, null),
        new("VC 605", "Evading Police", ChargeClass.Felony, 4, 300, null),
        new("VC 606", "Driving Without a License", ChargeClass.Misdemeanor, 2, 50, null),
        new("VC 607", "Illegal Parking", ChargeClass.Infraction, 0, 5, null),
        new("VC 608", "Vehicular Assault", ChargeClass.Felony, 5, 350, null),
        new("VC 609", "Vehicular Manslaughter", ChargeClass.Felony, 8, 550, null),
        new("PC 700", "Illegal Gambling", ChargeClass.Misdemeanor, 3, 75, null),
        new("PC 701", "Bootlegging", ChargeClass.Felony, 5, 200, null),
        new("PC 702", "Racketeering", ChargeClass.Felony, 7, 600, null),
        new("PC 703", "Loan Sharking", ChargeClass.Felony, 6, 400, null),
        new("PC 704", "Smuggling", ChargeClass.Felony, 6, 400, null),
        new("PC 705", "Operating an Illegal Business", ChargeClass.Misdemeanor, 4, 150, null),
        new("PC 706", "Criminal Conspiracy", ChargeClass.Felony, 6, 350, null),
        new("PC 707", "Aiding and Abetting", ChargeClass.MisdemeanorOrFelony, 0, null, ChargeSpecial.Variable),    ];

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

    /// <summary>Total jail and bail for a set of charge codes, with the special-case flags.</summary>
    public static Booking Book(IEnumerable<string> codes, double rate = 1.0)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var matched = new List<Charge>();
        var minutes = 0;
        var bail = 0;
        var execution = false;
        var variable = false;

        foreach (var code in codes)
        {
            var charge = Get(code);
            if (charge is null) continue;   // an unknown code is skipped, never guessed at
            matched.Add(charge);
            minutes += charge.JailMinutes;
            // Rounded per charge so the total always equals the sum of the displayed lines.
            if (charge.BailAt(rate) is { } b) bail += b;
            if (charge.Special == ChargeSpecial.Execution) execution = true;
            if (charge.Special == ChargeSpecial.Variable) variable = true;
        }

        return new Booking(matched, minutes, bail, execution, variable);
    }
}

/// <summary>The result of booking a set of charges.</summary>
public sealed record Booking(
    IReadOnlyList<Charge> Charges,
    int JailMinutes,
    int Bail,
    bool Execution,
    bool Variable)
{
    /// <summary>"Execution", "9 min", "9 min + based on the associated charge", "No jail time".</summary>
    public string SentenceLabel()
    {
        if (Execution) return "Execution";
        var parts = new List<string>(2);
        if (JailMinutes > 0) parts.Add($"{JailMinutes} min");
        if (Variable) parts.Add("based on the associated charge");
        return parts.Count > 0 ? string.Join(" + ", parts) : "No jail time";
    }

    /// <summary>"$250", "Based on the associated charge", "No bail (execution)".</summary>
    public string BailLabel()
    {
        if (Execution) return "No bail (execution)";
        if (Variable) return "Based on the associated charge";
        return "$" + Bail.ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    }
}
