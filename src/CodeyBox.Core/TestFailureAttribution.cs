namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable switch for base-branch test-failure attribution. Disabled
/// keeps the historical fail-closed behavior: every observed test failure is
/// treated as caused by the work item diff.
/// </summary>
public sealed record TestFailureAttributionOptions
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Shared, swappable holder for the current <see cref="TestFailureAttributionOptions"/>.
/// </summary>
public sealed class TestFailureAttributionOptionsSnapshot
{
    private TestFailureAttributionOptions _current;

    public TestFailureAttributionOptionsSnapshot(TestFailureAttributionOptions initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public TestFailureAttributionOptions Current => Volatile.Read(ref _current);

    public bool Enabled => Current.Enabled;

    public void Replace(TestFailureAttributionOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref _current, next);
    }
}

public enum TestFailureRunOutcome
{
    Passed = 0,
    Failed = 1,
    Unavailable = 2,
    NotRun = 3,
}

public enum TestFailureAttribution
{
    DiffAttributable = 0,
    NotDiffAttributable = 1,
}

public enum TestFailureAttributionSkipReason
{
    None = 0,
    Disabled = 1,
    BaseRerunUnavailable = 2,
    UnsupportedCommand = 3,
}

public sealed record TestFailureRunPair(
    string TestName,
    TestFailureRunOutcome BaseRun,
    TestFailureRunOutcome DiffRun,
    TestFailureAttributionSkipReason SkipReason = TestFailureAttributionSkipReason.None);

public sealed record TestFailureAttributionResult(
    string TestName,
    TestFailureRunOutcome BaseRun,
    TestFailureRunOutcome DiffRun,
    TestFailureAttribution Attribution,
    TestFailureAttributionSkipReason SkipReason = TestFailureAttributionSkipReason.None);

/// <summary>
/// Pure decision helper for runtime flake escalation. It intentionally knows
/// nothing about sandboxes, git, or dotnet; callers supply the observed
/// outcomes for the base and diff runs.
/// </summary>
public static class TestFailureAttributionClassifier
{
    public static TestFailureAttributionResult Classify(TestFailureRunPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var attribution = pair.SkipReason is TestFailureAttributionSkipReason.Disabled
                or TestFailureAttributionSkipReason.BaseRerunUnavailable
                or TestFailureAttributionSkipReason.UnsupportedCommand
            ? TestFailureAttribution.DiffAttributable
            : pair.BaseRun == TestFailureRunOutcome.Passed && pair.DiffRun == TestFailureRunOutcome.Failed
                ? TestFailureAttribution.DiffAttributable
                : TestFailureAttribution.NotDiffAttributable;

        return new TestFailureAttributionResult(
            pair.TestName,
            pair.BaseRun,
            pair.DiffRun,
            attribution,
            pair.SkipReason);
    }

    public static IReadOnlyList<TestFailureAttributionResult> Classify(
        IEnumerable<TestFailureRunPair> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return pairs.Select(Classify).ToArray();
    }

    public static IReadOnlyList<TestFailureAttributionResult> FailClosed(
        IEnumerable<string> testNames,
        TestFailureAttributionSkipReason reason)
    {
        ArgumentNullException.ThrowIfNull(testNames);
        return testNames.Select(name => Classify(new TestFailureRunPair(
            name,
            BaseRun: reason == TestFailureAttributionSkipReason.Disabled
                ? TestFailureRunOutcome.NotRun
                : TestFailureRunOutcome.Unavailable,
            DiffRun: TestFailureRunOutcome.Failed,
            SkipReason: reason))).ToArray();
    }
}
