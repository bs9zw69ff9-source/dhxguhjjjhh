using Microsoft.Extensions.Logging;
using PavlovBot.Core.Cases;
using PavlovBot.Core.Data;
using PavlovBot.Core.Events;
using PavlovBot.Core.Text;
using PavlovBot.Host.Moderation;
using PavlovBot.Host.Storage;

namespace PavlovBot.Host.Cases;

/// <param name="Case">The case as it now stands, or as it was found.</param>
/// <param name="Error">Why nothing changed, when nothing did.</param>
public readonly record struct CaseResult(ModerationCase? Case, string? Error)
{
    public bool Ok => Error is null && Case is not null;

    public static CaseResult Failed(string error) => new(null, error);
    public static CaseResult Success(ModerationCase updated) => new(updated, null);
}

/// <summary>
/// Opening, updating and closing moderation cases.
/// </summary>
/// <remarks>
/// WHY THE DOCUMENT STORE AND NOT THE EVENT TABLE. Cases are bounded - a busy server opens a
/// handful a week, not thousands a day - and every query is "give me this case" or "list the
/// open ones". That is exactly the shape SerializedStore is good at, and the argument for
/// moving the timeline out of it does not apply here. Using the table for both would be
/// consistency for its own sake at the cost of a schema nothing needs.
///
/// A CASE ENFORCES NOTHING. Opening one restricts nobody, closing one unbans nobody. If a
/// case applied punishments then opening a speculative one would have consequences, and staff
/// would stop opening them - which would cost the record that is the entire point.
///
/// EVERY MUTATION IS A READ-MODIFY-WRITE THROUGH UpdateAsync, so two moderators adding
/// evidence to one case at the same time cannot lose an entry. The evidence chain would
/// detect that loss afterwards, but detecting it is a worse outcome than not causing it.
/// </remarks>
public sealed class CaseService(
    SerializedStore store,
    AuditLog audit,
    ILogger<CaseService> logger,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Longest a piece of evidence or a note may be.</summary>
    /// <remarks>
    /// Generous, because a statement is the whole point of a case and truncating somebody's
    /// account of what happened defeats it. Bounded at all because the case list is read as
    /// one document and an unbounded field is an unbounded parse.
    /// </remarks>
    public const int MaxText = 1500;

    private List<ModerationCase> Load() => store.Read<List<ModerationCase>>(Datasets.Cases, []);

    /// <summary>Every case, newest first.</summary>
    public IReadOnlyList<ModerationCase> All() => [.. Load().Where(c => c is not null).OrderByDescending(c => c.Id)];

    /// <summary>One case by number, or null.</summary>
    public ModerationCase? Find(int id) => Load().FirstOrDefault(c => c is not null && c.Id == id);

    /// <summary>Cases about one player, newest first.</summary>
    public IReadOnlyList<ModerationCase> For(string subject) =>
        [.. All().Where(c => string.Equals(c.Subject, subject.Trim(), StringComparison.OrdinalIgnoreCase))];

    /// <summary>Cases that have not reached an ending.</summary>
    public IReadOnlyList<ModerationCase> Open() => [.. All().Where(c => !c.Closed)];

    /// <summary>
    /// Open a case, with any evidence that is already known.
    /// </summary>
    /// <remarks>
    /// SEEDED EVIDENCE IS THE POINT. A case opened from a risk assessment should already carry
    /// the signals that prompted it - asking a moderator to retype what the bot just told them
    /// is how cases end up with no evidence on them at all.
    /// </remarks>
    public async Task<CaseResult> OpenAsync(
        string subject,
        string reason,
        string openedBy,
        IEnumerable<(EvidenceKind Kind, string Content, string Source)>? seed = null,
        CancellationToken ct = default)
    {
        var player = Sanitize.Id(subject ?? "");
        if (player.Length == 0) return CaseResult.Failed("that name has nothing usable in it");

        var now = _time.GetUtcNow();
        ModerationCase? created = null;

        await store.UpdateAsync<List<ModerationCase>>(Datasets.Cases, [], cases =>
        {
            /* THE NEXT ID IS MAX+1, NOT COUNT+1. Count reuses a number the moment a case is
               ever removed, and two cases sharing an id would make every reference to "case
               1842" ambiguous - including references written down in a Discord channel months
               ago, which cannot be corrected. */
            var next = cases.Count == 0 ? 1 : cases.Where(c => c is not null).Max(c => c.Id) + 1;

            var evidence = new List<CaseEvidence>();
            foreach (var (kind, content, source) in seed ?? [])
                evidence.Add(EvidenceChain.Next(evidence, kind, Clamp(content), Clamp(source), openedBy, now));

            created = new ModerationCase
            {
                Id = next,
                Subject = player,
                Reason = Clamp(reason),
                OpenedBy = openedBy,
                OpenedAt = now,
                Status = CaseStatus.Open,
                Evidence = evidence,
            };

            cases.Add(created);
            return cases;
        }, ct).ConfigureAwait(false);

        if (created is null) return CaseResult.Failed("the case could not be written");

        await RecordAsync("case-open", openedBy, player, $"case #{created.Id}: {created.Reason}", ct).ConfigureAwait(false);
        logger.LogInformation("case opened | #{Id} | subject=\"{Subject}\" | by={By}", created.Id, player, openedBy);

        return CaseResult.Success(created);
    }

    /// <summary>Append evidence. Never replaces anything already there.</summary>
    public Task<CaseResult> AddEvidenceAsync(
        int id, EvidenceKind kind, string content, string source, string addedBy, CancellationToken ct = default) =>
        MutateAsync(id, addedBy, c => c with
        {
            Evidence = [.. c.Evidence, EvidenceChain.Next(c.Evidence, kind, Clamp(content), Clamp(source), addedBy, _time.GetUtcNow())],
        }, "case-evidence", $"evidence added to case #{id}", ct);

    /// <summary>Append a note.</summary>
    public Task<CaseResult> AddNoteAsync(int id, string text, string author, CancellationToken ct = default) =>
        MutateAsync(id, author, c => c with
        {
            Notes = [.. c.Notes, new CaseNote(author, Clamp(text), _time.GetUtcNow())],
        }, "case-note", $"note added to case #{id}", ct);

    /// <summary>Hand the case to somebody.</summary>
    public Task<CaseResult> AssignAsync(int id, string assignee, string by, CancellationToken ct = default) =>
        MutateAsync(id, by, c => c with { AssignedTo = assignee },
            "case-assign", $"case #{id} assigned to {assignee}", ct);

    /// <summary>
    /// Move a case to a new status, subject to the transition rules.
    /// </summary>
    /// <remarks>
    /// ONE METHOD FOR EVERY STATUS CHANGE, so resolve, dismiss, escalate and reopen cannot
    /// drift apart. The rules live in <see cref="CaseTransitions"/> and are consulted here
    /// once.
    /// </remarks>
    public async Task<CaseResult> SetStatusAsync(
        int id, CaseStatus status, string? resolution, string by, CancellationToken ct = default)
    {
        var current = Find(id);
        if (current is null) return CaseResult.Failed($"there is no case #{id}");

        if (!CaseTransitions.Allows(current.Status, status))
            return CaseResult.Failed(CaseTransitions.Explain(current.Status, status));

        var now = _time.GetUtcNow();
        var closing = status is CaseStatus.Resolved or CaseStatus.Dismissed;

        return await MutateAsync(id, by, c => c with
        {
            Status = status,
            /* CLEARED ON REOPEN. A reopened case still showing its old resolution and closing
               time describes two contradictory states at once, and whichever a reader believes
               is a coin toss. */
            ClosedAt = closing ? now : null,
            Resolution = closing ? Clamp(resolution ?? "no reason recorded") : null,
        }, $"case-{status.ToString().ToLowerInvariant()}", $"case #{id} -> {CaseTransitions.Name(status)}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Whether a case's evidence chain still verifies.</summary>
    /// <remarks>
    /// Checked on every display rather than on a timer. A tamper check nobody runs is not a
    /// control, and the moment somebody reads a case is when they need to know.
    /// </remarks>
    public static EvidenceChain.Verdict Verify(ModerationCase? subject) =>
        subject is null ? EvidenceChain.Verdict.Ok : EvidenceChain.Verify(subject.Evidence);

    private async Task<CaseResult> MutateAsync(
        int id, string by, Func<ModerationCase, ModerationCase> change, string action, string detail, CancellationToken ct)
    {
        ModerationCase? updated = null;

        var result = await store.UpdateAsync<List<ModerationCase>>(Datasets.Cases, [], cases =>
        {
            var index = cases.FindIndex(c => c is not null && c.Id == id);
            if (index < 0) return null;   // veto: no such case, nothing written

            updated = change(cases[index]);
            cases[index] = updated;
            return cases;
        }, ct).ConfigureAwait(false);

        if (!result.Ok || updated is null) return CaseResult.Failed($"there is no case #{id}");

        await RecordAsync(action, by, updated.Subject, detail, ct).ConfigureAwait(false);
        return CaseResult.Success(updated);
    }

    /// <summary>
    /// Audit and timeline, together, for anything that changes a case.
    /// </summary>
    /// <remarks>
    /// THROUGH AuditLog rather than straight to the timeline, so case activity lands in the
    /// staff log channel alongside every other staff action and gets its timeline entry from
    /// the same funnel. A second recording path would be a second thing to keep in step.
    /// </remarks>
    private Task RecordAsync(string action, string by, string subject, string detail, CancellationToken ct) =>
        audit.RecordAsync(action, by, subject, detail, ct);

    private static string Clamp(string? value)
    {
        var text = Sanitize.Message(value ?? "").Trim();
        if (text.Length == 0) return "none given";
        return text.Length <= MaxText ? text : text[..MaxText];
    }
}
