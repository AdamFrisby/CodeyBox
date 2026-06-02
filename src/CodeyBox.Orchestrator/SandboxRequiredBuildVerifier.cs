using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Required .NET build verifier. It detects .NET build markers in the host
/// bare repo, then runs the build in a sandbox cloned from a disposable bare
/// clone so branch-controlled build scripts cannot mutate the authoritative
/// repository.
/// </summary>
public sealed class SandboxRequiredBuildVerifier : IRequiredBuildVerifier
{
    public const string AuditorName = RequiredBuildGateIdentity.AuditorName;
    public const string DisplayCommand = RequiredBuildGateIdentity.DisplayCommand;

    private const int OutputMaxBytes = 16 * 1024;
    // POSIX shells conventionally use 127 for "command not found".
    private const int DotnetCommandNotFoundExitCode = 127;
    // Internal BuildScript sentinel: marker inspection said this gate applies,
    // but no buildable .NET target was present after checkout.
    private const int NoRequiredBuildTargetExitCode = 125;

    private const string BuildScript = """
        set -eu
        dotnet_command_not_found_exit=127
        no_required_build_target_exit=125

        if ! command -v dotnet >/dev/null 2>&1; then
          echo "dotnet is not available in the sandbox PATH" >&2
          exit "$dotnet_command_not_found_exit"
        fi

        targets_file="${TMPDIR:-/tmp}/codeybox-required-build-targets-$$"
        root_solutions="${TMPDIR:-/tmp}/codeybox-required-build-root-solutions-$$"
        cleanup() { rm -f "$targets_file" "$root_solutions"; }
        trap cleanup EXIT INT TERM

        find . -maxdepth 1 -type f \( -name '*.slnx' -o -name '*.sln' \) | sort > "$root_solutions"
        cat "$root_solutions" > "$targets_file"

        if [ ! -s "$targets_file" ]; then
          find . \( -type d \( -name '.git' -o -name 'bin' -o -name 'obj' -o -name 'node_modules' \) -prune \) -o \( -type f \( -name '*.slnx' -o -name '*.sln' \) -print \) | sort > "$targets_file"
        fi

        if [ -s "$root_solutions" ]; then
          find . \( -type d \( -name '.git' -o -name 'bin' -o -name 'obj' -o -name 'node_modules' \) -prune \) -o \( -type f -name '*.csproj' -print \) | sort |
          while IFS= read -r project; do
            lower=$(printf '%s' "$project" | LC_ALL=C tr '[:upper:]' '[:lower:]')
            case "$lower" in
              *test*.csproj|*/test*/*.csproj|*/tests/*.csproj) printf '%s\n' "$project" ;;
            esac
          done >> "$targets_file"
        fi

        if [ ! -s "$targets_file" ]; then
          find . \( -type d \( -name '.git' -o -name 'bin' -o -name 'obj' -o -name 'node_modules' \) -prune \) -o \( -type f -name '*.csproj' -print \) | sort > "$targets_file"
        fi

        sort -u "$targets_file" -o "$targets_file"
        if [ ! -s "$targets_file" ]; then
          echo "No .NET solution or project file was found after marker detection." >&2
          exit "$no_required_build_target_exit"
        fi

        while IFS= read -r target; do
          [ -n "$target" ] || continue
          echo "CodeyBox required build: dotnet build $target"
          dotnet build "$target"
        done < "$targets_file"
        """;

    private readonly ISandboxProvider _sandboxes;
    private readonly IGitHost _gitHost;
    private readonly PipelineOptions _pipelineOptions;
    private readonly IAuditReportStore? _auditReports;
    private readonly ILogger<SandboxRequiredBuildVerifier> _log;

    public SandboxRequiredBuildVerifier(
        ISandboxProvider sandboxes,
        IGitHost gitHost,
        PipelineOptions pipelineOptions,
        IAuditReportStore? auditReports,
        ILogger<SandboxRequiredBuildVerifier> log)
    {
        _sandboxes = sandboxes;
        _gitHost = gitHost;
        _pipelineOptions = pipelineOptions;
        _auditReports = auditReports;
        _log = log;
    }

    public async Task<RequiredBuildProbeResult> ProbeAsync(
        RequiredBuildProbeRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var inspection = await InspectDotnetBuildMarkersAsync(request, ct);
        return inspection.ToProbeResult();
    }

