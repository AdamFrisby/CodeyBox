using System.Globalization;
using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// How the coverage gate reacts to uncovered changed lines.
/// </summary>
public enum CoverageMode
{
    /// <summary>
    /// Surface uncovered changed lines as non-blocking Info findings and always
    /// pass. Used to measure real uncovered-line volume before the gate blocks
    /// merges. This is the default so the rollout is observation-first.
    /// </summary>
    ReportOnly,

    /// <summary>
    /// Fail the audit (one Error per uncovered changed line) so the rework loop
    /// must add tests or a justified exclusion.
    /// </summary>
    Blocking,
}

/// <summary>Parses the <c>Audit:Coverage:Mode</c> config string into a mode.</summary>
public static class CoverageModeParser
{
    /// <summary>
    /// <c>blocking</c> → <see cref="CoverageMode.Blocking"/>; anything else
    /// (including null/empty and unrecognised values) → <see cref="CoverageMode.ReportOnly"/>.
    /// Unknown values fail OPEN to report-only so a config typo can never
    /// silently start blocking merges.
    /// </summary>
    public static CoverageMode Parse(string? mode)
    {
        var trimmed = mode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return CoverageMode.ReportOnly;

        return trimmed.Replace("-", "", StringComparison.Ordinal)
                      .Replace("_", "", StringComparison.Ordinal)
                      .ToLowerInvariant() switch
        {
            "blocking" or "block" => CoverageMode.Blocking,
            _ => CoverageMode.ReportOnly,
        };
    }
}

/// <summary>
/// Configuration for <see cref="CoverageAuditor"/>, bound from
/// <c>CodeyBox:Audit:Coverage</c>. Every operational value (mode, test command,
/// collector name, size caps) is a hot-reloadable option rather than a source
/// literal.
/// </summary>
public sealed record CoverageAuditorOptions
{
    /// <summary>Display name; surfaces in findings and the rework prompt.</summary>
    public string Name { get; init; } = CoverageAuditor.AuditorName;

    /// <summary>
    /// Rollout mode string: <c>report-only</c> (default) or <c>blocking</c>.
    /// Raw string so config can use the hyphenated form; parsed via
    /// <see cref="CoverageModeParser"/>.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>Justified exclusions for genuinely untestable changed lines.</summary>
    public IReadOnlyList<CoverageExclusion> Exclusions { get; init; } = [];

    /// <summary>
    /// Base test command. The collector and results-directory flags are appended
    /// by the auditor. Defaults to the same <c>--no-build</c> invocation the
    /// deterministic test gate uses, so it reuses the build the compile gate
    /// already produced in the shared tool sandbox.
    /// </summary>
    public IReadOnlyList<string> TestCommand { get; init; } = ["dotnet", "test", "--no-build"];

    /// <summary>Coverlet data-collector name passed to <c>--collect</c>.</summary>
    public string Collector { get; init; } = "XPlat Code Coverage";

    /// <summary>
    /// Max bytes read from a single Cobertura report. Bounds memory against a
    /// pathologically large coverage file. Default 64 MiB.
    /// </summary>
    public int MaxCoverageReportBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// Max number of Cobertura report files read (one per test assembly).
    /// Bounds work against a repository that emits an unbounded number of
    /// results directories. Default 256.
    /// </summary>
    public int MaxCoverageReportFiles { get; init; } = 256;

    public CoverageMode ResolveMode() => CoverageModeParser.Parse(Mode);
}

/// <summary>
/// Config shape for the <c>CodeyBox:Audit</c> section. Carries the coverage
/// gate and the admission flake gate; exists so the section binds to a typed
/// root (and the unbound-config-key validator can walk it) rather than being
/// an untyped blob.
/// </summary>
public sealed class AuditSectionOptions
{
    public CoverageAuditorOptions Coverage { get; set; } = new();

    /// <summary>
    /// Merge-to-main admission flake gate (<c>CodeyBox:Audit:Flake</c>).
    /// Controls how many times the pre-merge gate runs the affected tests.
    /// </summary>
    public FlakeAdmissionOptions Flake { get; set; } = new();
}

