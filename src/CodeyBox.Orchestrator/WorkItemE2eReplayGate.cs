using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeyBox.Orchestrator;

/// <summary>One declared e2e case the gate could not make green, with why.</summary>
public sealed record E2eReplayGateBlocker(string TestCaseId, string Name, string Reason);

/// <summary>
/// Outcome of one gate evaluation for a work item. When
/// <see cref="Enabled"/> is false the gate did nothing. Otherwise
/// <see cref="Blockers"/> lists the declared e2e cases that lack a working
/// committed replay — a non-empty list blocks the work item.
/// </summary>
public sealed record E2eReplayGateResult(
    IReadOnlyList<string> VerifiedCaseIds,
    IReadOnlyList<E2eReplayGateBlocker> Blockers)
{
    public bool Enabled { get; init; } = true;

    public bool Blocked => Blockers.Count > 0;

    /// <summary>The result for a disabled gate: enabled=false, nothing verified, nothing blocked.</summary>
    public static E2eReplayGateResult Disabled { get; } =
        new(Array.Empty<string>(), Array.Empty<E2eReplayGateBlocker>()) { Enabled = false };
}

/// <summary>
/// Post-implementation gate that makes "every declared e2e capability gets a
/// real, green replay" actually happen per work item. For each declared
/// e2e-replay <see cref="TestCase"/> linked to the item it:
/// <list type="number">
///   <item>authors a missing replay with the cheap-model driver and attaches it,</item>
///   <item>verifies the committed replay re-runs green on the E2E pool,</item>
///   <item>re-authors a broken (red-verifying) replay up to the configured cap, and</item>
///   <item>reports a blocker for any case that still lacks a working replay.</item>
/// </list>
///
/// <para>The gate is a pure orchestrator over three injected seams
/// (<see cref="ITestCaseStore"/>, <see cref="IE2eReplayAuthoringDriver"/>,
/// <see cref="IE2eReplayVerifier"/>); it holds no execution/authoring
/// machinery of its own, so it is fully testable with fakes. It never fabricates
/// a replay or a pass — an unauthorable or unverifiable case blocks, which is
/// the correct fail-closed posture for a "must have a working replay" gate.</para>
/// </summary>
public sealed class WorkItemE2eReplayGate
{
    private readonly ITestCaseStore _testCases;
    private readonly IE2eReplayAuthoringDriver? _driver;
    private readonly IE2eReplayVerifier _verifier;
    private readonly IOptionsMonitor<E2eReplayAuthoringOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkItemE2eReplayGate> _logger;

    public WorkItemE2eReplayGate(
        ITestCaseStore testCases,
        IE2eReplayVerifier verifier,
        IOptionsMonitor<E2eReplayAuthoringOptions> options,
        IE2eReplayAuthoringDriver? driver = null,
        TimeProvider? timeProvider = null,
        ILogger<WorkItemE2eReplayGate>? logger = null)
    {
        _testCases = testCases ?? throw new ArgumentNullException(nameof(testCases));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _driver = driver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<WorkItemE2eReplayGate>.Instance;
    }

