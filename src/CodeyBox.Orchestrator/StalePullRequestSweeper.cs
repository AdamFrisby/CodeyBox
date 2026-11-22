using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Detail payload for the <c>upstream.pr_stale_base</c> webhook event. Emitted
/// when a CodeyBox-authored pull request's base branch has moved and produced
/// a merge conflict the auto-merger can no longer resolve.
///
/// <para>This event is always emitted as the detection signal. When
/// <see cref="StalePullRequestSweeperOptions.RouteToConflictRework"/> is
/// disabled (the default) that is the only reaction — operators (or a
/// downstream tracker) rebase the PR manually, close it as superseded, or
/// re-run the work item. When routing is enabled the sweeper additionally
/// drives the owning work item into the conflict-rework state machine (bounded
/// by <see cref="StalePullRequestSweeperOptions.MaxReworkAttempts"/>).</para>
/// </summary>
public sealed record StalePullRequestDetails
{
    public required string ProjectId { get; init; }
    public required int PullRequestNumber { get; init; }
    public required string PullRequestUrl { get; init; }
    public required string HeadBranch { get; init; }
    public required string HeadSha { get; init; }
    public required string BaseBranch { get; init; }
    /// <summary>
    /// First time this <c>(projectId, pr, headSha)</c> identity was observed
    /// as stale, in UTC. Lets receivers tell first-detection from late re-fire
    /// (e.g. after a CodeyBox restart wipes the in-memory dedup set).
    /// </summary>
    public required DateTimeOffset FirstDetectedAt { get; init; }
}

/// <summary>
/// Periodic background sweep that detects CodeyBox-authored pull requests
/// whose base branch has moved and produced a merge conflict the orchestrator
/// can no longer resolve in-pipeline. For each newly-detected stale PR the
/// sweeper emits an <c>upstream.pr_stale_base</c> webhook event and an audit
/// log entry so operators see the orphan within minutes rather than days.
///
/// <para><b>Remediation:</b> when
/// <see cref="StalePullRequestSweeperOptions.RouteToConflictRework"/> is enabled
/// and the remediation collaborators are wired, the sweeper goes beyond
/// notifying: it looks up the owning work item and routes it into the existing
/// conflict-rework state machine (rebase onto the refreshed canonical base +
/// agentic conflict resolution + re-merge/re-push) via
/// <see cref="StaleBaseConflictReworkRouter"/>. On exhausting the configured
/// attempt cap the item is parked at
/// <see cref="WorkItemState.MergeConflictResolutionFailed"/>. When the flag is
/// off (the default) the sweeper stays notify-only.</para>
///
/// <para><b>Identity / idempotency:</b> a stale PR is identified by the
/// tuple <c>(projectId, prNumber, headSha)</c>. Repeated observations of the
/// same identity do not re-fire the event. If the operator pushes a new
/// commit to the PR (changing <c>headSha</c>) and it is still stale, a fresh
/// event fires — this lets a tracker telling "PR has unresolved conflicts"
/// from "PR's most recent rebase attempt also failed".</para>
///
/// <para><b>Scope:</b> currently only PRs whose head branch begins with the
/// configured prefix (default <c>codeybox/</c>) are considered, since human
/// authored PRs are not the orchestrator's concern.</para>
/// </summary>
public sealed class StalePullRequestSweeper : BackgroundService
{
    private readonly IProjectRepository _projects;
    private readonly IUpstreamRemoteFactory _upstreamFactory;
    private readonly IWebhookDispatcher _webhooks;
    private readonly Func<StalePullRequestSweeperOptions> _optsAccessor;
    private readonly ILogger<StalePullRequestSweeper> _log;
    private readonly TimeProvider _time;

    // Optional remediation collaborators. When both are wired AND the
    // RouteToConflictRework option is on, a detected stale-base conflict drives
    // the owning work item into the conflict-rework state machine instead of
    // only firing the notify signal. Null (or option off) preserves the
    // historical notify-only behaviour.
    private readonly IWorkItemStore? _store;
    private readonly StaleBaseConflictReworkRouter? _reworkRouter;

