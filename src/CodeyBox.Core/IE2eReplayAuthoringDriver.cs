using System.Threading;
using System.Threading.Tasks;

namespace CodeyBox.Core;

/// <summary>
/// Context for one authoring pass. On the first author both fields are null;
/// when re-authoring a broken replay the gate passes the previous artifact and
/// the failing run result so a driver can bias its re-exploration.
/// </summary>
public sealed record E2eReplayAuthoringRequest
{
    /// <summary>The currently-committed replay JSON being refreshed; null on first authoring.</summary>
    public string? PreviousArtifactJson { get; init; }

    /// <summary>The failing replay result that triggered a re-author; null on first authoring.</summary>
    public E2eRunResult? FailedReplay { get; init; }

    /// <summary>True when this pass refreshes an existing (broken) replay rather than authoring a new one.</summary>
    public bool IsReauthoring => PreviousArtifactJson is not null || FailedReplay is not null;
}

/// <summary>
/// Result of one authoring pass. Either a driver produced a fresh replay
/// artifact JSON (<see cref="Authored"/> true) or it honestly reports that it
/// could not (<see cref="UnresolvedReason"/> set) — the gate treats an
/// unresolved case as blocking rather than ever inventing a fake replay.
/// </summary>
public sealed record E2eReplayAuthoringOutcome
{
    public bool Authored { get; init; }

    /// <summary>Deterministic replay artifact JSON to persist into <see cref="TestCase.ExecutableArtifactJson"/>. Non-null when <see cref="Authored"/> is true.</summary>
    public string? ArtifactJson { get; init; }

    /// <summary>Id of the cheap model that authored the replay. Non-null when <see cref="Authored"/> is true.</summary>
    public string? AuthorModelId { get; init; }

    /// <summary>Why authoring did not produce a replay. Non-null when <see cref="Authored"/> is false.</summary>
    public string? UnresolvedReason { get; init; }

    public static E2eReplayAuthoringOutcome Success(string artifactJson, string authorModelId)
        => new() { Authored = true, ArtifactJson = artifactJson, AuthorModelId = authorModelId };

    public static E2eReplayAuthoringOutcome Unresolved(string reason)
        => new() { Authored = false, UnresolvedReason = reason };
}

/// <summary>
/// Produces (or refreshes) the deterministic replay artifact for a declared
/// e2e-replay <see cref="TestCase"/>.
///
/// <para><b>Cheap models only.</b> Implementations MUST drive authoring with a
/// cheap model on the cloud E2E pool — never a coding-fleet frontier agent —
/// and reject a non-cheap model id at construction. This contract carries no
/// model; the concrete driver owns the model and its allowlist enforcement.</para>
///
/// <para>The seam lives in Core so the orchestration gate (Orchestrator) and
/// the computer-use author (ExploratoryTesting) meet here without either
/// referencing the other.</para>
/// </summary>
public interface IE2eReplayAuthoringDriver
{
    Task<E2eReplayAuthoringOutcome> AuthorAsync(
        TestCase testCase,
        E2eReplayAuthoringRequest request,
        CancellationToken ct = default);
}