    public async Task<E2eReplayGateResult> EvaluateAsync(WorkItemId workItemId, CancellationToken ct = default)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled)
            return E2eReplayGateResult.Disabled;

        var maxReauthors = Math.Clamp(
            opts.MaxReauthorAttempts,
            E2eReplayAuthoringOptions.MinReauthorAttempts,
            E2eReplayAuthoringOptions.MaxAllowedReauthorAttempts);

        var verified = new List<string>();
        var blockers = new List<E2eReplayGateBlocker>();

        await foreach (var testCase in _testCases.ListByWorkItemAsync(workItemId.ToString(), ct).ConfigureAwait(false))
        {
            var need = E2eReplayGatePolicy.Classify(testCase);
            switch (need)
            {
                case E2eReplayCaseNeed.NotApplicable:
                    continue;

                case E2eReplayCaseNeed.Satisfied:
                    verified.Add(testCase.Id);
                    continue;

                default:
                    var outcome = await EnsureWorkingReplayAsync(testCase, need, maxReauthors, ct).ConfigureAwait(false);
                    if (outcome.Resolved)
                        verified.Add(testCase.Id);
                    else
                        blockers.Add(new E2eReplayGateBlocker(testCase.Id, testCase.Name, outcome.Reason!));
                    continue;
            }
        }

        if (blockers.Count > 0)
            _logger.LogWarning(
                "E2E replay gate blocked work item {WorkItemId}: {BlockCount} declared e2e case(s) lack a working replay.",
                workItemId, blockers.Count);

        return new E2eReplayGateResult(verified, blockers);
    }

    private async Task<CaseOutcome> EnsureWorkingReplayAsync(
        TestCase testCase,
        E2eReplayCaseNeed need,
        int maxReauthors,
        CancellationToken ct)
    {
        var current = testCase;

        if (need == E2eReplayCaseNeed.NeedsAuthoring)
        {
            var authored = await AuthorAndPersistAsync(current, new E2eReplayAuthoringRequest(), ct).ConfigureAwait(false);
            if (authored.Persisted is null)
                return CaseOutcome.Block(authored.Reason ?? "no committed replay and authoring did not produce one");
            current = authored.Persisted;
        }

        var reauthorsLeft = maxReauthors;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var verification = await _verifier.VerifyAsync(current.Id, ct).ConfigureAwait(false);
            if (verification.Passed)
                return CaseOutcome.Ok();

            if (reauthorsLeft <= 0)
                return CaseOutcome.Block($"replay verification failed ({verification.Status}): {verification.Detail}");

            reauthorsLeft--;
            _logger.LogInformation(
                "Re-authoring broken e2e replay for test case {TestCaseId} ({Remaining} attempt(s) left after this).",
                current.Id, reauthorsLeft);

            var request = new E2eReplayAuthoringRequest
            {
                PreviousArtifactJson = current.ExecutableArtifactJson,
                FailedReplay = ParseFailedReplay(verification.Detail),
            };
            var reauthored = await AuthorAndPersistAsync(current, request, ct).ConfigureAwait(false);
            if (reauthored.Persisted is null)
                return CaseOutcome.Block(reauthored.Reason ?? "replay broke and re-authoring did not produce a refreshed replay");
            current = reauthored.Persisted;
        }
    }

    /// <summary>
    /// Drives the cheap-model author for one case and, on success, attaches the
    /// fresh artifact to the case. Attaching resets the last-run fields so the
    /// new artifact is treated as unverified (a stale pass never masks it).
    /// Returns the persisted case, or a reason when the case could not be
    /// authored/persisted.
    /// </summary>
    private async Task<PersistResult> AuthorAndPersistAsync(
        TestCase testCase,
        E2eReplayAuthoringRequest request,
        CancellationToken ct)
    {
        if (_driver is null)
            return PersistResult.Fail("no cheap-model authoring driver is wired");

        var outcome = await _driver.AuthorAsync(testCase, request, ct).ConfigureAwait(false);
        if (!outcome.Authored || string.IsNullOrWhiteSpace(outcome.ArtifactJson))
        {
            var reason = outcome.UnresolvedReason ?? "authoring produced no replay artifact";
            _logger.LogWarning("Cheap-model authoring did not produce a replay for test case {TestCaseId}: {Reason}", testCase.Id, reason);
            return PersistResult.Fail(reason);
        }

        // Re-read the latest row so we attach onto (and only touch) the current
        // record rather than clobbering a concurrent operator edit.
        var latest = await _testCases.GetAsync(testCase.Id, ct).ConfigureAwait(false) ?? testCase;
        var updated = latest with
        {
            ExecutableArtifactJson = outcome.ArtifactJson,
            UpdatedAt = _timeProvider.GetUtcNow(),
            // A rewritten artifact invalidates any prior run outcome — clear it
            // so classification/verification re-runs against the new replay.
            LastRunPassed = null,
            LastRunAt = null,
            LastRunResult = null,
        };

        if (!await _testCases.UpdateAsync(updated, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Test case {TestCaseId} vanished before its authored replay could be attached.", testCase.Id);
            return PersistResult.Fail("test case was removed before the authored replay could be attached");
        }

        _logger.LogInformation(
            "Attached {Kind} e2e replay to test case {TestCaseId} (author model {Model}).",
            request.IsReauthoring ? "re-authored" : "authored", testCase.Id, outcome.AuthorModelId);
        return PersistResult.Ok(updated);
    }

    private static E2eRunResult? ParseFailedReplay(string? detail)
    {
        // The verifier folds the run result into a summary string; there is no
        // structured E2eRunResult to hand back, so re-authoring gets the summary
        // as context only. Kept as a seam for a future structured hand-off.
        if (string.IsNullOrWhiteSpace(detail))
            return null;
        return new E2eRunResult { Passed = false, Summary = detail };
    }

    private readonly record struct CaseOutcome(bool Resolved, string? Reason)
    {
        public static CaseOutcome Ok() => new(true, null);
        public static CaseOutcome Block(string reason) => new(false, reason);
    }

    private readonly record struct PersistResult(TestCase? Persisted, string? Reason)
    {
        public static PersistResult Ok(TestCase persisted) => new(persisted, null);
        public static PersistResult Fail(string reason) => new(null, reason);
    }
}