    // Stale-PR identity → first-detection timestamp. Used both to dedupe the
    // event firing (repeated ticks against the same identity stay quiet) and
    // to recover the original detection timestamp when the resolved-event
    // fires after a base-branch rebase.
    private readonly Dictionary<StalePrIdentity, DateTimeOffset> _firstSeenAt = [];

    private StalePullRequestSweeperOptions _opts => _optsAccessor();

    /// <summary>
    /// Constant-options constructor for unit tests that don't need the
    /// IOptionsMonitor hot-reload path.
    /// </summary>
    public StalePullRequestSweeper(
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        IWebhookDispatcher webhooks,
        StalePullRequestSweeperOptions opts,
        ILogger<StalePullRequestSweeper> log,
        TimeProvider? time = null,
        IWorkItemStore? store = null,
        StaleBaseConflictReworkRouter? reworkRouter = null)
        : this(projects, upstreamFactory, webhooks, () => opts, log, time, store, reworkRouter) { }

    public StalePullRequestSweeper(
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        IWebhookDispatcher webhooks,
        Func<StalePullRequestSweeperOptions> optsAccessor,
        ILogger<StalePullRequestSweeper> log,
        TimeProvider? time = null,
        IWorkItemStore? store = null,
        StaleBaseConflictReworkRouter? reworkRouter = null)
    {
        _projects = projects;
        _upstreamFactory = upstreamFactory;
        _webhooks = webhooks;
        _optsAccessor = optsAccessor;
        _log = log;
        _time = time ?? TimeProvider.System;
        _store = store;
        _reworkRouter = reworkRouter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first tick a little so we don't race the rest of the
        // hosted-service startup (project repo cold reads, etc.).
        try { await Task.Delay(TimeSpan.FromSeconds(5), _time, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // The interval is re-read from the live options throughout the wait
        // (in bounded slices), so CheckInterval edits hot-reload without a
        // restart — a single fixed PeriodicTimer/Task.Delay would pin the
        // startup value until it elapsed.
        var cycleStart = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_opts.Enabled)
            {
                try
                {
                    await RunSweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (UpstreamApiDeferredException)
                {
                    // The shared GitHub budget tracker already logged the
                    // single low-budget warning with reset time and top
                    // consumers; per-tick deferrals stay quiet.
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "StalePullRequestSweeper sweep failed; will retry next tick");
                }
            }

            // Wait until cycleStart + current CheckInterval, re-evaluating the
            // due time every slice so a shorter configured interval takes
            // effect within one slice rather than after the old delay ends.
            while (true)
            {
                var due = cycleStart + ClampInterval(_opts.CheckInterval);
                var remaining = due - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                    break;
                var slice = remaining < WaitSlice ? remaining : WaitSlice;
                try { await Task.Delay(slice, _time, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
            cycleStart = _time.GetUtcNow();
        }
    }

    /// <summary>Upper bound on a single options re-read while waiting between sweeps.</summary>
    private static readonly TimeSpan WaitSlice = TimeSpan.FromSeconds(5);

    private static TimeSpan ClampInterval(TimeSpan configured)
    {
        // Floor the polling cadence at 30 s so a misconfigured value cannot
        // hammer the GitHub API. The 5-minute SLA in the bug spec is met
        // comfortably by the default 60 s.
        var minInterval = TimeSpan.FromSeconds(30);
        return configured < minInterval ? minInterval : configured;
    }

    /// <summary>
    /// Runs a single sweep across all projects. Exposed for tests so they can
    /// drive the sweep directly without waiting on the PeriodicTimer.
    /// </summary>
    internal async Task RunSweepAsync(CancellationToken ct)
    {
        IReadOnlyList<Project> projects;
        try
        {
            projects = await _projects.ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "StalePullRequestSweeper: failed to enumerate projects");
            return;
        }

        var prefix = _opts.BranchPrefix;
        if (string.IsNullOrEmpty(prefix))
        {
            _log.LogWarning("StalePullRequestSweeper: BranchPrefix is empty; skipping sweep");
            return;
        }

        var observedIdentities = new HashSet<StalePrIdentity>();
        var sweptProjectIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            if (!string.Equals(project.Upstream.Kind, "github", StringComparison.OrdinalIgnoreCase))
                continue;

            if (await SweepProjectAsync(project, prefix, observedIdentities, ct))
                sweptProjectIds.Add(project.Id.Value);
        }

        // Prune dedup entries only for projects whose PR list was observed
        // this sweep. A deferred or failed project has no current snapshot:
        // treating it as empty would erase dedup state and refire events for
        // PRs that are still stale once the budget recovers.
        var stale = _firstSeenAt.Keys
            .Where(k => sweptProjectIds.Contains(k.ProjectId) && !observedIdentities.Contains(k))
            .ToList();
        foreach (var key in stale) _firstSeenAt.Remove(key);
    }

    private async Task<bool> SweepProjectAsync(
        Project project,
        string prefix,
        HashSet<StalePrIdentity> observedIdentities,
        CancellationToken ct)
    {
        IUpstreamRemote upstream;
        try
        {
            upstream = _upstreamFactory.Create(project);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex,
                "StalePullRequestSweeper: skipping project {ProjectId}; upstream factory threw",
                project.Id.Value);
            return false;
        }

        IReadOnlyList<UpstreamPullRequest> openPrs;
        try
        {
            openPrs = await upstream.ListOpenPullRequestsAsync(prefix, ct);
        }
        catch (UpstreamApiDeferredException ex)
        {
            // Shared GitHub budget is below reserve or inside a known
            // rate-limit window: skip this project until the next tick. The
            // tracker already emitted the single low-budget warning.
            _log.LogInformation(
                "StalePullRequestSweeper: GitHub API budget deferred for project {ProjectId} until {DeferredUntil}; skipping this tick",
                project.Id.Value, ex.DeferredUntil);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "StalePullRequestSweeper: ListOpenPullRequestsAsync failed for project {ProjectId}",
                project.Id.Value);
            return false;
        }

