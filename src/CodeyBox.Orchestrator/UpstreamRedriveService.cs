using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// One-off reconciliation for work items stranded by a stale upstream branch
/// ref: items in <c>Failed</c> / <c>MergeConflictResolutionFailed</c> /
/// <c>Merged</c> that still have an open pull request on their own
/// <c>codeybox/*</c> branch. The classic instance is a retried item whose new
/// work was composed on a fresh base while the remote branch (and its PR)
/// still points at the previous attempt's head: a plain retry parks again
/// because the lease guard cannot prove the remote tip is CodeyBox's own
/// history.
/// </summary>
public sealed class UpstreamRedriveService
{
    /// <summary>States eligible for an upstream re-drive. In-flight states are
    /// never offered: re-dispatching an item another worker owns would corrupt
    /// its pipeline.</summary>
    public static readonly IReadOnlyList<WorkItemState> EligibleStates =
    [
        WorkItemState.Failed,
        WorkItemState.MergeConflictResolutionFailed,
        WorkItemState.Merged,
    ];

    private readonly IWorkItemStore _store;
    private readonly IProjectRepository _projects;
    private readonly IUpstreamRemoteFactory _upstreamFactory;
    private readonly WorkItemRetrier _retrier;
    private readonly ILogger<UpstreamRedriveService> _log;

    public UpstreamRedriveService(
        IWorkItemStore store,
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        WorkItemRetrier retrier,
        ILogger<UpstreamRedriveService> log)
    {
        _store = store;
        _projects = projects;
        _upstreamFactory = upstreamFactory;
        _retrier = retrier;
        _log = log;
    }

    public sealed record UpstreamRedriveCandidate(
        string ProjectId,
        string WorkItemId,
        string Title,
        WorkItemState State,
        string WorkBranch,
        int PullRequestNumber,
        string PullRequestUrl,
        string PullRequestHeadSha,
        string? LastPushedWorkBranchSha,
        string Reason);

