using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// One-off reconciliation for work items stranded by a stale upstream branch
/// ref: items in <c>Failed</c> / <c>MergeConflictResolutionFailed</c> /
/// <c>Merged</c> whose own <c>codeybox/*</c> branch has a stale or diverged
/// remote. The classic instance is a retried item whose new work was composed
/// on a fresh base while the remote branch (and its PR) still points at the
/// previous attempt's head: a plain retry parks again because the lease guard
/// cannot prove the remote tip is CodeyBox's own history.
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
    private readonly UpstreamBranchOwnershipSnapshot _ownership;
    private readonly CommitAttributionPolicy? _attributionPolicy;

    public UpstreamRedriveService(
        IWorkItemStore store,
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        WorkItemRetrier retrier,
        ILogger<UpstreamRedriveService> log,
        UpstreamBranchOwnershipSnapshot? ownership = null,
        CommitAttributionPolicy? attributionPolicy = null)
    {
        _store = store;
        _projects = projects;
        _upstreamFactory = upstreamFactory;
        _retrier = retrier;
        _log = log;
        _ownership = ownership ?? new UpstreamBranchOwnershipSnapshot(new UpstreamBranchOwnershipOptions());
        _attributionPolicy = attributionPolicy;
    }

    public sealed record UpstreamRedriveCandidate(
        string ProjectId,
        string WorkItemId,
        string Title,
        WorkItemState State,
        string WorkBranch,
        int? PullRequestNumber,
        string? PullRequestUrl,
        string? PullRequestHeadSha,
        string? LastPushedWorkBranchSha,
        string Reason);

    public async Task<IReadOnlyList<UpstreamRedriveCandidate>> ListCandidatesAsync(CancellationToken ct = default)
    {
        var candidates = new List<UpstreamRedriveCandidate>();
        var projects = await _projects.ListAsync(ct);
        var projectsById = projects.ToDictionary(p => p.Id);
        var prsByProject = new Dictionary<string, ProjectPrIndex>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            IReadOnlyList<UpstreamPullRequest> openPrs;
            try
            {
                var upstream = _upstreamFactory.Create(project);
                openPrs = await upstream.ListOpenPullRequestsAsync(CodeyBoxBranchPolicy.OwnedBranchPrefix, ct);
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    "Skipping project {Project} in upstream re-drive candidate scan: {Message}",
                    project.Id.Value, ex.Message);
                continue;
            }
            prsByProject[project.Id.Value] = ProjectPrIndex.Build(openPrs);
        }

        await foreach (var item in _store.ListAsync(ct))
        {
            if (!EligibleStates.Contains(item.State))
                continue;
            if (string.IsNullOrWhiteSpace(item.WorkBranch)
                || !CodeyBoxBranchPolicy.IsOwnedWorkBranch(item.WorkBranch))
                continue;
            if (!prsByProject.TryGetValue(item.ProjectId.Value, out var index))
                continue;

            var workBranch = item.WorkBranch;
            UpstreamPullRequest? match = null;
            if (item.MergedPrNumber is { } recorded
                && index.ByNumber.TryGetValue(recorded, out var byNumber)
                && string.Equals(byNumber.HeadBranch, workBranch, StringComparison.Ordinal))
            {
                match = byNumber;
            }
            match ??= index.FindByBranch(workBranch);

            if (match is not null)
            {
                candidates.Add(new UpstreamRedriveCandidate(
                    item.ProjectId.Value,
                    item.Id.ToString(),
                    item.Title,
                    item.State,
                    workBranch,
                    match.Number,
                    match.Url,
                    match.HeadSha,
                    item.LastPushedWorkBranchSha,
                    string.Equals(item.FailureKind, WorkItemFailureKinds.UpstreamBlocked, StringComparison.OrdinalIgnoreCase)
                        ? "upstream_blocked: owned-branch lease guard diverged from an unrecorded prior push — candidate for operator-authorized re-drive"
                        : "open PR on the item's own branch after a settled state — candidate for operator-authorized re-drive"));
                continue;
            }

            if (!string.Equals(item.FailureKind, WorkItemFailureKinds.UpstreamBlocked, StringComparison.OrdinalIgnoreCase))
                continue;

            IUpstreamRemote itemUpstream;
            try
            {
                if (!projectsById.TryGetValue(item.ProjectId, out var candidateProject))
                    continue;
                itemUpstream = _upstreamFactory.Create(candidateProject);
            }
            catch (Exception)
            {
                continue;
            }
            var remoteHead = await SafeGetBranchHeadAsync(itemUpstream, workBranch, ct).ConfigureAwait(false);
            if (remoteHead is null)
                continue;
            candidates.Add(new UpstreamRedriveCandidate(
                item.ProjectId.Value,
                item.Id.ToString(),
                item.Title,
                item.State,
                workBranch,
                null,
                null,
                remoteHead,
                item.LastPushedWorkBranchSha,
                "upstream_blocked with no open PR on the branch but a live remote tip — re-drive pushes the branch alone and opens a fresh PR"));
        }

        return candidates;
    }

    private sealed class ProjectPrIndex
    {
        public readonly Dictionary<int, UpstreamPullRequest> ByNumber = new();
        public readonly Dictionary<string, List<UpstreamPullRequest>> ByBranch =
            new(StringComparer.Ordinal);

        public static ProjectPrIndex Build(IReadOnlyList<UpstreamPullRequest> openPrs)
        {
            var index = new ProjectPrIndex();
            foreach (var pr in openPrs)
            {
                index.ByNumber[pr.Number] = pr;
                if (!string.IsNullOrWhiteSpace(pr.HeadBranch))
                {
                    if (!index.ByBranch.TryGetValue(pr.HeadBranch, out var list))
                        index.ByBranch[pr.HeadBranch] = list = [];
                    list.Add(pr);
                }
            }
            return index;
        }

        public UpstreamPullRequest? FindByBranch(string workBranch)
        {
            if (!ByBranch.TryGetValue(workBranch, out var list) || list.Count == 0)
                return null;
            return list.OrderBy(p => p.Number).First();
        }
    }

    public sealed record UpstreamRedriveResult(
        bool Success,
        string? Error,
        WorkItemState? ResumeState,
        string? ActualFrom,
        int? PullRequestNumber,
        string? LeaseBaseSha);

    /// <summary>
    /// Operator-authorized re-drive without an explicit ownership assertion.
    /// Items with no recorded push still require the remote tip to prove
    /// itself via commit trailers; pass <c>confirmOwnership: true</c> to
    /// assert ownership explicitly.
    /// </summary>
    public Task<UpstreamRedriveResult> RedriveAsync(WorkItemId id, CancellationToken ct = default)
        => RedriveAsync(id, confirmOwnership: false, ct);

    /// <summary>
    /// Operator-authorized re-drive of the upstream step for one stranded
    /// item. When the item has no recorded PR number, the open PR is
    /// discovered by head branch (<c>codeybox/&lt;id&gt;</c>), recorded on the
    /// item, and used; when no open PR exists at all, the push is re-driven
    /// alone and the pipeline opens a fresh PR. The observed remote tip is
    /// recorded as the lease base and the item resumes from the upstream
    /// phase under the normal lease rules — a concurrent third-party push
    /// still fails the lease and parks. Authorization covers the observed
    /// tip, never a blind overwrite.
    ///
    /// Ownership proof for branches with no recorded push: the remote tip is
    /// accepted only when every commit exclusive of the current base tip
    /// carries any accepted CodeyBox trailer, or when <paramref name="confirmOwnership"/>
    /// is true (the operator asserts the tip is CodeyBox's own history; logged
    /// and audited). Otherwise the re-drive is refused.
    /// </summary>
    public async Task<UpstreamRedriveResult> RedriveAsync(
        WorkItemId id, bool confirmOwnership, CancellationToken ct = default)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is null)
            return new UpstreamRedriveResult(false, $"work item {id} not found", null, null, 0, string.Empty);
        if (!EligibleStates.Contains(item.State))
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: work item {id} is {item.State}, not a settled upstream-blocked state", null, null, 0, string.Empty);
        if (string.IsNullOrWhiteSpace(item.WorkBranch)
            || !CodeyBoxBranchPolicy.IsOwnedWorkBranch(item.WorkBranch))
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: work item {id} has no CodeyBox-owned work branch", null, null, 0, string.Empty);

        var project = await _projects.GetAsync(item.ProjectId, ct);
        if (project is null)
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: project {item.ProjectId} not found", null, null, 0, string.Empty);

        var workBranch = item.WorkBranch;
        IUpstreamRemote upstream;
        try
        {
            upstream = _upstreamFactory.Create(project);
        }
        catch (Exception ex)
        {
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: failed to resolve upstream remote: {ex.Message}", null, null, 0, string.Empty);
        }

        IReadOnlyList<UpstreamPullRequest> openPrs;
        try
        {
            openPrs = await upstream.ListOpenPullRequestsAsync(CodeyBoxBranchPolicy.OwnedBranchPrefix, ct);
        }
        catch (Exception ex)
        {
            return new UpstreamRedriveResult(
                false, $"cannot re-drive upstream: failed to list open pull requests: {ex.Message}", null, null, 0, string.Empty);
        }

        UpstreamPullRequest? targetPr = null;
        if (item.MergedPrNumber is { } recordedNumber)
        {
            var pr = openPrs.FirstOrDefault(p => p.Number == recordedNumber);
            if (pr is null)
                return new UpstreamRedriveResult(
                    false, $"cannot re-drive upstream: no open PR #{recordedNumber} for work branch '{workBranch}'", null, null, 0, string.Empty);
            if (!string.Equals(pr.HeadBranch, workBranch, StringComparison.Ordinal))
                return new UpstreamRedriveResult(
                    false, $"cannot re-drive upstream: recorded PR #{recordedNumber} targets '{pr.HeadBranch}', not work branch '{workBranch}'", null, null, 0, string.Empty);
            try
            {
                Validation.ValidateCommitSha(pr.HeadSha, "pull request head sha");
            }
            catch (ArgumentException ex)
            {
                return new UpstreamRedriveResult(
                    false, $"cannot re-drive upstream: recorded PR #{recordedNumber} reports an invalid head sha: {ex.Message}", null, null, 0, string.Empty);
            }
            targetPr = pr;
        }
        else
        {
            targetPr = openPrs
                .Where(p => string.Equals(p.HeadBranch, workBranch, StringComparison.Ordinal))
                .OrderBy(p => p.Number)
                .FirstOrDefault();
            if (targetPr is not null)
            {
                try
                {
                    Validation.ValidateCommitSha(targetPr.HeadSha, "pull request head sha");
                }
                catch (ArgumentException ex)
                {
                    return new UpstreamRedriveResult(
                        false, $"cannot re-drive upstream: discovered PR #{targetPr.Number} reports an invalid head sha: {ex.Message}", null, null, 0, string.Empty);
                }
                _log.LogInformation(
                    "Upstream re-drive for work item {Id} discovered open PR #{Pr} on branch {Branch} (no recorded PR number); recording it",
                    item.Id.ToString(), targetPr.Number, workBranch);
            }
        }

        string? observedTip = targetPr?.HeadSha;
        if (observedTip is null)
        {
            observedTip = await SafeGetBranchHeadAsync(upstream, workBranch, ct).ConfigureAwait(false);
            if (observedTip is not null)
            {
                _log.LogInformation(
                    "Upstream re-drive for work item {Id} found no open PR on branch {Branch}; re-driving the push alone against remote tip {Sha}",
                    item.Id.ToString(), workBranch, observedTip);
            }
        }

        var fresh = await _store.GetAsync(id, ct);
        if (fresh is null)
            return new UpstreamRedriveResult(false, $"work item {id} not found", null, null, 0, string.Empty);
        var hasRecordedPush = !string.IsNullOrWhiteSpace(fresh.LastPushedWorkBranchSha);

        string? leaseBase = observedTip;
        if (observedTip is not null && !hasRecordedPush && !confirmOwnership)
        {
            var gate = await CheckOwnershipGateAsync(
                upstream, fresh, project, workBranch, observedTip, targetPr, ct).ConfigureAwait(false);
            if (gate is not null)
                return gate;
        }
        if (observedTip is not null && confirmOwnership)
        {
            _log.LogInformation(
                "Upstream re-drive for work item {Id} proceeds with operator-confirmed ownership of branch {Branch} tip {Sha}",
                item.Id.ToString(), workBranch, observedTip);
            AuditLog.UpstreamRedriveOwnershipConfirmed(item.Id, workBranch, observedTip);
        }

        var updated = fresh;
        if (targetPr is not null
            && (fresh.MergedPrNumber != targetPr.Number
                || !string.Equals(fresh.MergedPrUrl, targetPr.Url, StringComparison.Ordinal)))
        {
            updated = updated with
            {
                MergedPrNumber = targetPr.Number,
                MergedPrUrl = targetPr.Url,
            };
        }
        if (leaseBase is not null
            && !string.Equals(updated.LastPushedWorkBranchSha, leaseBase, StringComparison.OrdinalIgnoreCase))
        {
            updated = updated with { LastPushedWorkBranchSha = leaseBase };
        }
        if (!ReferenceEquals(updated, fresh))
            await _store.UpdateAsync(updated, ct);

        var retry = await _retrier.RetryAsync(updated, "upstream", "upstream-redrive", ct);
        if (!retry.Success)
            return new UpstreamRedriveResult(
                false, $"upstream re-drive not armed: {retry.Error}", null, null, 0, string.Empty);
        return new UpstreamRedriveResult(
            true, null, retry.ResumeState, retry.ActualFrom, targetPr?.Number, leaseBase ?? string.Empty);
    }

    /// <summary>
    /// Ownership gate for a tip with no recorded push and no explicit
    /// operator confirmation. The range is pinned to the CURRENT base tip
    /// (<c>&lt;base tip&gt;..&lt;remote tip&gt;</c>) so commits already
    /// reachable from the base — e.g. operator commits merged into the work
    /// branch — never count as third-party work. Returns null when the tip
    /// is accepted (every exclusive commit carries any accepted CodeyBox
    /// trailer, or the range is empty), otherwise a refusal result naming
    /// the offending commit shas and the working confirmation flag.
    /// </summary>
    private async Task<UpstreamRedriveResult?> CheckOwnershipGateAsync(
        IUpstreamRemote upstream,
        WorkItem item,
        Project project,
        string workBranch,
        string observedTip,
        UpstreamPullRequest? targetPr,
        CancellationToken ct)
    {
        var baseBranch = !string.IsNullOrWhiteSpace(item.BaseBranch)
            ? item.BaseBranch
            : !string.IsNullOrWhiteSpace(project.DefaultBaseBranch)
                ? project.DefaultBaseBranch
                : targetPr?.BaseBranch;
        if (string.IsNullOrWhiteSpace(baseBranch))
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream: branch '{workBranch}' has no recorded push and no base branch to prove ownership against. " +
                RedriveConfirmationHint(item.Id),
                null, null, 0, string.Empty);

        var attribution = _attributionPolicy?.Resolve(project) ?? CommitAttribution.Default;
        var accepted = BranchOwnershipPolicy.EffectiveAcceptedKeys(
            _ownership.Current.RequiredTrailerKeys ?? [], attribution);
        if (accepted.Length == 0)
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream: branch '{workBranch}' has no recorded push and commit attribution is disabled, " +
                $"so no trailer can prove tip {observedTip} is CodeyBox's own history. " +
                RedriveConfirmationHint(item.Id),
                null, null, 0, string.Empty);

        var baseRevision = await SafeResolveBaseRevisionAsync(upstream, baseBranch, ct).ConfigureAwait(false)
            ?? baseBranch;

        IReadOnlyList<OwnedBranchCommit>? commits;
        try
        {
            commits = await upstream.ListBranchCommitsAsync(
                baseRevision, observedTip, Math.Max(1, _ownership.Current.MaxCommitsToVerify), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                "Upstream re-drive ownership check for work item {Id} failed: {Message}",
                item.Id.ToString(), ex.Message);
            commits = null;
        }
        if (commits is null)
            return new UpstreamRedriveResult(
                false,
                $"cannot re-drive upstream: branch '{workBranch}' has no recorded push and its tip {observedTip} cannot be proven to be CodeyBox's own history (commit history unreadable). " +
                RedriveConfirmationHint(item.Id),
                null, null, 0, string.Empty);
        if (commits.Count == 0)
            return null;
        var unowned = BranchOwnershipPolicy.FindUnownedCommitShas(commits, accepted);
        if (unowned.Count == 0)
            return null;
        return new UpstreamRedriveResult(
            false,
            $"cannot re-drive upstream: remote tip {observedTip} of '{workBranch}' contains {unowned.Count} commit(s) without a CodeyBox trailer " +
            $"({BranchOwnershipPolicy.DescribeUnownedCommits(unowned)}), so it may include third-party work. " +
            RedriveConfirmationHint(item.Id),
            null, null, 0, string.Empty);
    }

    /// <summary>
    /// Resolves <paramref name="baseBranch"/> to its current head sha so the
    /// ownership range excludes everything already on the base. Returns null
    /// when the tip cannot be read; callers fall back to the branch name,
    /// which the forge resolves to the same tip.
    /// </summary>
    private async Task<string?> SafeResolveBaseRevisionAsync(
        IUpstreamRemote upstream, string baseBranch, CancellationToken ct)
    {
        try
        {
            var sha = await upstream.GetBranchHeadShaAsync(baseBranch, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sha))
                return null;
            Validation.ValidateCommitSha(sha.Trim(), "base branch head sha");
            return sha.Trim().ToLowerInvariant();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                "Upstream re-drive could not resolve current tip of base branch {Branch}: {Message}",
                baseBranch, ex.Message);
            return null;
        }
    }

    private static string RedriveConfirmationHint(WorkItemId id) =>
        $"After confirming no third party pushed to the branch, re-drive with POST /workitems/{id}/redrive-upstream passing {{\"{UpstreamOwnedBranchDivergedException.ConfirmOwnershipFlag}\": true}} to assert the remote tip is CodeyBox's own history.";

    private async Task<string?> SafeGetBranchHeadAsync(
        IUpstreamRemote upstream, string branch, CancellationToken ct)
    {
        try
        {
            var sha = await upstream.GetBranchHeadShaAsync(branch, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sha))
                return null;
            Validation.ValidateCommitSha(sha.Trim(), "branch head sha");
            return sha.Trim().ToLowerInvariant();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                "Upstream re-drive could not read remote tip of branch {Branch}: {Message}",
                branch, ex.Message);
            return null;
        }
    }
}
