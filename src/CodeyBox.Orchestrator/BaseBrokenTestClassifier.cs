using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Verdict of the audit-time test-red check: the base tip adopted for the
/// condition plus the evidence to record on it.
/// </summary>
internal sealed record BaseTestBrokenVerdict(
    string BaseSha,
    IReadOnlyList<string> FailingTests,
    IReadOnlyList<string> SuspectCommits,
    string Summary);

/// <summary>
/// Audit-time counterpart of <see cref="BaseBrokenBuildClassifier"/> for
/// tests: decides whether an iteration's test-only failures belong to the
/// base branch rather than the item's diff. Fail-closed — any uncertainty
/// (disabled kill-switch, non-test blocking findings, a single
/// diff-attributable or skipped attribution) returns null so the normal
/// rework/flake paths own the failure.
///
/// Attribution rule: every blocking finding must come from a test-runner
/// auditor (resolved via <see cref="ITestRunnerAuditor"/> /
/// <see cref="ITestRunnerAuditorProvider"/>, never name matching) and every
/// observed test failure must carry a genuine base-branch verdict
/// (<see cref="NonDeterministicTestEscalationPolicy.IsActionableAttribution"/>:
/// failed on the diff, failed on the base rerun). The failure is then
/// adopted by an already-active condition when one exists (the "main is
/// red" case — no new evidence needed), or recorded against the current
/// base tip when the forge reports that tip's required checks red. A green
/// or unknown tip CI leaves the failure to the flake path, which already
/// parks the parent without blame.
/// </summary>
internal sealed class BaseBrokenTestClassifier
{
    private readonly BaseBrokenConditionTracker? _tracker;
    private readonly IGitHost _gitHost;
    private readonly IUpstreamRemoteFactory? _upstreamFactory;
    private readonly PipelineTuningSnapshot _tuning;
    private readonly ILogger _log;

