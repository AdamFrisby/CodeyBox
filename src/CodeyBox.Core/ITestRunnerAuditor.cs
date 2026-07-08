namespace CodeyBox.Core;

/// <summary>
/// A first-class deterministic test-runner auditor. Promotes what used to be a
/// generic <c>ShellCommandAuditor</c> special-cased as "really a dotnet test"
/// into a declared capability: the type OWNS building its invocation (including
/// test selection filters, per-test hang-dump args and timeouts), exposes how to
/// enumerate the test universe, and carries its own result classifier.
///
/// The <c>ITestSelector</c> seam (a separate work item) consumes
/// <see cref="TestSuite"/> to enumerate candidate tests and then calls
/// <see cref="BuildInvocation"/> with a narrowed <see cref="TestSelection"/>.
/// With an all-tests selection and default options the emitted command is
/// byte-identical to the legacy generic-shell path.
/// </summary>
public interface ITestRunnerAuditor : IAuditor
{
    /// <summary>The test framework and how to enumerate its test universe.</summary>
    TestSuiteDescriptor TestSuite { get; }

    /// <summary>
    /// Classifier applied to a non-zero run so genuine test failures are
    /// distinguished from an unrunnable environment.
    /// </summary>
    IAuditResultClassifier ResultClassifier { get; }

    /// <summary>
    /// Live per-run options sourced from hot-reloadable configuration. The
    /// pipeline reads <see cref="TestRunOptions.IdleTimeout"/> from this to apply
    /// a test-specific idle guard; <see cref="RunAsync"/> feeds the whole value
    /// into <see cref="BuildInvocation"/>.
    /// </summary>
    TestRunOptions CurrentRunOptions { get; }

    /// <summary>
    /// Builds the full argv for a test run. Owns <c>--filter</c> injection for a
    /// narrowed <paramref name="selection"/> and <c>--blame-hang</c> args when
    /// <paramref name="options"/> requests per-test hang dumps. With
    /// <see cref="TestSelection.All"/> and <see cref="TestRunOptions.Default"/>
    /// the result equals the auditor's base command.
    /// </summary>
    IReadOnlyList<string> BuildInvocation(TestSelection selection, TestRunOptions options);
}

/// <summary>
/// Implemented by auditors that may WRAP a <see cref="ITestRunnerAuditor"/>
/// (e.g. the language-preset multi-project wrapper). Lets the pipeline reach the
/// inner test runner without the wrapper falsely claiming to be one itself.
/// </summary>
public interface ITestRunnerAuditorProvider
{
    /// <summary>The wrapped test runner, or null when this auditor wraps none.</summary>
    ITestRunnerAuditor? TestRunner { get; }
}

/// <summary>Test frameworks a <see cref="ITestRunnerAuditor"/> can drive.</summary>
public enum TestFramework
{
    DotnetTest,
}

/// <summary>
/// Describes a test suite: the framework plus the argv that enumerates the whole
/// test universe (e.g. <c>dotnet test --no-build --list-tests</c>). Consumed by
/// the test-selector seam.
/// </summary>
public sealed record TestSuiteDescriptor(
    TestFramework Framework,
    IReadOnlyList<string> EnumerationArgv);

/// <summary>
/// The subset of tests to run. <see cref="All"/> (empty filters) runs the whole
/// suite and yields the legacy byte-identical command.
/// </summary>
public sealed record TestSelection(IReadOnlyList<string> Filters)
{
    /// <summary>Run every test — no <c>--filter</c> is emitted.</summary>
    public static TestSelection All { get; } = new([]);

    /// <summary>True when no narrowing filter is applied.</summary>
    public bool IsAll => Filters.Count == 0;
}

/// <summary>
/// Options for a single test run. Absorbs the per-test hang-dump and
/// test-specific idle-timeout design so those knobs are declared members of the
/// test-runner type rather than special-cased on a generic shell path.
/// </summary>
public sealed record TestRunOptions
{
    /// <summary>
    /// Per-test hang-dump timeout. When set and positive,
    /// <c>--blame-hang --blame-hang-timeout &lt;value&gt;</c> is appended so a
    /// single wedged test produces a dump instead of stalling the whole run.
    /// Null (the default) omits blame-hang entirely, keeping the emitted command
    /// byte-identical to the legacy path.
    /// </summary>
    public TimeSpan? BlameHangTimeout { get; init; }

    /// <summary>
    /// Auditor-level idle guard override the pipeline applies for this run. Does
    /// NOT affect the emitted command. Null falls back to the global auditor idle
    /// timeout.
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>Default options: no blame-hang, no idle-timeout override.</summary>
    public static TestRunOptions Default { get; } = new();
}
