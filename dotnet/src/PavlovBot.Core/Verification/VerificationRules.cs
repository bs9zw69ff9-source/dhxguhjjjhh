namespace PavlovBot.Core.Verification;

public enum VerificationConflict
{
    None = 0,

    /// <summary>This Discord account is already verified.</summary>
    AlreadyVerified,

    /// <summary>That Pavlov name is verified to somebody else.</summary>
    NameTaken,

    /// <summary>A confirmed IP is already tied to a different verified account - an alt.</summary>
    Alt,
}

/// <param name="OtherId">The Discord account already holding the name or IP.</param>
public sealed record VerificationVerdict(VerificationConflict Conflict, string? OtherId = null, string? Reason = null)
{
    public bool IsAllowed => Conflict == VerificationConflict.None;
    public static VerificationVerdict Allowed { get; } = new(VerificationConflict.None);
}

/// <param name="Name">The Pavlov name this account is verified as.</param>
/// <param name="Ips">Confirmed IPs, which is what makes alt detection possible at all.</param>
public sealed record VerificationRecord(string Name, IReadOnlyList<string> Ips);

/// <summary>
/// One Discord account, one Pavlov name, no alts.
/// </summary>
/// <remarks>
/// A Pavlov name is linked to a Discord user via their confirmed IPs. Two rules follow:
/// one account per name in BOTH directions, and a confirmed IP already tied to a different
/// verified account means this is an alt and cannot be verified.
///
/// THE IP RULE HAS FALSE POSITIVES AND THAT IS UNDERSTOOD. Housemates, siblings and a
/// shared campus NAT all look identical to an alt from here. It is a refusal to
/// self-verify, not a ban - the intended resolution is that staff verify them by hand -
/// so the cost of being wrong is one manual step, and the cost of the opposite default is
/// that the whole mechanism does nothing.
///
/// Pure: the store is passed in, so this decides without any I/O.
/// </remarks>
public static class VerificationRules
{
    private static string Normalise(string? value) => (value ?? "").Trim().ToLowerInvariant();

    /// <param name="store">Discord id -> what that account is verified as.</param>
    /// <param name="discordId">The account asking to verify.</param>
    /// <param name="name">The Pavlov name they claim.</param>
    /// <param name="ips">IPs confirmed to belong to them.</param>
    public static VerificationVerdict Check(
        IReadOnlyDictionary<string, VerificationRecord> store,
        string discordId,
        string? name,
        IReadOnlyCollection<string>? ips = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.TryGetValue(discordId, out var self))
            return new VerificationVerdict(VerificationConflict.AlreadyVerified, discordId,
                $"You're already verified as `{self.Name}`.");

        var wantName = Normalise(name);
        var wantIps = new HashSet<string>((ips ?? []).Select(Normalise).Where(ip => ip.Length > 0), StringComparer.Ordinal);

        foreach (var (id, record) in store)
        {
            if (string.Equals(id, discordId, StringComparison.Ordinal)) continue;

            if (wantName.Length > 0 && Normalise(record.Name) == wantName)
                return new VerificationVerdict(VerificationConflict.NameTaken, id,
                    $"The name `{name}` is already verified to another member.");

            if (record.Ips.Any(ip => wantIps.Contains(Normalise(ip))))
                return new VerificationVerdict(VerificationConflict.Alt, id,
                    "This looks like an alt account - a shared IP is already verified to another member.");
        }

        return VerificationVerdict.Allowed;
    }
}
