using System.Text.Json.Serialization;

namespace CodeyBox.Core;

/// <summary>
/// Outcome of a phase executed on an executor host (or in process).
/// A phase that fails because the host was unreachable or the transfer broke
/// is NOT represented here — that surfaces as
/// <see cref="ExecutorPhaseTransportException"/> so the caller retries
/// elsewhere instead of charging the work item with an agent failure.
/// </summary>
public enum ExecutorPhaseOutcome
{
    Succeeded = 0,
    AgentFailed = 1,
}

/// <summary>
/// Resource usage attributed to one phase execution. All values are produced
/// by the code under test (the executor-side handler or its in-process
/// twin) and are validated non-negative by the dispatch proxy before they
/// are cached or returned.
/// </summary>
public sealed record ExecutorPhaseUsage(
    [property: JsonPropertyName("inputTokens")] long InputTokens,
    [property: JsonPropertyName("outputTokens")] long OutputTokens,
    [property: JsonPropertyName("costUsd")] decimal CostUsd);

/// <summary>
/// Dispatch envelope for one phase of a work item. The proxy keys idempotent
/// delivery on work item + phase + attempt: a redelivered dispatch (same key,
/// same body hash) returns the original result instead of provisioning a
/// second sandbox, while a new attempt uses a new key and executes fresh.
/// </summary>
public sealed record ExecutorPhaseRequest
{
    /// <summary>Work item the phase belongs to. Non-empty, at most 128 chars.</summary>
    public required string WorkItemId { get; init; }

    /// <summary>
    /// Phase name (for example "work", "audit", "merge"). Open vocabulary so
    /// future pipeline phases need no contract change; restricted to
    /// <c>[A-Za-z0-9_-]</c>, at most 64 chars.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Attempt number within the phase. Must be zero or positive; redelivery
    /// of the same attempt is idempotent, a new attempt is a new dispatch.
    /// </summary>
    public required int Attempt { get; init; }

    /// <summary>
    /// Bare-repo id to stage to the executor (normally the work item id).
    /// Only this repo is transferred — never the whole repos root.
    /// </summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Serialized phase input (for example the work item snapshot). Bounded
    /// by dispatch options; covered by the idempotency body hash.
    /// </summary>
    public required string PayloadJson { get; init; }
}

/// <summary>
/// Result of one phase execution: agent-visible outcome plus the commit the
/// phase produced, the findings it reported, and the usage it consumed.
/// </summary>
public sealed record ExecutorPhaseResult
{
    public required ExecutorPhaseOutcome Outcome { get; init; }

    /// <summary>
    /// Full hex commit sha the phase left on its branch, if it produced one.
    /// Lowercase hex, 40 (SHA-1) or 64 (SHA-256) chars, or null/empty when
    /// the phase produced no commit.
    /// </summary>
    public string? CommitSha { get; init; }

    /// <summary>Findings reported by the phase (for example audit findings).</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    public required ExecutorPhaseUsage Usage { get; init; }

    /// <summary>Agent-facing error detail for <see cref="ExecutorPhaseOutcome.AgentFailed"/>.</summary>
    public string? ErrorMessage { get; init; }
}