    /// <summary>
    /// Lists items eligible for an operator-authorized upstream re-drive:
    /// settled in a redrivable state with an open PR on their own owned
    /// branch. Read-only; safe to call on demand and once at startup.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamRedriveCandidate>> ListCandidatesAsync(CancellationToken ct = default)
    {
        var candidates = new List<UpstreamRedriveCandidate>();
        IReadOnlyList<Project> projects;
        try
        {
            projects = await _projects.ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Upstream re-drive scan: failed to enumerate projects");
            return candidates;
        }

        foreach (var project in projects)
        {
            IUpstreamRemote upstream;
            try
            {
                upstream = _upstreamFactory.Create(project);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "Upstream re-drive scan: skipping project {ProjectId}; upstream factory threw", project.Id.Value);
                continue;
            }

            IReadOnlyList<UpstreamPullRequest> openPrs;
            try
            {
                openPrs = await upstream.ListOpenPullRequestsAsync(CodeyBoxBranchPolicy.OwnedBranchPrefix, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Upstream re-drive scan: listing open PRs failed for project {ProjectId}", project.Id.Value);
                continue;
            }

            foreach (var pr in openPrs)
            {
                if (ct.IsCancellationRequested)
                    return candidates;
                var candidate = await TryBuildCandidateAsync(project, pr, ct);
                if (candidate is not null)
                    candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private async Task<UpstreamRedriveCandidate?> TryBuildCandidateAsync(
        Project project, UpstreamPullRequest pr, CancellationToken ct)
    {
        WorkItem? item;
        try
        {
            item = await _store.GetByMergedPrNumberAsync(project.Id, pr.Number, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Upstream re-drive scan: could not resolve work item for PR #{Number}", pr.Number);
            return null;
        }

        if (item is null || !EligibleStates.Contains(item.State))
            return null;
        if (!CodeyBoxBranchPolicy.IsOwnedWorkBranch(item.WorkBranch)
            || !string.Equals(item.WorkBranch, pr.HeadBranch, StringComparison.Ordinal))
            return null;

        var reason = string.Equals(item.FailureKind, WorkItemFailureKinds.UpstreamBlocked, StringComparison.OrdinalIgnoreCase)
            ? "upstream push blocked by the owned-branch lease guard; re-drive rewrites the branch under lease after operator authorization"
            : $"item is {item.State} with an open PR on its own branch; re-drive resumes the upstream step under the lease rules";
        return new UpstreamRedriveCandidate(
            project.Id.Value,
            item.Id.ToString(),
            item.Title,
            item.State,
            item.WorkBranch!,
            pr.Number,
            pr.Url,
            pr.HeadSha,
            item.LastPushedWorkBranchSha,
            reason);
    }

    public sealed record UpstreamRedriveResult(
        bool Success,
        string? Error,
        string? ResumeState,
        string? ActualFrom,
        int PullRequestNumber,
        string LeaseBaseSha);

    /// <summary>
    /// Operator-authorized re-drive of the upstream step for one item: records
    /// the PR's currently observed head sha as the lease base (the operator,
    /// by invoking this, asserts no third party pushed to the branch) and
    /// retries from the upstream phase under the normal lease rules. A third
    /// party push that lands between the observation and the guarded push
    /// still fails the lease and parks the item — authorization covers the
    /// observed tip, never a blind overwrite.
    /// </summary>
    public async Task<UpstreamRedriveResult> RedriveAsync(WorkItemId id, CancellationToken ct = default)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is null)
            return new UpstreamRedriveResult(false, $"work item {id} not found", null, null, 0, string.Empty);
        if (!EligibleStates.Contains(item.State))
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream for item in state {item.State}; only {string.Join(", ", EligibleStates)} are eligible",
                null, null, 0, string.Empty);
        if (!CodeyBoxBranchPolicy.IsOwnedWorkBranch(item.WorkBranch))
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream: work branch '{item.WorkBranch ?? "(unset)"}' is not a CodeyBox-owned branch",
                null, null, 0, string.Empty);
        if (item.MergedPrNumber is not { } prNumber)
            return new UpstreamRedriveResult(
                false, "cannot re-drive upstream: item has no recorded pull request number", null, null, 0, string.Empty);

        var project = await _projects.GetAsync(item.ProjectId, ct);
        if (project is null)
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: project {item.ProjectId.Value} not found", null, null, 0, string.Empty);

        IUpstreamRemote upstream;
        try
        {
            upstream = _upstreamFactory.Create(project);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: upstream factory threw: {ex.Message}", null, null, 0, string.Empty);
        }

        IReadOnlyList<UpstreamPullRequest> openPrs;
        try
        {
            openPrs = await upstream.ListOpenPullRequestsAsync(CodeyBoxBranchPolicy.OwnedBranchPrefix, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: listing open PRs failed: {ex.Message}", null, null, 0, string.Empty);
        }

        var pr = openPrs.FirstOrDefault(p => p.Number == prNumber);
        if (pr is null)
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream: no open PR #{prNumber} for this item (closed or merged); open a fresh PR manually or retry from an earlier phase",
                null, null, 0, string.Empty);
        if (!string.Equals(pr.HeadBranch, item.WorkBranch, StringComparison.Ordinal))
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream: open PR #{prNumber} targets branch '{pr.HeadBranch}', not the item's work branch '{item.WorkBranch}'",
                null, null, 0, string.Empty);
        string leaseBase;
        try
        {
            Validation.ValidateCommitSha(pr.HeadSha, "prHeadSha");
            leaseBase = pr.HeadSha;
        }
        catch (ArgumentException)
        {
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream: open PR #{prNumber} reports an invalid head sha; refusing to build a lease from it",
                null, null, 0, string.Empty);
        }

        var current = await _store.GetAsync(item.Id, ct) ?? item;
        await _store.UpdateAsync(current with { LastPushedWorkBranchSha = leaseBase }, ct);
        _log.LogInformation(
            "Upstream re-drive for work item {Id}: recorded PR #{Pr} head {Sha} as the operator-authorized lease base",
            item.Id, prNumber, leaseBase);

        var retry = await _retrier.RetryAsync(
            current with { LastPushedWorkBranchSha = leaseBase },
            RetryFromPolicy.Upstream,
            "upstream-redrive",
            ct);
        if (!retry.Success)
            return new UpstreamRedriveResult(
                false, retry.Error ?? "re-drive retry was refused", null, retry.ActualFrom, prNumber, leaseBase);
        return new UpstreamRedriveResult(
            true, null, retry.ResumeState?.ToString(), retry.ActualFrom, prNumber, leaseBase);
    }
}
