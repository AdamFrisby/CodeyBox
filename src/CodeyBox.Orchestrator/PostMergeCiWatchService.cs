using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Post-merge CI watch: the merge-result gate proves the combined tree
/// BUILDS, but two individually-green branches can still combine into a
/// tree whose TESTS fail. After every landed merge this service polls the
/// forge CI status for the merge commit; a red tip immediately records a
/// test-kind base-broken condition (reusing the build-red hold machinery,
/// extended from build failures to test failures) and files one
/// top-priority fix item naming the failing checks and the suspect commits.
///
/// Design note (cheapest close of the merge-result gap): a pre-landing
/// affected-test run on the merged tree was considered and deferred — it
/// duplicates CI cost on every landing, and a flaky selected test would
/// block landings that CI would have cleared on retry. The watch reuses the
/// forge verdict CI already computes, costs one status poll per interval
/// per recent merge, and degrades gracefully: forges that cannot report
/// checks (or SHAs the forge never saw, e.g. local-only squash SHAs)
/// resolve to "unknown" and the watch drops, leaving detection to the
/// audit-time test-red check (genuine base reproductions + forge-red tip).
/// </summary>
public sealed class PostMergeCiWatchService : BackgroundService
{
    private readonly IProjectRepository _projects;
    private readonly IUpstreamRemoteFactory _upstreamFactory;
    private readonly BaseBrokenConditionTracker _tracker;
    private readonly IWorkItemStore _items;
    private readonly PipelineTuningSnapshot _tuning;
    private readonly TimeProvider _time;
    private readonly ILogger<PostMergeCiWatchService> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, WatchedMerge> _watches = new(StringComparer.Ordinal);

    /// <summary>One watched landing: identity plus poll budget state.</summary>
    internal sealed record WatchedMerge(
        ProjectId ProjectId,
        string BaseBranch,
        string MergeSha,
        WorkItemId SourceItemId,
        IReadOnlyList<string> SuspectCommits,
        int Attempts);

