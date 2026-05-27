namespace CodeyBox.Core;

/// <summary>
/// Pre-merge CI gate: validates that the work item's local merge result
/// will not break <c>baseBranch</c> when pushed to the forge. Called by
/// the orchestrator's upstream-push phase *before* the auto-merge API
/// call so a green local CI run is required, not just the forge's
/// textual-conflict check.
///
/// <para>Motivated by the observation that the forge's
/// <c>mergeable == true</c> flag only checks for textual conflicts; it
/// does not catch the case where a PR's merge against a freshly-moved
/// <c>main</c> still applies cleanly but breaks the build or tests
/// (e.g. a helper renamed on <c>main</c> that the PR still calls under
/// its old name). PR-time CI may have been green against the PR's
/// original base; this gate re-validates against current
/// <c>baseBranch</c>.</para>
///
/// <para>Implementations should:</para>
/// <list type="number">
/// <item>Ensure <c>baseBranch</c> reflects the current upstream tip
/// (the orchestrator's <see cref="IUpstreamRemote.FetchBaseBranchAsync"/>
/// is the canonical refresh path).</item>
/// <item>Run the configured build/test commands against the post-merge
/// working tree.</item>
/// <item>Return <see cref="PreMergeVerifyResult.Success"/> = true on
/// green, or false with a populated
/// <see cref="PreMergeVerifyResult.FailureReason"/> and
/// <see cref="PreMergeVerifyResult.FailureMode"/> on red.</item>
/// </list>
///
/// <para>When the verifier is not wired (DI returns null) the orchestrator
/// skips the gate — this preserves backwards compatibility for projects
/// that have not opted in.</para>
/// </summary>
public interface IPreMergeVerifier
{
    Task<PreMergeVerifyResult> VerifyAsync(PreMergeVerifyRequest request, CancellationToken ct);
}

/// <summary>
/// Inputs to <see cref="IPreMergeVerifier.VerifyAsync"/>. Only the fields the
/// verifier needs to do its job: which repo + which sha to verify, and the
/// argv to run. The full <see cref="Project"/> is intentionally not exposed —
/// the gate's contract is "verify this tree" not "do anything else with this
/// project."
/// </summary>
public sealed record PreMergeVerifyRequest
{
    public required WorkItemId WorkItemId { get; init; }
    /// <summary>The project id the work item belongs to. Available for logging only.</summary>
    public required ProjectId ProjectId { get; init; }
    /// <summary>Opaque repository identifier resolved by the host's git module.</summary>
    public required string RepositoryId { get; init; }
    public required string BaseBranch { get; init; }
    public required string WorkBranch { get; init; }
    /// <summary>SHA of the local merge commit on <see cref="BaseBranch"/> at the time of verification.</summary>
    public required string MergeSha { get; init; }
    /// <summary>
    /// Operator-configured argv to run as the build/test command. Equals
    /// <see cref="ProjectUpstream.PreMergeVerifyArgv"/> at the time the
    /// orchestrator invokes the gate. Empty argv means "no verification";
    /// the orchestrator skips the gate entirely in that case so verifiers
    /// can assume <c>Argv.Count &gt; 0</c>.
    /// </summary>
    public required IReadOnlyList<string> Argv { get; init; }
}

/// <summary>Outcome of <see cref="IPreMergeVerifier.VerifyAsync"/>.</summary>
public sealed record PreMergeVerifyResult
{
    public required bool Success { get; init; }
    /// <summary>Short, operator-visible reason. Null when <see cref="Success"/> is true.</summary>
    public string? FailureReason { get; init; }
    /// <summary>
    /// Whether the failure was a textual rebase conflict
    /// (<see cref="PreMergeVerifyFailureMode.RebaseFailed"/>) or a build/test
    /// failure of the rebased tree
    /// (<see cref="PreMergeVerifyFailureMode.BuildOrTestFailed"/>). The
    /// orchestrator uses this to build a distinct <c>LastError</c> so
    /// operators can tell the two cases apart on the work item surface.
    /// </summary>
    public PreMergeVerifyFailureMode FailureMode { get; init; }

    public static PreMergeVerifyResult Ok() => new() { Success = true };

    public static PreMergeVerifyResult RebaseFailed(string reason) => new()
    {
        Success = false,
        FailureReason = reason,
        FailureMode = PreMergeVerifyFailureMode.RebaseFailed,
    };

    public static PreMergeVerifyResult BuildOrTestFailed(string reason) => new()
    {
        Success = false,
        FailureReason = reason,
        FailureMode = PreMergeVerifyFailureMode.BuildOrTestFailed,
    };
}

/// <summary>
/// Distinguishes the two failure modes the operator needs to tell apart:
/// a textual rebase conflict (needs operator rebase) vs. a clean rebase
/// whose build or tests fail against the new base (needs operator fix).
/// </summary>
public enum PreMergeVerifyFailureMode
{
    None = 0,
    RebaseFailed = 1,
    BuildOrTestFailed = 2,
}
