using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Real <see cref="IE2eReplayVerifier"/>: enqueues one <see cref="E2eRun"/> for
/// the test case and polls the run store until it reaches a terminal status.
/// The background <see cref="E2eRunDispatcher"/> (running on the cheap-CPU E2E
/// pool, never the coding fleet) claims and executes the queued run — this
/// verifier only enqueues and observes, so it inherits the same architectural
/// separation for free.
///
/// <para>On timeout the run is canceled and reported red: an unverifiable
/// replay must never be mistaken for a passing one. Polling uses the injected
/// <see cref="TimeProvider"/> so tests drive it deterministically.</para>
/// </summary>
public sealed class E2eRunReplayVerifier : IE2eReplayVerifier
{
    private readonly IE2eRunStore _runs;
    private readonly IOptionsMonitor<E2eReplayAuthoringOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<E2eRunReplayVerifier> _logger;
    private readonly Func<string> _runIdFactory;

    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web);

    public E2eRunReplayVerifier(
        IE2eRunStore runs,
        IOptionsMonitor<E2eReplayAuthoringOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<E2eRunReplayVerifier>? logger = null,
        Func<string>? runIdFactory = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<E2eRunReplayVerifier>.Instance;
        _runIdFactory = runIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async Task<E2eReplayVerificationOutcome> VerifyAsync(string testCaseId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(testCaseId))
            throw new ArgumentException("Test case id is required.", nameof(testCaseId));

        var opts = _options.CurrentValue;
        var timeout = opts.VerificationTimeout > TimeSpan.Zero ? opts.VerificationTimeout : TimeSpan.FromMinutes(20);
        var pollInterval = opts.VerificationPollInterval > TimeSpan.Zero ? opts.VerificationPollInterval : TimeSpan.FromSeconds(2);

        var runId = _runIdFactory();
        await _runs.CreateAsync(
            new E2eRun { Id = runId, TestCaseId = testCaseId, Status = E2eRunStatus.Queued, CreatedAt = _timeProvider.GetUtcNow() },
            ct).ConfigureAwait(false);

        var deadline = _timeProvider.GetUtcNow() + timeout;
        while (true)
        {
            var run = await _runs.GetAsync(runId, ct).ConfigureAwait(false);
            if (run is not null && IsTerminal(run.Status))
                return Interpret(run);

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                // Stop the still-queued/running run so it does not leak a pool
                // lease after we've given up waiting, then report red.
                await TryCancelAsync(runId).ConfigureAwait(false);
                var lastStatus = run?.Status ?? E2eRunStatus.Queued;
                _logger.LogWarning(
                    "E2E replay verification for test case {TestCaseId} timed out after {Timeout}; last status {Status}.",
                    testCaseId, timeout, lastStatus);
                return new E2eReplayVerificationOutcome(false, lastStatus, $"verification timed out after {timeout}");
            }

            await Task.Delay(pollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private async Task TryCancelAsync(string runId)
    {
        try
        {
            await _runs.CancelAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cancel timed-out E2E replay run {RunId}; it may already be terminal.", runId);
        }
    }

    private static bool IsTerminal(E2eRunStatus status)
        => status is E2eRunStatus.Passed or E2eRunStatus.Failed or E2eRunStatus.Error or E2eRunStatus.Canceled;

    private E2eReplayVerificationOutcome Interpret(E2eRun run)
    {
        if (run.Status == E2eRunStatus.Passed)
            return new E2eReplayVerificationOutcome(true, run.Status, DescribeResult(run.Result) ?? "passed");
        return new E2eReplayVerificationOutcome(false, run.Status, DescribeResult(run.Result) ?? run.Status.ToString());
    }

    private string? DescribeResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<E2eRunResult>(resultJson, ResultJson);
            if (parsed is null)
                return null;
            return parsed.FailureKind is { Length: > 0 } kind
                ? $"{kind}: {parsed.Summary}"
                : parsed.Summary;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
