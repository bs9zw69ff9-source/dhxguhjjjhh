using System.Security.Cryptography;
using System.Text;

namespace PavlovBot.Core.Security;

/// <summary>Raised when the built-in owner protections have been altered.</summary>
public sealed class OwnerGuardException(string message) : Exception(message);

/// <summary>
/// The owner identity that is compiled in rather than configured.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. Everything else about who holds power in this bot comes from
/// <c>.env</c> - <c>OWNER_IDS</c>, <c>SUPER_OWNER_IDS</c>, <c>MASTER_NAMES</c> - which means
/// anybody who can edit that file can remove the owner from their own bot, and anybody who
/// can edit it can also stop the bot from protecting the owner's in-game account from an
/// automatic ban. This pins one identity that does not depend on that file.
///
/// WHAT IT IS NOT. It is not tamper-PROOF and nothing in a source tree can be. Anyone able
/// to edit and rebuild this project can delete every check here, and the fingerprint below
/// can be recomputed by anyone who reads this comment. What the layering buys is that
/// tampering has to be DELIBERATE and has to be done in several places at once - a casual
/// edit, a merge that drops a line, or somebody removing an id from a list they did not
/// understand all produce a bot that refuses to start rather than one that quietly runs
/// without its owner. The real protections are filesystem permissions and who has root.
///
/// FAIL CLOSED. Every check here throws rather than warning. A bot that starts with its
/// owner protections removed is the exact outcome this exists to prevent, so "refuse to
/// start and say why" is the correct behaviour even though it is inconvenient.
/// </remarks>
public static class OwnerGuard
{
    /// <summary>The Discord account that always holds super-owner tier.</summary>
    public const ulong SuperOwnerId = 1014251293159731310UL;

    /// <summary>The in-game account that is never auto-banned.</summary>
    public const string MasterName = "LxPXHam";

    /// <summary>
    /// SHA-256 over the two values above, so EDITING one is caught as well as removing it.
    /// </summary>
    /// <remarks>
    /// The obvious attack on a hard-coded id is not deleting the check, it is changing the
    /// number to somebody else's - which leaves every check in place, passing, and pointing
    /// at the wrong person. Comparing against a digest means that swap has to be made in two
    /// places whose relationship is not obvious from either one alone.
    ///
    /// Not a secret and not pretending to be. It is a tripwire.
    /// </remarks>
    public const string Fingerprint = "4ab75c54ee5b5e473497e7ed88ce17d27a7917cebe26a77e3d83a848e4c7c583";

    /// <summary>The digest of whatever the constants currently say.</summary>
    public static string Compute() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{SuperOwnerId}:{MasterName}")));

    /// <summary>
    /// LAYER 1 - the constants still say what they were compiled to say.
    /// </summary>
    /// <remarks>
    /// Called at startup, and again on a timer by the supervisor, so patching the startup
    /// path alone is not enough to keep a tampered build running.
    /// </remarks>
    public static void Verify()
    {
        if (SuperOwnerId == 0)
            throw new OwnerGuardException("the built-in super owner id has been removed");

        if (string.IsNullOrWhiteSpace(MasterName))
            throw new OwnerGuardException("the built-in master name has been removed");

        var actual = Compute();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(Fingerprint)))
        {
            throw new OwnerGuardException(
                "the built-in owner identity does not match its fingerprint - it has been edited. " +
                $"Expected {Fingerprint}, computed {actual}. The bot will not start.");
        }
    }

    /// <summary>
    /// LAYER 2 - the compiled-in super owner is present in a resolved owner set.
    /// </summary>
    /// <remarks>
    /// Checked against what the access layer actually ended up with, not against
    /// configuration. Removing the union that adds it, or filtering it back out downstream,
    /// both fail here - which is the point of asserting the RESULT rather than the intent.
    /// </remarks>
    public static void VerifyOwners(IReadOnlySet<ulong> superOwners)
    {
        ArgumentNullException.ThrowIfNull(superOwners);
        Verify();

        if (!superOwners.Contains(SuperOwnerId))
        {
            throw new OwnerGuardException(
                $"the built-in super owner {SuperOwnerId} is missing from the resolved owner set. " +
                "Something is filtering it out after it is added. The bot will not start.");
        }
    }

    /// <summary>
    /// LAYER 3 - the compiled-in master name is present in a resolved master set.
    /// </summary>
    /// <remarks>
    /// Separate from the owner check because they protect different things and can be broken
    /// independently: removing the Discord id costs the owner their commands, removing the
    /// in-game name lets the bot's own automatic ban system ban them off their own server.
    /// The second is the one nobody would notice until it happened.
    /// </remarks>
    public static void VerifyMasters(IReadOnlySet<string> masters)
    {
        ArgumentNullException.ThrowIfNull(masters);
        Verify();

        if (!masters.Contains(MasterName))
        {
            throw new OwnerGuardException(
                $"the built-in master name \"{MasterName}\" is missing from the resolved master list. " +
                "The owner's in-game account would not be protected from an automatic ban. " +
                "The bot will not start.");
        }
    }

    /// <summary>Add the compiled-in id to a configured set. Used where owners are resolved.</summary>
    public static IReadOnlySet<ulong> WithBuiltIn(IEnumerable<ulong> configured)
    {
        var set = new HashSet<ulong>(configured ?? []) { SuperOwnerId };
        VerifyOwners(set);
        return set;
    }

    /// <summary>Add the compiled-in name to a configured set. Used where masters are resolved.</summary>
    public static IReadOnlySet<string> WithBuiltIn(IEnumerable<string> configured)
    {
        var set = new HashSet<string>(
            (configured ?? []).Select(n => n.Trim()).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase)
        { MasterName };

        VerifyMasters(set);
        return set;
    }
}