    public PostMergeCiWatchService(
        IProjectRepository projects,
        IUpstreamRemoteFactory upstreamFactory,
        BaseBrokenConditionTracker tracker,
        IWorkItemStore items,
        PipelineTuningSnapshot tuning,
        TimeProvider? timeProvider = null,
        ILogger<PostMergeCiWatchService>? log = null)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _upstreamFactory = upstreamFactory ?? throw new ArgumentNullException(nameof(upstreamFactory));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? NullLogger<PostMergeCiWatchService>.Instance;
    }

    /// <summary>
    /// Schedules a bounded CI watch for a landed merge commit. Best-effort:
    /// never throws (a scheduling fault must not fail a merge that already
    /// landed), drops invalid SHAs, and evicts the oldest watch past the
    /// configured cap (newest merge wins — the tip that matters).
    /// </summary>
    public void Schedule(
        ProjectId projectId,
        string baseBranch,
        string? mergeSha,
        WorkItemId sourceItemId,
        IReadOnlyList<string>? suspectCommits = null)
    {
        try
        {
            var sha = mergeSha?.Trim() ?? string.Empty;
            if (sha.Length == 0 || !BaseBrokenTestSupport.IsHexSha(sha))
            {
                _log.LogWarning(
                    "Post-merge CI watch not scheduled for project {ProjectId}: merge sha is not a commit sha",
                    projectId.Value);
                return;
            }
            var branch = string.IsNullOrWhiteSpace(baseBranch) ? "main" : baseBranch.Trim();
            var suspects = BaseBrokenTestSupport.SanitizeShas(
                suspectCommits is null || suspectCommits.Count == 0 ? [sha] : suspectCommits);
            if (suspects.Count == 0)
                suspects = [sha.ToLowerInvariant()];
            var key = $"{projectId.Value}\0{sha.ToLowerInvariant()}";
            var cap = Math.Max(1, _tuning.Current.PostMergeCiWatchMaxWatches);
            lock (_gate)
            {
                _watches[key] = new WatchedMerge(
                    projectId, branch, sha.ToLowerInvariant(), sourceItemId, suspects, 0);
                while (_watches.Count > cap)
                {
                    var oldest = _watches.Keys.First();
                    _watches.Remove(oldest);
                }
            }
            _log.LogInformation(
                "Scheduled post-merge CI watch for project {ProjectId}@{Sha}",
                projectId.Value, BaseBrokenTestSupport.ShortSha(sha));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Post-merge CI watch scheduling failed for project {ProjectId}; merge is unaffected",
                projectId.Value);
        }
    }

    /// <summary>Test seam: currently watched merge SHAs.</summary>
    internal IReadOnlyList<string> WatchedShasForTest()
    {
        lock (_gate)
            return _watches.Values.Select(w => w.MergeSha).ToList();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _tracker.HydrateAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Post-merge CI watch failed to hydrate the condition index");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = _tuning.Current.PostMergeCiWatchInterval;
            if (interval <= TimeSpan.Zero)
                return;
            try
            {
                await Task.Delay(interval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await PollOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deterministic entry point for tests; the loop just schedules it.</summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        List<WatchedMerge> snapshot;
        lock (_gate)
            snapshot = _watches.Values.ToList();
        foreach (var watch in snapshot)
        {
            try
            {
                await PollOneAsync(watch, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Post-merge CI watch failed for project {ProjectId}@{Sha}",
                    watch.ProjectId.Value, BaseBrokenTestSupport.ShortSha(watch.MergeSha));
                Drop(watch);
            }
        }
    }

    private async Task PollOneAsync(WatchedMerge watch, CancellationToken ct)
    {
        var tuning = _tuning.Current;
        if (!tuning.PostMergeCiWatchEnabled || !tuning.BaseBrokenDetectionEnabled)
        {
            Drop(watch);
            return;
        }

        var project = await _projects.GetAsync(watch.ProjectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            _log.LogWarning(
                "Post-merge CI watch: project {ProjectId} no longer resolves; dropping watch for {Sha}",
                watch.ProjectId.Value, BaseBrokenTestSupport.ShortSha(watch.MergeSha));
            Drop(watch);
            return;
        }

        UpstreamCheckSummary? summary;
        try
        {
            var remote = _upstreamFactory.Create(project);
            summary = await remote.GetCheckResultsAsync(watch.MergeSha, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Post-merge CI watch: could not read CI status for {Sha}; retrying within budget",
                BaseBrokenTestSupport.ShortSha(watch.MergeSha));
            BumpOrDrop(watch, tuning.PostMergeCiWatchMaxAttempts);
            return;
        }

        if (summary is null)
        {
            // Forge cannot report checks for this SHA (unsupported kind, or
            // a local-only SHA the forge never saw): unknown, not red. The
            // audit-time test-red check owns detection from here.
            Drop(watch);
            return;
        }

        if (summary.RequiredChecksPassed)
        {
            Drop(watch);
            return;
        }

        var failing = summary.Checks
            .Where(c => c.State == UpstreamCheckState.Failing)
            .Select(c => c.Name)
            .ToList();
        if (failing.Count == 0)
        {
            // Required checks unsatisfied but none individually failing
            // (still running): keep polling within budget.
            BumpOrDrop(watch, tuning.PostMergeCiWatchMaxAttempts);
            return;
        }

        await RecordRedAsync(project, watch, failing, ct).ConfigureAwait(false);
        Drop(watch);
    }

    private async Task RecordRedAsync(
        Project project,
        WatchedMerge watch,
        IReadOnlyList<string> failingChecks,
        CancellationToken ct)
    {
        var failingNames = BaseBrokenTestSupport.SanitizeNameListForRecord(failingChecks);
        var condition = new BaseBrokenCondition
        {
            ProjectId = watch.ProjectId,
            BaseBranch = watch.BaseBranch,
            BaseSha = watch.MergeSha,
            RepositoryId = watch.SourceItemId.ToString(),
            ErrorSummary = BaseBrokenTestSupport.BuildSummary(failingChecks, watch.SuspectCommits),
            FailureKind = BaseBrokenFailureKind.Test,
            FailingNames = failingNames,
            DetectedAt = _time.GetUtcNow(),
        };
        var recorded = await _tracker.RecordDetectedAsync(condition, ct).ConfigureAwait(false);
        var sourceItem = await _items.GetAsync(watch.SourceItemId, ct).ConfigureAwait(false);
        WorkItemId? fixItemId = null;
        if (sourceItem is not null)
        {
            fixItemId = await _tracker.EnsureFixItemAsync(
                recorded,
                sourceItem,
                _tuning.Current.BaseBrokenFixItemPriority,
                ct).ConfigureAwait(false);
        }
        else
        {
            _log.LogWarning(
                "Post-merge CI watch: source item {Source} for {Sha} no longer resolves; condition recorded without a fix item",
                watch.SourceItemId, BaseBrokenTestSupport.ShortSha(watch.MergeSha));
        }

        _log.LogWarning(
            "Post-merge CI watch: merge {Sha} on base '{Base}' is red ({Count} failing checks); recorded test-kind base-broken condition{FixNote}",
            BaseBrokenTestSupport.ShortSha(watch.MergeSha),
            watch.BaseBranch,
            failingNames.Count,
            fixItemId is { } fix ? $" (fix item {fix})" : string.Empty);
        AuditLog.BaseBrokenDetected(
            watch.ProjectId, watch.BaseBranch, watch.MergeSha, condition.ErrorSummary);
    }

    private void BumpOrDrop(WatchedMerge watch, int maxAttempts)
    {
        lock (_gate)
        {
            var key = $"{watch.ProjectId.Value}\0{watch.MergeSha}";
            if (!_watches.TryGetValue(key, out var current))
                return;
            if (current.Attempts + 1 >= Math.Max(1, maxAttempts))
                _watches.Remove(key);
            else
                _watches[key] = current with { Attempts = current.Attempts + 1 };
        }
    }

    private void Drop(WatchedMerge watch)
    {
        lock (_gate)
            _watches.Remove($"{watch.ProjectId.Value}\0{watch.MergeSha}");
    }
}
