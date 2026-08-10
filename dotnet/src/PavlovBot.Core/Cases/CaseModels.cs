using System.Security.Cryptography;
using System.Text;

namespace PavlovBot.Core.Cases;

/// <summary>Where a case is in its life.</summary>
/// <remarks>
/// DISMISSED AND RESOLVED ARE BOTH ENDINGS AND THEY ARE NOT THE SAME. Resolved means
/// something was found and dealt with; dismissed means the suspicion did not hold up. Folding
/// them into one "closed" state loses the only fact anybody will care about in six months,
/// which is whether the player was actually guilty of anything.
/// </remarks>
public enum CaseStatus
{
    Open,
    UnderReview,
    Escalated,
    Resolved,
    Dismissed,
}

/// <summary>What an evidence entry is.</summary>
/// <remarks>
/// TYPED, so the record says how much weight the entry deserves without a moderator having to
/// infer it from the wording. An automated risk signal and a staff member's recollection are
/// both evidence and they are not equally strong.
/// </remarks>
public enum EvidenceKind
{
    /// <summary>A person's account of what they saw.</summary>
    Statement,

    /// <summary>Produced by the bot: a risk signal, an evasion match, a VPN verdict.</summary>
    Detection,

    /// <summary>A line lifted from the timeline or a server log.</summary>
    LogLine,

    /// <summary>A link to a clip, a screenshot, a Discord message.</summary>
    Link,

    /// <summary>An action already taken, recorded so the case explains itself later.</summary>
    Action,
}

/// <summary>
/// One piece of evidence. Written once and never changed.
/// </summary>
/// <param name="Sequence">Its position in the chain, from 1.</param>
/// <param name="Kind">What sort of evidence this is.</param>
/// <param name="Content">The evidence itself.</param>
/// <param name="Source">Where it came from - a staff name, "risk-engine", "timeline".</param>
/// <param name="AddedBy">Who put it on the case.</param>
/// <param name="Hash">
/// The chain hash: this entry's content plus the previous entry's hash.
/// </param>
/// <remarks>
/// TAMPER-EVIDENT, NOT TAMPER-PROOF, and the difference is worth being honest about. Cases
/// live in the same database as everything else, and anyone who can reach that file can edit
/// it - no application-level design changes that. What the chain buys is that an edit cannot
/// be made QUIETLY: changing any entry changes its hash, which breaks every hash after it,
/// and <see cref="EvidenceChain.Verify"/> says so.
///
/// That is the achievable property, and it is the one the brief is really asking for: "do not
/// allow staff to silently alter historical evidence". Silently is the operative word.
/// </remarks>
public sealed record CaseEvidence(
    int Sequence,
    EvidenceKind Kind,
    string Content,
    string Source,
    string AddedBy,
    DateTimeOffset At,
    string Hash);

/// <param name="At">When the note was written. Notes are append-only too.</param>
public sealed record CaseNote(string Author, string Text, DateTimeOffset At);

/// <summary>
/// One investigation: a subject, why it was opened, what was found, and what was done.
/// </summary>
/// <remarks>
/// THE CASE IS THE UNIT OF MEMORY. Everything else in this bot records a fact - a ban, a
/// warning, a flagged join - and none of them records the REASONING. Six months later "why
/// was this player permanently banned" is answerable from a case and from nothing else.
///
/// IT DOES NOT ENFORCE ANYTHING. Opening a case restricts nobody and closing one unbans
/// nobody. A case that silently applied punishments would make the record of an investigation
/// into an instrument of one, and then nobody could open a speculative case without
/// consequences following.
/// </remarks>
/// <param name="Id">Sequential and human-quotable: "case 1842".</param>
/// <param name="Subject">The player under investigation.</param>
/// <param name="AssignedTo">Who owns it now, if anybody.</param>
/// <param name="Resolution">Why it ended, set when it reaches a terminal state.</param>
public sealed record ModerationCase
{
    public required int Id { get; init; }
    public required string Subject { get; init; }
    public required string Reason { get; init; }
    public required string OpenedBy { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }

    public CaseStatus Status { get; init; } = CaseStatus.Open;
    public string? AssignedTo { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? Resolution { get; init; }

    public IReadOnlyList<CaseEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<CaseNote> Notes { get; init; } = [];

    /// <summary>Whether the case has reached an ending.</summary>
    public bool Closed => Status is CaseStatus.Resolved or CaseStatus.Dismissed;

    /// <summary>How long it took, or how long it has been open.</summary>
    public TimeSpan Age(DateTimeOffset now) => (ClosedAt ?? now) - OpenedAt;
}

/// <summary>
/// Which status changes are allowed, and why the illegal ones are illegal.
/// </summary>
/// <remarks>
/// PURE AND CENTRAL, rather than an if-statement in the command. The rules are few but they
/// are the difference between a case history that can be trusted and one that cannot, and
/// spreading them across handlers is how the third handler ends up disagreeing with the first.
/// </remarks>
public static class CaseTransitions
{
    /// <summary>Whether a case may move from one status to another.</summary>
    /// <remarks>
    /// A CLOSED CASE REOPENS ONLY TO Open, never straight back to Escalated or UnderReview.
    /// Reopening is a decision that somebody has to make deliberately and then act on, and
    /// letting it land mid-workflow would make "this case was reopened" ambiguous about
    /// whether anybody actually picked it back up.
    ///
    /// A CASE NEVER GOES STRAIGHT FROM Resolved TO Dismissed. Those are contradictory
    /// findings, and flipping between them without passing through Open would rewrite the
    /// verdict with no visible step where somebody reconsidered.
    /// </remarks>
    public static bool Allows(CaseStatus from, CaseStatus to)
    {
        if (from == to) return false;   // not a transition; the caller should say "no change"

        return from switch
        {
            CaseStatus.Resolved or CaseStatus.Dismissed => to == CaseStatus.Open,
            _ => true,
        };
    }

