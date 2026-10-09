using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Core.ExternalBuilds;

/// <summary>
/// Durable lifecycle state of one provider-neutral external build.
/// Terminal states are sticky; cancellation is honored from non-terminal only.
/// </summary>
public enum ExternalBuildState
{
    Unknown = 0,
    /// <summary>Intent persisted, not yet submitted to any provider.</summary>
    IntentRecorded = 1,
    /// <summary>Submitted; provider acceptance uncertain (timeout/restart window).</summary>
    SubmitUncertain = 2,
    /// <summary>Provider accepted; queued remotely.</summary>
    Queued = 3,
    /// <summary>Provider reports running.</summary>
    Running = 4,
    /// <summary>Terminal evidence received; collecting artifacts/diagnostics.</summary>
    Collecting = 5,
    Succeeded = 6,
    Failed = 7,
    Cancelled = 8,
    /// <summary>Reconciliation impossible (provider unreachable, unknown run).</summary>
    ReconciliationBlocked = 9,
}

/// <summary>Provider-reported execution phase, echoed neutrally.</summary>
public enum ExternalBuildExecutionPhase
{
    Unknown = 0,
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>How a build reached its terminal state (budgets/cleanup truthfulness).</summary>
public enum ExternalBuildTerminalCause
{
    Unknown = 0,
    ProviderSucceeded = 1,
    ProviderFailed = 2,
    UserCancelled = 3,
    ProviderConfirmedCancellation = 4,
    DeadlineExceeded = 5,
    RequestTimeout = 6,
    ClientDisconnected = 7,
    ReconciliationImpossible = 8,
}

/// <summary>
/// Neutral comparable-build key used by the park predictor. All fields are
/// opaque neutral strings — provider, target, configuration, toolchain,
/// platform, cache class — with no language/ecosystem assumptions.
/// </summary>
public sealed record ExternalBuildTargetKey
{
    public required string ProviderId { get; init; }
    public required string TargetId { get; init; }
    public string Configuration { get; init; } = string.Empty;
    public string Toolchain { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string CacheClass { get; init; } = string.Empty;

    public bool Matches(ExternalBuildTargetKey? other) =>
        other is not null
        && string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal)
        && string.Equals(TargetId, other.TargetId, StringComparison.Ordinal)
        && string.Equals(Configuration, other.Configuration, StringComparison.Ordinal)
        && string.Equals(Toolchain, other.Toolchain, StringComparison.Ordinal)
        && string.Equals(Platform, other.Platform, StringComparison.Ordinal)
        && string.Equals(CacheClass, other.CacheClass, StringComparison.Ordinal);
}

/// <summary>
/// Immutable identity of the frozen candidate source handed to a provider.
/// Either a Git-published candidate ref or an uploaded snapshot digest.
/// </summary>
public sealed record ExternalBuildSourceIdentity
{
    public required string SourceDigestSha256 { get; init; }
    public string? CandidateRef { get; init; }
    public string? SnapshotId { get; init; }
    public required string BaseDigestSha256 { get; init; }
    public long ByteSize { get; init; }
    public int FileCount { get; init; }
}

/// <summary>Sandbox-supplied start request (tools validate before service).</summary>
public sealed record ExternalBuildStartRequest
{
    public required string ProjectId { get; init; }
    public required string WorkItemId { get; init; }
    public required string Phase { get; init; }
    public int Iteration { get; init; }
    public int Attempt { get; init; }
    public required string ApprovedTargetName { get; init; }
    public ExternalBuildSourceIdentity? Source { get; init; }
    public string? IdempotencyKey { get; init; }
    public Dictionary<string, string> AdapterParameters { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>Persisted build intent + execution record. Schema versioned for backward reads.</summary>
public sealed record ExternalBuildRecord
{
    public const int CurrentSchemaVersion = 1;

    public required string Id { get; init; }
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string ProjectId { get; init; }
    public required string WorkItemId { get; init; }
    public required string Phase { get; init; }
    public int Iteration { get; init; }
    public int Attempt { get; init; }
    public required ExternalBuildState State { get; init; }
    public required ExternalBuildTargetKey Target { get; init; }
    public required ExternalBuildSourceIdentity Source { get; init; }
    public required string ConfigDigest { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? IdempotencyBodyHash { get; init; }
    /// <summary>Client-chosen request identity, stable across retries.</summary>
    public string RequestId { get; init; } = string.Empty;
    /// <summary>Provider-assigned run identity; null until accepted.</summary>
    public string? ProviderRunId { get; init; }
    /// <summary>Exclusive owner fencing token; null when unowned.</summary>
    public string? FenceOwner { get; init; }
    public long FenceEpoch { get; init; }
    public int DispatchAttempts { get; init; }
    public int PollCount { get; init; }
    public ExternalBuildTerminalCause TerminalCause { get; init; }
    public ExternalBuildEvidence? Evidence { get; init; }
    public string? FailureDetail { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>True once a terminal completion was delivered through the acknowledged outbox.</summary>
    public bool DeliveryAcked { get; init; }
}

/// <summary>Pure lifecycle transitions.</summary>
public static class ExternalBuildLifecycle
{
    public static bool IsTerminal(ExternalBuildState state) => state is
        ExternalBuildState.Succeeded or ExternalBuildState.Failed
        or ExternalBuildState.Cancelled or ExternalBuildState.ReconciliationBlocked;

    public static bool CanTransition(ExternalBuildState from, ExternalBuildState to) => (from, to) switch
    {
        (_, ExternalBuildState.Cancelled) => !IsTerminal(from),
        (ExternalBuildState.IntentRecorded, ExternalBuildState.SubmitUncertain) => true,
        (ExternalBuildState.IntentRecorded, ExternalBuildState.Queued) => true,
        (ExternalBuildState.IntentRecorded, ExternalBuildState.Failed) => true,
        (ExternalBuildState.SubmitUncertain, ExternalBuildState.Queued) => true,
        (ExternalBuildState.SubmitUncertain, ExternalBuildState.Running) => true,
        (ExternalBuildState.SubmitUncertain, ExternalBuildState.Failed) => true,
        (ExternalBuildState.SubmitUncertain, ExternalBuildState.ReconciliationBlocked) => true,
        (ExternalBuildState.Queued, ExternalBuildState.Running) => true,
        (ExternalBuildState.Queued, ExternalBuildState.Collecting) => true,
        (ExternalBuildState.Running, ExternalBuildState.Collecting) => true,
        (ExternalBuildState.Collecting, ExternalBuildState.Succeeded) => true,
        (ExternalBuildState.Collecting, ExternalBuildState.Failed) => true,
        (ExternalBuildState.Queued, ExternalBuildState.Failed) => true,
        (ExternalBuildState.Running, ExternalBuildState.Failed) => true,
        _ => false,
    };

    public static string BodyHash(ExternalBuildStartRequest request, string configDigest)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Project = request.ProjectId.Trim().ToLowerInvariant(),
            Work = request.WorkItemId.Trim(),
            request.Phase,
            request.Iteration,
            request.Attempt,
            Target = request.ApprovedTargetName.Trim(),
            Source = request.Source?.SourceDigestSha256 ?? string.Empty,
            Config = configDigest,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string ComputeConfigDigest(ExternalBuildTargetKey target, IReadOnlyDictionary<string, string> parameters)
    {
        var payload = JsonSerializer.Serialize(new
        {
            target.ProviderId,
            target.TargetId,
            target.Configuration,
            target.Toolchain,
            target.Platform,
            target.CacheClass,
            Params = parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + kv.Value).ToArray(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16].ToLowerInvariant();
    }
}

/// <summary>Typed errors; never flattened to bare strings at a boundary.</summary>
public abstract class ExternalBuildException(string message) : InvalidOperationException(message);
public sealed class ExternalBuildNotEnabledException() : ExternalBuildException("External builds are not enabled.");
public sealed class ExternalBuildTargetNotApprovedException(string target)
    : ExternalBuildException($"Build target '{target}' is not operator-approved.");
public sealed class ExternalBuildOwnershipException(string buildId)
    : ExternalBuildException($"Caller does not own external build '{buildId}'.");
public sealed class ExternalBuildCapabilityExpiredException()
    : ExternalBuildException("Build capability has expired or was revoked.");
public sealed class ExternalBuildConflictException(string detail) : ExternalBuildException(detail);
public sealed class ExternalBuildReconciliationBlockedException(string detail) : ExternalBuildException(detail);
public sealed class ExternalBuildBudgetExceededException(string detail) : ExternalBuildException(detail);
