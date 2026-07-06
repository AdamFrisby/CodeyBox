using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background loop that drains <see cref="IE2eRunStore"/> for queued
/// <see cref="E2eRun"/> records, leases pool slots, replays the artifact, and
/// persists the result. Many runs execute concurrently up to the pool's
/// capacity — every claimed run is dispatched on a fresh task so the loop never
/// blocks on a single slow run.
///
/// <para>The dispatcher is the ONLY component that bridges queue → pool. It
/// never touches <see cref="WorkerPool"/> or the coding fleet's sandbox
/// provisioning, satisfying the brief's "nothing runs on the local coding
/// fleet" requirement architecturally rather than by convention.</para>
/// </summary>
public sealed class E2eRunDispatcher : BackgroundService
{
    private readonly IE2eRunStore _store;
    private readonly IE2eExecutionPool _pool;
    private readonly IE2eReplayRuntime _runtime;
    private readonly ITestCaseStore _testCases;
    private readonly IOptionsMonitor<E2eExecutionOptions> _options;
    private readonly E2eRunCancellationRegistry _cancellations;
    private readonly ILogger<E2eRunDispatcher> _logger;
    private readonly ConcurrentDictionary<string, Task> _activeTasks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = false };
    private readonly E2eReplayArtifactAdmissionValidator _artifactValidator;

    public E2eRunDispatcher(
        IE2eRunStore store,
        IE2eExecutionPool pool,
        IE2eReplayRuntime runtime,
        ITestCaseStore testCases,
        IOptionsMonitor<E2eExecutionOptions> options,
        E2eRunCancellationRegistry cancellations,
        E2eReplayArtifactAdmissionValidator artifactValidator,
        ILogger<E2eRunDispatcher> logger)
    {
        _store = store;
        _pool = pool;
        _runtime = runtime;
        _testCases = testCases;
        _options = options;
        _cancellations = cancellations;
        _artifactValidator = artifactValidator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("E2eRunDispatcher started; pool={Pool}, max-concurrent={Max}", _pool.Name, _pool.MaxConcurrent);
        await RecoverRunningRunsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            if (!opts.Enabled)
            {
                await DelaySafely(opts.PollInterval, stoppingToken);
                continue;
            }

            try
            {
                var pollInterval = opts.PollInterval;
                var dispatched = await TryDispatchOneAsync(stoppingToken);
                if (!dispatched)
                {
                    await DelaySafely(pollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "E2eRunDispatcher main loop saw an error; backing off.");
                await DelaySafely(_options.CurrentValue.PollInterval, stoppingToken);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        var tasks = _activeTasks.Values.ToArray();
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("E2eRunDispatcher shutdown timed out with {Count} active replay task(s).", _activeTasks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E2eRunDispatcher observed a replay task failure during shutdown drain.");
        }
    }

    /// <summary>
    /// Attempts to schedule one queued run for execution. Returns true when a
    /// dispatch task was started (so the caller skips the idle delay) and false
    /// otherwise. Exposed internal for tests so they can drive a single dispatch
    /// step without spinning up the BackgroundService loop.
    /// </summary>
    internal async Task<bool> TryDispatchOneAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return false;
        }

        if (_activeTasks.Count >= _pool.MaxConcurrent || _pool.InFlight >= _pool.MaxConcurrent)
        {
            return false;
        }

        if (!await _store.HasQueuedAsync(stoppingToken))
        {
            return false;
        }

        var dispatchId = Guid.NewGuid().ToString("N");
        var task = Task.Run(() => DispatchOneAsync(stoppingToken), CancellationToken.None);
        _activeTasks[dispatchId] = task;
        _ = task.ContinueWith(
            t =>
            {
                _activeTasks.TryRemove(dispatchId, out _);
                if (t.Exception is { } ex)
                    _logger.LogError(ex, "E2E dispatch task {DispatchId} faulted.", dispatchId);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    internal async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        while (true)
        {
            var tasks = _activeTasks.Values.ToArray();
            if (tasks.Length == 0)
                return;

            await Task.WhenAll(tasks).WaitAsync(ct);
        }
    }

    private async Task DispatchOneAsync(CancellationToken stoppingToken)
    {
        E2eRun? run = null;
        IE2eExecutionSlot? slot = null;
        CancellationTokenSource? runCancellation = null;
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linked = null;
        TestCase? testCaseForLastRun = null;
        var perRunTimeout = NormalizePerRunTimeout(_options.CurrentValue.PerRunTimeout);
        try
        {
            try
            {
                run = await _store.ClaimNextQueuedAsync(sandboxId: null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "E2eRunDispatcher claim failed; backing off.");
                return;
            }

            if (run is null)
                return;

            runCancellation = _cancellations.Register(run.Id);
            timeoutCts = new CancellationTokenSource(perRunTimeout);
            linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, stoppingToken, runCancellation.Token);

            var load = await LoadRunnableArtifactAsync(run, linked.Token);
            testCaseForLastRun = load.TestCase;
            if (load.FailureKind is not null)
            {
                if (load.FailureKind == "MissingTestCase")
                    _logger.LogWarning("E2E run {RunId} references missing test case {TestCaseId}.", run.Id, run.TestCaseId);
                await PersistErrorAndMaybeStampTestCaseAsync(run.Id, load.TestCase, load.FailureKind, load.Detail ?? string.Empty, CancellationToken.None);
                return;
            }

            try
            {
                slot = await _pool.LeaseAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "E2eRunDispatcher failed to lease a pool slot for run {RunId}.", run.Id);
                await PersistErrorAndMaybeStampTestCaseAsync(run.Id, testCaseForLastRun, "PoolLeaseFailed", ex.Message, CancellationToken.None);
                return;
            }

            var assigned = await _store.AssignSandboxAsync(run.Id, slot.SandboxId, CancellationToken.None);
            if (!assigned)
            {
                _logger.LogInformation("E2E run {RunId} was terminal before sandbox assignment; releasing leased sandbox {SandboxId}.", run.Id, slot.SandboxId);
                return;
            }

            run = run with { SandboxId = slot.SandboxId };
            await ExecuteClaimedRunAsync(slot, run, load.TestCase!, load.Artifact!, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && run is not null)
        {
            await PersistErrorAndMaybeStampTestCaseAsync(run.Id, testCaseForLastRun, "PerRunTimeout", $"exceeded {perRunTimeout}", CancellationToken.None);
        }
        catch (OperationCanceledException) when (runCancellation?.IsCancellationRequested == true && run is not null)
        {
            await PersistResultAsync(run.Id, E2eRunStatus.Canceled, BuildErrorResultJson("Canceled", "run canceled by operator"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested && run is not null)
        {
            await PersistResultAsync(run.Id, E2eRunStatus.Canceled, BuildErrorResultJson("ShutdownCancel", "dispatcher canceled before run finished"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (run is not null)
        {
            _logger.LogWarning(ex, "E2E run {RunId} crashed; recording Error.", run.Id);
            await PersistErrorAndMaybeStampTestCaseAsync(run.Id, testCaseForLastRun, "Exception", ex.Message, CancellationToken.None);
        }
        finally
        {
            linked?.Dispose();
            timeoutCts?.Dispose();
            if (run is not null && runCancellation is not null)
                _cancellations.Unregister(run.Id, runCancellation);
            try
            {
                if (slot is not null)
                    await slot.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "E2E pool slot disposal threw; continuing.");
            }
        }
    }

    private async Task<(TestCase? TestCase, E2eReplayArtifact? Artifact, string? FailureKind, string? Detail)> LoadRunnableArtifactAsync(
        E2eRun run,
        CancellationToken ct)
    {
        var testCase = await _testCases.GetAsync(run.TestCaseId, ct);
        if (testCase is null)
        {
            return (null, null, "MissingTestCase", $"test case {run.TestCaseId} not found");
        }

        if (testCase.AutomationKind != AutomationKind.E2eReplay)
        {
            return (testCase, null, "WrongAutomationKind", $"automation_kind={testCase.AutomationKind} is not E2eReplay");
        }

        return _artifactValidator.TryValidateJson(testCase.ExecutableArtifactJson, out var artifact, out var failureKind, out var detail)
            ? (testCase, artifact, null, null)
            : (testCase, null, failureKind, detail);
    }

    private async Task ExecuteClaimedRunAsync(
        IE2eExecutionSlot slot,
        E2eRun run,
        TestCase testCase,
        E2eReplayArtifact artifact,
        CancellationToken ct)
    {
        var result = await _runtime.ExecuteAsync(artifact, slot.Sandbox, ct);
        var resultJson = JsonSerializer.Serialize(result, ResultJson);
        var status = result.Passed
            ? E2eRunStatus.Passed
            : IsInfrastructureFailure(result.FailureKind) ? E2eRunStatus.Error : E2eRunStatus.Failed;
        var persisted = await PersistResultAsync(run.Id, status, resultJson, CancellationToken.None);
        if (persisted && status is E2eRunStatus.Passed or E2eRunStatus.Failed or E2eRunStatus.Error)
            await UpdateTestCaseLastRunAsync(testCase.Id, result, CancellationToken.None);
    }

    private async Task RecoverRunningRunsAsync(CancellationToken ct)
    {
        try
        {
            var recovered = await _store.RequeueRunningAsync(DateTimeOffset.UtcNow, ct);
            if (recovered > 0)
                _logger.LogWarning("E2eRunDispatcher requeued {Count} run(s) left Running by a prior process.", recovered);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E2eRunDispatcher failed to recover running E2E runs; continuing startup.");
        }
    }

    private async Task<bool> PersistResultAsync(string runId, E2eRunStatus status, string resultJson, CancellationToken ct)
    {
        var updated = await _store.UpdateStatusAsync(runId, status, startedAt: null, finishedAt: DateTimeOffset.UtcNow, result: resultJson, ct);
        if (updated)
            return true;

        var current = await _store.GetAsync(runId, CancellationToken.None);
        if (current?.Status == E2eRunStatus.Canceled)
            return false;

        throw new InvalidOperationException($"Persisting result for run '{runId}' affected no rows.");
    }

    private async Task UpdateTestCaseLastRunAsync(string testCaseId, E2eRunResult result, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await _testCases.UpdateLastRunAsync(testCaseId, result.Passed, now, result.Summary, ct);
        if (!updated)
            throw new InvalidOperationException($"Updating last-run for test case '{testCaseId}' affected no rows.");
    }

    private async Task PersistErrorAndMaybeStampTestCaseAsync(
        string runId,
        TestCase? testCase,
        string kind,
        string detail,
        CancellationToken ct)
    {
        var result = BuildErrorResult(kind, detail);
        var persisted = await PersistResultAsync(runId, E2eRunStatus.Error, JsonSerializer.Serialize(result, ResultJson), ct);
        if (persisted && testCase is not null)
            await UpdateTestCaseLastRunAsync(testCase.Id, result, CancellationToken.None);
    }

    private static string BuildErrorResultJson(string kind, string detail)
        => JsonSerializer.Serialize(BuildErrorResult(kind, detail), ResultJson);

    private static E2eRunResult BuildErrorResult(string kind, string detail)
        => new()
        {
            Passed = false,
            Summary = detail,
            FailureKind = kind,
            StepResults = Array.Empty<E2eStepResult>(),
            AssertionResults = Array.Empty<E2eAssertionResult>(),
        };

    // Internal (not private) so the classification map can be pinned by a
    // data-driven test — this single predicate decides whether a non-passing
    // replay records as Error (infrastructure) vs Failed (deterministic), so a
    // silently-dropped kind must be caught by a test, not code review.
    internal static bool IsInfrastructureFailure(string? failureKind) =>
        string.Equals(failureKind, "ReadinessProbe", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReadinessUrlRejected", StringComparison.Ordinal)
        || string.Equals(failureKind, "NavigationUrlRejected", StringComparison.Ordinal)
        || string.Equals(failureKind, "ExecException", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReplayDriverFailed", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReplayDriverProtocolError", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReplayDriverUnavailable", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReplayEgressFirewallUnavailable", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReplayEgressOriginRejected", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReplayEgressResolutionFailed", StringComparison.Ordinal)
        || string.Equals(failureKind, "OutputLimitExceeded", StringComparison.Ordinal);

    private static TimeSpan NormalizePerRunTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMinutes(15);

    private static async Task DelaySafely(TimeSpan delay, CancellationToken ct)
    {
        var effective = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(250);
        try
        {
            await Task.Delay(effective, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — normal.
        }
    }

}
