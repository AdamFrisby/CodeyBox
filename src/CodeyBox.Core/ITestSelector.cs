namespace CodeyBox.Core;

/// <summary>
/// Regression-test-selection seam. Given the change under review and a
/// <see cref="ITestRunnerAuditor"/> capability (from the DotnetTestAuditor
/// foundation), a selector decides which subset of the suite the
/// <c>csharp:test-pass</c> audit runs — enabling a SOUND narrowing for the audit
/// loop while the merge/release verification path always runs everything.
///
/// <para>The selector consumes the <b>capability</b>, never a raw argv: it reads
/// <see cref="ITestRunnerAuditor.TestSuite"/> to reason about the suite and
/// returns a <see cref="TestSelection"/> that the auditor applies through
/// <see cref="ITestRunnerAuditor.BuildInvocation"/>. It must never string-edit a
/// command line.</para>
///
/// <para>The default <see cref="RunAllTestSelector"/> returns
/// <see cref="TestSelection.All"/>, so with the default configuration the emitted
/// <c>dotnet test</c> invocation is byte-identical to today.</para>
/// </summary>
public interface ITestSelector
{
    /// <summary>
    /// Chooses the tests to run for <paramref name="request"/>. Pure: the result
    /// is a function of the request; a selector must not mutate the request or
    /// ambient state. Implementations MUST fall back to <see cref="TestSelection.All"/>
    /// whenever they cannot soundly narrow (see
    /// <see cref="TestSelectionRequest.ChangedFiles"/>) — running MORE tests is
    /// always safe; running fewer than the change requires is not.
    /// </summary>
    TestSelectionDecision Select(TestSelectionRequest request);
}

/// <summary>
/// Inputs to <see cref="ITestSelector.Select"/>. Carries the
/// <see cref="ITestRunnerAuditor"/> capability (NOT a raw argv), the base ref the
/// change is measured against, and the changed files/lines sourced from
/// <c>GET /workitems/{id}/diff</c>.
/// </summary>
public sealed record TestSelectionRequest
{
    public TestSelectionRequest(
        ITestRunnerAuditor testRunner,
        string baseRef,
        IReadOnlyList<TestSelectionChangedFile> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(testRunner);
        ArgumentNullException.ThrowIfNull(changedFiles);
        if (string.IsNullOrWhiteSpace(baseRef))
            throw new ArgumentException("baseRef must be non-empty", nameof(baseRef));

        TestRunner = testRunner;
        BaseRef = baseRef;
        ChangedFiles = changedFiles;
    }

    /// <summary>The test-runner capability whose suite is being narrowed.</summary>
    public ITestRunnerAuditor TestRunner { get; }

    /// <summary>Git ref the change is diffed against (e.g. the base branch tip).</summary>
    public string BaseRef { get; }

    /// <summary>
    /// The changed files (and touched line ranges) that motivate the selection.
    /// An EMPTY list means "the change set could not be determined" — a sound
    /// selector must then fall back to <see cref="TestSelection.All"/> rather than
    /// selecting nothing.
    /// </summary>
    public IReadOnlyList<TestSelectionChangedFile> ChangedFiles { get; }
}

/// <summary>
/// A single changed file with the line ranges the diff touched. Line ranges let a
/// future selector map edits to impacted tests; an empty range list means "whole
/// file changed" (line granularity was unavailable).
/// </summary>
public sealed record TestSelectionChangedFile
{
    public TestSelectionChangedFile(string path, IReadOnlyList<ChangedLineRange> changedRanges)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path must be non-empty", nameof(path));
        ArgumentNullException.ThrowIfNull(changedRanges);

        Path = path;
        ChangedRanges = changedRanges;
    }

    /// <summary>Repository-relative path of the changed file.</summary>
    public string Path { get; }

    /// <summary>
    /// Touched line ranges on the post-change side of the diff. Empty when line
    /// granularity is unavailable (treat as "whole file changed").
    /// </summary>
    public IReadOnlyList<ChangedLineRange> ChangedRanges { get; }
}

/// <summary>A half-open run of changed lines: <see cref="StartLine"/> for <see cref="LineCount"/> lines.</summary>
public sealed record ChangedLineRange
{
    public ChangedLineRange(int startLine, int lineCount)
    {
        if (startLine < 1)
            throw new ArgumentOutOfRangeException(nameof(startLine), startLine, "line numbers are 1-based");
        if (lineCount < 0)
            throw new ArgumentOutOfRangeException(nameof(lineCount), lineCount, "must be non-negative");

        StartLine = startLine;
        LineCount = lineCount;
    }

    /// <summary>First changed line, 1-based.</summary>
    public int StartLine { get; }

    /// <summary>Number of changed lines starting at <see cref="StartLine"/>; may be 0 (pure deletion point).</summary>
    public int LineCount { get; }

    /// <summary>One past the last changed line (exclusive).</summary>
    public int EndLineExclusive => StartLine + LineCount;
}

/// <summary>
/// The outcome of a selection: the tests to run plus a human-readable
/// justification surfaced to operators (why this subset, or why the whole suite).
/// </summary>
public sealed record TestSelectionDecision
{
    public TestSelectionDecision(TestSelection selection, string justification)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException("justification must be non-empty", nameof(justification));

        Selection = selection;
        Justification = justification;
    }

    /// <summary>The selected tests. <see cref="TestSelection.IsAll"/> means the whole suite.</summary>
    public TestSelection Selection { get; }

    /// <summary>Operator-facing explanation of the selection.</summary>
    public string Justification { get; }
}

/// <summary>
/// Default selector: always runs the entire suite. Yields
/// <see cref="TestSelection.All"/>, so the auditor's
/// <see cref="ITestRunnerAuditor.BuildInvocation"/> emits the byte-identical
/// legacy command. This is the configured behaviour when
/// <c>Audit:TestSelection:Mode</c> is <c>all</c> (the default).
/// </summary>
public sealed class RunAllTestSelector : ITestSelector
{
    /// <summary>Fixed justification recorded for every full-suite decision.</summary>
    public const string FullSuiteJustification =
        "test-selection mode 'all': running the entire suite";

    public TestSelectionDecision Select(TestSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TestSelectionDecision(TestSelection.All, FullSuiteJustification);
    }
}
