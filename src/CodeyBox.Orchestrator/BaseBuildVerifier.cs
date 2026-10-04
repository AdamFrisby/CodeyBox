using System.Collections.Concurrent;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>Outcome of a base-branch-tip build verification.</summary>
public enum BaseBuildOutcome
{
    /// <summary>The base tip compiled (or carried no build markers).</summary>
    Passed,

    /// <summary>The base tip failed the required build with a real (non-toolchain) error.</summary>
    Failed,

    /// <summary>
    /// The verification could not produce a verdict: sandbox/toolchain
    /// unavailability, retryable toolchain fault, or infrastructure error.
    /// Never cached — the next caller re-attempts the build.
    /// </summary>
    Inconclusive,
}

public sealed record BaseBuildVerdict(
    string BaseSha,
    BaseBuildOutcome Outcome,
    string Output,
    int ExitCode);

/// <summary>
/// Builds a base branch tip in a sandbox through the same
/// <see cref="IRequiredBuildVerifier"/> path the per-item gate uses (work
/// branch == base branch checks out the tip), so "the same build" really is
/// the same command line. Results are cached per (project, base SHA): a
/// commit's content is immutable, so a given tip can only ever produce one
/// verdict — concurrent failing items share a single sandbox build and a
/// later detector pays no rebuild cost while the same tip remains broken.
/// Only Passed/Failed verdicts are retained;
/// <see cref="BaseBuildOutcome.Inconclusive"/> (transient sandbox or
/// toolchain faults) is evicted so the next caller re-attempts.
/// </summary>
public sealed class BaseBuildVerifier
{
    /// <summary>
    /// Phase label stamped on the verification request. Timing/audit records
    /// show the base-tip build as its own phase rather than masquerading as
    /// the item's work or audit gate.
    /// </summary>
    internal const string Phase = "base-build";

    // Bounded cache: a project can only accumulate broken SHAs while the
    // base keeps moving; entries for superseded tips are worthless but must
    // not grow unboundedly. On overflow the whole map is dropped — it is a
    // pure cache, so losing entries only costs a rebuild.
    private const int MaxCachedVerdicts = 256;

    private readonly IRequiredBuildVerifier _verifier;
    private readonly IToolchainFaultClassifier? _toolchainFaultClassifier;
    private readonly ILogger<BaseBuildVerifier>? _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<BaseBuildVerdict>>> _verdicts = new();

    public BaseBuildVerifier(
        IRequiredBuildVerifier verifier,
        IToolchainFaultClassifier? toolchainFaultClassifier = null,
        ILogger<BaseBuildVerifier>? logger = null)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _toolchainFaultClassifier = toolchainFaultClassifier;
        _logger = logger;
    }

    /// <summary>
    /// Returns the (cached or freshly-built) verdict for the commit
    /// <paramref name="baseSha"/> — callers resolve the tip so the same
    /// verifier serves any repository/host context. Provisioning deferrals
    /// and cancellation propagate — they belong to the caller's
    /// recoverable-infra path, not to build attribution.
    /// </summary>
    public async Task<BaseBuildVerdict> VerifyTipAsync(
        string baseSha,
        string repositoryId,
        string baseBranch,
        WorkItemId workItemId,
        ProjectId projectId,
        RequiredBuildSandboxPolicy sandboxPolicy,
        CancellationToken ct)
    {
        var key = projectId.Value + "\0" + baseSha;

        // The shared build runs detached from any single caller's token: a
        // cancelled waiter must not poison the verdict for the others, and a
        // completed build remains useful to the next detector of the same tip.
        var lazy = _verdicts.GetOrAdd(
            key,
            _ => new Lazy<Task<BaseBuildVerdict>>(
                () => RunBaseBuildAsync(repositoryId, baseBranch, baseSha, workItemId, projectId, sandboxPolicy),
                LazyThreadSafetyMode.ExecutionAndPublication));
        if (_verdicts.Count > MaxCachedVerdicts)
            _verdicts.Clear();

        try
        {
            var verdict = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
            if (verdict.Outcome == BaseBuildOutcome.Inconclusive)
                _verdicts.TryRemove(new KeyValuePair<string, Lazy<Task<BaseBuildVerdict>>>(key, lazy));
            return verdict;
        }
        catch
        {
            _verdicts.TryRemove(new KeyValuePair<string, Lazy<Task<BaseBuildVerdict>>>(key, lazy));
            throw;
        }
    }

    private async Task<BaseBuildVerdict> RunBaseBuildAsync(
        string repositoryId,
        string baseBranch,
        string baseSha,
        WorkItemId workItemId,
        ProjectId projectId,
        RequiredBuildSandboxPolicy sandboxPolicy)
    {
        RequiredBuildVerificationResult result;
        try
        {
            result = await _verifier.VerifyAsync(new RequiredBuildVerificationRequest
            {
                WorkItemId = workItemId,
                ProjectId = projectId,
                RepositoryId = repositoryId,
                BaseBranch = baseBranch,
                WorkBranch = baseBranch,
                Phase = Phase,
                Iteration = null,
                SandboxPolicy = sandboxPolicy,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
        {
            _logger?.LogWarning(
                ex,
                "Base-tip build verification for {BaseBranch}@{Sha} could not complete",
                baseBranch, baseSha);
            return new BaseBuildVerdict(baseSha, BaseBuildOutcome.Inconclusive, ex.Message, 0);
        }

        return result.Status switch
        {
            RequiredBuildVerificationStatus.Passed
                or RequiredBuildVerificationStatus.Skipped =>
                    new BaseBuildVerdict(baseSha, BaseBuildOutcome.Passed, result.Output, result.ExitCode),
            RequiredBuildVerificationStatus.Failed =>
                IsRetryableToolchainFault(result)
                    ? new BaseBuildVerdict(baseSha, BaseBuildOutcome.Inconclusive, result.Output, result.ExitCode)
                    : new BaseBuildVerdict(baseSha, BaseBuildOutcome.Failed, result.Output, result.ExitCode),
            _ => new BaseBuildVerdict(
                baseSha,
                BaseBuildOutcome.Inconclusive,
                result.Reason ?? result.Output,
                result.ExitCode),
        };
    }

    /// <summary>
    /// A base build that died on a retryable toolchain signature (feed
    /// timeout, OOM, transport blip) is not evidence the base is broken — it
    /// would pin a permanent hold on an SHA whose verdict is really "ask
    /// again later". Classified the same way the per-item gate classifies
    /// the work-branch build.
    /// </summary>
    private bool IsRetryableToolchainFault(RequiredBuildVerificationResult result)
    {
        if (_toolchainFaultClassifier is null)
            return false;
        try
        {
            var classification = _toolchainFaultClassifier.Classify(new SubprocessResult(
                RequiredBuildGateIdentity.DisplayCommand,
                result.ExitCode,
                result.Output,
                Stderr: null));
            return classification.Disposition is ToolchainFaultDisposition.Retry
                or ToolchainFaultDisposition.Escalate;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
