namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Host-owned temporary candidate ref policy. The host alone publishes the
/// scoped candidate commit/ref for the frozen dirty-source snapshot; the
/// adapter only validates the namespace, derives the dispatch ref, and
/// decides when cleanup is safe. Cleanup deletes exactly the published ref
/// (expected identity + retention), never arbitrary branches, and never a
/// delivery PR.
/// </summary>
public static class GitHubActionsCandidateRefPolicy
{
    /// <summary>Default allowed namespace for dispatchable candidate branches.</summary>
    public const string DefaultPrefix = "refs/heads/codeybox-candidates/";

    /// <summary>Max candidate-ref chars accepted (bounds the dispatch input).</summary>
    public const int MaxRefChars = 512;

    public static string? ValidateForDispatch(string? candidateRef, string prefix)
    {
        if (string.IsNullOrWhiteSpace(candidateRef) || candidateRef.Length > MaxRefChars)
            return "candidate ref is missing or too long";
        if (!candidateRef.StartsWith(prefix, StringComparison.Ordinal))
            return $"candidate ref '{candidateRef}' is outside the approved '{prefix}' namespace";
        if (candidateRef.Contains("..", StringComparison.Ordinal) || candidateRef.Any(char.IsWhiteSpace))
            return "candidate ref contains traversal or whitespace";
        var dispatch = DeriveDispatchRef(candidateRef);
        if (string.IsNullOrWhiteSpace(dispatch) || dispatch.Contains("..", StringComparison.Ordinal))
            return "candidate ref does not derive a dispatchable branch";
        return null;
    }

    /// <summary>
    /// Derives the workflow_dispatch <c>ref</c> (branch/tag short name) from
    /// the full candidate ref. Only <c>refs/heads/</c> and <c>refs/tags/</c>
    /// members are dispatchable.
    /// </summary>
    public static string DeriveDispatchRef(string candidateRef)
    {
        ArgumentNullException.ThrowIfNull(candidateRef);
        if (candidateRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            return candidateRef["refs/heads/".Length..];
        if (candidateRef.StartsWith("refs/tags/", StringComparison.Ordinal))
            return candidateRef["refs/tags/".Length..];
        return string.Empty;
    }

    /// <summary>
    /// Safe-cleanup gate: delete only the exact published ref, only inside
    /// the approved namespace, only when the caller proves the ref still
    /// points at the published sha (nobody else moved it) and the retention
    /// window has elapsed or the build is terminal. Anything else is refused.
    /// </summary>
    public static bool CanDelete(
        string candidateRef,
        string prefix,
        string expectedRef,
        string? publishedSha,
        string? currentSha,
        DateTimeOffset publishedAt,
        DateTimeOffset now,
        TimeSpan retention,
        bool buildTerminal)
    {
        if (string.IsNullOrWhiteSpace(candidateRef)
            || string.IsNullOrWhiteSpace(expectedRef)
            || string.IsNullOrWhiteSpace(publishedSha)
            || string.IsNullOrWhiteSpace(currentSha))
            return false;
        if (!string.Equals(candidateRef, expectedRef, StringComparison.Ordinal))
            return false;
        if (!candidateRef.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        if (!string.Equals(publishedSha, currentSha, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsPlausibleSha(publishedSha))
            return false;
        return buildTerminal || now - publishedAt >= retention;
    }

    public static bool IsPlausibleSha(string? value) =>
        value is not null && value.Length == 40 && value.All(Uri.IsHexDigit);
}
