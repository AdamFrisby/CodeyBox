namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Dispatch request assembled by the provider from the frozen
/// framework record. The host publishes the temporary candidate ref; this
/// adapter only dispatches or adopts an already-published ref and never
/// moves work/base branches or opens delivery PRs.
/// </summary>
public sealed record GitHubActionsDispatchRequest(
    string Owner,
    string Repository,
    string WorkflowPath,
    string Ref,
    string RequestCorrelationId,
    string CandidateRef,
    string ExpectedHeadSha,
    string? ExpectedMergeSha,
    IReadOnlyDictionary<string, string> Inputs);

/// <summary>
/// Dispatch outcome. Real workflow_dispatch answers carry no run id (HTTP
/// 204), so <c>Uncertain</c> without a run id is the normal case — the
/// framework reconciles by correlation before any retry, never dispatching
/// a duplicate paid run. Accepted success codes are 200/201/202/204 (the
/// actual code is recorded, never assumed).
/// </summary>
public sealed record GitHubActionsDispatchOutcome(
    bool Accepted,
    string? RunId,
    string? Error,
    bool Uncertain,
    int HttpStatus);

/// <summary>
/// Transport seam around the GitHub Actions REST surface. The production
/// implementation speaks HTTP; tests substitute the deterministic fake.
/// Either way the REAL provider, mapper, and orchestration paths run — no
/// mock-call assertions. Implementations must honor cancellation, enforce
/// bounds before buffering, and surface rate-limit/auth failures typed.
/// </summary>
public interface IGitHubActionsTransport
{
    Task<GitHubActionsDispatchOutcome> DispatchAsync(
        GitHubActionsDispatchRequest request, CancellationToken ct);

    /// <summary>
    /// Correlates an uncertain dispatch to its run. The fake transport
    /// matches the exact correlation id embedded in the dispatch inputs.
    /// The HTTP transport matches by exact head branch + event + freshness
    /// (the runs API does not echo dispatch inputs). Either way a
    /// correlation miss is null, never "the latest run".
    /// </summary>
    Task<GitHubActionsRun?> FindRunByCorrelationAsync(
        string owner, string repository, string workflowPath,
        string correlationId, string? headBranch, DateTimeOffset? createdAfter, CancellationToken ct);

    Task<GitHubActionsRun?> GetRunAsync(
        string owner, string repository, long runId, CancellationToken ct);

    Task<IReadOnlyList<GitHubActionsJob>> ListJobsAsync(
        string owner, string repository, long runId, CancellationToken ct);

    Task CancelRunAsync(
        string owner, string repository, long runId, CancellationToken ct);

    Task<IReadOnlyList<GitHubActionsArtifactInfo>> ListArtifactsAsync(
        string owner, string repository, long runId, CancellationToken ct);

    /// <summary>
    /// Downloads one artifact archive. Enforces the caller's byte cap BEFORE
    /// buffering; throws <see cref="GitHubActionsEvidenceUnavailableException"/>
    /// when the artifact expired and <see cref="GitHubActionsAuthException"/>
    /// on permission failures.
    /// </summary>
    Task<byte[]> DownloadArtifactAsync(
        GitHubActionsArtifactInfo artifact, long maxBytes, CancellationToken ct);
}
