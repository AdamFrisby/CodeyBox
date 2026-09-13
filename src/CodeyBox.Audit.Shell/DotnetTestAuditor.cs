using System.Globalization;
using System.Text.Json;
using CodeyBox.Audit;
using CodeyBox.Core;

namespace CodeyBox.Audit.Shell;

/// <summary>
/// First-class <c>dotnet test</c> auditor. Replaces the previous arrangement
/// where <c>csharp:test-pass</c> was a generic <see cref="ShellCommandAuditor"/>
/// that three separate call sites had to sniff as "really a dotnet test"
/// (result-classifier selection by <c>argv[1]=="test"</c>, per-test hang
/// handling, and a future <c>--filter</c> injection).
///
/// This type OWNS building the invocation — base command, test selection
/// <c>--filter</c>, and <c>--blame-hang</c> args — and carries its own result
/// classifier. Actual execution (tool-presence probe, missing-tool handling,
/// classification) is delegated to a <see cref="ShellCommandAuditor"/> so the
/// well-tested shell run semantics are reused verbatim.
///
/// With an all-tests selection and default options the emitted command is
/// byte-identical to the legacy <c>["dotnet","test","--no-build"]</c> path.
/// </summary>
public sealed class DotnetTestAuditor : IAuditor, ITestRunnerAuditor, IShellAuditorArgvProvider
{
    private readonly DotnetTestAuditorOptions _opts;
    private readonly Func<TestRunOptions> _runOptions;
    private readonly DotnetTestCommandResultClassifier _classifier = new();

    public DotnetTestAuditor(DotnetTestAuditorOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        if (opts.BaseArgv.Count == 0)
            throw new ArgumentException("BaseArgv must be non-empty", nameof(opts));
        _opts = opts;
        _runOptions = opts.RunOptionsAccessor ?? (static () => TestRunOptions.Default);
    }

    public string Name => _opts.Name;
    public string Kind => "shell";
    public AuditCapabilities Required => AuditCapabilities.None;
    public bool CanShortCircuitOnBlockingFinding => _opts.CanShortCircuitOnBlockingFinding;
    public AuditorRole Role => _opts.Role;
    public BuildTestGateEvidence BuildTestGateEvidence => _opts.Role == AuditorRole.BuildTestGate
        ? _opts.BuildTestGateEvidence
        : BuildTestGateEvidence.None;

    public TestSuiteDescriptor TestSuite =>
        new(TestFramework.DotnetTest, [.. _opts.BaseArgv, "--list-tests"]);

    public IAuditResultClassifier ResultClassifier => _classifier;

    public TestRunOptions CurrentRunOptions => _runOptions();

    /// <summary>
    /// The argv this auditor invokes for a full test run under the current
    /// (hot-reloadable) options. Exposed so the work-phase prompt builder can
    /// advise the agent to run the same command before committing.
    /// </summary>
    public IReadOnlyList<string> Argv => BuildInvocation(TestSelection.All, CurrentRunOptions);

