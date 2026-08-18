using System.Globalization;
using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// Deterministic per-item testing-rigor gate. For each work item it:
///
/// <list type="number">
///   <item>Computes the files CHANGED in the item via <c>git diff --name-only
///   base...HEAD</c>, filtered by configured extensions and exclude prefixes.</item>
///   <item>Hands the scoped file list to an injected <see cref="IMutationRunner"/>,
///   which mutates that code and re-runs the project's test suite per mutant
///   (the runner is expected to parallelise — see docs/quality/mutation-rigor.md
///   for the runtime budget).</item>
///   <item>Reports one Error finding per surviving mutant in changed code
///   (un-gameable conformance: a no-assert / impl-mirroring test kills no
///   mutant and so doesn't count).</item>
///   <item>Reports an Error if the changed-code mutation score is below the
///   configured per-project threshold.</item>
///   <item>Enforces a RATCHET on the overall mutation score using an injected
///   <see cref="IMutationRatchetStore"/>: a regression vs. the previously-
///   recorded baseline (minus a small tolerance) is an Error. The baseline is
///   only updated when the audit passes, so a failing run cannot silently
///   lower the bar.</item>
/// </list>
///
/// Tool-only auditor: needs neither agent credentials nor network. Disabled by
/// default — <see cref="MutationTestingAuditorOptions.Enabled"/> must be
/// flipped per project. When enabled but no real engine is wired (the runner
/// is <see cref="NullMutationRunner"/>), the auditor emits a non-blocking
/// Warning so the operator notices rather than getting a false-green.
/// </summary>
public sealed class MutationTestingAuditor : IAuditor
{
    private readonly Func<MutationTestingAuditorOptions> _optsProvider;
    private readonly IMutationRunner _runner;
    private readonly IMutationRatchetStore _ratchet;

    /// <summary>
    /// Production constructor. The <paramref name="optsProvider"/> is called
    /// once per audit, so config reloads of <c>CodeyBox:Mutation</c>
    /// (threshold, budget, tolerance, …) take effect on the next audit
    /// without a process restart — matching the rest of the host's
    /// hot-reloadable options wiring. The composition root typically wires
    /// this to <c>IOptionsMonitor&lt;…&gt;.CurrentValue</c>.
    /// </summary>
    public MutationTestingAuditor(
        Func<MutationTestingAuditorOptions> optsProvider,
        IMutationRunner runner,
        IMutationRatchetStore ratchet)
    {
        _optsProvider = optsProvider;
        _runner = runner;
        _ratchet = ratchet;
    }

    /// <summary>
    /// Test constructor — accepts a fixed snapshot so unit tests don't have
    /// to wire an IOptionsMonitor.
    /// </summary>
    public MutationTestingAuditor(
        MutationTestingAuditorOptions opts,
        IMutationRunner runner,
        IMutationRatchetStore ratchet)
        : this(() => opts, runner, ratchet)
    {
    }

    private MutationTestingAuditorOptions _opts => _optsProvider();

    public string Name => _opts.Name;
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        // Snapshot once so the audit is internally consistent even if config
        // reloads mid-run (CurrentValue would otherwise change underneath us).
        var opts = _opts;

        if (!opts.Enabled)
            return new AuditResult(true, [], RawOutput: "mutation-testing auditor disabled");

        if (_runner is NullMutationRunner)
        {
            return new AuditResult(true,
            [
                new AuditFinding(
                    Name, AuditSeverity.Warning,
                    "mutation-testing engine not wired",
                    "The mutation-testing auditor is enabled but no IMutationRunner implementation has been registered. " +
                    "Register a Stryker- / Mutmut- / mull-backed IMutationRunner in DI to actually exercise the gate."),
            ],
            RawOutput: "no runner wired");
        }

        var listing = await ListChangedFilesAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
        if (listing.Error is { } enumerationError)
        {
            // Fail-closed on git-diff failure. Treating "cannot enumerate changed
            // files" as "no changed files" would let a misconfigured base ref,
            // missing remote, or shallow clone silently green-light the gate
            // this whole feature is built to make un-gameable.
            return new AuditResult(false,
            [
                new AuditFinding(
                    Name, AuditSeverity.Error,
                    "could not enumerate changed files",
                    "git diff failed twice (with and without the 'origin/' prefix) so the mutation-testing " +
                    $"auditor cannot determine which files are in scope. Stderr: {enumerationError}"),
            ],
            RawOutput: enumerationError);
        }
        var scoped = FilterToInScopeFiles(opts, listing.Files);
        if (scoped.Count == 0)
            return new AuditResult(true, [], RawOutput: "no changed files in scope");