/// <summary>
/// Deterministic, per-item, diff-scoped coverage gate (<c>tests:coverage</c>).
/// It runs the test suite with coverage collection, then blocks on any
/// executable line CHANGED in the work item (vs the merge-base) that no test
/// exercised.
///
/// <para>Pure function of (diff, tests): it carries no state across audit
/// iterations, so the finding list is fully determined by the current diff and
/// the current test run — the list shrinks monotonically as rework adds tests.
/// Only diff lines gate; unchanged uncovered code is ignored. Genuinely
/// untestable changed lines require an explicit justified exclusion — never a
/// silent skip; every exclusion is logged.</para>
///
/// <para>Rollout: the <c>CodeyBox:Audit:Coverage:Mode</c> knob defaults to
/// <c>report-only</c> (non-blocking Info findings) so operators can measure
/// real uncovered-line volume before flipping to <c>blocking</c>.</para>
///
/// Tool-only auditor: no agent credentials, no network. Runs in the shared
/// credential-free tool sandbox after the build/test gates, so
/// <c>dotnet test --no-build</c> reuses the compile gate's build. Skips (passes)
/// when the tree has no .NET project markers or the diff changed no lines.
/// </summary>
public sealed class CoverageAuditor : IAuditor
{
    public const string AuditorName = "tests:coverage";

    private readonly Func<CoverageAuditorOptions> _optsProvider;

    /// <summary>
    /// Production constructor. <paramref name="optsProvider"/> is invoked once
    /// per audit so hot-reloads of <c>CodeyBox:Audit:Coverage</c> take effect on
    /// the next audit without a restart (typically wired to
    /// <c>IOptionsMonitor&lt;…&gt;.CurrentValue</c>).
    /// </summary>
    public CoverageAuditor(Func<CoverageAuditorOptions> optsProvider)
    {
        ArgumentNullException.ThrowIfNull(optsProvider);
        _optsProvider = optsProvider;
    }

    /// <summary>Test constructor — accepts a fixed options snapshot.</summary>
    public CoverageAuditor(CoverageAuditorOptions opts)
        : this(() => opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
    }

    public string Name => _optsProvider().Name;
    public string Kind => "tool";
    public AuditCapabilities Required => AuditCapabilities.None;

    public async Task<AuditResult> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(context);

        // Snapshot once so the audit is internally consistent even if config
        // reloads mid-run.
        var opts = _optsProvider();
        var mode = opts.ResolveMode();

        var changed = await GetChangedLinesAsync(sandbox, workingDirectory, context, ct).ConfigureAwait(false);
        if (changed.Error is { } diffError)
        {
            // Fail-closed on git-diff failure: "cannot determine the diff" is not
            // "nothing changed". In blocking mode this blocks; in report-only it
            // is surfaced but does not block (rollout must never block).
            return Unverifiable(opts, mode,
                "could not enumerate changed lines",
                $"git diff failed (with and without the 'origin/' prefix) so the coverage gate cannot " +
                $"determine which lines are in scope. Stderr: {diffError}");
        }

        if (changed.LinesByFile.Count == 0)
            return Pass("no changed lines in scope");

        if (!await HasDotnetProjectAsync(sandbox, workingDirectory, ct).ConfigureAwait(false))
            return Pass("no .NET project markers found; coverage gate not applicable");

        var resultsDirectory = BuildResultsDirectory(context);
        var run = await RunCoverageAsync(sandbox, workingDirectory, opts, resultsDirectory, ct).ConfigureAwait(false);
        if (!run.Success)
        {
            return Unverifiable(opts, mode,
                "coverage test run failed",
                $"'{string.Join(' ', run.Argv)}' exited {run.ExitCode}. Coverage could not be measured. " +
                $"Output:\n{Truncate(run.Output)}");
        }

        var reportFiles = await FindReportFilesAsync(sandbox, workingDirectory, resultsDirectory, opts, ct)
            .ConfigureAwait(false);
        if (reportFiles.Count == 0)
        {
            return Unverifiable(opts, mode,
                "no coverage report produced",
                "The test run completed but produced no Cobertura report. Ensure the test project references " +
                "'coverlet.collector' so '--collect \"" + opts.Collector + "\"' emits coverage.");
        }

