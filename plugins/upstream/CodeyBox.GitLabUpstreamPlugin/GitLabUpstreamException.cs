using System.Net;

namespace CodeyBox.GitLabUpstreamPlugin;

/// <summary>
/// The GitLab instance was unreachable, refused the request, or answered
/// with an unexpected status. Forge-side failures are <em>infrastructure</em>:
/// they are never a verdict on the work item's diff. Throwing (rather than
/// returning a failure value) lets the orchestrator retry with backoff and
/// park the item as an infrastructure failure after its attempt budget.
///
/// <para>Soft outcomes that are part of normal operation — a merge request
/// that already exists (409 on create with an "already exists" body), a merge
/// request that cannot be auto-merged (405/409/422 on merge) — do not throw;
/// they return partial results instead.</para>
/// </summary>
public sealed class GitLabUpstreamException : InvalidOperationException
{
    /// <summary>HTTP status from the GitLab instance, when the failure was an HTTP response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Value of the <c>Retry-After</c> response header in seconds, when the
    /// instance supplied one (typically with 429 rate limiting).
    /// </summary>
    public int? RetryAfterSeconds { get; }

    public GitLabUpstreamException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public GitLabUpstreamException(string message, HttpStatusCode statusCode, int? retryAfterSeconds = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        RetryAfterSeconds = retryAfterSeconds;
    }
}
