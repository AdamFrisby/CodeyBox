using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodic sweeper that clears recorded base-broken conditions once the
/// base is healthy again. For each active condition it refreshes the repo
/// tracking the broken base (the fix item's repo when one was filed —
/// upstream fetches land there first), re-resolves the base tip, and:
/// <list type="bullet">
///   <item>same SHA, build-kind → the verdict is immutable; no rebuild, still held.</item>
///   <item>same SHA, test-kind → the forge CI status for that SHA is
///   re-polled (pending can turn green); a passing tip clears, anything
///   else keeps the hold.</item>
///   <item>new SHA that builds → a build-kind condition clears; a test-kind
///   condition clears only when the new tip's CI also passes (a green build
///   alone must never clear a test-red hold — that is exactly the gap this
///   work closes).</item>
///   <item>new SHA that also fails → the old condition clears and a new
///   condition (keyed by the new SHA, with its own deduped fix item)
///   replaces it, preserving the failure kind.</item>
/// </list>
/// Interval is the hot-reloadable
/// <see cref="PipelineTuningOptions.BaseBrokenRecheckInterval"/>.
/// </summary>
public sealed class BaseBrokenMonitorService : BackgroundService
{
    private readonly BaseBrokenConditionTracker _tracker;
    private readonly BaseBuildVerifier _baseBuilds;
    private readonly IGitHost _gitHost;
    private readonly IProjectRepository _projects;
    private readonly IWorkItemStore _items;
    private readonly PipelineTuningSnapshot _tuning;
    private readonly IUpstreamRemoteFactory? _upstreamFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<BaseBrokenMonitorService> _log;

