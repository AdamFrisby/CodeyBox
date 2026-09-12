using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Deep auditor that executes the full committed E2E replay suite for a project
/// against the release branch's built app via the cheap cloud-VM pool.
///
/// Features:
/// <list type="bullet">
///   <item>Deterministic replays with no model in the loop.</item>
///   <item>Massively parallel execution throttled by the pool's capacity.</item>
///   <item>Config-driven selection: "all" or "capability-filtered".</item>
///   <item>Persists per-replay execution outcomes against the release.</item>
///   <item>A failing replay produces a blocking <see cref="AuditSeverity.Error"/> finding.</item>
/// </list>
/// </summary>
public sealed class E2eRegressionDeepAuditor : IDeepAuditor
{
    public const string AuditorName = "e2e-regression";

    private readonly ITestCaseStore _testCases;
    private readonly IE2eExecutionPool _pool;
    private readonly IE2eReplayRuntime _runtime;
    private readonly IReleaseStore _releaseStore;
    private readonly IE2eRunStore? _runs;
    private readonly E2eReplayArtifactAdmissionValidator _artifactValidator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<E2eRegressionDeepAuditor> _logger;

    private static readonly JsonSerializerOptions ResultJsonOptions = new() { WriteIndented = false };

    public string Name => AuditorName;
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public E2eRegressionDeepAuditor(
        ITestCaseStore testCases,
        IE2eExecutionPool pool,
        IE2eReplayRuntime runtime,
        IReleaseStore releaseStore,
        IE2eRunStore? runs = null,
        E2eReplayArtifactAdmissionValidator? artifactValidator = null,
        TimeProvider? timeProvider = null,
        ILogger<E2eRegressionDeepAuditor>? logger = null)
    {
        _testCases = testCases ?? throw new ArgumentNullException(nameof(testCases));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _releaseStore = releaseStore ?? throw new ArgumentNullException(nameof(releaseStore));
        _runs = runs;
        _artifactValidator = artifactValidator ?? new E2eReplayArtifactAdmissionValidator();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<E2eRegressionDeepAuditor>.Instance;
    }

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        DeepAuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var config = context.E2eRegressionConfig ?? new ReleaseE2eRegressionConfig { Enabled = true };

        var allCases = new List<TestCase>();
        await foreach (var tc in _testCases.ListByProjectAsync(context.ProjectId, ct).ConfigureAwait(false))
        {
            allCases.Add(tc);
        }

        var committedE2eCases = allCases
            .Where(tc => E2eReplayGatePolicy.IsDeclaredE2eCase(tc) && E2eReplayGatePolicy.HasCommittedReplay(tc))
            .ToList();

        var selectedCases = SelectCases(committedE2eCases, config);

        _logger.LogInformation(
            "Release {ReleaseId}: E2E regression running {Count}/{Total} committed E2E test cases (selection: {Selection})",
            context.ReleaseId, selectedCases.Count, committedE2eCases.Count, config.Selection);

        if (selectedCases.Count == 0)
        {
            return new AuditResult(true, []);
        }

        var maxConcurrency = Math.Clamp(_pool.MaxConcurrent, 1, 512);
        using var throttle = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = selectedCases.Select(tc => ExecuteOneReplayAsync(tc, context, throttle, ct)).ToArray();
        var replayResults = await Task.WhenAll(tasks).ConfigureAwait(false);

        await _releaseStore.SaveE2eReplayResultsAsync(context.ReleaseId, context.Iteration, replayResults, ct)
            .ConfigureAwait(false);

        var findings = new List<AuditFinding>();
        foreach (var r in replayResults.Where(r => !r.Passed))
        {
            var desc = string.IsNullOrWhiteSpace(r.Summary)
                ? $"E2E replay failed with status {r.Status}."
                : $"{r.Summary} (FailureKind: {r.FailureKind ?? r.Status.ToString()})";

            findings.Add(new AuditFinding(
                AuditorName: AuditorName,
                Severity: AuditSeverity.Error,
                Title: $"E2E regression: {r.TestCaseName} failed",
                Description: desc,
                Location: r.TestCaseId));
        }

        var passedCount = replayResults.Count(r => r.Passed);
        var failedCount = replayResults.Length - passedCount;

        _logger.LogInformation(
            "Release {ReleaseId}: E2E regression complete for iteration {Iteration}: {Passed} passed, {Failed} failed",
            context.ReleaseId, context.Iteration, passedCount, failedCount);