        MutationRunReport report;
        try
        {
            report = await _runner.RunAsync(sandbox, workingDirectory, scoped, opts.Budget, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AuditResult(false,
            [
                new AuditFinding(
                    Name, AuditSeverity.Error,
                    "mutation-testing run failed",
                    $"The mutation-testing engine threw: {ex.GetType().Name}: {ex.Message}"),
            ]);
        }

        var findings = new List<AuditFinding>();

        // Un-gameable conformance: every surviving mutant in changed code is a
        // missing test branch. One Error per mutant so the rework prompt cites
        // each with file:line and mutator kind. Defence-in-depth against a
        // misbehaving runner: drop survivors whose file is not in the scoped
        // set so out-of-scope reports cannot flood the rework loop.
        var scopedSet = new HashSet<string>(scoped, StringComparer.OrdinalIgnoreCase);
        foreach (var mutant in report.SurvivingMutantsInChangedCode)
        {
            if (!scopedSet.Contains(mutant.FilePath)) continue;
            findings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: $"surviving mutant: {mutant.Mutator}",
                Description:
                    $"A '{mutant.Mutator}' mutation at {mutant.FilePath}:{mutant.Line} survived the test suite — " +
                    "no test failed when the mutation was applied, so the code path is effectively unverified. " +
                    "Tighten an existing assertion or add a test that would fail under this mutation. " +
                    $"Mutator detail: {mutant.Description}",
                Location: $"{mutant.FilePath}:{mutant.Line}"));
        }

        if (report.ChangedCodeMutationScorePercent < opts.ChangedCodeThresholdPercent - Epsilon)
        {
            findings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "changed-code mutation score below threshold",
                Description:
                    $"Mutation score on the changed code is " +
                    $"{Fmt(report.ChangedCodeMutationScorePercent)}%, below the configured threshold of " +
                    $"{Fmt(opts.ChangedCodeThresholdPercent)}%. Add or strengthen tests on the affected " +
                    "functions so plausible bugs would fail at least one test.",
                Location: null));
        }

        var ratchetKey = ResolveRatchetKey(opts, context);
        var previous = await _ratchet.TryGetAsync(ratchetKey, ct).ConfigureAwait(false);
        if (previous is double baseline
            && report.OverallMutationScorePercent < baseline - opts.RatchetTolerancePercent - Epsilon)
        {
            findings.Add(new AuditFinding(
                AuditorName: Name,
                Severity: AuditSeverity.Error,
                Title: "overall mutation score regressed",
                Description:
                    $"Overall mutation score dropped from {Fmt(baseline)}% to " +
                    $"{Fmt(report.OverallMutationScorePercent)}% (tolerance " +
                    $"{Fmt(opts.RatchetTolerancePercent)}%). The ratchet does not permit regressions — " +
                    "either restore the lost coverage or escalate to the operator to reset the baseline.",
                Location: null));
        }

        var passed = !findings.Any(f => f.Severity >= AuditSeverity.Error);
        if (passed)
        {
            // Only ratchet up on green. A failing run that nonetheless raised
            // the overall score must NOT lower the bar via a partial save.
            await _ratchet.SaveAsync(ratchetKey, report.OverallMutationScorePercent, ct).ConfigureAwait(false);
        }

        return new AuditResult(passed, findings, RawOutput: report.RawOutput);
    }

    private static IReadOnlyList<string> FilterToInScopeFiles(
        MutationTestingAuditorOptions opts, IReadOnlyList<string> files)
    {
        var exts = opts.FileExtensions;
        var excludes = opts.ExcludePathPrefixes;
        var output = new List<string>(files.Count);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            if (exts.Count > 0
                && !exts.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (excludes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            output.Add(file);
        }
        return output;
    }

    /// <summary>
    /// Result of trying to enumerate changed files via git. <see cref="Error"/>
    /// non-null means BOTH diff invocations failed — the caller MUST fail
    /// closed, since "cannot determine changes" is not the same as "no
    /// changes" and silently treating it as the latter green-lights the gate.
    /// <c>-z</c> is used so paths containing newlines, quotes, or non-ASCII
    /// bytes do not get quoted or split incorrectly.
    /// </summary>
    private readonly record struct ChangedFileListing(IReadOnlyList<string> Files, string? Error);

    private static async Task<ChangedFileListing> ListChangedFilesAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct)
    {
        var baseBranch = context.BaseBranch ?? string.Empty;
        var diff = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "diff", "--name-only", "-z",
                    "--end-of-options", $"origin/{baseBranch}...HEAD"],
        }, ct).ConfigureAwait(false);
        if (!diff.Success)
        {
            diff = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "diff", "--name-only", "-z",
                        "--end-of-options", $"{baseBranch}...HEAD"],
            }, ct).ConfigureAwait(false);
        }
        if (!diff.Success)
        {
            var stderr = string.IsNullOrWhiteSpace(diff.Stderr)
                ? "git diff exited non-zero with no stderr output"
                : diff.Stderr.Trim();
            return new ChangedFileListing([], stderr);
        }

        var files = diff.Stdout
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        return new ChangedFileListing(files, Error: null);
    }

    private static string ResolveRatchetKey(MutationTestingAuditorOptions opts, AuditContext context)
    {
        if (!string.IsNullOrWhiteSpace(opts.RatchetKey))
            return opts.RatchetKey!;
        // Default key prefixes the base branch with the work item's project id
        // when the orchestrator has plumbed one through. Multi-project hosts
        // commonly share a singleton auditor + ratchet store; without this
        // prefix, every project targeting 'main' would read and write the
        // same baseline and one project's pass would become another's
        // regression floor.
        var projectPrefix = string.IsNullOrWhiteSpace(context.ProjectId)
            ? ""
            : $"{context.ProjectId}:";
        return $"{projectPrefix}{context.BaseBranch}";
    }

    private static string Fmt(double percent)
        => percent.ToString("F1", CultureInfo.InvariantCulture);

    private const double Epsilon = 1e-9;
}