    /// <summary>Why a transition was refused, for a reply somebody can act on.</summary>
    public static string Explain(CaseStatus from, CaseStatus to)
    {
        if (from == to) return $"It is already {Name(from)}.";

        return $"A {Name(from).ToLowerInvariant()} case has to be reopened before it can " +
               $"become {Name(to).ToLowerInvariant()}. Reopening is its own decision, and " +
               "going straight there would change the verdict with no step where anybody " +
               "reconsidered it.";
    }

    public static string Name(CaseStatus status) => status switch
    {
        CaseStatus.UnderReview => "Under review",
        _ => status.ToString(),
    };
}

/// <summary>
/// The hash chain that makes evidence tampering visible.
/// </summary>
/// <remarks>
/// EACH ENTRY'S HASH COVERS THE PREVIOUS ENTRY'S HASH, so the entries form a chain rather
/// than a set of independent checksums. Independent checksums would only catch an edit that
/// forgot to recompute one; a chain also catches deletion, reordering and insertion, because
/// every one of those breaks the link at the point it happened - and <see cref="Verify"/>
/// reports WHERE, which is what turns "something is wrong" into an investigation.
///
/// SHA-256 rather than something cheaper. These are written a handful of times per case and
/// verified on read; the cost is irrelevant and a hash somebody could forge deliberately
/// would defeat the point.
/// </remarks>
public static class EvidenceChain
{
    /// <summary>The delimiter between hashed fields.</summary>
    /// <remarks>
    /// A DELIMITER IS REQUIRED, not tidiness. Without one the concatenation is ambiguous:
    /// ("ab","c") hashes identically to ("a","bc"), so two different evidence entries
    /// could share a hash and a swap between them would verify clean.
    ///
    /// 0x1F because it cannot occur in the content - every string reaching here has been
    /// through Sanitize, which strips control characters. A printable delimiter could be
    /// typed into a statement by whoever wanted the ambiguity back.
    ///
    /// NAMED, WITH AN ESCAPE, rather than written inline. As a literal it is an invisible
    /// byte in the source that an editor, a copy-paste or a merge could silently turn into
    /// a space, and the only symptom would be that every hash written before the change
    /// stops verifying - which reads exactly like evidence tampering.
    /// </remarks>
    private const char FieldSeparator = '\u001F';

    /// <summary>The hash an entry should carry, given what came before it.</summary>
    public static string HashFor(
        string? previousHash, int sequence, EvidenceKind kind, string content, string source, string addedBy, DateTimeOffset at)
    {
        /* UNIT SEPARATOR BETWEEN FIELDS, not a comma or a space. Without a delimiter the
           concatenation is ambiguous - ("ab","c") and ("a","bc") hash identically - and any
           printable delimiter could appear inside the evidence content itself. 0x1F cannot:
           ServerEvent-style content is stripped of control characters on the way in. */
        var payload = string.Join(FieldSeparator,
            previousHash ?? "",
            sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            content,
            source,
            addedBy,
            at.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>The next entry in a chain, hashed against what is already there.</summary>
    public static CaseEvidence Next(
        IReadOnlyList<CaseEvidence> existing,
        EvidenceKind kind,
        string content,
        string source,
        string addedBy,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var sequence = existing.Count + 1;
        var previous = existing.Count > 0 ? existing[^1].Hash : null;

        return new CaseEvidence(
            sequence, kind, content, source, addedBy, at,
            HashFor(previous, sequence, kind, content, source, addedBy, at));
    }

    /// <param name="Intact">True when every entry hashes to what it claims.</param>
    /// <param name="BrokenAt">
    /// The sequence number of the first entry that does not verify, or null when intact.
    /// </param>
    public sealed record Verdict(bool Intact, int? BrokenAt)
    {
        public static Verdict Ok { get; } = new(true, null);
    }

    /// <summary>
    /// Recompute the chain and report the first entry that does not match.
    /// </summary>
    /// <remarks>
    /// Called every time a case is DISPLAYED, not on a timer. A tamper check nobody runs is
    /// not a control, and the moment somebody is reading a case is exactly when they need to
    /// know whether to trust it.
    /// </remarks>
    public static Verdict Verify(IReadOnlyList<CaseEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        string? previous = null;

        for (var i = 0; i < evidence.Count; i++)
        {
            var entry = evidence[i];

            // The sequence is part of the hash, so a reordered or deleted entry fails here
            // even before the content is considered.
            var expected = HashFor(previous, entry.Sequence, entry.Kind, entry.Content, entry.Source, entry.AddedBy, entry.At);

            if (!string.Equals(expected, entry.Hash, StringComparison.Ordinal) || entry.Sequence != i + 1)
                return new Verdict(false, entry.Sequence);

            previous = entry.Hash;
        }

        return Verdict.Ok;
    }
}
