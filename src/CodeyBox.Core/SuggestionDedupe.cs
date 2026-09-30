namespace CodeyBox.Core;

/// <summary>
/// Normalization and similarity for suggestion dedupe. Pure functions: given a
/// suggestion's (category, filesReferenced, title) they produce a canonical
/// dedupe key and a token signature that <see cref="ISuggestionStore"/>
/// implementations compare to decide whether a new entry is a repeat of an
/// existing one.
/// </summary>
public static class SuggestionDedupe
{
    /// <summary>
    /// English function words plus the generic action verbs that dominate
    /// agent-authored suggestion titles. They carry no finding-identity
    /// signal, so they are stripped before comparison — "the", "does", and
    /// "add"/"fix" appear in near-identical repeats regardless of wording.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "by",
        "with", "at", "from", "as", "into", "over", "under", "per", "via",
        "is", "are", "was", "were", "be", "been", "being",
        "it", "its", "this", "that", "these", "those",
        "do", "does", "did", "not", "no",
        "we", "you", "they", "our", "your", "their", "there", "here",
        "when", "where", "which", "while", "than", "then", "so", "such",
        "if", "but", "about", "against", "between",
        "also", "still", "currently", "now", "some", "any", "all", "each",
        "should", "could", "would", "may", "might", "must", "can", "will",
        "add", "fix", "update", "remove", "create", "improve", "consider",
        "use", "make", "need", "needs", "suggestion",
    };

    /// <summary>
    /// Splits a title into normalized tokens: lower-cased, split on any
    /// non-letter/digit boundary, stop-words removed, de-duplicated and
    /// sorted ordinally so word order does not affect comparison.
    /// </summary>
    public static IReadOnlyList<string> TokenizeTitle(string title)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var span = title.AsSpan();
        var start = -1;
        for (var i = 0; i <= span.Length; i++)
        {
            var isTokenChar = i < span.Length && char.IsLetterOrDigit(span[i]);
            if (isTokenChar && start < 0)
            {
                start = i;
            }
            else if (!isTokenChar && start >= 0)
            {
                var token = span[start..i].ToString().ToLowerInvariant();
                if (token.Length > 1 && !StopWords.Contains(token))
                    tokens.Add(token);
                start = -1;
            }
        }
        var sorted = tokens.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    /// <summary>
    /// Canonical form of a referenced path for comparison: trimmed, lower-cased,
    /// backslashes flattened to '/', and leading "./" / trailing "/" stripped.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/').ToLowerInvariant();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimEnd('/');
    }

    /// <summary>
    /// Sorted, de-duplicated normalized file paths for a suggestion.
    /// </summary>
    public static IReadOnlyList<string> NormalizeFiles(IEnumerable<string> filesReferenced)
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in filesReferenced)
        {
            var normalized = NormalizePath(f);
            if (normalized.Length > 0)
                files.Add(normalized);
        }
        return [.. files];
    }

    /// <summary>
    /// The canonical dedupe key: normalized category + sorted normalized
    /// file paths + sorted normalized title tokens. Two suggestions with the
    /// same key are the same finding however their titles were worded.
    /// </summary>
    public static string ComputeDedupeKey(
        string category, IEnumerable<string> filesReferenced, string title) =>
        string.Concat(
            category.Trim().ToLowerInvariant(), "|",
            string.Join(',', NormalizeFiles(filesReferenced)), "|",
            string.Join(' ', TokenizeTitle(title)));

    /// <summary>
    /// Comparable view of a suggestion: its dedupe key plus the token set used
    /// for fuzzy matching — normalized title tokens unioned with each
    /// normalized file path as an atomic token (so referencing the same file
    /// contributes shared signal without splitting path segments into noise).
    /// </summary>
    public static SuggestionSignature SignatureFor(Suggestion suggestion) =>
        new(
            DedupeKey: suggestion.DedupeKey ?? ComputeDedupeKey(
                suggestion.Category, suggestion.FilesReferenced, suggestion.Title),
            Tokens: SignatureTokens(suggestion.FilesReferenced, suggestion.Title));

    /// <summary>
    /// Jaccard similarity over the signature token sets in [0, 1]. Callers are
    /// responsible for restricting comparisons to same-project, same-category
    /// candidates — those fields are part of the dedupe key, not the score.
    /// </summary>
    public static double Similarity(SuggestionSignature a, SuggestionSignature b)
    {
        if (a.Tokens.Count == 0 && b.Tokens.Count == 0)
            return 1.0;
        var (small, large) = a.Tokens.Count <= b.Tokens.Count
            ? (a.Tokens, b.Tokens)
            : (b.Tokens, a.Tokens);
        var intersection = 0;
        foreach (var token in small)
            if (large.Contains(token))
                intersection++;
        var union = a.Tokens.Count + b.Tokens.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static HashSet<string> SignatureTokens(
        IEnumerable<string> filesReferenced, string title)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in TokenizeTitle(title))
            tokens.Add(t);
        foreach (var f in NormalizeFiles(filesReferenced))
            tokens.Add("f:" + f);
        return tokens;
    }
}

/// <summary>A suggestion reduced to its dedupe-comparable parts.</summary>
public sealed record SuggestionSignature(
    string DedupeKey,
    IReadOnlySet<string> Tokens);

/// <summary>
/// Operator-tunable dedupe knobs, sourced from
/// <c>CodeyBox:PipelineTuning</c> (hot-reloadable). Bounds
/// <see cref="ISuggestionStore.CreateOrMergeAsync"/>.
/// </summary>
public sealed record SuggestionDedupePolicy
{
    /// <summary>
    /// Minimum Jaccard similarity over the normalized token signature for a
    /// new suggestion to merge into an existing one, in (0, 1].
    /// </summary>
    public required double SimilarityThreshold { get; init; }

    /// <summary>
    /// How far back a dismissed suggestion still attracts repeats: a matching
    /// dismissed suggestion bumps its count instead of spawning a new row
    /// when dismissed within this window. Zero disables dismissed matching;
    /// open suggestions always match.
    /// </summary>
    public required TimeSpan DismissedMatchWindow { get; init; }

    /// <summary>
    /// Cap on recorded <see cref="Suggestion.SourceWorkItemIds"/>. Once full,
    /// repeats still bump <see cref="Suggestion.OccurrenceCount"/> but the new
    /// source id is not appended. Must be ≥ 1.
    /// </summary>
    public int MaxRecordedSourceIds { get; init; } = 25;

    public void Validate()
    {
        if (!double.IsFinite(SimilarityThreshold)
            || SimilarityThreshold <= 0.0
            || SimilarityThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SimilarityThreshold),
                SimilarityThreshold,
                "SimilarityThreshold must be in the interval (0, 1].");
        }
        if (DismissedMatchWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DismissedMatchWindow),
                "DismissedMatchWindow must be non-negative.");
        }
        if (MaxRecordedSourceIds < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRecordedSourceIds),
                "MaxRecordedSourceIds must be >= 1.");
        }
    }
}

/// <summary>Outcome of <see cref="ISuggestionStore.CreateOrMergeAsync"/>.</summary>
/// <param name="Suggestion">
/// The canonical row: the newly inserted suggestion when
/// <paramref name="Merged"/> is false, or the pre-existing suggestion (with
/// bumped count and extended source list) when true.
/// </param>
/// <param name="Merged">
/// True when the incoming suggestion was recognized as a repeat of an
/// existing open or recently-dismissed suggestion — no new row was created.
/// The canonical row keeps its own state, so a dismissed suggestion stays
/// dismissed.
/// </param>
public sealed record SuggestionCreateOutcome(Suggestion Suggestion, bool Merged);