/// <summary>
/// Configuration for <see cref="MutationTestingAuditor"/>. All values are
/// per-project — the API host materialises one auditor instance per project
/// from its <c>CodeyBox:Mutation</c> section.
/// </summary>
public sealed record MutationTestingAuditorOptions
{
    /// <summary>Display name; surfaces in findings and the rework prompt.</summary>
    public string Name { get; init; } = "tests:mutation-rigor";

    /// <summary>
    /// Master gate. Off by default — mutation testing is expensive and
    /// operator-opt-in per project (or per audit profile).
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Minimum mutation-kill score (percent) for the CHANGED code in this work
    /// item. Aim high: the changed slice is small and tests for it should be
    /// fresh, so 80-90% is a reasonable starting threshold.
    /// </summary>
    public double ChangedCodeThresholdPercent { get; init; } = 80.0;

    /// <summary>
    /// Wall-clock budget (whole minutes) handed to the runner. Mutation
    /// testing is expensive, so the runner is expected to parallelise
    /// per-mutant across cores and abort straggling mutants once this budget
    /// is exhausted. The auditor does not itself enforce a hard kill — the
    /// runner does. Int rather than TimeSpan so config can use a plain
    /// integer ("BudgetMinutes": 15) consistent with the rest of the host
    /// (e.g. PerIterationTimeoutMinutes).
    /// </summary>
    public int BudgetMinutes { get; init; } = 15;

    /// <summary>
    /// Convenience accessor: <see cref="BudgetMinutes"/> as a <see cref="TimeSpan"/>.
    /// Clamped to a minimum of 1 minute so a misconfigured 0 or negative
    /// value cannot cause the runner to be handed a zero/negative budget.
    /// </summary>
    public TimeSpan Budget => TimeSpan.FromMinutes(Math.Max(1, BudgetMinutes));

    /// <summary>
    /// Tolerance applied when comparing the new overall score against the
    /// stored baseline, in absolute percentage points. A small (~0.5%)
    /// tolerance avoids flapping on noise from mutation-test nondeterminism
    /// while still catching genuine regressions.
    /// </summary>
    public double RatchetTolerancePercent { get; init; } = 0.5;

    /// <summary>
    /// File extensions kept in the changed-file list. Default targets C#;
    /// projects in other stacks override (e.g. <c>[".py"]</c>,
    /// <c>[".ts", ".tsx"]</c>).
    /// </summary>
    public IReadOnlyList<string> FileExtensions { get; init; } = [".cs"];

    /// <summary>
    /// Path prefixes (case-insensitive) excluded from the changed-file list.
    /// Test code itself is excluded by default so changes to test files don't
    /// generate mutants that are tautologically killed.
    /// </summary>
    public IReadOnlyList<string> ExcludePathPrefixes { get; init; } =
        ["tests/", "test/", ".codeybox/"];

    /// <summary>
    /// Optional override for the ratchet-store lookup key. Null means the
    /// auditor derives the key from <see cref="AuditContext.BaseBranch"/> so
    /// trunk-vs-release-branch baselines can diverge cleanly.
    /// </summary>
    public string? RatchetKey { get; init; }
}
