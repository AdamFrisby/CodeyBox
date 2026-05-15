namespace CodeyBox.Core;

/// <summary>
/// Shared heuristics that map an agent's stderr / stdout into an
/// <see cref="AgentFailureKind"/>. Used as the default body of
/// <see cref="IAgentRunner.ClassifyFailure"/>; runner-specific overrides can
/// extend it with patterns the shared list doesn't yet cover.
///
/// <para>
/// Pattern dictionaries are exposed as static fields so the operator can tune
/// or replace them in tests without touching runner internals. The patterns
/// here are intentionally substring-only — the orchestrator-side
/// <c>QuotaFailureDetector</c> still owns the structured-stream parsing and
/// reset-window extraction; this classifier is responsible only for the
/// in-iteration fallback decision (which agent kind, if any, to retry on).
/// </para>
/// </summary>
public static class AgentFailureClassifier
{
    /// <summary>
    /// Substrings (case-insensitive) that signal mid-flight quota exhaustion
    /// across the supported agent CLIs (codex, claude, gemini, copilot).
    /// </summary>
    public static readonly IReadOnlyList<string> QuotaPatterns = new[]
    {
        // Anthropic / OpenAI
        "rate_limit_exceeded",
        "rate limit exceeded",
        "usage_limit",
        "hit your usage limit",
        "hit your limit",
        // Google / Gemini
        "RESOURCE_EXHAUSTED",
        "exceeded the rate limit",
        "quota exceeded",
        "exhausted your capacity",
        // Anthropic overloaded shape
        "overloaded_error",
        // Bare HTTP shapes the CLIs surface verbatim
        "HTTP 429",
        "status 429",
        "API Error: 429",
        "HTTP 529",
        "status 529",
    };

    /// <summary>
    /// Substrings that signal an authentication / authorisation failure —
    /// typically a revoked OAuth token, expired API key, or HTTP 401/403.
    /// Quota responses surface as 429 / RESOURCE_EXHAUSTED and are matched by
    /// <see cref="QuotaPatterns"/> instead.
    /// </summary>
    public static readonly IReadOnlyList<string> AuthPatterns = new[]
    {
        "401 Unauthorized",
        "API Error: 401",
        "403 Forbidden",
        "invalid_api_key",
        "authentication failed",
        "credentials are invalid",
        "token has expired",
        "OAuth token expired",
    };

    /// <summary>
    /// Substrings that signal a transient connectivity failure where a retry
    /// (against the same agent or another) may succeed without operator action.
    /// </summary>
    public static readonly IReadOnlyList<string> TransientNetworkPatterns = new[]
    {
        "ECONNRESET",
        "ETIMEDOUT",
        "ECONNREFUSED",
        "EAI_AGAIN",
        "Connection reset by peer",
        "Temporary failure in name resolution",
        "Name or service not known",
        "TLS handshake timeout",
        "503 Service Unavailable",
        "504 Gateway Timeout",
        "upstream connect error",
    };

    /// <summary>
    /// Classifies an agent failure. Returns <see cref="AgentFailureKind.Normal"/>
    /// when no exceptional shape was detected — i.e. the agent ran and reported
    /// a work-related failure (failed tests, refused task, malformed output).
    ///
    /// <para>
    /// Order of checks is fixed: quota first (so a 429 in stderr is never
    /// stolen by a generic "connection reset" hint somewhere else in the
    /// payload), then auth, then network. Never throws.
    /// </para>
    /// </summary>
    public static AgentFailureClassification Classify(string? stderr, string? stdout = null)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return new AgentFailureClassification(AgentFailureKind.Unknown, Reason: "no output captured");

        if (ContainsAny(stderr, QuotaPatterns) || ContainsAny(stdout, QuotaPatterns))
            return new AgentFailureClassification(AgentFailureKind.QuotaExhausted, Reason: "quota pattern matched");

        if (ContainsAny(stderr, AuthPatterns) || ContainsAny(stdout, AuthPatterns))
            return new AgentFailureClassification(AgentFailureKind.AuthError, Reason: "auth pattern matched");

        if (ContainsAny(stderr, TransientNetworkPatterns) || ContainsAny(stdout, TransientNetworkPatterns))
            return new AgentFailureClassification(AgentFailureKind.TransientNetwork, Reason: "network pattern matched");

        return new AgentFailureClassification(AgentFailureKind.Normal);
    }

    private static bool ContainsAny(string? haystack, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        for (var i = 0; i < needles.Count; i++)
        {
            if (haystack.Contains(needles[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
