using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

// SandboxWorkBranchPublisher — single bounded contract for publishing the
// sandbox HEAD to the bare-repo work branch from every phase (work, rework,
// self-review, mechanical-edit, and resumed meaningful-checkpoint
// publication). The resumed-checkpoint path historically pushed HEAD directly
// with no reconciliation, so a target that advanced after the checkpoint was
// taken failed the item with a non-fast-forward rejection instead of
// rebasing; every publication site now shares this helper.
//
// Bounds (no loops, no retries — a moving target stops with a typed failure
// instead of chasing the remote):
//   * Ordinary `push HEAD:<branch>` first; a fast-forward publishes untouched.
//   * At most one fetch, one rebase, and one further ordinary push.
//   * Never `--force`, `--force-with-lease`, push refspec `+`, delete, reset,
//     or `rebase --skip`: the final push retains target history by construction
//     (the server rejects anything else), and a conflicting rebase is aborted.
//   * At most MaxGitCommands sandbox execs per call (see the constant).
//
// Durability: before any fetch/rebase can rewrite sandbox refs, the original
// source tip and the observed target tip are pinned under item-scoped,
// sandbox-local preservation refs. They stay reachable until the sandbox is
// torn down — including across rebase, abort, cancellation, and failure — so
// no original commit is ever droppable or garbage-collectable mid-operation.
// Callers must clear durable resume checkpoints only after this method returns
// AND their own state sync succeeds; every failure here throws, so falling
// through to a checkpoint clear is impossible without an explicit bug.
internal sealed record SandboxWorkBranchPublication(
    string PublishedSha,
    string SourceSha,
    string? TargetSha,
    bool Reconciled);

internal static class SandboxWorkBranchPublisher
{
    /// <summary>
    /// Upper bound on sandbox git commands per publication: 1 source read, 1
    /// initial push, 1 fetch, 1 target read, 2 preservation pins, 1 rebase
    /// (+1 abort only on rebase failure), 1 target-retained check, 1 final
    /// push, 1 published-tip read.
    /// Tests pin this bound; raise it only with a matching test update.
    /// </summary>
    public const int MaxGitCommands = 11;

    private const string PreservedSourceRef = "refs/codeybox/preserved/source-tip";
    private const string PreservedTargetRef = "refs/codeybox/preserved/target-tip";

    private const int DiagnosticExcerptBytes = 2048;

    /// <summary>
    /// Matches either sandbox-publication failure shape (conflict or
    /// stage failure) anywhere in the exception chain so the pipeline
    /// recovery contract can classify the boundary without matching on
    /// message text.
    /// </summary>
    public static bool IsPublicationFailure(Exception? ex) =>
        SandboxPushReconcileConflictException.TryFindIn(ex, out _)
        || SandboxWorkBranchPublishException.TryFindIn(ex, out _);

    /// <summary>
    /// Publishes the sandbox HEAD to the bare work branch, reconciling a
    /// non-fast-forward rejection with one bounded fetch + rebase + push.
    /// History is never rewritten: no force-push, no reset, no rebase
    /// abort-skipping. Throws
    /// <see cref="SandboxPushReconcileConflictException"/> when the histories
    /// cannot be reconciled without rewriting, and
    /// <see cref="SandboxWorkBranchPublishException"/> when a stage fails
    /// before any rewrite could happen. Cancellation propagates unwrapped.
    /// </summary>
    public static async Task<SandboxWorkBranchPublication> PublishHeadAsync(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> exec,
        string branch,
        ILogger? log = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exec);
        Validation.ValidateBranchName(branch, nameof(branch));

        var sourceSha = await ReadVerifiedHeadAsync(exec, branch, "read-source", ct).ConfigureAwait(false);

