using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Typed adapter errors. Thrown at the adapter boundary, never flattened to
/// bare strings; the framework maps them to durable states (uncertain poll
/// bumps, never silent passes). All external provider output is untrusted
/// data: validated here, at the sink, before it reaches neutral evidence.
/// </summary>
public class GitHubActionsException(string message, Exception? inner = null)
    : ExternalBuildException(message, inner);

/// <summary>Credential rejected or lacking permission (401 / non-rate-limit 403). Blocked, not retried.</summary>
public sealed class GitHubActionsAuthException(string message, Exception? inner = null)
    : GitHubActionsException(message, inner);

/// <summary>Request rejected by the provider (404 / 422 shape problems). Blocked, not retried.</summary>
public sealed class GitHubActionsValidationException(string message, Exception? inner = null)
    : GitHubActionsException(message, inner);

/// <summary>Network failure, timeout, or 5xx. Retryable with backoff.</summary>
public sealed class GitHubActionsTransientException(string message, Exception? inner = null)
    : GitHubActionsException(message, inner);

/// <summary>
/// Required evidence is missing, skipped, expired, malformed, or oversized.
/// Never counts as pass; carries the machine-readable <see cref="Reason"/>
/// the provider surfaces as bounded failure detail.
/// </summary>
public sealed class GitHubActionsEvidenceUnavailableException(string reason, Exception? inner = null)
    : GitHubActionsException("insufficient evidence: " + reason, inner)
{
    public string Reason { get; } = reason;
}
