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

    public Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        var shadow = _opts.Shadow;
        if (shadow is not null && IsShadowMode(shadow))
            return RunWithShadowAsync(sandbox, workingDirectory, context, shadow, ct);
        return RunFullAsync(sandbox, workingDirectory, context, ct);
    }

    private static bool IsShadowMode(TestSelectionShadowConfig shadow)
    {
        try
        {
            return shadow.ModeAccessor() == TestSelectionMode.CoverageShadow;
        }
        catch (Exception)
        {
            // A hot-reloaded invalid mode must never break the test gate: skip
            // the shadow (the options validator surfaces the bad value at load).
            return false;
        }
    }

    /// <summary>
    /// SHADOW-BEFORE-ENFORCE: computes the advisory selection and records the
    /// validation verdict, but ALWAYS executes the full suite. The narrowed
    /// <c>--filter</c> argv is computed for the record only and is never
    /// executed by this ticket.
    /// </summary>
    private async Task<AuditResult> RunWithShadowAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        TestSelectionShadowConfig shadow,
        CancellationToken ct)
    {
        var changedFiles = await TestSelectionShadowIO.GetChangedFilesAsync(
            sandbox, workingDirectory, context.BaseBranch, ct).ConfigureAwait(false);
        var baseline = await TestSelectionShadowIO.ReadBaselineAsync(
            sandbox, workingDirectory, shadow.OptionsAccessor, ct).ConfigureAwait(false);

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
                    this, context.BaseBranch, changedFiles, baseline.Baseline));
            }
            catch (Exception ex)
            {
                // Fail-safe: any selector error falls back to the full run.
                decision = new TestSelectionDecision(
                    TestSelection.All,
                    $"selector error ({ex.GetType().Name})");
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
                Findings = [.. result.Findings, new AuditFinding(
                    AuditorName: Name,
                    Severity: AuditSeverity.Info,
                    Title: "test-selection shadow record not emitted",
                    Description: $"The advisory selection ran but its record could not be emitted ({ex.GetType().Name}). " +
                        "The full test suite still ran; no test was skipped.")],
            };
        }

        return result;
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
    /// Bare names are matched by fully-qualified name; entries that already carry
    /// an <c>=</c> or <c>~</c> operator are passed through unchanged so a selector
    /// can supply raw expressions (this covers <c>!=</c> too, since it contains
    /// <c>=</c>). Multiple entries are OR-joined.
    /// </summary>
    private static string BuildFilterExpression(IReadOnlyList<string> filters)
        => string.Join("|", filters.Select(f =>
            f.Contains('=', StringComparison.Ordinal) || f.Contains('~', StringComparison.Ordinal)
                ? f
                : $"FullyQualifiedName={f}"));

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
    /// Advisory test-selection shadow (SHADOW-BEFORE-ENFORCE). When set and the
    /// live mode is <c>coverage-shadow</c>, each run computes the selector's
    /// advisory decision, still executes the FULL suite, and emits a shadow
    /// record (would-be selection plus whether any deselected test failed).
    /// Null (the default) disables the shadow — byte-identical legacy runs.
    /// Real skipping is never performed here.
    /// </summary>
    public TestSelectionShadowConfig? Shadow { get; init; }
}
