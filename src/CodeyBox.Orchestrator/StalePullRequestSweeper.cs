using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Detail payload for the <c>upstream.pr_stale_base</c> webhook event. Emitted
/// when a CodeyBox-authored pull request's base branch has moved and produced
/// a merge conflict the auto-merger can no longer resolve.
///
/// <para>Operators (or a downstream tracker watching the event bus) react by
/// rebasing the PR manually, closing it as superseded, or re-running the work
/// item. The orchestrator itself takes no destructive action — see the bug
/// spec's option (c).</para>
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
        TimeProvider? time = null)
        : this(projects, upstreamFactory, webhooks, () => opts, log, time) { }

    public StalePullRequestSweeper(
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        IWebhookDispatcher webhooks,
        Func<StalePullRequestSweeperOptions> optsAccessor,
        ILogger<StalePullRequestSweeper> log,
        TimeProvider? time = null)
    {
        _projects = projects;
        _upstreamFactory = upstreamFactory;
        _webhooks = webhooks;
        _optsAccessor = optsAccessor;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogInformation("StalePullRequestSweeper disabled via configuration; skipping");
            return;
        }

        // Floor the polling cadence at 30 s so a misconfigured value cannot
        // hammer the GitHub API. The 5-minute SLA in the bug spec is met
        // comfortably by the default 60 s.
        var minInterval = TimeSpan.FromSeconds(30);
        var interval = _opts.CheckInterval < minInterval ? minInterval : _opts.CheckInterval;
        if (_opts.CheckInterval < minInterval)
            _log.LogWarning(
                "StalePullRequestSweeper: CheckInterval {Configured} is below the 30-second minimum; clamped to 30 s",
                _opts.CheckInterval);

        // Stagger the first tick a little so we don't race the rest of the
        // hosted-service startup (project repo cold reads, etc.).
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "StalePullRequestSweeper sweep failed; will retry next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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

        foreach (var project in projects)
        {
            if (!string.Equals(project.Upstream.Kind, "github", StringComparison.OrdinalIgnoreCase))
                continue;

            await SweepProjectAsync(project, prefix, observedIdentities, ct);
        }

        // Prune dedup entries for PRs that have disappeared from the sweep
        // (closed, merged, or rebased into mergeable). Without this, the
        // dictionary would accumulate forever; with it, a PR that re-enters
        // a dirty state after being fixed gets a fresh event correctly.
        var stale = _firstSeenAt.Keys.Where(k => !observedIdentities.Contains(k)).ToList();
        foreach (var key in stale) _firstSeenAt.Remove(key);
    }

    private async Task SweepProjectAsync(
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
            return;
        }

        IReadOnlyList<UpstreamPullRequest> openPrs;
        try
        {
            openPrs = await upstream.ListOpenPullRequestsAsync(prefix, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "StalePullRequestSweeper: ListOpenPullRequestsAsync failed for project {ProjectId}",
                project.Id.Value);
            return;
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
        }
    }

    /// <summary>
    /// Identity tuple for dedup: project, PR number, and the head sha at the
    /// time we observed staleness. Re-firing on head-sha change is intentional —
    /// it gives operators a fresh signal that a rebase attempt did not resolve
    /// the conflict.
    /// </summary>
    private readonly record struct StalePrIdentity(string ProjectId, int PullRequestNumber, string HeadSha);
}
