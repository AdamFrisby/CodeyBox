using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public E2eRunDispatcher(
        IE2eRunStore store,
        IE2eExecutionPool pool,
        IE2eReplayRuntime runtime,
        ITestCaseStore testCases,
        IOptionsMonitor<E2eExecutionOptions> options,
        E2eRunCancellationRegistry cancellations,
        ILogger<E2eRunDispatcher> logger)
    {
        _store = store;
        _pool = pool;
        _runtime = runtime;
        _testCases = testCases;
        _options = options;
        _cancellations = cancellations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("E2eRunDispatcher started; pool={Pool}, max-concurrent={Max}", _pool.Name, _pool.MaxConcurrent);
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
    /// Attempts to claim one queued run and start its execution on a pool slot.
    /// Returns true when a run was claimed (so the caller skips the idle delay)
    /// and false otherwise. Exposed internal for tests so they can drive a
    /// single dispatch step without spinning up the BackgroundService loop.
    /// </summary>
    internal async Task<bool> TryDispatchOneAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return false;
        }

        if (_pool.InFlight >= _pool.MaxConcurrent)
        {
            return false;
        }

        if (!await _store.HasQueuedAsync(stoppingToken))
        {
            return false;
        }

        IE2eExecutionSlot? slot;
        try
        {
            slot = await _pool.LeaseAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E2eRunDispatcher failed to lease a pool slot; backing off.");
            return false;
        }

        E2eRun? claimed = null;
        try
        {
            claimed = await _store.ClaimNextQueuedAsync(slot.SandboxId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await slot.DisposeAsync();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E2eRunDispatcher claim failed; releasing slot.");
            await slot.DisposeAsync();
            return false;
        }

        if (claimed is null)
        {
            await slot.DisposeAsync();
            return false;
        }

        var runCancellation = _cancellations.Register(claimed.Id);
        var task = Task.Run(() => RunOneAsync(slot, claimed, runCancellation, stoppingToken));
        _activeTasks[claimed.Id] = task;
        _ = task.ContinueWith(
            t =>
            {
                _activeTasks.TryRemove(claimed.Id, out _);
                if (t.Exception is { } ex)
                    _logger.LogError(ex, "E2E run {RunId} task faulted.", claimed.Id);
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

    private async Task RunOneAsync(IE2eExecutionSlot slot, E2eRun run, CancellationTokenSource runCancellation, CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        using var timeoutCts = new CancellationTokenSource(opts.PerRunTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, stoppingToken, runCancellation.Token);

        try
        {
            var testCase = await _testCases.GetAsync(run.TestCaseId, linked.Token);
            if (testCase is null)
            {
                _logger.LogWarning("E2E run {RunId} references missing test case {TestCaseId}.", run.Id, run.TestCaseId);
                await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("MissingTestCase", $"test case {run.TestCaseId} not found"), CancellationToken.None);
                return;
            }

            if (testCase.AutomationKind != AutomationKind.E2eReplay)
            {
                await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("WrongAutomationKind", $"automation_kind={testCase.AutomationKind} is not E2eReplay"), CancellationToken.None);
                return;
            }

            E2eReplayArtifact? artifact;
            try
            {
                artifact = string.IsNullOrWhiteSpace(testCase.ExecutableArtifactJson)
                    ? null
                    : JsonSerializer.Deserialize<E2eReplayArtifact>(testCase.ExecutableArtifactJson, ArtifactJson);
            }
            catch (JsonException ex)
            {
                await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("ArtifactParseError", ex.Message), CancellationToken.None);
                return;
            }

            if (artifact is null)
            {
                await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("MissingArtifact", "test case has no executable artifact"), CancellationToken.None);
                return;
            }

            if (!E2eReplayArtifactValidation.TryValidate(artifact, out var failureKind, out var detail))
            {
                await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson(failureKind, detail), CancellationToken.None);
                return;
            }

            var result = await _runtime.ExecuteAsync(artifact, slot.Sandbox, linked.Token);
            var resultJson = JsonSerializer.Serialize(result, ResultJson);
            var status = result.Passed
                ? E2eRunStatus.Passed
                : IsInfrastructureFailure(result.FailureKind) ? E2eRunStatus.Error : E2eRunStatus.Failed;
            var persisted = await PersistResultAsync(run.Id, status, resultJson, CancellationToken.None);
            if (persisted && status is E2eRunStatus.Passed or E2eRunStatus.Failed or E2eRunStatus.Error)
                await UpdateTestCaseLastRunAsync(testCase.Id, result, CancellationToken.None);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("PerRunTimeout", $"exceeded {opts.PerRunTimeout}"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            await PersistResultAsync(run.Id, E2eRunStatus.Canceled, BuildErrorResultJson("Canceled", "run canceled by operator"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await PersistResultAsync(run.Id, E2eRunStatus.Canceled, BuildErrorResultJson("ShutdownCancel", "dispatcher canceled before run finished"), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E2E run {RunId} crashed; recording Error.", run.Id);
            await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("Exception", ex.Message), CancellationToken.None);
        }
        finally
        {
            _activeTasks.TryRemove(run.Id, out _);
            _cancellations.Unregister(run.Id, runCancellation);
            try { await slot.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "E2E pool slot disposal threw; continuing."); }
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

    private static string BuildErrorResultJson(string kind, string detail)
        => JsonSerializer.Serialize(new E2eRunResult
        {
            Passed = false,
            Summary = detail,
            FailureKind = kind,
            StepResults = Array.Empty<E2eStepResult>(),
            AssertionResults = Array.Empty<E2eAssertionResult>(),
        }, ResultJson);

    private static bool IsInfrastructureFailure(string? failureKind) =>
        string.Equals(failureKind, "ReadinessProbe", StringComparison.Ordinal)
        || string.Equals(failureKind, "ExecException", StringComparison.Ordinal)
        || string.Equals(failureKind, "AssertionException", StringComparison.Ordinal)
        || string.Equals(failureKind, "ReplayDriverUnavailable", StringComparison.Ordinal)
        || string.Equals(failureKind, "OutputLimitExceeded", StringComparison.Ordinal);

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