        var reports = new List<CoberturaReport>(reportFiles.Count);
        foreach (var file in reportFiles)
        {
            var read = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", "--", file],
                WorkingDirectory = workingDirectory,
                MaxStdoutBytes = opts.MaxCoverageReportBytes,
            }, ct).ConfigureAwait(false);

            if (!read.Success || read.StdoutLimitExceeded)
            {
                return Unverifiable(opts, mode,
                    "could not read coverage report",
                    $"Reading coverage report '{file}' failed (exit {read.ExitCode}" +
                    (read.StdoutLimitExceeded ? ", output exceeded the size cap" : "") + ").");
            }

            try
            {
                reports.Add(CoberturaParser.Parse(read.Stdout));
            }
            catch (System.Xml.XmlException ex)
            {
                return Unverifiable(opts, mode,
                    "could not parse coverage report",
                    $"Coverage report '{file}' was not well-formed Cobertura XML: {ex.Message}");
            }
        }

        var coverageMap = CoberturaParser.BuildLineMap(reports, workingDirectory);
        var result = CoverageGateEvaluator.Evaluate(changed.LinesByFile, coverageMap, opts.Exclusions);

        return BuildResult(opts, mode, result, run.Output);
    }

    private static AuditResult BuildResult(
        CoverageAuditorOptions opts,
        CoverageMode mode,
        CoverageGateResult result,
        string rawOutput)
    {
        var findings = new List<AuditFinding>();
        var uncoveredSeverity = mode == CoverageMode.Blocking ? AuditSeverity.Error : AuditSeverity.Info;

        foreach (var line in result.UncoveredLines)
        {
            findings.Add(new AuditFinding(
                AuditorName: opts.Name,
                Severity: uncoveredSeverity,
                Title: "changed line not covered by tests",
                Description:
                    $"{line.File}:{line.Line} is an executable line changed in this work item that no test " +
                    "exercised. Add or extend a test that runs this line. If it is genuinely untestable, add a " +
                    "justified entry under CodeyBox:Audit:Coverage:Exclusions.",
                Location: $"{line.File}:{line.Line}"));
        }

        // Log every applied exclusion so a justified skip is visible, never silent.
        foreach (var applied in result.AppliedExclusions)
        {
            findings.Add(new AuditFinding(
                AuditorName: opts.Name,
                Severity: AuditSeverity.Info,
                Title: "coverage exclusion applied",
                Description: $"{applied.File}:{applied.Line} is uncovered but excluded. Justification: {applied.Justification}",
                Location: $"{applied.File}:{applied.Line}"));
        }

        // An exclusion without a justification never suppresses — surface it so
        // the missing justification is fixed rather than silently trusted.
        foreach (var invalid in result.InvalidExclusions)
        {
            findings.Add(new AuditFinding(
                AuditorName: opts.Name,
                Severity: AuditSeverity.Warning,
                Title: "coverage exclusion missing justification",
                Description:
                    $"The coverage exclusion for {invalid.File}:{ExclusionRange(invalid)} has no justification and " +
                    "was IGNORED; any uncovered line it covers still gates. Every exclusion must carry a justification.",
                Location: $"{invalid.File}:{invalid.Line}"));
        }

        foreach (var unused in result.UnusedExclusions)
        {
            findings.Add(new AuditFinding(
                AuditorName: opts.Name,
                Severity: AuditSeverity.Info,
                Title: "coverage exclusion unused",
                Description:
                    $"The coverage exclusion for {unused.File}:{ExclusionRange(unused)} matched no uncovered changed " +
                    "line (stale, already covered, or the line was not changed). Consider removing it.",
                Location: $"{unused.File}:{unused.Line}"));
        }

        // Report-only never blocks; blocking fails when any changed line is uncovered.
        var passed = mode != CoverageMode.Blocking || result.UncoveredLines.Count == 0;
        return new AuditResult(passed, findings, RawOutput: rawOutput);
    }

    private static string ExclusionRange(CoverageExclusion exclusion)
        => exclusion.EffectiveLineEnd > exclusion.Line
            ? $"{exclusion.Line}-{exclusion.EffectiveLineEnd}"
            : exclusion.Line.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Coverage could not be verified. Honest, not silent: emits a finding at
    /// Error in blocking mode (blocks) or Warning in report-only (surfaced, does
    /// not block).
    /// </summary>
    private static AuditResult Unverifiable(
        CoverageAuditorOptions opts,
        CoverageMode mode,
        string title,
        string description)
    {
        var blocking = mode == CoverageMode.Blocking;
        return new AuditResult(
            Passed: !blocking,
            Findings:
            [
                new AuditFinding(
                    AuditorName: opts.Name,
                    Severity: blocking ? AuditSeverity.Error : AuditSeverity.Warning,
                    Title: title,
                    Description: description),
            ],
            RawOutput: description);
    }

    private static AuditResult Pass(string reason)
        => new(true, [], RawOutput: reason);

    private readonly record struct ChangedLines(
        IReadOnlyDictionary<string, IReadOnlySet<int>> LinesByFile,
        string? Error);

    private static async Task<ChangedLines> GetChangedLinesAsync(
        ISandbox sandbox,
        string workingDirectory,
        AuditContext context,
        CancellationToken ct)
    {
        var baseBranch = context.BaseBranch ?? string.Empty;

        // Three-dot merge-base range (same as the pipeline work diff); --unified=0
        // keeps only added/removed lines so counting matches the new file. Prefer
        // the origin ref, fall back to the local branch when origin isn't fetched.
        var diff = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "-C", workingDirectory, "diff", "--unified=0", "--no-color",
                    "--end-of-options", $"origin/{baseBranch}...HEAD"],
        }, ct).ConfigureAwait(false);

        if (!diff.Success)
        {
            diff = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "diff", "--unified=0", "--no-color",
                        "--end-of-options", $"{baseBranch}...HEAD"],
            }, ct).ConfigureAwait(false);
        }

        if (!diff.Success)
        {
            var stderr = string.IsNullOrWhiteSpace(diff.Stderr)
                ? "git diff exited non-zero with no stderr output"
                : diff.Stderr.Trim();
            return new ChangedLines(new Dictionary<string, IReadOnlySet<int>>(), stderr);
        }

        return new ChangedLines(UnifiedDiffParser.ChangedLinesByFile(diff.Stdout), Error: null);
    }

    private static async Task<bool> HasDotnetProjectAsync(
        ISandbox sandbox,
        string workingDirectory,
        CancellationToken ct)
    {
        var probe = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c",
                "find . -maxdepth 6 \\( -name '*.csproj' -o -name '*.sln' -o -name '*.slnx' \\) -print 2>/dev/null | head -n 1"],
            WorkingDirectory = workingDirectory,
        }, ct).ConfigureAwait(false);

        return probe.Success && !string.IsNullOrWhiteSpace(probe.Stdout);
    }

    private readonly record struct CoverageRun(
        bool Success, int ExitCode, string Output, IReadOnlyList<string> Argv);

    private static async Task<CoverageRun> RunCoverageAsync(
        ISandbox sandbox,
        string workingDirectory,
        CoverageAuditorOptions opts,
        string resultsDirectory,
        CancellationToken ct)
    {
        // Fresh, unique results directory per audit iteration keeps the gate
        // stateless and prevents a stale report from a previous run leaking in.
        var reset = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "rm -rf -- \"$1\" && mkdir -p -- \"$1\"", "sh", resultsDirectory],
            WorkingDirectory = workingDirectory,
        }, ct).ConfigureAwait(false);
        if (!reset.Success)
        {
            return new CoverageRun(false, reset.ExitCode,
                CombineOutput(reset), ["sh", "-c", "prepare results directory"]);
        }

        var argv = new List<string>(opts.TestCommand)
        {
            "--collect", opts.Collector,
            "--results-directory", resultsDirectory,
        };

        var run = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = argv,
            WorkingDirectory = workingDirectory,
        }, ct).ConfigureAwait(false);

        return new CoverageRun(run.Success, run.ExitCode, CombineOutput(run), argv);
    }

    private static async Task<IReadOnlyList<string>> FindReportFilesAsync(
        ISandbox sandbox,
        string workingDirectory,
        string resultsDirectory,
        CoverageAuditorOptions opts,
        CancellationToken ct)
    {
        var find = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "find \"$1\" -name coverage.cobertura.xml -print 2>/dev/null | sort", "sh", resultsDirectory],
            WorkingDirectory = workingDirectory,
        }, ct).ConfigureAwait(false);

        if (!find.Success)
            return [];

        return find.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(opts.MaxCoverageReportFiles)
            .ToList();
    }

    private static string BuildResultsDirectory(AuditContext context)
        => $"/tmp/codeybox-coverage-{Sanitize(context.WorkItemId.ToString())}-{context.Iteration}";

    private static string Sanitize(string value)
        => new(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static string CombineOutput(SandboxExecResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Stderr))
            return result.Stdout;
        if (string.IsNullOrWhiteSpace(result.Stdout))
            return result.Stderr;
        return result.Stdout + "\n" + result.Stderr;
    }

    private const int MaxOutputChars = 8000;

    private static string Truncate(string output)
        => output.Length <= MaxOutputChars ? output : output[^MaxOutputChars..];
}
