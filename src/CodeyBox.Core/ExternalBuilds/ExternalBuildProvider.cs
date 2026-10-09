namespace CodeyBox.Core.ExternalBuilds;

/// <summary>
/// Provider-neutral build-service contract. Vendor adapters (GitHub Actions,
/// Unity Build Automation, …) implement this interface and reuse the shared
/// lifecycle; they must not fork submit/reconcile/cancel semantics.
/// All provider output is untrusted data: the service validates and
/// reconciles it against authoritative state, never executes it.
/// </summary>
public interface IExternalBuildProvider
{
    /// <summary>Stable provider id (exact match with target approvals).</summary>
    string ProviderId { get; }

    /// <summary>True when the provider accepts Git-published candidate refs.</summary>
    bool SupportsGitPublication { get; }

    /// <summary>True when the provider accepts uploaded source snapshots.</summary>
    bool SupportsSnapshotUpload { get; }

    Task<ExternalBuildSubmitResult> SubmitAsync(
        ExternalBuildRecord intent, ExternalBuildSubmitInput input, CancellationToken ct);

    Task<ExternalBuildProviderStatus> GetStatusAsync(
        string providerRunId, CancellationToken ct);

    Task<ExternalBuildCancelResult> CancelAsync(
        string providerRunId, CancellationToken ct);

    Task<IReadOnlyList<ExternalBuildArtifactRef>> ListArtifactsAsync(
        string providerRunId, CancellationToken ct);

    Task<ExternalBuildArtifactPayload> ReadArtifactAsync(
        string providerRunId, string artifactName, CancellationToken ct);
}

public sealed record ExternalBuildSubmitInput
{
    public string? CandidateRef { get; init; }
    public ExternalBuildSnapshot? Snapshot { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.Ordinal);
}

public sealed record ExternalBuildSubmitResult(
    bool Accepted, string? ProviderRunId, string? Error, bool Uncertain = false);

public sealed record ExternalBuildProviderStatus(
    ExternalBuildExecutionPhase Phase,
    ExternalBuildEvidence? Evidence,
    string? Detail,
    DateTimeOffset ObservedAt);

public sealed record ExternalBuildCancelResult(bool Confirmed, string? Detail);

public sealed record ExternalBuildArtifactRef(
    string Name, long SizeBytes, string ContentDigestSha256, string MediaType);

public sealed record ExternalBuildArtifactPayload(
    string Name, byte[] Content, string MediaType, string ContentDigestSha256);

/// <summary>
/// Validated provider callback (webhook). Callbacks are wakeups only: the
/// service always reconciles against the provider's authoritative status
/// before acting. Signatures/timestamps are validated at this sink.
/// </summary>
public sealed record ExternalBuildCallback(
    string ProviderId, string ProviderRunId, string BuildId,
    ExternalBuildExecutionPhase ClaimedPhase, DateTimeOffset SentAt, string? Signature);

public static class ExternalBuildCallbackValidator
{
    /// <summary>Max clock skew accepted for provider callbacks coming from the future.</summary>
    public static readonly TimeSpan MaxFutureClockSkew = TimeSpan.FromMinutes(5);

    public static string? Validate(
        ExternalBuildCallback callback, string expectedProviderId,
        DateTimeOffset now, TimeSpan maxAge,
        Func<ExternalBuildCallback, bool> verifySignature)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(verifySignature);
        if (!string.Equals(callback.ProviderId, expectedProviderId, StringComparison.Ordinal))
            return "provider mismatch";
        if (string.IsNullOrWhiteSpace(callback.ProviderRunId) || string.IsNullOrWhiteSpace(callback.BuildId))
            return "missing run identity";
        if (callback.SentAt > now + MaxFutureClockSkew)
            return "callback from the future";
        if (now - callback.SentAt > maxAge)
            return "stale callback";
        if (!verifySignature(callback))
            return "invalid signature";
        return null;
    }
}