        foreach (var pr in openPrs)
        {
            if (!pr.HasMergeConflict) continue;

            var identity = new StalePrIdentity(project.Id.Value, pr.Number, pr.HeadSha);
            observedIdentities.Add(identity);
            if (_firstSeenAt.ContainsKey(identity)) continue; // already fired

            var now = _time.GetUtcNow();
            _firstSeenAt[identity] = now;

            AuditLog.UpstreamPrStaleBaseDetected(
                project.Id, pr.Number, pr.HeadBranch, pr.BaseBranch, pr.HeadSha);

            try
            {
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "upstream.pr_stale_base",
                    Project = project,
                    Details = new StalePullRequestDetails
                    {
                        ProjectId = project.Id.Value,
                        PullRequestNumber = pr.Number,
                        PullRequestUrl = pr.Url,
                        HeadBranch = pr.HeadBranch,
                        HeadSha = pr.HeadSha,
                        BaseBranch = pr.BaseBranch,
                        FirstDetectedAt = now,
                    },
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "StalePullRequestSweeper: webhook publish failed for PR #{Number} in project {ProjectId}",
                    pr.Number, project.Id.Value);
            }

            // Beyond notify-only: drive the owning work item into the existing
            // conflict-rework state machine (gated by RouteToConflictRework).
            await TryRouteToReworkAsync(project, pr, ct);
        }

        return true;
    }

    /// <summary>
    /// Looks up the work item that opened <paramref name="pr"/> and, when the
    /// feature is enabled, routes it into conflict-rework (or parks it at the
    /// existing terminal on cap exhaustion). A no-op when the remediation
    /// collaborators are not wired or the feature is off — the notify signal
    /// already fired above, so the sweeper stays notify-only in that case.
    /// </summary>
    private async Task TryRouteToReworkAsync(Project project, UpstreamPullRequest pr, CancellationToken ct)
    {
        if (_store is null || _reworkRouter is null || !_reworkRouter.Enabled)
            return;

        WorkItem? item;
        try
        {
            item = await _store.GetByMergedPrNumberAsync(project.Id, pr.Number, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "StalePullRequestSweeper: could not resolve work item for PR #{Number} in project {ProjectId}",
                pr.Number, project.Id.Value);
            return;
        }

        if (item is null)
        {
            _log.LogDebug(
                "StalePullRequestSweeper: no work item recorded for PR #{Number} in project {ProjectId}; nothing to route",
                pr.Number, project.Id.Value);
            return;
        }

        // Only remediate items that have finished work/audit and reached (or
        // parked past) the push. Re-dispatching an item another worker is
        // actively running would corrupt its in-flight pipeline.
        if (!IsEligibleForStaleBaseRework(item.State))
        {
            _log.LogDebug(
                "StalePullRequestSweeper: work item {Id} for PR #{Number} is in state {State}; not eligible for stale-base rework",
                item.Id, pr.Number, item.State);
            return;
        }

        StaleBaseReworkOutcome outcome;
        try
        {
            outcome = await _reworkRouter.TryRouteAsync(
                item,
                "stale-pr-sweep",
                StaleBaseConflictReworkRouter.SweeperEligibleSourceStates,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "StalePullRequestSweeper: routing work item {Id} for PR #{Number} into conflict-rework failed",
                item.Id, pr.Number);
            return;
        }

        switch (outcome)
        {
            case StaleBaseReworkOutcome.Routed:
                _log.LogInformation(
                    "StalePullRequestSweeper: routed work item {Id} into conflict-rework for stale PR #{Number}",
                    item.Id, pr.Number);
                break;
            case StaleBaseReworkOutcome.CapExhausted:
                await ParkAtMergeConflictFailedAsync(project, item, pr, ct);
                break;
            case StaleBaseReworkOutcome.NotEnabled:
            default:
                break;
        }
    }

    /// <summary>
    /// Parks a work item that has exhausted its stale-base rework budget at the
    /// existing terminal <see cref="WorkItemState.MergeConflictResolutionFailed"/>.
    /// Uses a conditional (compare-and-set) update so a concurrent transition
    /// (e.g. the item being re-dispatched by another surface) is not clobbered.
    /// </summary>
    private async Task ParkAtMergeConflictFailedAsync(
        Project project, WorkItem item, UpstreamPullRequest pr, CancellationToken ct)
    {
        if (_store is null)
            return;

        if (item.State == WorkItemState.MergeConflictResolutionFailed)
            return; // already parked; nothing to do

        var reason =
            $"stale-base PR #{pr.Number} exhausted the conflict-rework attempt cap " +
            $"({item.ConflictReworkAttempts}); manual resolution required";
        var parked = item.With(WorkItemState.MergeConflictResolutionFailed, reason);

        bool updated;
        try
        {
            updated = await _store.TryUpdateIfStateAsync(parked, item.State, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "StalePullRequestSweeper: parking work item {Id} at MergeConflictResolutionFailed failed",
                item.Id);
            return;
        }

        if (!updated)
        {
            _log.LogDebug(
                "StalePullRequestSweeper: work item {Id} changed state before cap-exhaustion park; skipping",
                item.Id);
            return;
        }

        try
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.merge_conflict_resolution_failed",
                WorkItem = parked,
                Project = project,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "StalePullRequestSweeper: cap-exhaustion webhook publish failed for work item {Id}",
                item.Id);
        }

        _log.LogInformation(
            "StalePullRequestSweeper: parked work item {Id} at MergeConflictResolutionFailed after exhausting stale-base rework attempts for PR #{Number}",
            item.Id, pr.Number);
    }

    /// <summary>
    /// A work item may be routed into stale-base rework only from a settled
    /// post-push state. In-flight states (work/audit/merge/push in progress)
    /// are excluded so the sweeper never re-dispatches an item another worker
    /// owns. Delegates to <see cref="StaleBaseConflictReworkRouter"/>, which owns
    /// the eligible-state set and re-checks it atomically at the re-dispatch —
    /// this up-front check only short-circuits an obviously ineligible snapshot.
    /// </summary>
    private static bool IsEligibleForStaleBaseRework(WorkItemState state) =>
        StaleBaseConflictReworkRouter.SweeperEligibleSourceStates.Contains(state);

    /// <summary>
    /// Identity tuple for dedup: project, PR number, and the head sha at the
    /// time we observed staleness. Re-firing on head-sha change is intentional —
    /// it gives operators a fresh signal that a rebase attempt did not resolve
    /// the conflict.
    /// </summary>
    private readonly record struct StalePrIdentity(string ProjectId, int PullRequestNumber, string HeadSha);
}
