using System;
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
    private readonly ILogger<E2eRunDispatcher> _logger;
    private static readonly JsonSerializerOptions ResultJson = new() { WriteIndented = false };

    public E2eRunDispatcher(
        IE2eRunStore store,
        IE2eExecutionPool pool,
        IE2eReplayRuntime runtime,
        ITestCaseStore testCases,
        IOptionsMonitor<E2eExecutionOptions> options,
        ILogger<E2eRunDispatcher> logger)
    {
        _store = store;
        _pool = pool;
        _runtime = runtime;
        _testCases = testCases;
        _options = options;
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

    /// <summary>
    /// Attempts to claim one queued run and start its execution on a pool slot.
    /// Returns true when a run was claimed (so the caller skips the idle delay)
    /// and false otherwise. Exposed internal for tests so they can drive a
    /// single dispatch step without spinning up the BackgroundService loop.
    /// </summary>
    internal async Task<bool> TryDispatchOneAsync(CancellationToken stoppingToken)
    {
        if (_pool.InFlight >= _pool.MaxConcurrent)
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

        _ = Task.Run(() => RunOneAsync(slot, claimed, stoppingToken), stoppingToken);
        return true;
    }

    private async Task RunOneAsync(IE2eExecutionSlot slot, E2eRun run, CancellationToken stoppingToken)
    {
        var opts = _options.CurrentValue;
        using var timeoutCts = new CancellationTokenSource(opts.PerRunTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, stoppingToken);

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
                    : JsonSerializer.Deserialize<E2eReplayArtifact>(testCase.ExecutableArtifactJson);
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

            var result = await _runtime.ExecuteAsync(artifact, slot.Sandbox, linked.Token);
            var resultJson = JsonSerializer.Serialize(result, ResultJson);
            var status = result.Passed ? E2eRunStatus.Passed : E2eRunStatus.Failed;
            await PersistResultAsync(run.Id, status, resultJson, CancellationToken.None);
            await UpdateTestCaseLastRunAsync(testCase, result, CancellationToken.None);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("PerRunTimeout", $"exceeded {opts.PerRunTimeout}"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await PersistResultAsync(run.Id, E2eRunStatus.Canceled, BuildErrorResultJson("ShutdownCancel", "dispatcher canceled before run finished"), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "E2E run {RunId} crashed; recording Error.", run.Id);
            await PersistResultAsync(run.Id, E2eRunStatus.Error, BuildErrorResultJson("Exception", ex.Message), CancellationToken.None);
        }
        finally
        {
            try { await slot.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "E2E pool slot disposal threw; continuing."); }
        }
    }

    private async Task PersistResultAsync(string runId, E2eRunStatus status, string resultJson, CancellationToken ct)
    {
        try
        {
            await _store.UpdateStatusAsync(runId, status, startedAt: null, finishedAt: DateTimeOffset.UtcNow, result: resultJson, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Persisting result for run {RunId} failed.", runId);
        }
    }

    private async Task UpdateTestCaseLastRunAsync(TestCase testCase, E2eRunResult result, CancellationToken ct)
    {
        try
        {
            var updated = testCase with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                LastRunPassed = result.Passed,
                LastRunAt = DateTimeOffset.UtcNow,
                LastRunResult = result.Summary,
            };
            await _testCases.UpdateAsync(updated, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updating last-run for test case {TestCaseId} failed.", testCase.Id);
        }
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
