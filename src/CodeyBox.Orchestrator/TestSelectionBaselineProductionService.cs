using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Post-merge baseline production loop: drains the per-project production
/// queue one job at a time (concurrency 1 per project is enforced by the
/// scheduler; this loop additionally runs a single job at a time so two
/// projects never contend a saturated host). Every outcome is recorded —
/// success stores the artifact host-side, failure records a reportable error
/// — and both publish a structured event. Failures never propagate: merges
/// already completed and audits fall back to the full suite, so this loop
/// swallows everything except host shutdown.
/// </summary>
public sealed class TestSelectionBaselineProductionService : BackgroundService
{
    internal static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(5);

    private readonly TestSelectionBaselineScheduler _scheduler;
    private readonly ITestSelectionBaselineStore _store;
    private readonly ITestSelectionBaselineJobRunner _runner;
    private readonly IProjectRepository _projects;
    private readonly ISandboxProvider _sandboxes;
    private readonly IWebhookDispatcher _webhooks;
    private readonly Func<TestSelectionBaselineProductionOptions> _options;
    private readonly ILogger<TestSelectionBaselineProductionService> _log;
    private readonly TimeProvider _clock;

    public TestSelectionBaselineProductionService(
        TestSelectionBaselineScheduler scheduler,
        ITestSelectionBaselineStore store,
        ITestSelectionBaselineJobRunner runner,
        IProjectRepository projects,
        ISandboxProvider sandboxes,
        IWebhookDispatcher webhooks,
        Func<TestSelectionBaselineProductionOptions> options,
        ILogger<TestSelectionBaselineProductionService> log,
        TimeProvider? clock = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
        _webhooks = webhooks ?? throw new ArgumentNullException(nameof(webhooks));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _options();
                if (!options.Enabled || _scheduler.PendingCount == 0)
                {
                    await Task.Delay(IdlePollDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                // Yield to work-item phases while the pipeline is saturated:
                // the job stays queued (newest commit still wins) and the
                // global sandbox/worker budget keeps serving audits and merges.
                // The runner's CreateAsync goes through the same admission gate,
                // so even a lost race queues behind — never ahead of — live phases.
                if (IsPoolSaturated())
                {
                    await Task.Delay(options.SaturatedPoolRecheckDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_scheduler.TryTake(out var request) || request is null)
                {
                    await Task.Delay(IdlePollDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunOneAsync(request, options, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Baseline production loop faulted; continuing");
                try
                {
                    await Task.Delay(IdlePollDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal async Task RunOneAsync(
        TestSelectionBaselineRequest request,
        TestSelectionBaselineProductionOptions options,
        CancellationToken ct = default)
    {
        var startTimestamp = _clock.GetTimestamp();
        try
        {
            var project = await TryGetProjectAsync(request.ProjectId, ct).ConfigureAwait(false);
            if (project is null || !project.TestSelectionBaselineEnabled)
            {
                _log.LogInformation(
                    "Dropping baseline job for project {ProjectId}: project unknown or not opted in",
                    request.ProjectId);
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);
            TestSelectionBaselineRunResult result;
            try
            {
                result = await _runner.RunAsync(request, options.Timeout, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TestSelectionBaselineRunException(
                    $"baseline production for {request.ProjectId}@{request.Commit} exceeded the {options.Timeout} timeout");
            }

            var duration = _clock.GetElapsedTime(startTimestamp);
            await _store.StoreAsync(
                request.ProjectId,
                result.Commit,
                result.ProducedAtUtc,
                result.Json,
                result.TestCount,
                duration,
                options.MaxRetainedPerProject,
                ct).ConfigureAwait(false);
            _log.LogInformation(
                "Stored test-selection baseline for project {ProjectId}@{Commit} ({Tests} tests, {Bytes} bytes)",
                request.ProjectId, result.Commit, result.TestCount, result.Json.Length);
            await PublishEventAsync(
                project,
                request,
                success: true,
                sizeBytes: result.Json.Length,
                testCount: result.TestCount,
                duration: duration,
                error: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var duration = _clock.GetElapsedTime(startTimestamp);
            var error = SanitizeError(ex);
            _log.LogWarning(
                ex,
                "Baseline production failed for project {ProjectId}@{Commit}; merges and audits are unaffected",
                request.ProjectId, request.Commit);
            try
            {
                await _store.RecordFailureAsync(request.ProjectId, request.Commit, error, duration, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception storeEx) when (storeEx is not OperationCanceledException)
            {
                _log.LogDebug(storeEx, "Baseline failure record failed for project {ProjectId}", request.ProjectId);
            }

            var project = await TryGetProjectAsync(request.ProjectId, ct).ConfigureAwait(false);
            if (project is not null)
            {
                await PublishEventAsync(
                    project,
                    request,
                    success: false,
                    sizeBytes: 0,
                    testCount: 0,
                    duration: duration,
                    error: error).ConfigureAwait(false);
            }
        }
        finally
        {
            _scheduler.Complete(request.ProjectId);
        }
    }

    private bool IsPoolSaturated()
    {
        try
        {
            if (_sandboxes is ISandboxAdmissionSnapshot snapshot)
                return snapshot.CurrentAdmittedSandboxes >= snapshot.MaxConcurrentSandboxes;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Sandbox admission snapshot failed; assuming capacity");
        }

        return false;
    }

    private async Task<Project?> TryGetProjectAsync(string projectId, CancellationToken ct)
    {
        try
        {
            return await _projects.GetAsync(new ProjectId(projectId), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Baseline job: project lookup failed for {ProjectId}", projectId);
            return null;
        }
    }

    private async Task PublishEventAsync(
        Project project,
        TestSelectionBaselineRequest request,
        bool success,
        long sizeBytes,
        int testCount,
        TimeSpan duration,
        string? error)
    {
        try
        {
            await _webhooks.PublishAsync(new WebhookEvent
            {
                Event = success ? "test-selection.baseline_produced" : "test-selection.baseline_failed",
                Project = project,
                Details = new TestSelectionBaselineProducedDetails
                {
                    ProjectId = request.ProjectId,
                    Commit = request.Commit,
                    Success = success,
                    SizeBytes = sizeBytes,
                    TestCount = testCount,
                    DurationMs = (long)duration.TotalMilliseconds,
                    Error = error,
                },
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Baseline production event publish failed for project {ProjectId}", request.ProjectId);
        }
    }

    internal static string SanitizeError(Exception ex)
    {
        var message = ex.Message ?? ex.GetType().Name;
        var sanitized = new string(message.Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? '_' : c).ToArray()).Trim();
        if (sanitized.Length == 0)
            sanitized = ex.GetType().Name;
        return sanitized.Length <= 2000 ? sanitized : sanitized[^2000..];
    }
}