    private async Task<DotnetBuildMarkerInspection> InspectDotnetBuildMarkersAsync(
        RequiredBuildProbeRequest request,
        CancellationToken ct)
    {
        try
        {
            Validation.ValidateBranchName(request.WorkBranch, nameof(request.WorkBranch));
            var repoPath = _gitHost.GetRepoPath(request.RepositoryId);

            var workInspection = await InspectBranchForDotnetBuildMarkersAsync(
                repoPath,
                request.WorkBranch,
                ct);
            if (!workInspection.Success)
            {
                return DotnetBuildMarkerInspection.Unavailable(
                    $"failed to inspect branch '{request.WorkBranch}' for .NET build markers: {workInspection.FailureDetail}");
            }

            if (workInspection.HasMarkers)
                return DotnetBuildMarkerInspection.Applies(workBranchHasMarkers: true, baseBranchHasMarkers: false);

            var baseBranch = request.BaseBranch;
            if (string.IsNullOrWhiteSpace(baseBranch))
                baseBranch = await _gitHost.GetDefaultBranchAsync(request.RepositoryId, ct);
            Validation.ValidateBranchName(baseBranch, nameof(request.BaseBranch));

            if (string.Equals(baseBranch, request.WorkBranch, StringComparison.Ordinal))
                return DotnetBuildMarkerInspection.NotApplicable();

            var baseInspection = await InspectBranchForDotnetBuildMarkersAsync(
                repoPath,
                baseBranch,
                ct);
            if (!baseInspection.Success)
            {
                return DotnetBuildMarkerInspection.Unavailable(
                    $"failed to inspect base branch '{baseBranch}' for .NET build markers: {baseInspection.FailureDetail}");
            }

            if (baseInspection.HasMarkers)
            {
                return DotnetBuildMarkerInspection.Applies(
                    workBranchHasMarkers: false,
                    baseBranchHasMarkers: true,
                    baseBranch: baseBranch,
                    reason: $"base branch '{baseBranch}' contains .NET build markers, but work branch '{request.WorkBranch}' does not");
            }

            return DotnetBuildMarkerInspection.NotApplicable();
        }
        catch (NotSupportedException ex)
        {
            return DotnetBuildMarkerInspection.Unavailable(
                $"failed to inspect branch '{request.WorkBranch}' for .NET build markers: {SingleLineSummary(ex.Message)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DotnetBuildMarkerInspection.Unavailable(
                $"failed to inspect branch '{request.WorkBranch}' for .NET build markers: {SingleLineSummary(ex.Message)}");
        }
    }