    public IReadOnlyList<string> BuildInvocation(TestSelection selection, TestRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(options);

        var argv = new List<string>(_opts.BaseArgv);

        if (!selection.IsAll)
        {
            argv.Add("--filter");
            argv.Add(BuildFilterExpression(selection.Filters));
        }

        if (options.BlameHangTimeout is { } hang && hang > TimeSpan.Zero)
        {
            argv.Add("--blame-hang");
            argv.Add("--blame-hang-timeout");
            argv.Add(FormatHangTimeout(hang));
        }

        return argv;
    }

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        var shadow = _opts.Shadow;
        if (shadow is not null)
        {
            var mode = ResolveMode(shadow);
            if (mode == TestSelectionMode.CoverageShadow)
                return await RunWithShadowAsync(sandbox, workingDirectory, context, shadow, ct).ConfigureAwait(false);
            if (mode == TestSelectionMode.ProjectGraph)
                return await RunWithEnforcementAsync(
                    sandbox, workingDirectory, context, shadow,
                    ProjectGraphTestSelector.SelectorName, ct).ConfigureAwait(false);
            if (mode == TestSelectionMode.Coverage)
                return await RunWithEnforcementAsync(
                    sandbox, workingDirectory, context, shadow,
                    CoverageTestSelector.SelectorName, ct).ConfigureAwait(false);
        }
        var full = await RunFullAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
        return full with { TestSelection = TestSelectionTelemetryComputer.FullSuiteWithoutShadow(ResolveModeName(shadow)) };
    }

    private static string ResolveModeName(TestSelectionShadowConfig? shadow)
    {
        if (shadow is null)
            return TestSelectionMode.All.ToString();
        try
        {
            return shadow.ModeAccessor().ToString();
        }
        catch (Exception)
        {
            return TestSelectionMode.All.ToString();
        }
    }

    private static TestSelectionMode ResolveMode(TestSelectionShadowConfig shadow)
    {
        try
        {
            return shadow.ModeAccessor();
        }
        catch (Exception)
        {
            // A hot-reloaded invalid mode must never break the test gate: fall
            // through to the full suite (the options validator surfaces the bad
            // value at load).
            return TestSelectionMode.All;
        }
    }

    /// <summary>
    /// SHADOW-BEFORE-ENFORCE: computes the advisory selection and records the
    /// validation verdict, but ALWAYS executes the full suite. The narrowed
    /// <c>--filter</c> argv is computed for the record only and is never
    /// executed by this path.
    /// </summary>
    private async Task<AuditResult> RunWithShadowAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        TestSelectionShadowConfig shadow,
        CancellationToken ct)
    {
        var changedFiles = await TestSelectionShadowIO.GetChangedFilesAsync(
            sandbox, workingDirectory, context.BaseBranch, shadow.OptionsAccessor, ct).ConfigureAwait(false);
        var baseline = await TestSelectionShadowIO.ReadBaselineAsync(
            sandbox, workingDirectory, shadow.OptionsAccessor, ct).ConfigureAwait(false);
        var currentCommit = await TestSelectionShadowIO.GetCurrentCommitAsync(
            sandbox, workingDirectory, ct).ConfigureAwait(false);

        var selectionDetail = $"baseline: {baseline.Detail}";
        TestSelectionDecision decision;
        if (string.IsNullOrWhiteSpace(context.BaseBranch))
        {
            decision = new TestSelectionDecision(TestSelection.All, "unknown base ref");
        }
        else
        {
            try
            {
                decision = shadow.Selector.Select(new TestSelectionRequest(
                    this, context.BaseBranch, changedFiles, baseline.Baseline, currentCommit));
            }
            catch (Exception ex)
            {
                // Fail-safe: any selector error falls back to the full run.
                decision = new TestSelectionDecision(
                    TestSelection.All,
                    $"selector error ({ex.GetType().Name}: {TruncateForDetail(ex.Message)})");
            }
        }
        selectionDetail = $"{decision.Justification} | {selectionDetail}";

        // Record-only: the WOULD-BE narrowed command, proving the --filter
        // injection shape without executing it.
        var wouldBeArgv = string.Join(' ', BuildInvocation(decision.Selection, CurrentRunOptions));

        // Structural full-suite: the executed invocation always uses
        // TestSelection.All, regardless of the advisory decision above.
        var result = await RunFullAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);

        var failedTests = DotnetTestOutputParser.Parse(Name, result.RawOutput ?? "").FailedTestNames;
        var universe = baseline.Baseline is null
            ? (IReadOnlyList<string>)[]
            : [.. baseline.Baseline.Tests.Keys];

        var record = BuildShadowRecord(
            shadow, context, decision, selectionDetail, wouldBeArgv, universe, failedTests);
        var telemetry = TestSelectionTelemetryComputer.FromShadowRecord(record, universe.Count);

        try
        {
            shadow.Sink.Emit(record);
        }
        catch (Exception ex)
        {
            // Shadow telemetry must never fail the gate: surface visibly as a
            // non-blocking finding instead of throwing or swallowing silently.
            return result with
            {
                TestSelection = telemetry,
                Findings = [.. result.Findings, new AuditFinding(
                    AuditorName: Name,
                    Severity: AuditSeverity.Info,
                    Title: "test-selection shadow record not emitted",
                    Description: $"The advisory selection ran but its record could not be emitted ({ex.GetType().Name}). " +
                        "The full test suite still ran; no test was skipped.")],
            };
        }

        return result with { TestSelection = telemetry };
    }

    private static TestSelectionShadowRecord BuildShadowRecord(
        TestSelectionShadowConfig shadow,
        AuditContext context,
        TestSelectionDecision decision,
        string selectionDetail,
        string wouldBeArgv,
        IReadOnlyList<string> universe,
        IReadOnlyList<string> failedTests)
    {
        var modeName = TestSelectionMode.CoverageShadow.ToString();
        var baseRef = string.IsNullOrWhiteSpace(context.BaseBranch) ? "unknown" : context.BaseBranch;
        if (decision.Selection.IsAll)
        {
            return TestSelectionShadowEvaluator.Evaluate(
                shadow.SelectorName, modeName, baseRef,
                true, new HashSet<string>(StringComparer.Ordinal),
                universe, failedTests, wouldBeArgv, selectionDetail);
        }

        if (!TestSelectionShadowEvaluator.TryResolveSelectedTests(decision, universe, out var selected))
        {
            return new TestSelectionShadowRecord(
                shadow.SelectorName, modeName, baseRef,
                false, selectionDetail + " | unresolvable raw filter expressions",
                [], 0, [], 0,
                failedTests, failedTests.Count, [],
                TestSelectionShadowRecord.AssessmentUnverifiable, wouldBeArgv);
        }

        return TestSelectionShadowEvaluator.Evaluate(
            shadow.SelectorName, modeName, baseRef,
            false, selected,
            universe, failedTests, wouldBeArgv, selectionDetail);
    }

    /// <summary>
    /// ENFORCING selection (<c>Audit:TestSelection:Mode=project-graph</c> or
    /// <c>coverage</c>): resolves the affected tests and executes ONLY that
    /// subset via <c>--filter</c>. The <paramref name="enforcedSelectorName"/>
    /// stamps the telemetry selector (and its layers): the project-graph rung
    /// executes the superset, while the coverage rung executes the
    /// coverage-narrowed subset nested inside that superset — or, when the
    /// coverage rung falls back (<see cref="CoverageTestSelector.ProjectGraphRungMarker"/>),
    /// the superset itself, with the fired rung recorded in telemetry
    /// fallbacks. Fail-safe: any selector error, unreadable options, unknown
    /// base ref, an ambiguous result (empty/blank filters), a filter-build
    /// failure, or a narrowed run that executes zero tests falls back to the
    /// full suite. The merge/release path never reaches here — it takes no
    /// <c>ITestSelector</c> dependency by construction.
    /// </summary>
    private async Task<AuditResult> RunWithEnforcementAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        TestSelectionShadowConfig shadow,
        string enforcedSelectorName,
        CancellationToken ct)
    {
        var changedFiles = await TestSelectionShadowIO.GetChangedFilesAsync(
            sandbox, workingDirectory, context.BaseBranch, shadow.OptionsAccessor, ct).ConfigureAwait(false);
        var baseline = await TestSelectionShadowIO.ReadBaselineAsync(
            sandbox, workingDirectory, shadow.OptionsAccessor, ct).ConfigureAwait(false);
        var currentCommit = await TestSelectionShadowIO.GetCurrentCommitAsync(
            sandbox, workingDirectory, ct).ConfigureAwait(false);

        var universe = baseline.Baseline is null
            ? (IReadOnlyList<string>)[]
            : [.. baseline.Baseline.Tests.Keys];
        var modeName = enforcedSelectorName == CoverageTestSelector.SelectorName
            ? TestSelectionMode.Coverage.ToString()
            : TestSelectionMode.ProjectGraph.ToString();

        TestSelectionDecision decision;
        if (string.IsNullOrWhiteSpace(context.BaseBranch))
        {
            decision = new TestSelectionDecision(TestSelection.All, "unknown base ref");
        }
        else
        {
            try
            {
                decision = shadow.Selector.Select(new TestSelectionRequest(
                    this, context.BaseBranch, changedFiles, baseline.Baseline, currentCommit));
            }
            catch (Exception ex)
            {
                // Fail-safe: any selector error falls back to the full run.
                decision = new TestSelectionDecision(
                    TestSelection.All,
                    $"selector error ({ex.GetType().Name}: {TruncateForDetail(ex.Message)})");
            }
        }

        var detail = $"{decision.Justification} | baseline: {baseline.Detail}";
        if (decision.Selection.IsAll || IsAmbiguousSelection(decision.Selection))
        {
            if (!decision.Selection.IsAll)
            {
                detail += " | ambiguous selection fell back to the full suite";
                decision = new TestSelectionDecision(TestSelection.All, detail);
            }
            var full = await RunFullAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
            var telemetry = TestSelectionTelemetryComputer.FromEnforcedSelection(
                modeName, enforcedSelectorName, decision, universe.Count, detail);
            return full with { TestSelection = telemetry };
        }

        IReadOnlyList<string> invocation;
        try
        {
            invocation = BuildInvocation(decision.Selection, CurrentRunOptions);
        }
        catch (Exception ex)
        {
            var buildFailure = detail + $" | filter build failed ({ex.GetType().Name}: {TruncateForDetail(ex.Message)})";
            var full = await RunFullAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
            var fallbackTelemetry = TestSelectionTelemetryComputer.FromEnforcedSelection(
                modeName, enforcedSelectorName,
                new TestSelectionDecision(TestSelection.All, buildFailure),
                universe.Count, buildFailure);
            return full with { TestSelection = fallbackTelemetry };
        }

        var narrowed = await RunInvocationAsync(invocation, context, sandbox, workingDirectory, ct).ConfigureAwait(false);
        if (narrowed.Passed && RanZeroTests(narrowed.RawOutput ?? ""))
        {
            var zeroTests = detail + " | narrowed run executed zero tests; fell back to the full suite";
            var full = await RunFullAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
            var fallbackTelemetry = TestSelectionTelemetryComputer.FromEnforcedSelection(
                modeName, enforcedSelectorName,
                new TestSelectionDecision(TestSelection.All, zeroTests),
                universe.Count, zeroTests);
            return full with { TestSelection = fallbackTelemetry };
        }
        var enforcedTelemetry = TestSelectionTelemetryComputer.FromEnforcedSelection(
            modeName, enforcedSelectorName, decision, universe.Count, detail,
            RungFallbacks(decision));
        return narrowed with { TestSelection = enforcedTelemetry };
    }

    /// <summary>
    /// Ladder-rung attribution for a narrowed enforcing run: when the coverage
    /// selector descended to its project-graph rung, the coverage reason rides
    /// in telemetry fallbacks so operators see which rung fired. Any other
    /// narrowed run narrowed without falling back.
    /// </summary>
    private static IReadOnlyList<string>? RungFallbacks(TestSelectionDecision decision)
        => decision.Justification.Contains(
            CoverageTestSelector.ProjectGraphRungMarker, StringComparison.Ordinal)
            ? [decision.Justification]
            : null;

    private static string TruncateForDetail(string message, int maxChars = 200)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "no detail";
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxChars ? singleLine : singleLine[..maxChars] + "...";
    }

    private static bool IsAmbiguousSelection(TestSelection selection)
    {
        if (selection.IsAll)
            return false;
        if (selection.Filters.Count == 0)
            return true;
        return selection.Filters.Any(string.IsNullOrWhiteSpace);
    }

    private Task<AuditResult> RunInvocationAsync(
        IReadOnlyList<string> invocation,
        AuditContext context,
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        var inner = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = _opts.Name,
            Argv = invocation,
            ResultClassifier = _classifier,
            CanShortCircuitOnBlockingFinding = _opts.CanShortCircuitOnBlockingFinding,
            Role = _opts.Role,
            BuildTestGateEvidence = _opts.BuildTestGateEvidence,
            SelfHealNuGetHome = _opts.SelfHealNuGetHome,
            TestFailureAttributionOptions = _opts.TestFailureAttributionOptions,
        });
        return inner.RunAsync(sandbox, workingDirectory, context, ct);
    }

    private Task<AuditResult> RunFullAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct)
    {
        // Delegate the run to a ShellCommandAuditor built from the current
        // invocation so the tool-presence probe, missing-tool handling and
        // result classification stay identical to the generic shell path.
        var invocation = BuildInvocation(TestSelection.All, CurrentRunOptions);
        var inner = new ShellCommandAuditor(new ShellCommandAuditorOptions
        {
            Name = _opts.Name,
            Argv = invocation,
            ResultClassifier = _classifier,
            CanShortCircuitOnBlockingFinding = _opts.CanShortCircuitOnBlockingFinding,
            Role = _opts.Role,
            BuildTestGateEvidence = _opts.BuildTestGateEvidence,
            // dotnet test performs a NuGet restore (even with --no-build it reads
            // the settings), so it self-heals an unusable $HOME/.nuget through the
            // single SelfHealNuGetHome mechanism (NuGetHomeSelfHeal wrapper) — the
            // one NuGet-home heal source every .NET gate shares.
            SelfHealNuGetHome = _opts.SelfHealNuGetHome,
            TestFailureAttributionOptions = _opts.TestFailureAttributionOptions,
        });
        return inner.RunAsync(sandbox, workingDirectory, context, ct);
    }

    /// <summary>
    /// Maps a set of selected tests to a <c>dotnet test --filter</c> expression.
    /// Every entry is emitted as an escaped <c>FullyQualifiedName=</c> match:
    /// test names originate from the test-selection baseline (untrusted
    /// sandbox-produced input), so no entry is ever passed through as raw
    /// filter syntax — VSTest metacharacters are backslash-escaped via
    /// <see cref="VstestFilterEscaping"/>. Multiple entries are OR-joined.
    /// </summary>
    private static string BuildFilterExpression(IReadOnlyList<string> filters)
        => string.Join("|", filters.Select(f =>
            $"FullyQualifiedName={VstestFilterEscaping.EscapeValue(f)}"));

    /// <summary>
    /// True when a narrowed run's output shows ZERO tests executed: VSTest
    /// prints "No test matches the given testcase filter" (exit 0), or a
    /// summary with a zero total. Such a run must fall back to the full suite
    /// rather than report a pass — a crafted filter that matches nothing would
    /// otherwise falsify the gate.
    /// </summary>
    private static bool RanZeroTests(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;
        if (output.Contains("No test matches the given testcase filter", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if ((trimmed.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase))
                && trimmed.Contains("Total: 0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Formats a hang timeout for <c>--blame-hang-timeout</c>, which accepts a
    /// number suffixed with a unit. Whole-second values are emitted as <c>s</c>;
    /// any non-whole-second value (including sub-second) falls back to <c>ms</c>.
    /// </summary>
    private static string FormatHangTimeout(TimeSpan timeout)
    {
        var totalMs = (long)timeout.TotalMilliseconds;
        return totalMs % 1000 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{totalMs / 1000}s")
            : string.Create(CultureInfo.InvariantCulture, $"{totalMs}ms");
    }
}

/// <summary>
/// Construction inputs for <see cref="DotnetTestAuditor"/>. <see cref="BaseArgv"/>
/// is the framework command the preset supplies (e.g.
/// <c>["dotnet","test","--no-build"]</c>); selection filters and blame-hang args
/// are layered on by <see cref="DotnetTestAuditor.BuildInvocation"/>.
/// </summary>
public sealed record DotnetTestAuditorOptions
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> BaseArgv { get; init; }
    public bool CanShortCircuitOnBlockingFinding { get; init; }
    public AuditorRole Role { get; init; } = AuditorRole.None;
    public BuildTestGateEvidence BuildTestGateEvidence { get; init; } = BuildTestGateEvidence.None;

    /// <summary>
    /// Forwarded to the delegated <see cref="ShellCommandAuditor"/> so a
    /// <c>dotnet test</c> run self-heals a root-owned <c>~/.nuget</c>. Off by
    /// default; see <see cref="ShellCommandAuditorOptions.SelfHealNuGetHome"/>.
    /// </summary>
    public bool SelfHealNuGetHome { get; init; }

    /// <summary>
    /// Live accessor for hot-reloadable run options (blame-hang / idle-timeout).
    /// Null defaults to <see cref="TestRunOptions.Default"/>, which keeps the
    /// emitted command byte-identical to the legacy path.
    /// </summary>
    public Func<TestRunOptions>? RunOptionsAccessor { get; init; }

    /// <summary>
    /// Optional hot-reloadable test-failure-attribution options. When set, a
    /// classified test failure triggers a base-checkout rerun that attributes
    /// each failing test to the diff or to pre-existing state.
    /// </summary>
    public TestFailureAttributionOptionsSnapshot? TestFailureAttributionOptions { get; init; }

    /// <summary>
    /// Test-selection configuration. When set, the live mode decides the run:
    /// <c>coverage-shadow</c> computes the selector's advisory decision, still
    /// executes the FULL suite, and emits a shadow record (SHADOW-BEFORE-ENFORCE);
    /// <c>project-graph</c> executes ONLY the project-graph subset (enforcing),
    /// <c>coverage</c> executes ONLY the coverage subset nested inside that
    /// superset (enforcing, with fallback down the coverage → project-graph →
    /// all ladder), each falling back to the full suite on any error or
    /// ambiguous result; <c>all</c> (or unset) runs the full suite. Null (the
    /// default) disables selection — byte-identical legacy runs.
    /// </summary>
    public TestSelectionShadowConfig? Shadow { get; init; }
}
