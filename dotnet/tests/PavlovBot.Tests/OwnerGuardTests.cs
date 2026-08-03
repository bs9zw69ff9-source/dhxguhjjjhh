using System.Globalization;
using PavlovBot.Core.Data;
using PavlovBot.Core.Security;
using PavlovBot.Host.Discord;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// LAYER 6 - the guard itself, pinned.
/// </summary>
/// <remarks>
/// The other five layers all live in code that somebody editing the bot could delete in one
/// commit. These make the deletion NOISY: the literals below are written out longhand rather
/// than read from <see cref="OwnerGuard"/>, so
///
///   * REMOVING a constant fails the BUILD (this file stops compiling), and
///   * CHANGING a constant fails a TEST, naming what changed and to what.
///
/// A test that asserted <c>OwnerGuard.SuperOwnerId == OwnerGuard.SuperOwnerId</c> would pass
/// forever no matter what the value became, which is the trap this file exists to avoid.
///
/// None of this makes the bot tamper-proof - see the remarks on <see cref="OwnerGuard"/>.
/// Anyone who can rebuild the project can delete this file too. It raises the number of
/// places that have to be changed together, and it means a careless edit or a bad merge is
/// caught by CI instead of by the owner discovering they have been locked out.
/// </remarks>
public class OwnerGuardTests
{
    /* Written out rather than referenced. If either changes, THIS is the assertion that
       names the old value and the new one. */
    private const ulong PinnedSuperOwnerId = 1014251293159731310UL;
    private const string PinnedMasterName = "LxPXHam";

    private static SerializedStore Store() => new(new MemoryBackend(), new SystemTextJsonCodec());

    // ---- the constants themselves ----

    [Fact]
    public void TheSuperOwnerIdIsTheCompiledInOne()
    {
        Assert.Equal(PinnedSuperOwnerId, OwnerGuard.SuperOwnerId);
    }

    [Fact]
    public void TheMasterNameIsTheCompiledInOne()
    {
        Assert.Equal(PinnedMasterName, OwnerGuard.MasterName);
    }

    [Fact]
    public void TheFingerprintMatchesTheConstants()
    {
        // The tripwire for the swap that leaves every check in place but pointing at
        // somebody else. Both halves have to be edited together or this fails.
        Assert.Equal(OwnerGuard.Fingerprint, OwnerGuard.Compute());
        OwnerGuard.Verify();
    }

    [Fact]
    public void TheFingerprintIsTheDigestOfIdAndName()
    {
        /* Recomputed here from the pinned literals rather than from the constants, so
           editing BOTH the constants and the fingerprint - the only edit that keeps
           Verify() happy - still fails. */
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{PinnedSuperOwnerId.ToString(CultureInfo.InvariantCulture)}:{PinnedMasterName}")));

        Assert.Equal(OwnerGuard.Fingerprint, expected);
    }

    // ---- the set assertions fail closed ----

    [Fact]
    public void AnOwnerSetWithoutTheBuiltInIsRejected()
    {
        var ex = Assert.Throws<OwnerGuardException>(
            () => OwnerGuard.VerifyOwners(new HashSet<ulong> { 42UL }));

        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AMasterSetWithoutTheBuiltInIsRejected()
    {
        var ex = Assert.Throws<OwnerGuardException>(
            () => OwnerGuard.VerifyMasters(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "someone" }));

        Assert.Contains(PinnedMasterName, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyConfigurationStillYieldsTheBuiltIn()
    {
        Assert.Contains(PinnedSuperOwnerId, OwnerGuard.WithBuiltIn(Array.Empty<ulong>()));
        Assert.Contains(PinnedMasterName, OwnerGuard.WithBuiltIn(Array.Empty<string>()));
    }

    [Fact]
    public void ConfiguredEntriesAreKeptAlongsideTheBuiltIn()
    {
        // The guard ADDS an identity, it does not replace the configured ones.
        var owners = OwnerGuard.WithBuiltIn([7UL, 8UL]);
        Assert.Contains(7UL, owners);
        Assert.Contains(8UL, owners);
        Assert.Contains(PinnedSuperOwnerId, owners);

        var masters = OwnerGuard.WithBuiltIn(["  spaced  ", "", "   "]);
        Assert.Contains("spaced", masters);
        Assert.Contains(PinnedMasterName, masters);
        Assert.DoesNotContain("", masters);
    }

    [Fact]
    public void TheBuiltInIsNotDuplicatedWhenAlsoConfigured()
    {
        Assert.Single(OwnerGuard.WithBuiltIn([PinnedSuperOwnerId]));

        // Case-insensitively, because MASTER_NAMES is matched that way everywhere else.
        Assert.Single(OwnerGuard.WithBuiltIn([PinnedMasterName.ToUpperInvariant()]));
    }

    // ---- LAYER 2 and 5: the access layer ----

    [Fact]
    public void TheBuiltInIsASuperOwnerWithNothingConfigured()
    {
        var access = new Access(Store(), []);

        Assert.True(access.IsSuperOwner(new FakeUser(PinnedSuperOwnerId)));
        Assert.True(access.IsOwner(new FakeUser(PinnedSuperOwnerId)));
        Assert.Equal("SUPER OWNER", access.DescribeAccess(new FakeUser(PinnedSuperOwnerId)));
    }

    [Fact]
    public void TheBuiltInPassesEveryGateWithNoGuildMember()
    {
        // Same shape as the owner-lockout bug: no roles resolved, id-based authority only.
        var access = new Access(Store(), []);
        var owner = new FakeUser(PinnedSuperOwnerId);

        foreach (var required in Enum.GetValues<RequiredAccess>())
            Assert.True(access.Passes(required, owner), $"built-in owner refused {required}");
    }

    [Fact]
    public void AddingTheBuiltInDoesNotPromoteAnybodyElse()
    {
        // The guard must widen authority by exactly one id and no more.
        var access = new Access(Store(), []);

        Assert.False(access.IsSuperOwner(new FakeMember(PinnedSuperOwnerId + 1)));
        Assert.False(access.IsOwner(new FakeMember(PinnedSuperOwnerId - 1)));
        Assert.False(access.IsOwner(new FakeMember(0UL)));
    }

    // ---- LAYER 3 and 5: the auto-ban protection ----

    [Fact]
    public void TheBuiltInIsAMasterWithNothingConfigured()
    {
        var masters = new MasterNames([], Store());

        Assert.True(masters.IsMaster(PinnedMasterName));
        Assert.True(masters.IsMaster(PinnedMasterName.ToUpperInvariant()));
        Assert.True(masters.IsMaster($"  {PinnedMasterName}  "));
        Assert.Contains(PinnedMasterName, masters.Masters);
    }

    [Fact]
    public void AddingTheBuiltInDoesNotProtectAnybodyElse()
    {
        var masters = new MasterNames([], Store());

        Assert.False(masters.IsMaster("LxPXHam2"));
        Assert.False(masters.IsMaster("xPXHam"));
        Assert.False(masters.IsMaster(""));
    }
}
