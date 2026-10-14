using System.Diagnostics.CodeAnalysis;

namespace ResidentChat.Core.Knowledge;

/// <summary>
/// A trusted tenant-scoped query request. The tenant ID must come from signed
/// operator identity, never an untrusted message or request parameter.
/// </summary>
public sealed record ApprovedFaqQuery(Guid TenantId, string Text, int MaxResults);

/// <summary>
/// Only manually approved, operator-owned FAQ content is eligible for retrieval.
/// No resident transcripts, attachments, personal names or apartment identifiers.
/// </summary>
public sealed record ApprovedFaqHit(
    Guid TenantId,
    Guid FaqId,
    string Question,
    string Answer,
    string SourceRef,
    DateTimeOffset ApprovedAt);

/// <summary>
/// Fail-closed query and result validation. This policy does not decide whether
/// an answer should be sent to a resident; no outbound integration exists.
/// </summary>
public static class ApprovedFaqPolicy
{
    public const int MaxQueryChars = 200;
    public const int MaxResults = 3;
    public const int MaxQuestionChars = 240;
    public const int MaxAnswerChars = 2_048;
    public const int MaxSourceRefChars = 256;

    public static bool TryCreate(
        Guid authorizedTenantId, string? rawQuery, int maxResults,
        [NotNullWhen(true)] out ApprovedFaqQuery? query)
    {
        query = null;
        if (authorizedTenantId == Guid.Empty || maxResults is < 1 or > MaxResults
            || rawQuery is null)
            return false;

        var text = rawQuery.Trim();
        if (text.Length is < 3 or > MaxQueryChars)
            return false;

        // No control characters, including CR/LF or NUL, are accepted into
        // this restricted search input. SQL execution must use a parameter.
        foreach (var c in text)
        {
            if (char.IsControl(c))
                return false;
        }

        query = new ApprovedFaqQuery(authorizedTenantId, text, maxResults);
        return true;
    }

    public static IReadOnlyList<ApprovedFaqHit> ValidateResults(
        ApprovedFaqQuery query, IEnumerable<ApprovedFaqHit> candidateRows)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidateRows);

        if (query.TenantId == Guid.Empty || query.MaxResults is < 1 or > MaxResults)
            return [];

        var results = new List<ApprovedFaqHit>();
        foreach (var row in candidateRows)
        {
            // Any unexpected record invalidates the entire batch; do not
            // silently discard a cross-tenant row and return partial data.
            if (row is null || row.TenantId != query.TenantId
                || row.FaqId == Guid.Empty || row.ApprovedAt == default
                || !HasBoundedText(row.Question, MaxQuestionChars)
                || !HasBoundedText(row.Answer, MaxAnswerChars)
                || !HasBoundedText(row.SourceRef, MaxSourceRefChars))
                return [];

            results.Add(row);
            if (results.Count > query.MaxResults)
                return [];
        }
        return results;
    }

    private static bool HasBoundedText(string? text, int max) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= max;
}
