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
/// A null score means the run produced no evidence for that scope — never a
/// fabricated value. In particular a run scoped to changed files (via the
/// engine's file filter) does NOT establish an overall-project score, so
/// <see cref="OverallMutationScorePercent"/> is null and <see cref="Scope"/>
/// is <see cref="MutationRunScope.ChangedFilesOnly"/> for such runs; the
/// auditor must not compare or ratchet a null overall score.
/// </summary>
public sealed record MutationRunReport(
    double? ChangedCodeMutationScorePercent,
    double? OverallMutationScorePercent,
    IReadOnlyList<SurvivingMutant> SurvivingMutantsInChangedCode,
    TimeSpan Duration,
    string? RawOutput = null,
    MutationRunStatus Status = MutationRunStatus.Completed,
    string? StatusDetail = null,
    string? ToolVersion = null,
    string? SourceCommitSha = null,
    string? ProjectSelection = null,
    string? ConfigDigest = null,
    MutationRunScope Scope = MutationRunScope.FullProject);

/// <summary>
/// Outcome of a mutation-runner invocation. Only <see cref="Completed"/>
/// carries scores. <see cref="NoApplicableCode"/> and
/// <see cref="UnsupportedProject"/> are explicit "no evidence" outcomes the
/// auditor surfaces without blocking the merge; <see cref="NoCoveringTests"/>
/// blocks it (untested production code is a rigor failure, not missing
/// evidence).
/// </summary>
public enum MutationRunStatus
{
    /// <summary>The engine ran and produced scores.</summary>
    Completed,

    /// <summary>
    /// The tree has no production code to mutate for the changed files
    /// (non-.NET tree, or changes confined to test/docs/unmapped files).
    /// </summary>
    NoApplicableCode,

    /// <summary>
    /// The changed code is in a language/project the runner does not support.
    /// </summary>
    UnsupportedProject,

    /// <summary>
    /// Changed production code has no covering test project, so mutation
    /// cannot run. Unlike <see cref="NoApplicableCode"/> this blocks the
    /// gate: untested production code is exactly what the rigor gate exists
    /// to catch.
    /// </summary>
    NoCoveringTests,
}

/// <summary>
/// Scope the engine actually mutated. Only <see cref="FullProject"/> runs
/// establish an overall-project score the ratchet may compare and advance;
/// <see cref="ChangedFilesOnly"/> runs leave the overall baseline untouched.
/// Reports constructed without scope information (legacy runners) default to
/// <see cref="FullProject"/>, preserving the previous contract.
/// </summary>
public enum MutationRunScope
{
    /// <summary>Only the changed files were mutated; no overall score exists.</summary>
    ChangedFilesOnly,

    /// <summary>The whole project was mutated; the overall score is complete.</summary>
    FullProject,
}

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
