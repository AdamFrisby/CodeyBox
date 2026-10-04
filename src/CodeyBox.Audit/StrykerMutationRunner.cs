using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Audit;

/// <summary>
/// Why a Stryker run produced no scores. Surfaced in the auditor finding so
/// threshold failure (real scores below the bar) is never confused with
/// tool/build/test/report/timeout failure (no trustworthy evidence).
/// </summary>
public enum StrykerFailureKind
{
    /// <summary>Stryker itself crashed, was mis-invoked, or vanished.</summary>
    Tool,

    /// <summary>The initial build Stryker performs failed.</summary>
    Build,

    /// <summary>The initial test run (or test discovery) failed.</summary>
    Test,

    /// <summary>
    /// Stryker exited but left no usable report (missing, oversized,
    /// truncated, stale, or unscorable).
    /// </summary>
    Report,

    /// <summary>The wall-clock budget expired before scores were produced.</summary>
    Timeout,
}

/// <summary>
/// Stryker is not provisioned in the audit sandbox. Carries the provisioning
/// diagnostic (binary, expected version, where it must be installed) so the
/// operator can fix the baseline image. Never a pass, never a source finding.
/// </summary>
public sealed class StrykerToolMissingException : AuditUnavailableException
{
    public StrykerToolMissingException(string message)
        : base(message)
    {
    }

    public StrykerToolMissingException(string message, int exitCode, string output)
        : base(message, exitCode, output)
    {
    }
}

/// <summary>
/// Stryker ran (or was attempted) but produced no trustworthy scores.
/// <see cref="Kind"/> tells the auditor which failure channel to report;
/// threshold failure is NOT represented here — below-threshold scores arrive
/// as a normal <see cref="MutationRunReport"/> the gate evaluates.
/// </summary>
public sealed class StrykerRunFailedException : AuditUnavailableException
{
    /// <summary>Which failure channel produced no evidence.</summary>
    public required StrykerFailureKind Kind { get; init; }

    public StrykerRunFailedException(string message)
        : base(message)
    {
    }

    public StrykerRunFailedException(string message, int exitCode, string output)
        : base(message, exitCode, output)
    {
    }
}

/// <summary>
/// Concrete <see cref="IMutationRunner"/> backed by the pinned Stryker.NET
/// tool, executed inside the existing credential-free audit sandbox through
/// <see cref="ISandbox.ExecAsync"/> argv arrays (never shell strings).
///
/// <para>Per audit the runner: probes the pre-provisioned tool (it never
/// installs anything), deterministically selects the production projects that
/// own the changed files plus every test project referencing them, scopes
/// mutation to the changed paths via repeated <c>--mutate</c> patterns, runs
/// one Stryker invocation per project group under the shared wall-clock
/// budget, and parses the machine-readable JSON report into scores. Console
/// text and exit codes never establish scores; only a fresh, bounded,
/// parseable report does.</para>
///
/// <para>Scoped runs never establish an overall-project score: the report
/// carries <c>Overall = null</c> with <see
/// cref="MutationRunScope.ChangedFilesOnly"/> so the auditor skips the
/// ratchet instead of copying the changed score.</para>
/// </summary>
public sealed partial class StrykerMutationRunner : IMutationRunner
{
    /// <summary>
    /// External requirements the operator must provision into the audit
    /// sandbox baseline image: (binary, requirement, provisioning hint).
    /// Hints are display text only — the runner never executes them.
    /// </summary>
    public static IReadOnlyList<(string Binary, string Requirement, string ProvisionHint)> RequiredTools { get; }
        =
        [
            ("dotnet",
                ".NET SDK able to build the repository",
                "Bake the .NET SDK into the audit sandbox baseline image."),
            ("dotnet-stryker",
                $"version {StrykerMutationRunnerOptions.PinnedVersion} (pinned and integration-tested)",
                $"Bake the pinned tool into the audit sandbox baseline image, e.g. 'dotnet tool install " +
                $"--global dotnet-stryker --version {StrykerMutationRunnerOptions.PinnedVersion}'. " +
                "The runner never installs tools at audit time."),
        ];

