using CodeyBox.Core;

namespace CodeyBox.Agents;

/// <summary>
/// The provider rate-limit shapes relayed verbatim by BYOK-capable CLIs,
/// owned here so every detector honours the same set. A new provider pattern
/// is added once, in <see cref="ProviderRateLimitPatterns"/>, and both the
/// Copilot and opencode detectors pick it up; keeping a second copy in either
/// detector would let the two silently diverge.
/// </summary>
/// <remarks>
/// Numeric <c>429</c> rows stay anchored with companion text (<c>HTTP 429</c>,
/// <c>status 429</c>, <c>API Error: 429</c>, <c>429 Too Many Requests</c>,
/// <c>429 Error</c>) rather than matching a bare <c>429</c>, so model output
/// that merely cites the number (retry counts, code under review) is not
/// misclassified as a provider refusal. These are transient throughput /
/// concurrency refusals that clear on their own, so they park on the
/// rate-limit backoff rather than terminating the item as a generic failure.
/// </remarks>
public static class SharedRateLimitPatterns
{
    /// <summary>
    /// Transient provider rate-limit rows shared by the Copilot and opencode
    /// quota-failure detectors. Detectors prepend these to their own
    /// provider-specific rows so a refusal carrying both shapes parks on the
    /// rate-limit backoff.
    /// </summary>
    public static readonly QuotaFailurePattern[] ProviderRateLimitPatterns =
    [
        new("rate_limit_exceeded", QuotaFailureKind.RateLimitExceeded),
        new("rate limit exceeded", QuotaFailureKind.RateLimitExceeded),
        new("429 Too Many Requests", QuotaFailureKind.RateLimitExceeded),
        new("429 Error", QuotaFailureKind.RateLimitExceeded),
        new("HTTP 429", QuotaFailureKind.RateLimitExceeded),
        new("status 429", QuotaFailureKind.RateLimitExceeded),
        new("API Error: 429", QuotaFailureKind.RateLimitExceeded),
        new("too many requests", QuotaFailureKind.RateLimitExceeded),
    ];
}
