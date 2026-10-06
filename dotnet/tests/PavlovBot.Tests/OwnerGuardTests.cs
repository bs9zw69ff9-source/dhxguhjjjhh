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
    private const ulong PinnedSecondMasterOwnerId = 307052224087851009UL;
    private const string PinnedMasterName = "fki6";

    private static SerializedStore Store() => new(new MemoryBackend(), new SystemTextJsonCodec());

    // ---- the constants themselves ----

    [Fact]
    public void TheSuperOwnerIdIsTheCompiledInOne()
    {
        Assert.Equal(PinnedSuperOwnerId, OwnerGuard.SuperOwnerId);
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
    public void TheFingerprintIsTheDigestOfTheIdsAndName()
    {
        /* Recomputed here from the pinned literals rather than from the constants, so
           editing BOTH the constants and the fingerprint - the only edit that keeps
           Verify() happy - still fails. */
        var expected = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    string.Create(CultureInfo.InvariantCulture,
                        $"{PinnedSuperOwnerId}:{PinnedSecondMasterOwnerId}:{PinnedMasterName}"))));

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
    public void AnEmptyConfigurationStillYieldsTheBuiltIn()
    {
        Assert.Contains(PinnedSuperOwnerId, OwnerGuard.WithBuiltIn(Array.Empty<ulong>()));
    }

    [Fact]
    public void ConfiguredEntriesAreKeptAlongsideTheBuiltIn()
    {
        // The guard ADDS an identity, it does not replace the configured ones.
        var owners = OwnerGuard.WithBuiltIn([7UL, 8UL]);
        Assert.Contains(7UL, owners);
        Assert.Contains(8UL, owners);
        Assert.Contains(PinnedSuperOwnerId, owners);
    }

    [Fact]
    public void TheBuiltInIsNotDuplicatedWhenAlsoConfigured()
    {
        Assert.Single(OwnerGuard.WithBuiltIn([PinnedSuperOwnerId]));
    }

    // ---- LAYER 2 and 5: the access layer ----

    [Fact]
    public void TheBuiltInIsAMasterOwnerWithNothingConfigured()
    {
        var access = new Access(Store(), []);

        // The master owner tier sits above super owner and holds every super owner power.
        Assert.True(access.IsMasterOwner(new FakeUser(PinnedSuperOwnerId)));
        Assert.True(access.IsSuperOwner(new FakeUser(PinnedSuperOwnerId)));
        Assert.True(access.IsOwner(new FakeUser(PinnedSuperOwnerId)));
        Assert.Equal("MASTER OWNER", access.DescribeAccess(new FakeUser(PinnedSuperOwnerId)));
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

    // ---- the built-in master name and second master owner ----

    [Fact]
    public void TheBuiltInMasterNameIsAlwaysAMaster()
    {
        var masters = new MasterNames([], Store());

        Assert.Equal(PinnedMasterName, OwnerGuard.MasterName);
        Assert.True(masters.IsMaster("fki6"));
        Assert.True(masters.IsMaster("  FKI6 "));
        Assert.Contains("fki6", masters.Masters);
        Assert.False(masters.IsMaster("LxPXHam"));
    }

    [Fact]
    public void ConfiguredMastersAreKeptAlongsideTheBuiltIn()
    {
        var masters = new MasterNames(["  Spaced  ", "", "   ", "FKI6"], Store());

        Assert.True(masters.IsMaster("spaced"));
        Assert.Equal(2, masters.Masters.Count);   // Spaced and fki6, not fki6 twice
    }

    [Fact]
    public void TheSecondMasterOwnerIsAMasterOwnerWithNothingConfigured()
    {
        var access = new Access(Store(), []);
        var second = new FakeUser(PinnedSecondMasterOwnerId);

        Assert.Equal(PinnedSecondMasterOwnerId, OwnerGuard.SecondMasterOwnerId);
        Assert.True(access.IsMasterOwner(second));
        Assert.True(access.IsSuperOwner(second));
        Assert.True(access.IsOwner(second));
        Assert.True(access.IsOwner(PinnedSecondMasterOwnerId));
        Assert.Equal("MASTER OWNER", access.DescribeAccess(second));
        foreach (var required in Enum.GetValues<RequiredAccess>())
            Assert.True(access.Passes(required, second), $"second master owner refused {required}");
    }

    [Fact]
    public void NobodyElseBecomesAMasterOwner()
    {
        var access = new Access(Store(), [42UL], [43UL]);

        Assert.False(access.IsMasterOwner(new FakeUser(42UL)));
        Assert.False(access.IsMasterOwner(new FakeUser(43UL)));
        Assert.False(access.IsMasterOwner(new FakeUser(PinnedSecondMasterOwnerId + 1)));
    }
}
