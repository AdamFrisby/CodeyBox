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
    public const string AuditorName = "process:required-build";
    public const string DisplayCommand = "dotnet build";

    private const int OutputMaxBytes = 16 * 1024;

    private const string BuildScript = """
        set -eu

        if ! command -v dotnet >/dev/null 2>&1; then
          echo "dotnet is not available in the sandbox PATH" >&2
          exit 127
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
          exit 125
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
        try
        {
            Validation.ValidateBranchName(request.WorkBranch, nameof(request.WorkBranch));
            var repoPath = _gitHost.GetRepoPath(request.RepositoryId);
            var (stdout, stderr, exitCode) = await RunHostGitCaptureNoThrowAsync(
                repoPath,
                ct,
                "ls-tree",
                "-r",
                "--name-only",
                request.WorkBranch);
            if (exitCode != 0)
            {
                var detail = SingleLineSummary(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                return RequiredBuildProbeResult.Unavailable(
                    $"failed to inspect branch '{request.WorkBranch}' for .NET build markers: {detail}");
            }

            return stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(IsRequiredDotnetBuildMarkerPath)
                ? RequiredBuildProbeResult.Applies
                : RequiredBuildProbeResult.NotApplicable;
        }
        catch (NotSupportedException ex)
        {
            return RequiredBuildProbeResult.Unavailable(
                $"failed to inspect branch '{request.WorkBranch}' for .NET build markers: {SingleLineSummary(ex.Message)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RequiredBuildProbeResult.Unavailable(
                $"failed to inspect branch '{request.WorkBranch}' for .NET build markers: {SingleLineSummary(ex.Message)}");
        }
    }

    public async Task<RequiredBuildVerificationResult> VerifyAsync(
        RequiredBuildVerificationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var probe = await ProbeAsync(new RequiredBuildProbeRequest
        {
            WorkItemId = request.WorkItemId,
            ProjectId = request.ProjectId,
            RepositoryId = request.RepositoryId,
            WorkBranch = request.WorkBranch,
        }, ct);

        if (probe.Status == RequiredBuildProbeStatus.NotApplicable)
            return RequiredBuildVerificationResult.Skipped;
        if (probe.Status == RequiredBuildProbeStatus.Unavailable)
            return RequiredBuildVerificationResult.Unavailable(
                $"could not verify required build: {probe.Reason}");

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

            if (build.ExitCode == 127
                && rawOutput.Contains("dotnet is not available in the sandbox PATH", StringComparison.OrdinalIgnoreCase))
            {
                return RequiredBuildVerificationResult.Unavailable(
                    "could not verify required build: dotnet is not available in the audit-tool sandbox",
                    build.ExitCode,
                    redactedOutput);
            }

            if (build.ExitCode == 125)
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