    public async Task<RequiredBuildVerificationResult> VerifyAsync(
        RequiredBuildVerificationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var inspection = await InspectDotnetBuildMarkersAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = request.WorkItemId,
            ProjectId = request.ProjectId,
            RepositoryId = request.RepositoryId,
            BaseBranch = request.BaseBranch,
            WorkBranch = request.WorkBranch,
        }, ct);

        if (inspection.Status == RequiredBuildProbeStatus.NotApplicable)
            return RequiredBuildVerificationResult.Skipped;
        if (inspection.Status == RequiredBuildProbeStatus.Unavailable)
            return RequiredBuildVerificationResult.Unavailable(
                $"could not verify required build: {inspection.Reason}");

        if (inspection.BaseBranchHasMarkers && !inspection.WorkBranchHasMarkers)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var output =
                $"Required .NET build markers exist on base branch '{inspection.BaseBranch}', " +
                $"but work branch '{request.WorkBranch}' contains no solution or project file. " +
                "The branch deleted or moved the files required for the non-skippable build gate.";
            await PersistReportAsync(
                request.WorkItemId,
                request.Iteration ?? 0,
                startedAt,
                TimeSpan.Zero,
                success: false,
                rawOutput: output,
                exitCode: NoRequiredBuildTargetExitCode,
                ct);
            return RequiredBuildVerificationResult.Failed(NoRequiredBuildTargetExitCode, output);
        }

        string? isolatedRepoPath = null;
        try
        {
            isolatedRepoPath = await CreateIsolatedBuildRepositoryAsync(
                request.RepositoryId,
                request.WorkItemId,
                ct);
            var startedAt = DateTimeOffset.UtcNow;
            var sw = Stopwatch.StartNew();
            var access = _gitHost.GetIsolatedRepoSandboxAccess(isolatedRepoPath);
            var spec = BuildSandboxSpec(access, request);

            await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
            await RunOrUnavailableAsync(
                sandbox,
                ct,
                "git",
                "clone",
                access.CloneUrlInsideSandbox,
                SandboxConventions.WorkDir);
            await RunOrUnavailableAsync(
                sandbox,
                ct,
                "git",
                "-C",
                SandboxConventions.WorkDir,
                "checkout",
                "-B",
                request.WorkBranch,
                $"origin/{request.WorkBranch}");

            var build = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", BuildScript],
                WorkingDirectory = SandboxConventions.WorkDir,
            }, ct);
            sw.Stop();

            var rawOutput = CombinedOutput(build);
            var redactedOutput = TruncateOutput(rawOutput);
            await PersistReportAsync(
                request.WorkItemId,
                request.Iteration ?? 0,
                startedAt,
                sw.Elapsed,
                build.Success,
                redactedOutput,
                build.ExitCode,
                ct);

            if (build.Success)
                return RequiredBuildVerificationResult.Passed(build.ExitCode, redactedOutput);

            if (build.ExitCode == DotnetCommandNotFoundExitCode
                && rawOutput.Contains("dotnet is not available in the sandbox PATH", StringComparison.OrdinalIgnoreCase))
            {
                return RequiredBuildVerificationResult.Unavailable(
                    "could not verify required build: dotnet is not available in the audit-tool sandbox",
                    build.ExitCode,
                    redactedOutput);
            }

            if (build.ExitCode == NoRequiredBuildTargetExitCode)
            {
                return RequiredBuildVerificationResult.Unavailable(
                    "could not verify required build: no .NET solution or project file was found after marker detection",
                    build.ExitCode,
                    redactedOutput);
            }

            return RequiredBuildVerificationResult.Failed(build.ExitCode, redactedOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RequiredBuildVerificationResult.Unavailable(
                $"could not verify required build: {SingleLineSummary(ex.Message)}",
                output: string.Empty);
        }
        finally
        {
            if (isolatedRepoPath is not null)
            {
                await _gitHost.DisposeIsolatedMergeCloneAsync(
                    request.RepositoryId,
                    isolatedRepoPath,
                    CancellationToken.None);
            }
        }
    }

    private async Task<string> CreateIsolatedBuildRepositoryAsync(
        string repositoryId,
        WorkItemId workItemId,
        CancellationToken ct)
    {
        try
        {
            return await _gitHost.CreateIsolatedMergeCloneAsync(repositoryId, workItemId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not create isolated build repository: {SingleLineSummary(ex.Message)}",
                ex);
        }
    }

    private SandboxSpec BuildSandboxSpec(
        SandboxRepositoryAccess access,
        RequiredBuildVerificationRequest request)
    {
        var net = new SandboxNetworkPolicy
        {
            AllowedHosts = [],
            HostGitEndpoint = access.Network.HostGitEndpoint,
            ProfileName = request.NetworkProfile,
        };

        return new SandboxSpec
        {
            ImageReference = _pipelineOptions.SandboxImageReference,
            Mounts =
            [
                .. access.Mounts,
                new SandboxMount { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
            ],
            Environment = new Dictionary<string, string>(),
            Network = net,
            Flavor = request.Flavor,
            WorkingDirectory = SandboxConventions.WorkDir,
            TimingWorkItemId = request.WorkItemId,
            TimingPhase = request.Phase,
            BaselineImageRef = request.BaselineImageRef,
        };
    }

    private static async Task RunOrUnavailableAsync(
        ISandbox sandbox,
        CancellationToken ct,
        params string[] argv)
    {
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"{argv[0]} failed while preparing required build: {SingleLineSummary(CombinedOutput(result))}");
        }
    }

    private async Task PersistReportAsync(
        WorkItemId workItemId,
        int iteration,
        DateTimeOffset startedAt,
        TimeSpan elapsed,
        bool success,
        string rawOutput,
        int exitCode,
        CancellationToken ct)
    {
        if (_auditReports is null)
            return;

        try
        {
            var findings = success
                ? []
                : new List<AuditReportFinding>
                {
                    new(
                        FindingIdComputer.Compute(AuditorName, "required build failed", []),
                        AuditSeverity.Error.ToString(),
                        $"required build failed: {DisplayCommand}",
                        $"Required build exited with code {exitCode}.",
                        [],
                        []),
                };
            await _auditReports.CreateAsync(new AuditReport
            {
                Id = Guid.NewGuid().ToString(),
                WorkItemId = workItemId.ToString(),
                Iteration = iteration,
                AuditorName = AuditorName,
                AuditorKind = "shell",
                WorstSeverity = success ? "none" : AuditSeverity.Error.ToString(),
                StartedAt = startedAt,
                EndedAt = startedAt + elapsed,
                DurationMs = (long)elapsed.TotalMilliseconds,
                Findings = findings,
                RawOutput = rawOutput,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to persist required build report for work item {WorkItemId}", workItemId);
        }
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunHostGitCaptureNoThrowAsync(
        string workdir,
        CancellationToken ct,
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    private static async Task<BranchDotnetMarkerInspection> InspectBranchForDotnetBuildMarkersAsync(
        string repoPath,
        string branch,
        CancellationToken ct)
    {
        var (stdout, stderr, exitCode) = await RunHostGitCaptureNoThrowAsync(
            repoPath,
            ct,
            "ls-tree",
            "-r",
            "--name-only",
            branch);
        if (exitCode != 0)
        {
            var detail = SingleLineSummary(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            return BranchDotnetMarkerInspection.Failed(
                string.IsNullOrWhiteSpace(detail) ? $"git ls-tree exited {exitCode}" : detail);
        }

        var hasMarkers = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(IsRequiredDotnetBuildMarkerPath);
        return BranchDotnetMarkerInspection.Completed(hasMarkers);
    }

    private sealed record BranchDotnetMarkerInspection(
        bool Success,
        bool HasMarkers,
        string? FailureDetail)
    {
        public static BranchDotnetMarkerInspection Completed(bool hasMarkers) =>
            new(true, hasMarkers, null);

        public static BranchDotnetMarkerInspection Failed(string failureDetail) =>
            new(false, false, failureDetail);
    }

    private sealed record DotnetBuildMarkerInspection(
        RequiredBuildProbeStatus Status,
        bool WorkBranchHasMarkers,
        bool BaseBranchHasMarkers,
        string? BaseBranch = null,
        string? Reason = null)
    {
        public static DotnetBuildMarkerInspection NotApplicable() =>
            new(RequiredBuildProbeStatus.NotApplicable, false, false);

        public static DotnetBuildMarkerInspection Applies(
            bool workBranchHasMarkers,
            bool baseBranchHasMarkers,
            string? baseBranch = null,
            string? reason = null) =>
            new(RequiredBuildProbeStatus.Applies, workBranchHasMarkers, baseBranchHasMarkers, baseBranch, reason);

        public static DotnetBuildMarkerInspection Unavailable(string reason) =>
            new(RequiredBuildProbeStatus.Unavailable, false, false, Reason: reason);

        public RequiredBuildProbeResult ToProbeResult() =>
            Status switch
            {
                RequiredBuildProbeStatus.Applies => Reason is null
                    ? RequiredBuildProbeResult.Applies
                    : new RequiredBuildProbeResult(RequiredBuildProbeStatus.Applies, Reason),
                RequiredBuildProbeStatus.NotApplicable => RequiredBuildProbeResult.NotApplicable,
                RequiredBuildProbeStatus.Unavailable => RequiredBuildProbeResult.Unavailable(
                    Reason ?? "build marker inspection failed"),
                _ => RequiredBuildProbeResult.Unavailable($"unknown probe status {Status}"),
            };
    }

    private static bool IsRequiredDotnetBuildMarkerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return false;

        if (segments.Any(static s =>
                s.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || s.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || s.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || s.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var fileName = segments[^1];
        return fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombinedOutput(SandboxExecResult result)
        => string.IsNullOrWhiteSpace(result.Stderr)
            ? result.Stdout
            : string.IsNullOrWhiteSpace(result.Stdout)
                ? result.Stderr
                : result.Stdout + "\n" + result.Stderr;

    private static string TruncateOutput(string output)
        => RawOutputRedactor.TruncateToBytes(
            RawOutputRedactor.Redact(output),
            OutputMaxBytes);

    private static string SingleLineSummary(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var chars = new char[text.Length];
        var pos = 0;
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t' || char.IsControl(ch) || ch == ' ')
            {
                if (!lastWasSpace)
                {
                    chars[pos++] = ' ';
                    lastWasSpace = true;
                }
                continue;
            }

            chars[pos++] = ch;
            lastWasSpace = false;
        }

        return new string(chars, 0, pos).Trim();
    }
}