    public BaseBrokenMonitorService(
        BaseBrokenConditionTracker tracker,
        BaseBuildVerifier baseBuilds,
        IGitHost gitHost,
        IProjectRepository projects,
        IWorkItemStore items,
        PipelineTuningSnapshot tuning,
        TimeProvider? timeProvider = null,
        ILogger<BaseBrokenMonitorService>? log = null,
        IUpstreamRemoteFactory? upstreamFactory = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _baseBuilds = baseBuilds ?? throw new ArgumentNullException(nameof(baseBuilds));
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? NullLogger<BaseBrokenMonitorService>.Instance;
        _upstreamFactory = upstreamFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Restore the in-memory hold index from the durable store before the
        // first sweep — after a restart the dispatcher must see persisted
        // holds even if the monitor's interval has not elapsed yet.
        try
        {
            await _tracker.HydrateAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Base-broken monitor failed to hydrate the condition index");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = _tuning.Current.BaseBrokenRecheckInterval;
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

            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deterministic entry point for tests; the loop just schedules it.</summary>
    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        foreach (var condition in _tracker.GetActiveConditions())
        {
            try
            {
                await RecheckAsync(condition, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Base-broken monitor failed to re-check project {ProjectId} base '{Base}' tip {Sha}",
                    condition.ProjectId.Value, condition.BaseBranch, condition.BaseSha);
            }
        }
    }

    private async Task RecheckAsync(BaseBrokenCondition condition, CancellationToken ct)
    {
        var project = await _projects.GetAsync(condition.ProjectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            _log.LogWarning(
                "Base-broken monitor: project {ProjectId} no longer resolves; clearing condition for base tip {Sha}",
                condition.ProjectId.Value, condition.BaseSha);
            await _tracker.ClearAsync(condition.ProjectId, condition.BaseSha, ct).ConfigureAwait(false);
            return;
        }

        if (!TryParseRepositoryId(condition.RepositoryId, out var repoItemId))
        {
            _log.LogWarning(
                "Base-broken monitor: condition for project {ProjectId} base tip {Sha} carries an unusable repository id '{RepoId}'; clearing",
                condition.ProjectId.Value, condition.BaseSha, condition.RepositoryId);
            await _tracker.ClearAsync(condition.ProjectId, condition.BaseSha, ct).ConfigureAwait(false);
            return;
        }

        // Refresh the recorded repo so the tip reflects upstream/base motion.
        // EnsureRepositoryAsync fetches baseBranch when the repo already
        // exists and creates the clone when it does not (e.g. the fix item
        // has not been picked up yet).
        var repoId = await _gitHost.EnsureRepositoryAsync(
            repoItemId, project.RepositoryUrl, condition.BaseBranch, ct).ConfigureAwait(false);
        var tipSha = await _gitHost.ResolveCommitAsync(repoId, condition.BaseBranch, ct)
            .ConfigureAwait(false);

        if (string.Equals(tipSha, condition.BaseSha, StringComparison.OrdinalIgnoreCase))
        {
            // Same SHA. A build-kind verdict is immutable — no rebuild
            // needed, the hold stays. A test-kind verdict can still resolve
            // (CI pending → green), so re-poll the forge when wired.
            if (condition.FailureKind == BaseBrokenFailureKind.Test
                && await IsTipCiPassingAsync(project, tipSha, ct).ConfigureAwait(false))
            {
                _log.LogWarning(
                    "Base-broken monitor: CI now passes for test-red base tip {Sha}; clearing condition for project {ProjectId}",
                    condition.BaseSha, condition.ProjectId.Value);
                await _tracker.ClearAsync(condition.ProjectId, condition.BaseSha, ct).ConfigureAwait(false);
            }
            return;
        }

        var auditTarget = SandboxTargetResolver.ResolveAudit(
            project.NetworkProfiles.AuditTool,
            AuditCapabilities.None);
        var verdict = await _baseBuilds.VerifyTipAsync(
            tipSha,
            repoId,
            condition.BaseBranch,
            repoItemId,
            condition.ProjectId,
            new RequiredBuildSandboxPolicy
            {
                NetworkProfile = auditTarget.NetworkProfile,
                BaselineImageRef = null,
            },
            ct).ConfigureAwait(false);

        switch (verdict.Outcome)
        {
            case BaseBuildOutcome.Passed when condition.FailureKind == BaseBrokenFailureKind.Build:
                await _tracker.ClearAsync(condition.ProjectId, condition.BaseSha, ct).ConfigureAwait(false);
                return;

            case BaseBuildOutcome.Passed:
            {
                // A green build never clears a test-red hold by itself. The
                // new tip clears only when its CI passes; a red tip
                // supersedes (new SHA, new failing checks); an unknown CI
                // verdict keeps the hold so a moved-but-unproven tip cannot
                // silently release parked items.
                var failing = await TryGetFailingChecksAsync(project, tipSha, ct).ConfigureAwait(false);
                if (failing is not null && failing.Count == 0)
                {
                    await _tracker.ClearAsync(condition.ProjectId, condition.BaseSha, ct).ConfigureAwait(false);
                    return;
                }
                if (failing is { Count: > 0 })
                {
                    await SupersedeWithTestAsync(
                        condition, repoId, repoItemId, tipSha, failing, ct).ConfigureAwait(false);
                }
                return;
            }

            case BaseBuildOutcome.Failed:
            {
                // The base moved but still fails: supersede the stale-SHA
                // condition with one keyed by the new tip and file its own
                // deduped fix item.
                await _tracker.ClearAsync(condition.ProjectId, condition.BaseSha, ct)
                    .ConfigureAwait(false);
                var next = new BaseBrokenCondition
                {
                    ProjectId = condition.ProjectId,
                    BaseBranch = condition.BaseBranch,
                    BaseSha = verdict.BaseSha,
                    RepositoryId = repoId,
                    ErrorSummary = Summarize(verdict.Output),
                    DetectedAt = _time.GetUtcNow(),
                };
                var recorded = await _tracker.RecordDetectedAsync(next, ct).ConfigureAwait(false);
                var sourceItem = await _items.GetAsync(repoItemId, ct).ConfigureAwait(false);
                if (sourceItem is not null)
                {
                    await _tracker.EnsureFixItemAsync(
                        recorded,
                        sourceItem,
                        _tuning.Current.BaseBrokenFixItemPriority,
                        ct).ConfigureAwait(false);
                }
                return;
            }

            default:
                // Inconclusive (transient sandbox/toolchain fault): keep the
                // hold; the next sweep re-attempts the build.
                return;
        }
    }

    /// <summary>
    /// Supersedes a test-kind condition with a fresh one for a moved,
    /// still-red tip: new SHA, the tip's current failing checks, the tip as
    /// the prime suspect — plus its own deduped fix item.
    /// </summary>
    private async Task SupersedeWithTestAsync(
        BaseBrokenCondition condition,
        string repoId,
        WorkItemId repoItemId,
        string tipSha,
        IReadOnlyList<string> failingChecks,
        CancellationToken ct)
    {
        await _tracker.ClearAsync(condition.ProjectId, condition.BaseSha, ct).ConfigureAwait(false);
        var suspects = BaseBrokenTestSupport.SanitizeShas([tipSha]);
        var next = new BaseBrokenCondition
        {
            ProjectId = condition.ProjectId,
            BaseBranch = condition.BaseBranch,
            BaseSha = tipSha,
            RepositoryId = repoId,
            ErrorSummary = BaseBrokenTestSupport.BuildSummary(failingChecks, suspects),
            FailureKind = BaseBrokenFailureKind.Test,
            FailingNames = BaseBrokenTestSupport.SanitizeNameListForRecord(failingChecks),
            DetectedAt = _time.GetUtcNow(),
        };
        var recorded = await _tracker.RecordDetectedAsync(next, ct).ConfigureAwait(false);
        var sourceItem = await _items.GetAsync(repoItemId, ct).ConfigureAwait(false);
        if (sourceItem is not null)
        {
            await _tracker.EnsureFixItemAsync(
                recorded,
                sourceItem,
                _tuning.Current.BaseBrokenFixItemPriority,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True only when the forge proves the tip's required checks green.
    /// Unknown (unwired/unsupported forge, transport fault, pending) is
    /// false — never a clear signal.
    /// </summary>
    private async Task<bool> IsTipCiPassingAsync(Project project, string tipSha, CancellationToken ct)
    {
        var failing = await TryGetFailingChecksAsync(project, tipSha, ct).ConfigureAwait(false);
        return failing is not null && failing.Count == 0;
    }

    /// <summary>
    /// Forge verdict for a tip: failing check names (red), empty (green),
    /// or null (unknown — unwired/unsupported forge, fault, or pending).
    /// Null never clears and never records.
    /// </summary>
    private async Task<IReadOnlyList<string>?> TryGetFailingChecksAsync(
        Project project,
        string tipSha,
        CancellationToken ct)
    {
        if (_upstreamFactory is null)
            return null;
        try
        {
            var remote = _upstreamFactory.Create(project);
            var summary = await remote.GetCheckResultsAsync(tipSha, ct).ConfigureAwait(false);
            if (summary is null)
                return null;
            if (summary.RequiredChecksPassed)
                return [];
            var failing = summary.Checks
                .Where(c => c.State == UpstreamCheckState.Failing)
                .Select(c => c.Name)
                .ToList();
            // Required checks unsatisfied but none individually failing
            // (e.g. still running): unknown, not red.
            return failing.Count == 0 ? null : failing;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Base-broken monitor: could not read CI status for base tip {Sha}; keeping condition",
                BaseBrokenTestSupport.ShortSha(tipSha));
            return null;
        }
    }

    private static bool TryParseRepositoryId(string repositoryId, out WorkItemId id)
    {
        id = default;
        if (!Guid.TryParse(repositoryId, out var guid))
            return false;
        id = new WorkItemId(guid);
        return true;
    }

    private static string Summarize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "(no build output captured)";
        const int max = 1024;
        var cleaned = BaseBrokenFixItemPolicy.StripControl(output.Trim());
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }
}
