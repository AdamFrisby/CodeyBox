using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Audit;

/// <summary>Group-execution half of <see cref="StrykerMutationRunner"/>.</summary>
public sealed partial class StrykerMutationRunner
{
    private async Task<StrykerGroupOutcome> RunGroupAsync(
        ISandbox sandbox,
        string root,
        StrykerProjectGroup group,
        StrykerMutationRunnerOptions opts,
        DateTimeOffset deadline,
        List<string> createdDirs,
        CancellationToken ct)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            throw BudgetExceeded(opts, group, remaining);

        var groupDir = group.ProjectDirectory.Length == 0
            ? root
            : root + "/" + group.ProjectDirectory;
        var outputDir = OutputDirPrefix + Guid.NewGuid().ToString("N");
        createdDirs.Add(outputDir);

        await MakeOutputDirectoryAsync(sandbox, root, outputDir, opts, ct).ConfigureAwait(false);

        var argv = BuildStrykerArgv(opts, group, outputDir);
        _log.LogDebug(
            "Stryker invocation for {Project}: {Argv}",
            StrykerPaths.SanitizeForLog(group.ProjectCsproj),
            StrykerPaths.SanitizeForLog(string.Join(' ', argv.Skip(2)), 800));

        SandboxExecResult run;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(remaining);
            run = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = argv,
                WorkingDirectory = groupDir,
                ExtraEnvironment = SandboxEnvironment,
                MaxStdoutBytes = opts.MaxConsoleBytes,
                MaxStderrBytes = opts.MaxConsoleBytes,
                KillOnOutputLimit = false,
            }, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await KillAndCleanupAsync(sandbox, root, outputDir).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await KillAndCleanupAsync(sandbox, root, outputDir).ConfigureAwait(false);
            throw BudgetExceeded(opts, group, TimeSpan.Zero);
        }
        if (run.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(run.ExitCode);

        var toolVersion = ParseToolVersion(run.Stdout) ?? "unknown";
        if (!toolVersion.Equals("unknown", StringComparison.Ordinal)
            && !toolVersion.Equals(opts.ExpectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new StrykerToolMissingException(
                $"Stryker version mismatch: expected {opts.ExpectedVersion} but the sandbox ran " +
                $"{toolVersion}. Re-provision the audit sandbox baseline with the pinned tool; " +
                "scores under unvalidated engine semantics are not accepted.");

        var reportPath = outputDir + "/" + ReportRelativePath;
        var report = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["cat", "--", reportPath],
            WorkingDirectory = root,
            MaxStdoutBytes = opts.MaxReportBytes,
            MaxStderrBytes = ProbeCapBytes,
        }, ct).ConfigureAwait(false);
        if (report.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(report.ExitCode);
        if (report.StdoutLimitExceeded)
            throw OversizedReport(opts, reportPath);
        if (!report.Success)
            throw ClassifyNoReport(run.ExitCode, CombineOutput(run),
                $"Stryker left no readable report at '{reportPath}' (cat exited {report.ExitCode}).");

        var parsed = StrykerReportParser.TryParse(report.Stdout);
        if (!parsed.Success)
            throw new StrykerRunFailedException(
                $"Stryker report at '{reportPath}' is unusable: {parsed.Error} The console output " +
                $"cannot substitute for the machine-readable report: {StrykerPaths.SanitizeForLog(CombineOutput(run), 1200)}")
            {
                Kind = StrykerFailureKind.Report,
            };

        return new StrykerGroupOutcome
        {
            Parsed = parsed.Report!,
            ToolVersion = toolVersion,
            ConsoleTail = $"[exit {run.ExitCode}]\n" + StrykerPaths.SanitizeForLog(CombineOutput(run), ConsoleTailChars),
            ChangedSet = group.ChangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ReportProjectRoot = parsed.Report!.ReportProjectRoot,
            ReportBytes = System.Text.Encoding.UTF8.GetByteCount(report.Stdout),
            ReportSha256 = Sha256Hex(report.Stdout),
            OutputDirectory = outputDir,
        };
    }

    private static StrykerRunFailedException BudgetExceeded(
        StrykerMutationRunnerOptions opts, StrykerProjectGroup group, TimeSpan remaining) =>
        new($"Stryker mutation run exceeded its wall-clock budget before '{group.ProjectCsproj}' " +
            $"could run (remaining: {remaining}). Raise CodeyBox:Mutation:BudgetMinutes, narrow the " +
            "changed scope, or lower Stryker:Concurrency pressure; partial mutant sets are not scored.")
        {
            Kind = StrykerFailureKind.Timeout,
        };

    private static StrykerRunFailedException OversizedReport(
        StrykerMutationRunnerOptions opts, string reportPath) =>
        new($"Stryker report at '{reportPath}' exceeds MaxReportBytes ({opts.MaxReportBytes}). " +
            "Raise the cap if the tree legitimately produces larger reports; the truncated bytes " +
            "were not parsed.")
        {
            Kind = StrykerFailureKind.Report,
        };

    private static List<string> BuildStrykerArgv(
        StrykerMutationRunnerOptions opts, StrykerProjectGroup group, string outputDir)
    {
        // Sink-side guard (defense in depth alongside StrykerPaths): every
        // repo-derived value is validated AT the argv sink so a future
        // caller can never pass a dash-leading value that Stryker would
        // parse as a flag. Stryker's "--" end-of-options handling is
        // version-dependent, so fail-closed rejection is the version-proof
        // guard; legitimate repo names never start with '-'.
        var argv = new List<string>(opts.ToolCommand)
        {
            "-p", EnsureSafeCliValue(group.ProjectFileName, "project file name"),
        };
        foreach (var test in group.TestProjects)
        {
            argv.Add("-tp");
            argv.Add(EnsureSafeCliValue(RelativeSegments(group.ProjectDirectory, test), "test project path"));
        }
        foreach (var pattern in group.MutatePatterns)
        {
            argv.Add("-m");
            argv.Add(EnsureSafeCliValue(pattern, "mutate pattern"));
        }
        argv.Add("-r");
        argv.Add("Json");
        argv.Add("-O");
        argv.Add(outputDir);
        argv.Add("--skip-version-check");
        argv.Add("-l");
        argv.Add(CanonicalLevel(opts.MutationLevel));
        argv.Add("--configuration");
        argv.Add(opts.Configuration.Trim());
        argv.Add("-b");
        argv.Add("0");
        if (opts.Concurrency > 0)
        {
            argv.Add("-c");
            argv.Add(opts.Concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return argv;
    }

    private static string EnsureSafeCliValue(string value, string what)
    {
        // Repo-controlled values reach Stryker option positions (-p/-tp/-m)
        // where a leading '-' would desynchronize flag parsing (scope
        // manipulation or gate distortion). Reject fail-closed: empty,
        // dash-leading, or control/NUL-carrying values never reach argv.
        // Segment-internal dashes (e.g. "my-dir/file.cs") are harmless and
        // remain allowed; only a leading dash (or a segment starting with
        // '-' after a '/' in patterns/relative paths) is dangerous.
        if (string.IsNullOrEmpty(value)
            || value[0] == '-'
            || value[0] == '\0'
            || value.Contains('\0'))
            throw StrykerOptionInjection(value, what);
        foreach (var c in value)
        {
            if (char.IsControl(c))
                throw StrykerOptionInjection(value, what);
        }
        foreach (var segment in value.Replace('\\', '/').Split('/'))
        {
            if (segment.Length > 0 && segment[0] == '-' && segment is not (".." or "."))
                throw StrykerOptionInjection(value, what);
        }
        return value;
    }

    private static StrykerRunFailedException StrykerOptionInjection(string value, string what) =>
        new($"Stryker {what} '{StrykerPaths.SanitizeForLog(value)}' starts with '-' or carries " +
            "control characters and could parse as a CLI flag; refusing to run. Rename the path " +
            "so it does not begin with '-' (or a path segment beginning with '-').")
        {
            Kind = StrykerFailureKind.Tool,
        };

    private static string CanonicalLevel(string level) =>
        level.Trim().ToLowerInvariant() switch
        {
            "basic" => "Basic",
            "advanced" => "Advanced",
            "complete" => "Complete",
            _ => "Standard",
        };

    private static string? ParseToolVersion(string stdout)
    {
        var match = VersionBannerPattern.Match(stdout);
        if (!match.Success)
            return null;
        var version = match.Groups[1].Value.Trim();
        // The banner echoes tool output that reaches findings/provenance:
        // accept only a plausible version token so ANSI escapes or control
        // characters can never flow downstream. Anything else is "unknown".
        if (version.Length is < 1 or > 64)
            return null;
        foreach (var c in version)
        {
            if (!(char.IsLetterOrDigit(c) || c is '.' or '-' or '+' or '_'))
                return null;
        }
        return version;
    }

    private async Task MakeOutputDirectoryAsync(
        ISandbox sandbox, string root, string outputDir,
        StrykerMutationRunnerOptions opts, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(opts.ProbeTimeoutSeconds));
        var mkdir = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["mkdir", "-p", "--", outputDir],
            WorkingDirectory = root,
            MaxStdoutBytes = ProbeCapBytes,
            MaxStderrBytes = ProbeCapBytes,
        }, timeout.Token).ConfigureAwait(false);
        if (mkdir.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(mkdir.ExitCode);
        if (!mkdir.Success)
            throw new StrykerRunFailedException(
                $"Stryker could not create its transient output directory '{outputDir}' " +
                $"(mkdir exited {mkdir.ExitCode}): {StrykerPaths.SanitizeForLog(CombineOutput(mkdir), 500)}")
            {
                Kind = StrykerFailureKind.Tool,
            };
        var ls = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["ls", "-A", "--", outputDir],
            WorkingDirectory = root,
            MaxStdoutBytes = ProbeCapBytes,
            MaxStderrBytes = ProbeCapBytes,
        }, timeout.Token).ConfigureAwait(false);
        if (ls.ExecutionUnavailable)
            throw new SandboxExecutionUnavailableException(ls.ExitCode);
        if (!ls.Success)
            throw new StrykerRunFailedException(
                $"Stryker could not list its transient output directory '{outputDir}' " +
                $"(ls exited {ls.ExitCode}).")
            {
                Kind = StrykerFailureKind.Tool,
            };
        if (!string.IsNullOrWhiteSpace(ls.Stdout))
            throw new StrykerRunFailedException(
                $"Stryker transient output directory '{outputDir}' was not fresh (unexpected " +
                "pre-existing content). Refusing to run: a stale report could be mistaken for " +
                "fresh evidence.")
            {
                Kind = StrykerFailureKind.Report,
            };
    }

    private static StrykerRunFailedException ClassifyNoReport(
        int exitCode, string consoleOutput, string prefix)
    {
        // consoleOutput is raw tool stdout/stderr reflecting repo content and
        // reaches the finding Description, hence the rework prompt: sanitize
        // (control characters become '?', bounded length) before embedding.
        var tail = StrykerPaths.SanitizeForLog(consoleOutput, 1500);
        if (exitCode == 0)
            return new StrykerRunFailedException(
                $"{prefix} Stryker exited 0, but a zero exit without the machine-readable report " +
                $"establishes nothing — success is never inferred from the exit code. Output: {tail}")
            {
                Kind = StrykerFailureKind.Report,
            };
        if (exitCode == 2 && ContainsAny(consoleOutput, ["threshold", "break"]))
            return new StrykerRunFailedException(
                $"{prefix} Stryker signalled a threshold break (exit 2) but the report needed to " +
                $"establish the score is missing. Output: {tail}")
            {
                Kind = StrykerFailureKind.Report,
            };
        if (ContainsAny(consoleOutput, BuildFailureMarkers))
            return new StrykerRunFailedException(
                $"{prefix} The initial build failed, so no mutants were tested. Output: {tail}")
            {
                Kind = StrykerFailureKind.Build,
            };
        if (ContainsAny(consoleOutput, TestFailureMarkers))
            return new StrykerRunFailedException(
                $"{prefix} The initial test run failed, so mutant results would be meaningless. " +
                $"Output: {tail}")
            {
                Kind = StrykerFailureKind.Test,
            };
        return new StrykerRunFailedException(
            $"{prefix} Stryker failed (exit {exitCode}) without producing a report. Output: {tail}")
        {
            Kind = StrykerFailureKind.Tool,
        };
    }

    private static bool ContainsAny(string haystack, IEnumerable<string> markers)
    {
        foreach (var marker in markers)
        {
            if (haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Maps a Stryker report file key to a repository-relative path, or null
    /// when the key escapes the repository. Absolute keys are resolved
    /// against the sandbox <paramref name="root"/> first; when the sandbox
    /// provider remaps paths between the sandbox view and the tool's view
    /// (e.g. dev-process symlinks), the report's own
    /// <paramref name="reportProjectRoot"/> anchors the key to the known
    /// <paramref name="groupDir"/> instead. Relative keys resolve against the
    /// project root, falling back to repository-relative. Pure: no I/O.
    /// </summary>
    private static string? MapReportKey(
        string root, string groupDir, string key, string? reportProjectRoot)
    {
        foreach (var c in key)
        {
            if (c == '\0' || char.IsControl(c))
                return null;
        }
        if (key.Length > StrykerReportParser.MaxFileKeyLength)
            return null;
        var forward = key.Replace('\\', '/');
        if (forward.StartsWith('/'))
        {
            if (IsUnderRoot(forward, root))
                return StrykerPaths.NormalizeRepoPath(forward[(root.Length + 1)..]);
            var fromProject = StripProjectRoot(forward, reportProjectRoot);
            if (fromProject is not null)
                return JoinGroupDir(groupDir, fromProject);
            return null;
        }
        if (!string.IsNullOrWhiteSpace(reportProjectRoot))
        {
            var baseDir = reportProjectRoot.Replace('\\', '/').TrimEnd('/');
            if (baseDir.StartsWith('/'))
            {
                var combined = baseDir + "/" + forward;
                var viaRoot = StripProjectRoot(combined, baseDir);
                if (viaRoot is not null)
                {
                    // Absolute-via-projectRoot: anchor to the group dir when
                    // the sandbox tool view differs from the root view.
                    if (IsUnderRoot(combined, root))
                        return StrykerPaths.NormalizeRepoPath(combined[(root.Length + 1)..]);
                    return JoinGroupDir(groupDir, viaRoot);
                }
            }
            else
            {
                var collapsed = CollapseSegments(baseDir + "/" + forward);
                if (collapsed is not null)
                    return StrykerPaths.NormalizeRepoPath(collapsed);
            }
        }
        return StrykerPaths.NormalizeRepoPath(forward);
    }

    private static bool IsUnderRoot(string absolute, string root) =>
        absolute.Length > root.Length
        && absolute.StartsWith(root, StringComparison.Ordinal)
        && absolute[root.Length] == '/';

    private static string? StripProjectRoot(string absolute, string? reportProjectRoot)
    {
        if (string.IsNullOrWhiteSpace(reportProjectRoot))
            return null;
        var baseDir = reportProjectRoot.Replace('\\', '/').TrimEnd('/');
        if (!baseDir.StartsWith('/'))
            return null;
        if (absolute.Length <= baseDir.Length
            || !absolute.StartsWith(baseDir, StringComparison.Ordinal)
            || absolute[baseDir.Length] != '/')
            return null;
        return CollapseSegments(absolute[(baseDir.Length + 1)..]);
    }

    private static string? JoinGroupDir(string groupDir, string? relative)
    {
        if (relative is null)
            return null;
        var joined = groupDir.Length == 0 ? relative : groupDir + "/" + relative;
        return StrykerPaths.NormalizeRepoPath(joined);
    }

    private static string? CollapseSegments(string path)
    {
        var stack = new Stack<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (stack.Count == 0)
                    return null;
                stack.Pop();
                continue;
            }
            stack.Push(segment);
        }
        return string.Join('/', stack.Reverse());
    }

    private static string RelativeSegments(string fromDir, string toPath)
    {
        if (fromDir.Length == 0)
            return toPath;
        var from = fromDir.Split('/');
        var to = toPath.Split('/');
        var common = 0;
        while (common < from.Length && common < to.Length
               && from[common].Equals(to[common], StringComparison.Ordinal))
            common++;
        var parts = new List<string>();
        for (var i = common; i < from.Length; i++)
            parts.Add("..");
        for (var i = common; i < to.Length; i++)
            parts.Add(to[i]);
        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    private static string DescribeSelection(StrykerSelection selection)
    {
        var parts = selection.Groups.Select(g =>
            g.TestProjects.Count == 0
                ? $"{g.ProjectCsproj} (no covering test projects!)"
                : $"{g.ProjectCsproj} <= {string.Join(", ", g.TestProjects)}");
        return string.Join(" | ", parts);
    }

    private static string ComputeConfigDigest(
        StrykerMutationRunnerOptions opts, string toolVersion, string selection)
    {
        var canonical =
            $"stryker-v1|tool={toolVersion}|concurrency={opts.Concurrency}|" +
            $"level={CanonicalLevel(opts.MutationLevel)}|config={opts.Configuration.Trim()}|" +
            $"break=0|reporters=Json|selection={selection}";
        return Sha256Hex(canonical);
    }

    private static string Sha256Hex(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildProvenance(
        string toolVersion,
        string expectedVersion,
        string sourceSha,
        string selection,
        string digest,
        int totalValid,
        int totalDetected,
        int totalIgnored,
        int totalErrored,
        int changedValid,
        int changedDetected,
        double changedScore,
        int survivorCount,
        IReadOnlyList<string> consoleTails)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Stryker.NET mutation-run provenance:");
        builder.Append("  tool: dotnet-stryker ").Append(toolVersion)
            .Append(" (expected ").Append(expectedVersion).AppendLine(")");
        builder.Append("  source SHA: ").AppendLine(sourceSha);
        builder.Append("  selection: ").AppendLine(StrykerPaths.SanitizeForLog(selection, SelectionMaxChars));
        builder.Append("  config digest: ").AppendLine(digest);
        builder.AppendLine("  scope: changed-files-only (overall-project score unavailable by design)");
        builder.Append("  engine totals: valid=").Append(totalValid)
            .Append(" detected=").Append(totalDetected)
            .Append(" ignored=").Append(totalIgnored)
            .Append(" errored=").Append(totalErrored).AppendLine();
        builder.Append("  changed: valid=").Append(changedValid)
            .Append(" detected=").Append(changedDetected)
            .Append(" score=").Append(changedScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
            .Append("% survivors=").Append(survivorCount).AppendLine();
        builder.AppendLine("  overall: unavailable (a changed-files-only run does not establish it)");
        // Console tails are raw tool output and land in RawOutput, which the
        // orchestrator surfaces to operators and rework prompts: re-sanitize
        // (idempotent) so no control characters or ANSI escapes survive.
        foreach (var tail in consoleTails)
            builder.AppendLine(StrykerPaths.SanitizeForLog(tail, ConsoleTailChars));
        var text = builder.ToString();
        return text.Length <= RawOutputMaxChars ? text : text[^RawOutputMaxChars..];
    }

    private static bool IsScorable(string status) =>
        StrykerReportParser.DetectedStatuses.Contains(status)
        || StrykerReportParser.UndetectedStatuses.Contains(status);

    private static bool IsDetected(string status) =>
        StrykerReportParser.DetectedStatuses.Contains(status);

    private static bool IsUndetected(string status) =>
        StrykerReportParser.UndetectedStatuses.Contains(status);

    private static bool IsSurvivor(string status) =>
        status.Equals("Survived", StringComparison.OrdinalIgnoreCase)
        || status.Equals("NoCoverage", StringComparison.OrdinalIgnoreCase);

    private static SurvivingMutant DescribeSurvivor(string repoPath, StrykerParsedMutant mutant)
    {
        // Mutator/replacement echo report JSON strings (mutated source text)
        // and reach the finding Title/Description, hence the rework prompt:
        // sanitize before embedding even though the parser already rejects
        // control characters (defense in depth against future callers).
        var mutator = StrykerPaths.SanitizeForLog(mutant.Mutator, 120);
        var replacement = StrykerPaths.SanitizeForLog(mutant.Replacement, 120);
        var detail = mutant.Status.Equals("NoCoverage", StringComparison.OrdinalIgnoreCase)
            ? $"{mutator} '{replacement}' has no covering test (uncovered)"
            : mutant.CoveredBy > 0
                ? $"{mutator} '{replacement}' survived; covered by {mutant.CoveredBy} test(s), none killed it"
                : $"{mutator} '{replacement}' survived; no covering test recorded";
        if (detail.Length > SurvivorDescriptionMaxChars)
            detail = detail[..SurvivorDescriptionMaxChars] + "…";
        return new SurvivingMutant(repoPath, mutant.Line, mutator, detail);
    }

    private async Task KillAndCleanupAsync(ISandbox sandbox, string root, string outputDir)
    {
        try
        {
            await sandbox.KillActiveExecsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
        {
        }
        await RemoveDirectoriesAsync(sandbox, root, [outputDir]).ConfigureAwait(false);
    }

    private static async Task RemoveDirectoriesAsync(
        ISandbox sandbox, string root, IReadOnlyList<string> dirs)
    {
        foreach (var dir in dirs)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["rm", "-rf", "--", dir],
                    WorkingDirectory = root,
                    MaxStdoutBytes = ProbeCapBytes,
                    MaxStderrBytes = ProbeCapBytes,
                }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (SandboxDeferralGuard.ShouldWrap(ex))
            {
            }
        }
    }

    private static string CombineOutput(SandboxExecResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Stderr))
            return result.Stdout;
        if (string.IsNullOrWhiteSpace(result.Stdout))
            return result.Stderr;
        return result.Stdout + "\n" + result.Stderr;
    }
}