        // HEAD (not the named local branch) is the publication candidate: on
        // resumed or detached checkouts the local branch ref may be stale or
        // absent, while HEAD is always the agent's resulting tree.
        string[] pushArgv = ["git", "-C", SandboxConventions.WorkDir, "push", "origin", $"HEAD:{branch}"];
        var push = await exec(new SandboxExec { Argv = pushArgv }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(push);
        if (push.Success)
            return new SandboxWorkBranchPublication(sourceSha, sourceSha, TargetSha: null, Reconciled: false);

        if (!IsNonFastForwardRejection(push.Stdout, push.Stderr))
            throw new SandboxWorkBranchPublishException(
                branch, "push", sourceSha, detail: DescribeGitFailure(push));

        log?.LogWarning(
            "Sandbox push of work branch {Branch} was rejected as non-fast-forward; fetching and rebasing once (source {Source})",
            branch,
            ToShortSha(sourceSha));

        // Fetch the authoritative target. The forced remote-tracking update
        // below is local bookkeeping only: the remote tip stays put, the
        // observed target is pinned under a preservation ref next, and the
        // previous tracking value survives in the sandbox reflog.
        var fetchArgv = new string[]
        {
            "git", "-C", SandboxConventions.WorkDir, "fetch", "--no-tags", "origin",
            $"+refs/heads/{branch}:refs/remotes/origin/{branch}",
        };
        var fetch = await exec(new SandboxExec { Argv = fetchArgv }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(fetch);
        if (!fetch.Success)
            throw new SandboxWorkBranchPublishException(
                branch, "fetch", sourceSha, detail: DescribeGitFailure(fetch));

        var targetSha = await ReadRemoteTrackingTipAsync(exec, branch, ct).ConfigureAwait(false);

        // Pin both original tips before the rebase can rewrite sandbox refs.
        await UpdatePreservationRefAsync(exec, PreservedSourceRef, sourceSha, "preserve-source", branch, ct).ConfigureAwait(false);
        await UpdatePreservationRefAsync(exec, PreservedTargetRef, targetSha, "preserve-target", branch, ct).ConfigureAwait(false);

        var rebaseArgv = new string[]
        {
            "git", "-C", SandboxConventions.WorkDir,
            "-c", "user.name=CodeyBox",
            "-c", "user.email=codeybox@localhost",
            "rebase", $"origin/{branch}",
        };
        var rebase = await exec(new SandboxExec { Argv = rebaseArgv }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(rebase);
        if (!rebase.Success)
        {
            await exec(new SandboxExec
            {
                Argv = ["git", "-C", SandboxConventions.WorkDir, "rebase", "--abort"],
            }, CancellationToken.None).ConfigureAwait(false);
            throw new SandboxPushReconcileConflictException(
                branch, "rebase", stage: "rebase", sourceSha: sourceSha, targetSha: targetSha);
        }

        // A rebase onto origin/<branch> must leave the target reachable from
        // the candidate; anything else means history was not retained and the
        // candidate must not be published.
        var retainedArgv = new string[]
        {
            "git", "-C", SandboxConventions.WorkDir,
            "merge-base", "--is-ancestor", targetSha, "HEAD",
        };
        var retained = await exec(new SandboxExec { Argv = retainedArgv }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(retained);
        if (!retained.Success)
            throw new SandboxPushReconcileConflictException(
                branch, "rebase", stage: "verify-target-retained", sourceSha: sourceSha, targetSha: targetSha);

        var repush = await exec(new SandboxExec { Argv = pushArgv }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(repush);
        if (repush.Success)
        {
            var publishedSha = await ReadVerifiedHeadAsync(exec, branch, "verify-publication", ct).ConfigureAwait(false);
            return new SandboxWorkBranchPublication(publishedSha, sourceSha, targetSha, Reconciled: true);
        }

        if (IsNonFastForwardRejection(repush.Stdout, repush.Stderr))
            throw new SandboxPushReconcileConflictException(
                branch, "moving-target", stage: "push-after-reconcile", sourceSha: sourceSha, targetSha: targetSha);

        throw new SandboxWorkBranchPublishException(
            branch, "push-after-reconcile", sourceSha, targetSha, detail: DescribeGitFailure(repush));
    }

    private static async Task<string> ReadVerifiedHeadAsync(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> exec, string branch, string stage, CancellationToken ct)
    {
        var head = await exec(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "--verify", "HEAD"],
        }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(head);
        if (!head.Success)
            throw new SandboxWorkBranchPublishException(branch, stage, detail: DescribeGitFailure(head));
        var sha = head.Stdout.Trim();
        Validation.ValidateCommitSha(sha, "sourceHead");
        return sha;
    }

    private static async Task<string> ReadRemoteTrackingTipAsync(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> exec, string branch, CancellationToken ct)
    {
        var tip = await exec(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "rev-parse", "--verify", $"refs/remotes/origin/{branch}"],
        }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(tip);
        if (!tip.Success)
            throw new SandboxWorkBranchPublishException(
                branch, "read-target", sourceSha: null, targetSha: null, detail: DescribeGitFailure(tip));
        var sha = tip.Stdout.Trim();
        Validation.ValidateCommitSha(sha, "reconcileTarget");
        return sha;
    }

    private static async Task UpdatePreservationRefAsync(
        Func<SandboxExec, CancellationToken, Task<SandboxExecResult>> exec,
        string preservationRef,
        string sha,
        string stage,
        string branch,
        CancellationToken ct)
    {
        var pin = await exec(new SandboxExec
        {
            Argv = ["git", "-C", SandboxConventions.WorkDir, "update-ref", preservationRef, sha],
        }, ct).ConfigureAwait(false);
        PipelineAgentExecutor.ThrowIfExecutionUnavailable(pin);
        if (!pin.Success)
            throw new SandboxWorkBranchPublishException(
                branch, stage, detail: DescribeGitFailure(pin));
    }

    private static bool IsNonFastForwardRejection(string stdout, string stderr)
    {
        var output = stdout + "\n" + stderr;
        return output.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || output.Contains("! [rejected]", StringComparison.OrdinalIgnoreCase)
            || output.Contains("fetch first", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeGitFailure(SandboxExecResult result)
    {
        var excerpt = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        var redacted = RawOutputRedactor.Redact(excerpt.Trim());
        return $"exit {result.ExitCode}: {RawOutputRedactor.TruncateToBytes(redacted, DiagnosticExcerptBytes)}";
    }

    private static string ToShortSha(string sha) =>
        sha.Length >= 12 ? sha[..12] : "unknown";
}