        return new AuditResult(findings.Count == 0, findings);
    }

    private static IReadOnlyList<TestCase> SelectCases(IReadOnlyList<TestCase> cases, ReleaseE2eRegressionConfig config)
    {
        var isCapabilityFiltered = config.Selection.Equals("capability-filtered", StringComparison.OrdinalIgnoreCase)
            || (config.Capabilities.Count > 0 && !config.Selection.Equals("all", StringComparison.OrdinalIgnoreCase));

        if (!isCapabilityFiltered)
        {
            return cases;
        }

        return cases
            .Where(tc => !string.IsNullOrWhiteSpace(tc.Label)
                      && config.Capabilities.Contains(tc.Label, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<ReleaseE2eReplayResult> ExecuteOneReplayAsync(
        TestCase testCase,
        DeepAuditContext context,
        SemaphoreSlim throttle,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        if (!_artifactValidator.TryValidateJson(testCase.ExecutableArtifactJson, out var artifact, out var failureKind, out var detail))
        {
            _logger.LogWarning("Release {ReleaseId}: Test case {TestCaseId} artifact invalid: {Detail}",
                context.ReleaseId, testCase.Id, detail);

            var errorResult = new E2eRunResult
            {
                Passed = false,
                Summary = detail ?? "Artifact validation failed",
                FailureKind = failureKind ?? "InvalidArtifact",
            };
            var serialized = JsonSerializer.Serialize(errorResult, ResultJsonOptions);

            if (_runs is not null)
            {
                var run = new E2eRun
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TestCaseId = testCase.Id,
                    Status = E2eRunStatus.Error,
                    CreatedAt = now,
                    StartedAt = now,
                    FinishedAt = now,
                    Result = serialized,
                    BatchId = $"release-{context.ReleaseId}-{context.Iteration}",
                };
                await _runs.CreateAsync(run, ct).ConfigureAwait(false);
            }

            await _testCases.UpdateLastRunAsync(testCase.Id, false, now, serialized, ct).ConfigureAwait(false);

            return new ReleaseE2eReplayResult
            {
                ReleaseId = context.ReleaseId,
                Iteration = context.Iteration,
                TestCaseId = testCase.Id,
                TestCaseName = testCase.Name,
                Label = testCase.Label,
                Passed = false,
                Status = E2eRunStatus.Error,
                ResultJson = serialized,
                DurationMs = 0,
                FailureKind = failureKind ?? "InvalidArtifact",
                Summary = detail,
                CreatedAt = now,
            };
        }

        await throttle.WaitAsync(ct).ConfigureAwait(false);
        IE2eExecutionSlot? slot = null;
        var runId = Guid.NewGuid().ToString("N");
        var startedAt = _timeProvider.GetUtcNow();

        try
        {
            if (_runs is not null)
            {
                var run = new E2eRun
                {
                    Id = runId,
                    TestCaseId = testCase.Id,
                    Status = E2eRunStatus.Running,
                    CreatedAt = startedAt,
                    StartedAt = startedAt,
                    BatchId = $"release-{context.ReleaseId}-{context.Iteration}",
                };
                await _runs.CreateAsync(run, ct).ConfigureAwait(false);
            }

            slot = await _pool.LeaseAsync(ct).ConfigureAwait(false);

            if (_runs is not null)
            {
                await _runs.AssignSandboxAsync(runId, slot.SandboxId, ct).ConfigureAwait(false);
            }

            var replayResult = await _runtime.ExecuteAsync(artifact!, slot.Sandbox, ct).ConfigureAwait(false);
            var finishedAt = _timeProvider.GetUtcNow();

            var status = replayResult.Passed
                ? E2eRunStatus.Passed
                : (E2eRunDispatcher.IsInfrastructureFailure(replayResult.FailureKind) ? E2eRunStatus.Error : E2eRunStatus.Failed);

            var resultJson = JsonSerializer.Serialize(replayResult, ResultJsonOptions);

            if (_runs is not null)
            {
                await _runs.UpdateStatusAsync(runId, status, startedAt, finishedAt, resultJson, ct).ConfigureAwait(false);
            }

            await _testCases.UpdateLastRunAsync(testCase.Id, replayResult.Passed, finishedAt, resultJson, ct).ConfigureAwait(false);

            return new ReleaseE2eReplayResult
            {
                ReleaseId = context.ReleaseId,
                Iteration = context.Iteration,
                TestCaseId = testCase.Id,
                TestCaseName = testCase.Name,
                Label = testCase.Label,
                Passed = replayResult.Passed,
                Status = status,
                ResultJson = resultJson,
                DurationMs = replayResult.DurationMs,
                FailureKind = replayResult.FailureKind,
                Summary = replayResult.Summary,
                CreatedAt = finishedAt,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var finishedAt = _timeProvider.GetUtcNow();
            _logger.LogWarning(ex, "Release {ReleaseId}: E2E run crashed for test case {TestCaseId}", context.ReleaseId, testCase.Id);

            var crashResult = new E2eRunResult
            {
                Passed = false,
                Summary = ex.Message,
                FailureKind = "Exception",
            };
            var resultJson = JsonSerializer.Serialize(crashResult, ResultJsonOptions);

            if (_runs is not null)
            {
                await _runs.UpdateStatusAsync(runId, E2eRunStatus.Error, startedAt, finishedAt, resultJson, CancellationToken.None).ConfigureAwait(false);
            }

            await _testCases.UpdateLastRunAsync(testCase.Id, false, finishedAt, resultJson, CancellationToken.None).ConfigureAwait(false);

            return new ReleaseE2eReplayResult
            {
                ReleaseId = context.ReleaseId,
                Iteration = context.Iteration,
                TestCaseId = testCase.Id,
                TestCaseName = testCase.Name,
                Label = testCase.Label,
                Passed = false,
                Status = E2eRunStatus.Error,
                ResultJson = resultJson,
                DurationMs = (long)(finishedAt - startedAt).TotalMilliseconds,
                FailureKind = "Exception",
                Summary = ex.Message,
                CreatedAt = finishedAt,
            };
        }
        finally
        {
            if (slot is not null)
            {
                try { await slot.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error disposing pool slot"); }
            }
            throttle.Release();
        }
    }
}