    private const int CsprojReadCapBytes = 64 * 1024;
    private const int DiscoveryListCapBytes = 256 * 1024;
    private const int ProbeCapBytes = 64 * 1024;
    private const int RawOutputMaxChars = 8000;
    private const int ConsoleTailChars = 3000;
    private const int SurvivorDescriptionMaxChars = 240;
    private const int SelectionMaxChars = 2000;
    private const string OutputDirPrefix = "/tmp/codeybox-stryker-";
    private const string ReportRelativePath = "reports/mutation-report.json";

    private static readonly IReadOnlyDictionary<string, string> SandboxEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };

    private static readonly Regex VersionBannerPattern = new(
        @"(?m)^Version:\s*(\S+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProjectReferencePattern = new(
        "<ProjectReference\\s+Include\\s*=\\s*\"([^\"]+)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlySet<string> BuildFailureMarkers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "initial build failed", "build failed", "error CS", "error MSB", "Build FAILED",
        };

    private static readonly IReadOnlySet<string> TestFailureMarkers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "test run failed", "no test is available", "no test matches",
            "initial test run failed", "vstest failed", "testhost",
        };

    private static readonly IReadOnlySet<string> ArtifactDirectoryNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", "TestResults", "artifacts",
        };

    private readonly Func<StrykerMutationRunnerOptions> _optsProvider;
    private readonly ILogger<StrykerMutationRunner> _log;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Production constructor. <paramref name="optsProvider"/> is read fresh
    /// on every audit so <c>CodeyBox:Mutation:Stryker</c> hot-reloads without
    /// a restart (typically wired to the host options monitor).
    /// </summary>
    public StrykerMutationRunner(
        Func<StrykerMutationRunnerOptions> optsProvider,
        ILogger<StrykerMutationRunner> log,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(optsProvider);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _optsProvider = optsProvider;
        _log = log;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task<MutationRunReport> RunAsync(
        ISandbox sandbox,
        string workingDirectory,
        IReadOnlyList<string> changedFiles,
        TimeSpan budget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var opts = _optsProvider();
        var configErrors = opts.Validate();
        if (configErrors.Count > 0)
            throw new InvalidOperationException(
                "Invalid Stryker mutation-runner configuration: " + string.Join(" ", configErrors));

        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new InvalidOperationException("Stryker runner received an empty working directory.");
        var root = workingDirectory.TrimEnd('/');
        if (root.Length == 0)
            root = "/";
        var start = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        var changed = NormalizeChangedFiles(changedFiles, opts.MaxChangedFiles);
        if (changed.Count == 0)
            return NoEvidence(MutationRunStatus.NoApplicableCode,
                "No changed files were supplied to the mutation runner.", stopwatch.Elapsed);

        await ProbeToolAsync(sandbox, root, opts, ct).ConfigureAwait(false);

        var discovered = await DiscoverProjectsAsync(sandbox, root, opts, ct).ConfigureAwait(false);
        if (discovered.Production.Count == 0 && discovered.Test.Count == 0)
        {
            return new MutationRunReport(
                ChangedCodeMutationScorePercent: null,
                OverallMutationScorePercent: null,
                SurvivingMutantsInChangedCode: [],
                Duration: stopwatch.Elapsed,
                RawOutput: "Stryker: no .NET project files found; the tree is not a supported .NET project.",
                Status: MutationRunStatus.UnsupportedProject,
                StatusDetail: "No *.csproj files were discovered; Stryker.NET only mutates .NET projects.",
                ToolVersion: null,
                SourceCommitSha: await ReadSourceShaAsync(sandbox, root, ct).ConfigureAwait(false),
                ProjectSelection: "(none: no .NET projects discovered)",
                ConfigDigest: null,
                Scope: MutationRunScope.ChangedFilesOnly);
        }

        var selection = BuildSelection(changed, discovered, opts);
        if (selection.Groups.Count == 0)
        {
            var detail = DescribeNoSelection(selection);
            return new MutationRunReport(
                ChangedCodeMutationScorePercent: null,
                OverallMutationScorePercent: null,
                SurvivingMutantsInChangedCode: [],
                Duration: stopwatch.Elapsed,
                RawOutput: "Stryker: " + detail,
                Status: MutationRunStatus.NoApplicableCode,
                StatusDetail: detail,
                ToolVersion: null,
                SourceCommitSha: await ReadSourceShaAsync(sandbox, root, ct).ConfigureAwait(false),
                ProjectSelection: DescribeDiscovered(discovered),
                ConfigDigest: null,
                Scope: MutationRunScope.ChangedFilesOnly);
        }
        if (selection.Groups.Count > opts.MaxProjectsPerRun)
            throw new StrykerRunFailedException(
                $"Stryker selection matched {selection.Groups.Count} projects under test, above " +
                $"MaxProjectsPerRun ({opts.MaxProjectsPerRun}). Narrow the diff or raise the cap; " +
                "projects were not silently dropped.")
            {
                Kind = StrykerFailureKind.Report,
            };
        var uncovered = selection.Groups
            .Where(g => g.TestProjects.Count == 0)
            .Select(g => g.ProjectCsproj)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        if (uncovered.Count > 0)
        {
            var detail = $"Changed production code in {string.Join(", ", uncovered.Take(5).Select(p => StrykerPaths.SanitizeForLog(p)))} " +
                "has no covering test project, so mutation cannot run. Add a test project that " +
                "references it (or pin Stryker:Projects explicitly).";
            return new MutationRunReport(
                ChangedCodeMutationScorePercent: null,
                OverallMutationScorePercent: null,
                SurvivingMutantsInChangedCode: [],
                Duration: stopwatch.Elapsed,
                RawOutput: "Stryker: " + detail,
                Status: MutationRunStatus.NoCoveringTests,
                StatusDetail: detail,
                ToolVersion: null,
                SourceCommitSha: await ReadSourceShaAsync(sandbox, root, ct).ConfigureAwait(false),
                ProjectSelection: DescribeSelection(selection),
                ConfigDigest: null,
                Scope: MutationRunScope.ChangedFilesOnly);
        }

        var deadline = start + budget;
        var sourceSha = await ReadSourceShaAsync(sandbox, root, ct).ConfigureAwait(false);
        var toolVersion = "unknown";
        var survivors = new List<SurvivingMutant>();
        var seenMutants = new HashSet<string>(StringComparer.Ordinal);
        var reportedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedValid = 0;
        var changedDetected = 0;
        var totalMutants = 0;
        var totalValid = 0;
        var totalDetected = 0;
        var totalIgnored = 0;
        var totalErrored = 0;
        var consoleTails = new List<string>();
        var createdDirs = new List<string>();

        try
        {
            foreach (var group in selection.Groups)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await RunGroupAsync(
                    sandbox, root, group, opts, deadline, createdDirs, ct).ConfigureAwait(false);
                toolVersion = outcome.ToolVersion;
                totalMutants += outcome.Parsed.Mutants.Count;
                foreach (var fileKey in outcome.Parsed.Files)
                {
                    var mapped = MapReportKey(root, group.ProjectDirectory, fileKey, outcome.ReportProjectRoot);
                    if (mapped is not null)
                        reportedKeys.Add(mapped);
                }
                totalValid += outcome.Parsed.Mutants.Count(m => IsScorable(m.Status));
                consoleTails.Add(outcome.ConsoleTail);

                foreach (var mutant in outcome.Parsed.Mutants)
                {
                    var repoPath = MapReportKey(root, group.ProjectDirectory, mutant.FileKey, outcome.ReportProjectRoot);
                    if (repoPath is null)
                        continue;
                    if (!outcome.ChangedSet.Contains(repoPath))
                        continue;
                    var identity = $"{repoPath}\0{mutant.Line}\0{mutant.Mutator}\0{mutant.Replacement}";
                    if (!seenMutants.Add(identity))
                        continue;
                    if (IsDetected(mutant.Status))
                    {
                        changedValid++;
                        changedDetected++;
                    }
                    else if (IsUndetected(mutant.Status))
                    {
                        changedValid++;
                        if (IsSurvivor(mutant.Status))
                            survivors.Add(DescribeSurvivor(repoPath, mutant));
                    }
                }

                totalDetected += outcome.Parsed.Killed + outcome.Parsed.Timeout;
                totalIgnored += outcome.Parsed.Ignored;
                totalErrored += outcome.Parsed.Errored;
            }
        }
        finally
        {
            await RemoveDirectoriesAsync(sandbox, root, createdDirs).ConfigureAwait(false);
        }

        if (totalValid == 0 || changedValid == 0)
        {
            // Zero mutants is ambiguous: a comment/whitespace-only change
            // legitimately yields no mutants, while a selection/pattern drift
            // (or an engine that ignored everything) yields reports that miss
            // the changed files. The report's file keys disambiguate: when no
            // mutant was generated anywhere AND every changed file resolved
            // into the report, there is nothing to gate.
            if (totalMutants == 0 && changed.All(c => reportedKeys.Contains(c)))
            {
                var files = string.Join(", ", changed.Take(5).Select(p => StrykerPaths.SanitizeForLog(p)));
                var nothingMutable =
                    $"Stryker resolved the changed file(s) ({files}) but they contain no " +
                    "mutable statements, so no score can be established and none is needed.";
                return new MutationRunReport(
                    ChangedCodeMutationScorePercent: null,
                    OverallMutationScorePercent: null,
                    SurvivingMutantsInChangedCode: [],
                    Duration: stopwatch.Elapsed,
                    RawOutput: nothingMutable,
                    Status: MutationRunStatus.NoApplicableCode,
                    StatusDetail: nothingMutable,
                    ToolVersion: toolVersion,
                    SourceCommitSha: sourceSha,
                    ProjectSelection: DescribeSelection(selection),
                    ConfigDigest: null,
                    Scope: MutationRunScope.ChangedFilesOnly);
            }
            throw new StrykerRunFailedException(
                totalValid == 0
                    ? "Stryker produced no scorable mutants for the selected projects. Check the " +
                      "project selection and mutate scope; a zero-mutant run is not evidence of tested code."
                    : "Stryker produced mutants but none fall in the changed files — the report does " +
                      "not cover the code under audit, so no changed-code score can be established.")
            {
                Kind = StrykerFailureKind.Report,
            };
        }

        survivors.Sort(static (a, b) =>
            string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase) is { } c and not 0
                ? c
                : a.Line.CompareTo(b.Line));
        var changedScore = 100.0 * changedDetected / changedValid;
        var selectionText = DescribeSelection(selection);
        var digest = ComputeConfigDigest(opts, toolVersion, selectionText);
        var rawOutput = BuildProvenance(
            toolVersion, opts.ExpectedVersion, sourceSha, selectionText, digest,
            totalValid, totalDetected, totalIgnored, totalErrored,
            changedValid, changedDetected, changedScore, survivors.Count, consoleTails);

        _log.LogInformation(
            "Stryker mutation run complete: changed score {Score:F1}% over {Valid} mutants in {Count} project(s), tool {Tool}",
            changedScore, changedValid, selection.Groups.Count, toolVersion);

        return new MutationRunReport(
            ChangedCodeMutationScorePercent: changedScore,
            OverallMutationScorePercent: null,
            SurvivingMutantsInChangedCode: survivors,
            Duration: stopwatch.Elapsed,
            RawOutput: rawOutput,
            Status: MutationRunStatus.Completed,
            StatusDetail: null,
            ToolVersion: toolVersion,
            SourceCommitSha: sourceSha,
            ProjectSelection: selectionText,
            ConfigDigest: digest,
            Scope: MutationRunScope.ChangedFilesOnly);
    }

    private static MutationRunReport NoEvidence(
        MutationRunStatus status, string detail, TimeSpan elapsed) =>
        new(null, null, [], elapsed,
            RawOutput: "Stryker: " + detail,
            Status: status,
            StatusDetail: detail,
            Scope: MutationRunScope.ChangedFilesOnly);

    private static IReadOnlyList<string> NormalizeChangedFiles(
        IReadOnlyList<string> changedFiles, int maxChangedFiles)
    {
        if (changedFiles.Count > maxChangedFiles)
            throw new StrykerRunFailedException(
                $"Stryker runner received {changedFiles.Count} changed files, above MaxChangedFiles " +
                $"({maxChangedFiles}). Narrow the diff or raise the cap; files were not silently dropped.")
            {
                Kind = StrykerFailureKind.Report,
            };
        var normalized = new List<string>(changedFiles.Count);
        foreach (var file in changedFiles)
        {
            var clean = StrykerPaths.NormalizeRepoPath(file);
            if (clean is null)
                throw new StrykerRunFailedException(
                    $"Stryker runner rejected a changed path that escapes the repository or is malformed: " +
                    $"'{StrykerPaths.SanitizeForLog(file)}'. Hostile paths never reach the tool.")
                {
                    Kind = StrykerFailureKind.Report,
                };
            normalized.Add(clean);
        }
        return normalized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }
}
