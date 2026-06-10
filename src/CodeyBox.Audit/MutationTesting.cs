using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Abstraction over a mutation-testing engine (Stryker, Mutmut, mull, …).
/// The <see cref="MutationTestingAuditor"/> delegates the actual mutate-and-
/// re-run-tests work to this interface so the auditor itself stays language-
/// agnostic and unit-testable. Implementations are operator-supplied and
/// registered in DI alongside the auditor.
/// </summary>
public interface IMutationRunner
{
    /// <summary>
    /// Mutates the code under <paramref name="changedFiles"/> (scoped to keep
    /// the run fast), re-executes the project's test suite per mutant in
    /// parallel under the wall-clock <paramref name="budget"/>, and returns a
    /// summary report. Runners SHOULD honour the budget by cancelling slow
    /// mutants rather than blowing the audit-iteration timeout.
    /// </summary>
    Task<MutationRunReport> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> changedFiles,
        TimeSpan budget,
        CancellationToken ct = default);
}

/// <summary>
/// Structured result of one mutation-testing run. Percent values are 0-100.
/// </summary>
public sealed record MutationRunReport(
    double ChangedCodeMutationScorePercent,
    double OverallMutationScorePercent,
    IReadOnlyList<SurvivingMutant> SurvivingMutantsInChangedCode,
    TimeSpan Duration,
    string? RawOutput = null);

/// <summary>One mutant the test suite did NOT kill.</summary>
public sealed record SurvivingMutant(
    string FilePath,
    int Line,
    string Mutator,
    string Description);

/// <summary>
/// Persisted "best score so far" baseline so the auditor can enforce
/// no-regression on the overall mutation score across work items. Per-project
/// state is keyed; implementations may back the store with a file, SQLite,
/// memory, etc.
/// </summary>
public interface IMutationRatchetStore
{
    /// <summary>
    /// Returns the previously-recorded overall mutation score for
    /// <paramref name="key"/>, or null when no baseline exists yet (first run).
    /// </summary>
    Task<double?> TryGetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Records a new baseline. Callers MUST only invoke this on a passing
    /// audit so a failing run does not silently lower the bar.
    /// </summary>
    Task SaveAsync(string key, double percent, CancellationToken ct = default);
}

/// <summary>
/// Process-local ratchet store. Useful for tests and single-process
/// deployments. Production hosts that span multiple processes should swap in
/// a file- or SQLite-backed implementation.
/// </summary>
public sealed class InMemoryMutationRatchetStore : IMutationRatchetStore
{
    private readonly Dictionary<string, double> _state = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<double?> TryGetAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _state.TryGetValue(key, out var v) ? v : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(string key, double percent, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _state[key] = percent;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Inert default runner: reports 100% / no survivors. Wired by default so the
/// auditor can be registered in DI without an operator-supplied engine; the
/// auditor itself short-circuits to pass when <see
/// cref="MutationTestingAuditorOptions.Enabled"/> is false, so this null
/// runner only fires when an operator opted in but has not yet wired a real
/// engine — in that case the auditor emits a non-blocking Warning instead
/// of silently green-lighting unknown coverage.
/// </summary>
public sealed class NullMutationRunner : IMutationRunner
{
    public Task<MutationRunReport> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> changedFiles,
        TimeSpan budget,
        CancellationToken ct = default)
        => Task.FromResult(new MutationRunReport(
            ChangedCodeMutationScorePercent: 100.0,
            OverallMutationScorePercent: 100.0,
            SurvivingMutantsInChangedCode: [],
            Duration: TimeSpan.Zero,
            RawOutput: "NullMutationRunner: no engine wired"));
}
