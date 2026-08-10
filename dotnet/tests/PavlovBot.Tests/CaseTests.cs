using Microsoft.Extensions.Logging.Abstractions;
using PavlovBot.Core.Cases;
using PavlovBot.Core.Data;
using PavlovBot.Host.Cases;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;
using Xunit;

namespace PavlovBot.Tests;

/// <summary>
/// Moderation cases: the record of an investigation, and whether it can be trusted.
/// </summary>
/// <remarks>
/// THE EVIDENCE CHAIN IS THE PART WORTH TESTING HARDEST. Everything else here is bookkeeping;
/// the chain is the claim that a case cannot be quietly rewritten, and a tamper check that
/// does not actually catch tampering is worse than none - it invites people to trust a record
/// that nothing is protecting.
///
/// The property is TAMPER-EVIDENT, not tamper-proof, and the tests say so. Anyone who can
/// reach bot.db can edit it; what the chain guarantees is that the edit shows.
/// </remarks>
public class CaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"pavlov-cases-{Guid.NewGuid():N}");
    private readonly SerializedStore _store;
    private readonly CaseService _cases;

    public CaseTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new SerializedStore(new FileKeyValueBackend(_directory), new SystemTextJsonCodec());
        _cases = new CaseService(_store, new AuditLog(_store), NullLogger<CaseService>.Instance);
    }

    private Task<CaseResult> Open(string subject = "Evader", string reason = "possible ban evasion") =>
        _cases.OpenAsync(subject, reason, "ModOne");

    // ---- opening ----

    [Fact]
    public async Task OpeningACaseNumbersItAndRecordsWho()
    {
        var result = await Open();

        Assert.True(result.Ok);
        Assert.Equal(1, result.Case!.Id);
        Assert.Equal("Evader", result.Case.Subject);
        Assert.Equal("ModOne", result.Case.OpenedBy);
        Assert.Equal(CaseStatus.Open, result.Case.Status);
    }

    /// <summary>
    /// Ids come from the highest so far, never from the count.
    /// </summary>
    /// <remarks>
    /// Count+1 reuses a number the moment any case is removed, and two cases sharing an id
    /// makes every written-down reference to "case 3" ambiguous - including ones in Discord
    /// channels months old, which cannot be corrected.
    /// </remarks>
    [Fact]
    public async Task IdsAreNeverReusedEvenAfterARemoval()
    {
        await Open();
        await Open();
        var third = await Open();
        Assert.Equal(3, third.Case!.Id);

        // Simulate a case being removed by hand, then open another.
        await _store.UpdateAsync<List<ModerationCase>>(Datasets.Cases, [], list =>
        {
            list.RemoveAll(c => c.Id == 2);
            return list;
        });

        var fourth = await Open();

        Assert.Equal(4, fourth.Case!.Id);
    }

    [Fact]
    public async Task SeededEvidenceIsChainedFromTheStart()
    {
        var result = await _cases.OpenAsync("Evader", "evasion", "ModOne",
        [
            (EvidenceKind.Detection, "risk 82/100", "risk-engine"),
            (EvidenceKind.Detection, "shares an address with a banned player", "risk-engine"),
        ]);

        Assert.Equal(2, result.Case!.Evidence.Count);
        Assert.Equal([1, 2], result.Case.Evidence.Select(e => e.Sequence));
        Assert.True(CaseService.Verify(result.Case).Intact);
    }

    // ---- the evidence chain ----

    [Fact]
    public async Task EvidenceAppendsAndTheChainStaysIntact()
    {
        var opened = await Open();

        await _cases.AddEvidenceAsync(opened.Case!.Id, EvidenceKind.Statement, "I saw them wall", "ModTwo", "ModTwo");
        await _cases.AddEvidenceAsync(opened.Case.Id, EvidenceKind.Link, "https://example.invalid/clip", "ModTwo", "ModTwo");

        var subject = _cases.Find(opened.Case.Id);

        Assert.Equal(2, subject!.Evidence.Count);
        Assert.True(CaseService.Verify(subject).Intact);
    }

    /// <summary>
    /// EDITING AN ENTRY BREAKS THE CHAIN, and the break names the entry.
    /// </summary>
    /// <remarks>
    /// The central claim. This edits the stored case directly, which is what somebody with
    /// database access would do - the bot itself offers no way to alter evidence.
    /// </remarks>
    [Fact]
    public async Task EditingEvidenceIsDetectedAndLocated()
    {
        var opened = await Open();
        await _cases.AddEvidenceAsync(opened.Case!.Id, EvidenceKind.Statement, "they were cheating", "ModTwo", "ModTwo");
        await _cases.AddEvidenceAsync(opened.Case.Id, EvidenceKind.Statement, "and again the next day", "ModTwo", "ModTwo");

        await Tamper(opened.Case.Id, evidence =>
        {
            var first = evidence[0];
            evidence[0] = first with { Content = "they were definitely cheating" };
            return evidence;
        });

        var verdict = CaseService.Verify(_cases.Find(opened.Case.Id));

        Assert.False(verdict.Intact);
        Assert.Equal(1, verdict.BrokenAt);
    }

    /// <summary>Deleting an entry is caught too, which a per-entry checksum would miss.</summary>
    /// <remarks>
    /// The reason the hashes CHAIN rather than standing alone. Independent checksums only
    /// catch an edit that forgot to recompute one; removing an inconvenient entry entirely
    /// would leave every remaining checksum valid.
    /// </remarks>
    [Fact]
    public async Task DeletingEvidenceIsDetected()
    {
        var opened = await Open();
        for (var i = 0; i < 3; i++)
            await _cases.AddEvidenceAsync(opened.Case!.Id, EvidenceKind.Statement, $"entry {i}", "ModTwo", "ModTwo");

        await Tamper(opened.Case!.Id, evidence => { evidence.RemoveAt(1); return evidence; });

        Assert.False(CaseService.Verify(_cases.Find(opened.Case.Id)).Intact);
    }

    /// <summary>Reordering is caught, because the sequence is inside the hash.</summary>
    [Fact]
    public async Task ReorderingEvidenceIsDetected()
    {
        var opened = await Open();
        await _cases.AddEvidenceAsync(opened.Case!.Id, EvidenceKind.Statement, "first", "ModTwo", "ModTwo");
        await _cases.AddEvidenceAsync(opened.Case.Id, EvidenceKind.Statement, "second", "ModTwo", "ModTwo");

        await Tamper(opened.Case.Id, evidence => [evidence[1], evidence[0]]);

        Assert.False(CaseService.Verify(_cases.Find(opened.Case.Id)).Intact);
    }

    /// <summary>
    /// Re-hashing an edited entry still breaks, because the NEXT entry covers the old hash.
    /// </summary>
    /// <remarks>
    /// The most determined attack the design defends against: somebody who edits an entry AND
    /// recomputes its hash correctly. The chain still breaks at the following entry, which is
    /// the entire reason each hash covers the previous one.
    /// </remarks>
    [Fact]
    public async Task RecomputingTheHashOfAnEditedEntryStillBreaksTheChain()
    {
        var opened = await Open();
        await _cases.AddEvidenceAsync(opened.Case!.Id, EvidenceKind.Statement, "original", "ModTwo", "ModTwo");
        await _cases.AddEvidenceAsync(opened.Case.Id, EvidenceKind.Statement, "later", "ModTwo", "ModTwo");

        await Tamper(opened.Case.Id, evidence =>
        {
            var first = evidence[0] with { Content = "rewritten" };

            // Rehash it correctly, as somebody who understood the scheme would.
            evidence[0] = first with
            {
                Hash = EvidenceChain.HashFor(null, first.Sequence, first.Kind, first.Content, first.Source, first.AddedBy, first.At),
            };
            return evidence;
        });

        var verdict = CaseService.Verify(_cases.Find(opened.Case.Id));

        Assert.False(verdict.Intact);
        Assert.Equal(2, verdict.BrokenAt);   // entry 1 now verifies; entry 2 no longer follows it
    }

    /// <summary>Two entries with swapped field boundaries do not collide.</summary>
    /// <remarks>
    /// Why the hashed fields carry a delimiter. Without one, ("ab","c") and ("a","bc") hash
    /// identically, so a swap between two entries would verify clean.
    /// </remarks>
    [Fact]
    public void FieldBoundariesAreNotAmbiguous()
    {
        var at = DateTimeOffset.UnixEpoch;

        var a = EvidenceChain.HashFor(null, 1, EvidenceKind.Statement, "ab", "c", "mod", at);
        var b = EvidenceChain.HashFor(null, 1, EvidenceKind.Statement, "a", "bc", "mod", at);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AnEmptyChainVerifies()
    {
        Assert.True(EvidenceChain.Verify([]).Intact);
    }

    // ---- transitions ----

    [Fact]
    public async Task AStatusChangeIsRecorded()
    {
        var opened = await Open();

        var result = await _cases.SetStatusAsync(opened.Case!.Id, CaseStatus.UnderReview, null, "ModOne");

        Assert.True(result.Ok);
        Assert.Equal(CaseStatus.UnderReview, result.Case!.Status);
        Assert.Null(result.Case.ClosedAt);
    }

    [Fact]
    public async Task ClosingRecordsTheFindingAndTheTime()
    {
        var opened = await Open();

        var result = await _cases.SetStatusAsync(opened.Case!.Id, CaseStatus.Resolved, "banned for evasion", "ModOne");

        Assert.True(result.Case!.Closed);
        Assert.Equal("banned for evasion", result.Case.Resolution);
        Assert.NotNull(result.Case.ClosedAt);
    }

    /// <summary>
    /// A closed case cannot flip straight to the opposite verdict.
    /// </summary>
    /// <remarks>
    /// Resolved and Dismissed are contradictory findings. Going between them without passing
    /// through Open would rewrite the verdict with no visible step where anybody reconsidered.
    /// </remarks>
    [Fact]
    public async Task AResolvedCaseCannotBecomeDismissedWithoutBeingReopened()
    {
        var opened = await Open();
        await _cases.SetStatusAsync(opened.Case!.Id, CaseStatus.Resolved, "guilty", "ModOne");

        var flip = await _cases.SetStatusAsync(opened.Case.Id, CaseStatus.Dismissed, "actually not", "ModTwo");

        Assert.False(flip.Ok);
        Assert.Contains("reopened", flip.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reopening clears the finding, so the case does not describe two states.</summary>
    [Fact]
    public async Task ReopeningClearsTheOldFinding()
    {
        var opened = await Open();
        await _cases.SetStatusAsync(opened.Case!.Id, CaseStatus.Resolved, "guilty", "ModOne");

        var reopened = await _cases.SetStatusAsync(opened.Case.Id, CaseStatus.Open, null, "ModTwo");

        Assert.True(reopened.Ok);
        Assert.Null(reopened.Case!.Resolution);
        Assert.Null(reopened.Case.ClosedAt);
        Assert.False(reopened.Case.Closed);
    }

    /// <summary>Reopening preserves everything the case already held.</summary>
    /// <remarks>
    /// The control on the test above: clearing too much would satisfy it and destroy the
    /// record, which is the one thing a case exists to keep.
    /// </remarks>
    [Fact]
    public async Task ReopeningKeepsTheEvidenceAndNotes()
    {
        var opened = await Open();
        await _cases.AddEvidenceAsync(opened.Case!.Id, EvidenceKind.Statement, "they walled", "ModTwo", "ModTwo");
        await _cases.AddNoteAsync(opened.Case.Id, "spoke to them", "ModTwo");
        await _cases.SetStatusAsync(opened.Case.Id, CaseStatus.Dismissed, "no evidence", "ModOne");

        var reopened = await _cases.SetStatusAsync(opened.Case.Id, CaseStatus.Open, null, "ModTwo");

        Assert.Single(reopened.Case!.Evidence);
        Assert.Single(reopened.Case.Notes);
        Assert.True(CaseService.Verify(reopened.Case).Intact);
    }

    [Fact]
    public async Task ActingOnACaseThatDoesNotExistFailsCleanly()
    {
        var result = await _cases.AddNoteAsync(999, "hello", "ModOne");

        Assert.False(result.Ok);
        Assert.Contains("999", result.Error!, StringComparison.Ordinal);
    }

    // ---- listing ----

    [Fact]
    public async Task OpenListsExcludeClosedCases()
    {
        var first = await Open("Alice");
        await Open("Bob");
        await _cases.SetStatusAsync(first.Case!.Id, CaseStatus.Resolved, "warned", "ModOne");

        Assert.Single(_cases.Open());
        Assert.Equal(2, _cases.All().Count);
        Assert.Single(_cases.For("bob"));   // case-insensitive, like every other name lookup
    }

    /// <summary>Edit the stored case directly, as somebody with database access would.</summary>
    private Task Tamper(int id, Func<List<CaseEvidence>, List<CaseEvidence>> edit) =>
        _store.UpdateAsync<List<ModerationCase>>(Datasets.Cases, [], list =>
        {
            var index = list.FindIndex(c => c.Id == id);
            list[index] = list[index] with { Evidence = edit([.. list[index].Evidence]) };
            return list;
        });

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