    public BaseBrokenTestClassifier(
        BaseBrokenConditionTracker? tracker,
        IGitHost gitHost,
        PipelineTuningSnapshot tuning,
        IUpstreamRemoteFactory? upstreamFactory = null,
        ILogger? log = null)
    {
        _tracker = tracker;
        _gitHost = gitHost ?? throw new ArgumentNullException(nameof(gitHost));
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _upstreamFactory = upstreamFactory;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Pure gate: true when the iteration's failures are test-only (every
    /// blocking finding from a test-runner auditor) and unanimously
    /// base-reproducing (every attribution a genuine base verdict).
    /// </summary>
    internal static bool IsTestOnlyBaseFailure(
        IReadOnlyList<AuditFinding> blocking,
        IReadOnlyList<TestFailureAttributionResult> attributions,
        IReadOnlyCollection<string> testRunnerAuditorNames)
    {
        if (blocking is null || blocking.Count == 0)
            return false;
        if (attributions is null || attributions.Count == 0)
            return false;
        foreach (var finding in blocking)
        {
            if (!testRunnerAuditorNames.Contains(finding.AuditorName))
                return false;
        }
        foreach (var attribution in attributions)
        {
            if (!NonDeterministicTestEscalationPolicy.IsActionableAttribution(attribution))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Test-runner auditor names for the scheduled panel: direct
    /// <see cref="ITestRunnerAuditor"/> implementations plus wrappers
    /// exposing one via <see cref="ITestRunnerAuditorProvider"/>.
    /// </summary>
    internal static HashSet<string> TestRunnerAuditorNames(IEnumerable<IAuditor> auditors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var auditor in auditors)
        {
            if (auditor is ITestRunnerAuditor)
                names.Add(auditor.Name);
            else if (auditor is ITestRunnerAuditorProvider provider && provider.TestRunner is not null)
                names.Add(auditor.Name);
        }
        return names;
    }

    public async Task<BaseTestBrokenVerdict?> TryClassifyAsync(
        WorkItem item,
        Project project,
        string repoId,
        string baseBranch,
        IReadOnlyList<AuditFinding> blocking,
        IReadOnlyList<TestFailureAttributionResult> attributions,
        IReadOnlyCollection<string> testRunnerAuditorNames,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(project);
        if (_tracker is null)
            return null;
        if (!_tuning.Current.BaseBrokenDetectionEnabled)
            return null;
        if (!IsTestOnlyBaseFailure(blocking, attributions, testRunnerAuditorNames))
            return null;

        var failingTests = SelectFailingTests(attributions);
        if (failingTests.Count == 0)
            return null;

        // Case 1 — main is already recorded red: adopt the newest active
        // condition. Suspects are parsed back out of its stored summary so
        // the fix item names the same commits the detector saw.
        await _tracker.HydrateAsync(ct).ConfigureAwait(false);
        var active = _tracker.GetActiveConditions()
            .Where(c => c.ProjectId == project.Id)
            .OrderByDescending(c => c.DetectedAt)
            .ToList();
        if (active.Count > 0)
        {
            var adopted = active[0];
            var suspects = BaseBrokenTestSupport.ParseSuspects(adopted.ErrorSummary);
            var names = failingTests.Count > 0 ? failingTests : adopted.FailingNames;
            _log.LogWarning(
                "Work item {Id} test failures adopted by active base-broken condition for project {ProjectId} at {Sha}",
                item.Id, project.Id.Value, adopted.BaseSha);
            return new BaseTestBrokenVerdict(
                adopted.BaseSha,
                names,
                suspects,
                BaseBrokenTestSupport.BuildSummary(names, suspects));
        }

        // Case 2 — no recorded condition: only the forge's verdict on the
        // current tip can prove red. Resolve the tip, then ask the forge.
        string tipSha;
        try
        {
            tipSha = await _gitHost.ResolveCommitAsync(repoId, baseBranch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Work item {Id}: could not resolve base tip for test-red check; leaving failure item-attributed",
                item.Id);
            return null;
        }

        var failingChecks = await TryGetFailingChecksAsync(project, tipSha, ct).ConfigureAwait(false);
        if (failingChecks is null || failingChecks.Count == 0)
            return null;

        var combined = failingTests.Concat(failingChecks)
            .Distinct(StringComparer.Ordinal)
            .Take(BaseBrokenFixItemPolicy.MaxRecordedFailingNames)
            .ToList();
        var suspectsForTip = BaseBrokenTestSupport.SanitizeShas([tipSha]);
        return new BaseTestBrokenVerdict(
            tipSha,
            combined,
            suspectsForTip,
            BaseBrokenTestSupport.BuildSummary(combined, suspectsForTip));
    }

    private static IReadOnlyList<string> SelectFailingTests(
        IReadOnlyList<TestFailureAttributionResult> attributions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var attribution in attributions)
        {
            var name = NonDeterministicTestEscalationPolicy.NormalizeTestName(attribution.TestName);
            if (name is null || !seen.Add(name))
                continue;
            result.Add(name);
            if (result.Count >= BaseBrokenFixItemPolicy.MaxRecordedFailingNames)
                break;
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Forge verdict for the tip: failing check names when the forge reports
    /// required checks red, empty when green, null when unknown
    /// (unsupported forge, transport fault, or still pending). Null/empty
    /// never records — only a proven-red tip does.
    /// </summary>
    private async Task<IReadOnlyList<string>?> TryGetFailingChecksAsync(
        Project project,
        string tipSha,
        CancellationToken ct)
    {
        if (_upstreamFactory is null)
            return null;
        UpstreamCheckSummary? summary;
        try
        {
            var remote = _upstreamFactory.Create(project);
            summary = await remote.GetCheckResultsAsync(tipSha, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not read CI status for base tip {Sha}; leaving test failure item-attributed",
                BaseBrokenTestSupport.ShortSha(tipSha));
            return null;
        }

        if (summary is null || summary.RequiredChecksPassed)
            return null;
        var failing = summary.Checks
            .Where(c => c.State == UpstreamCheckState.Failing)
            .Select(c => c.Name)
            .ToList();
        return failing.Count == 0 ? null : failing;
    }
}
