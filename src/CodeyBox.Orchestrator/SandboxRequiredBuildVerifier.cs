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
    private const int OutputMaxBytes = 16 * 1024;

    // Filename suffixes that identify .NET build markers. The host streams
    // ls-tree output line-by-line and only keeps paths whose final filename
    // ends with one of these, so a tree containing millions of non-.NET files
    // does not consume unbounded memory in the orchestrator.
    private static readonly string[] DotnetMarkerSuffixes =
    [
        ".sln",
        ".slnx",
        ".csproj",
    ];

    // Upper bound on .NET marker paths the probe will inspect per branch.
    // Real-world monorepos with thousands of projects fit; anything larger is
    // treated as a tree we cannot safely inspect (the gate falls back to
    // Unavailable rather than partial-data).
    private const int MaxDotnetMarkerPathsPerBranch = 8192;
    // POSIX shells conventionally use 127 for "command not found".
    private const int DotnetCommandNotFoundExitCode = 127;
    // Internal BuildScript sentinel: marker inspection said this gate applies,
    // but no buildable .NET target was present after checkout.
    private const int NoRequiredBuildTargetExitCode = 125;
    // Internal verifier sentinel for branch-controlled clone / checkout / build
    // execution exceeding the required-build budget.
    private const int BuildTimeoutExitCode = 124;

    // Exposed to tests so the actual gate script — the exact artifact the
    // sandbox executes via `sh -c` — can be run under a controlled shell.
    internal static readonly string BuildScript = $$"""
        set -eu
        dotnet_command_not_found_exit={{DotnetCommandNotFoundExitCode}}
        no_required_build_target_exit={{NoRequiredBuildTargetExitCode}}

        if ! command -v dotnet >/dev/null 2>&1; then
          echo "dotnet is not available in the sandbox PATH" >&2
          exit "$dotnet_command_not_found_exit"
        fi

        tmp_root="${TMPDIR:-/tmp}"
        targets_file="$tmp_root/codeybox-required-build-targets-$$"

        # The build gate must survive sandbox images whose per-user home is not
        # writable by the build user. `dotnet build` reads — and, when absent,
        # creates — the per-user NuGet settings directory ($HOME/.nuget/NuGet)
        # before honouring any repo-, solution-, or RestoreConfigFile-level
        # configuration, so an image whose $HOME (or $HOME/.nuget) is owned by
        # another user (e.g. root) fails every restore with
        # "Failed to read NuGet.Config ... Permission denied" and produces no
        # assemblies. Redirect the CLI/NuGet per-user home to a directory this
        # script owns so the gate no longer depends on $HOME being writable.
        dotnet_home="$tmp_root/codeybox-dotnet-home-$$"

        cleanup() { rm -rf "$targets_file" "$dotnet_home"; }
        trap cleanup EXIT INT TERM

        mkdir -p "$dotnet_home"
        export DOTNET_CLI_HOME="$dotnet_home"
        export DOTNET_NOLOGO=1
        export DOTNET_CLI_TELEMETRY_OPTOUT=1

        # Relocating DOTNET_CLI_HOME also relocates the NuGet global-packages
        # folder ($DOTNET_CLI_HOME/.nuget/packages). Images that pre-bake their
        # package cache under the original per-user home would then restore
        # against an empty folder and require network access. Preserve that
        # cache (read access is sufficient — restore never writes to an
        # already-extracted package) so offline/pinned images keep working.
        if [ -z "${NUGET_PACKAGES:-}" ] && [ -n "${HOME:-}" ] && [ -d "$HOME/.nuget/packages" ]; then
          export NUGET_PACKAGES="$HOME/.nuget/packages"
        fi

        find . -maxdepth 1 -type f \( -name '*.slnx' -o -name '*.sln' \) | sort > "$targets_file"

        if [ ! -s "$targets_file" ]; then
          find . \( -type d \( -name '.git' -o -name 'bin' -o -name 'obj' -o -name 'node_modules' \) -prune \) -o \( -type f \( -name '*.slnx' -o -name '*.sln' \) -print \) | sort > "$targets_file"
        fi

        # If we discovered any solution file (root or nested), append test
        # projects: a nested .sln may not include every test project and the
        # build gate must still cover the full test surface. When no solution
        # exists at all, the csproj-only fallback below already picks up test
        # projects.
        if [ -s "$targets_file" ]; then
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

        # Heal an inherited, non-writable per-user NuGet home before restore so a
        # COW-inherited root-owned $HOME/.nuget cannot abort the build with
        # "Failed to read NuGet.Config due to unauthorized access". The recovery
        # is repository-owned (scripts/nuget-home-heal.sh) and dot-sourced so its
        # fallback DOTNET_CLI_HOME propagates to the dotnet invocations below; it
        # is a no-op when the home is usable and is skipped when the repository
        # does not ship it. This adds no capability the gate lacks — it already
        # runs the branch's arbitrary build logic via `dotnet build`.
        if [ -f scripts/nuget-home-heal.sh ]; then
          . ./scripts/nuget-home-heal.sh
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

    public SandboxRequiredBuildVerifier(
        ISandboxProvider sandboxes,
        IGitHost gitHost,
        PipelineOptions pipelineOptions)
    {
        _sandboxes = sandboxes;
        _gitHost = gitHost;
        _pipelineOptions = pipelineOptions;
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

            var workPaths = await ListBranchPathsAsync(request.RepositoryId, request.WorkBranch, ct);
            var workHasMarkers = workPaths.Any(IsRequiredDotnetBuildMarkerPath);

            var baseBranch = request.BaseBranch;
            if (string.IsNullOrWhiteSpace(baseBranch))
                baseBranch = await _gitHost.GetDefaultBranchAsync(request.RepositoryId, ct);
            Validation.ValidateBranchName(baseBranch, nameof(request.BaseBranch));

            // Work-on-base case: no base/work comparison applies; only the
            // work-branch markers decide whether the gate is enforced.
            if (string.Equals(baseBranch, request.WorkBranch, StringComparison.Ordinal))
            {
                return workHasMarkers
                    ? DotnetBuildMarkerInspection.Applies(
                        workBranchHasMarkers: true,
                        baseBranchHasMarkers: false,
                        missingRequiredMarkers: Array.Empty<string>())
                    : DotnetBuildMarkerInspection.NotApplicable();
            }

            var basePaths = await ListBranchPathsAsync(request.RepositoryId, baseBranch, ct);
            var baseHasMarkers = basePaths.Any(IsRequiredDotnetBuildMarkerPath);

            // "Required" base markers are the ones whose deletion would silently
            // narrow the build gate: root solution files plus test projects.
            // Removing any of these from the work branch must downgrade neither
            // the gate's applicability nor its outcome.
            var workPathSet = new HashSet<string>(workPaths, StringComparer.Ordinal);
            // When base carries no solution file, the build script falls back
            // to building every .csproj it finds, so every base .csproj is
            // load-bearing — deleting any one (test or production) silently
            // narrows the gate. When a solution exists, the .sln itself
            // references each project and a missing csproj would surface as
            // a build failure through the solution.
            var baseHasAnySolution = basePaths.Any(IsSolutionPath);
            var missingRequired = basePaths
                .Where(p => IsRequiredBaseMarkerPath(p, baseHasAnySolution))
                .Where(p => !workPathSet.Contains(p))
                .ToArray();

            if (missingRequired.Length > 0 || (baseHasMarkers && !workHasMarkers))
            {
                return DotnetBuildMarkerInspection.Applies(
                    workBranchHasMarkers: workHasMarkers,
                    baseBranchHasMarkers: true,
                    baseBranch: baseBranch,
                    missingRequiredMarkers: missingRequired);
            }

            if (workHasMarkers)
            {
                return DotnetBuildMarkerInspection.Applies(
                    workBranchHasMarkers: true,
                    baseBranchHasMarkers: baseHasMarkers,
                    baseBranch: baseBranch,
                    missingRequiredMarkers: Array.Empty<string>());
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

    private async Task<IReadOnlyList<string>> ListBranchPathsAsync(
        string repositoryId,
        string branch,
        CancellationToken ct)
    {
        try
        {
            // Streamed, suffix-filtered ls-tree: the host reads ls-tree output
            // line-by-line and only retains paths whose filename ends with a
            // .NET marker suffix. A capped result count guards against trees
            // with an unbounded number of markers (defensive: real codebases
            // fit comfortably under the cap).
            return await _gitHost.ListFilesEndingWithAsync(
                repositoryId,
                branch,
                DotnetMarkerSuffixes,
                MaxDotnetMarkerPathsPerBranch,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not NotSupportedException)
        {
            throw new InvalidOperationException(
                $"failed to inspect branch '{branch}' for .NET build markers: {SingleLineSummary(ex.Message)}",
                ex);
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

        if (inspection.MissingRequiredMarkers.Count > 0 || (inspection.BaseBranchHasMarkers && !inspection.WorkBranchHasMarkers))
        {
            var output = inspection.MissingRequiredMarkers.Count > 0
                ? $"Work branch '{request.WorkBranch}' deleted or moved required .NET build marker(s) " +
                  $"present on base branch '{inspection.BaseBranch}': {string.Join(", ", inspection.MissingRequiredMarkers)}. " +
                  "The non-skippable build gate requires these markers to be present on the work branch."
                : $"Required .NET build markers exist on base branch '{inspection.BaseBranch}', " +
                  $"but work branch '{request.WorkBranch}' contains no solution or project file. " +
                  "The branch deleted or moved the files required for the non-skippable build gate.";
            return RequiredBuildVerificationResult.Failed(NoRequiredBuildTargetExitCode, output);
        }

        string? isolatedRepoPath = null;
        try
        {
            isolatedRepoPath = await CreateIsolatedBuildRepositoryAsync(
                request.RepositoryId,
                request.WorkItemId,
                ct);
            var access = _gitHost.GetIsolatedRepoSandboxAccess(isolatedRepoPath);
            var spec = BuildSandboxSpec(access, request);

            await using var sandbox = await _sandboxes.CreateAsync(spec, ct);
            using var buildTimeoutCts = new CancellationTokenSource(_pipelineOptions.RequiredBuildVerificationTimeout);
            using var buildCts = CancellationTokenSource.CreateLinkedTokenSource(ct, buildTimeoutCts.Token);
            var buildCt = buildCts.Token;
            SandboxExecResult build;
            try
            {
                await RunOrUnavailableAsync(
                    sandbox,
                    buildCt,
                    "git",
                    "clone",
                    access.CloneUrlInsideSandbox,
                    SandboxConventions.WorkDir);
                await RunOrUnavailableAsync(
                    sandbox,
                    buildCt,
                    "git",
                    "-C",
                    SandboxConventions.WorkDir,
                    "checkout",
                    "-B",
                    request.WorkBranch,
                    $"origin/{request.WorkBranch}");

                build = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["sh", "-c", BuildScript],
                    WorkingDirectory = SandboxConventions.WorkDir,
                }, buildCt);
            }
            catch (OperationCanceledException) when (buildTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return RequiredBuildVerificationResult.Failed(
                    BuildTimeoutExitCode,
                    BuildTimeoutExceededOutput());
            }

            var rawOutput = CombinedOutput(build);
            var redactedOutput = TruncateOutput(rawOutput);

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
        catch (SandboxProvisioningDeferredException)
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
                await _gitHost.DisposeIsolatedRepositoryCloneAsync(
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
            return await _gitHost.CreateIsolatedRepositoryCloneAsync(repositoryId, workItemId, ct);
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
        // The audit-tool sandbox target is pre-resolved by the orchestrator
        // and arrives via the request's SandboxPolicy. This verifier never
        // sees the full Project aggregate. Audit-tool sandboxes are always
        // headless for required-build verification.
        var net = new SandboxNetworkPolicy
        {
            AllowedHosts = [],
            HostGitEndpoint = access.Network.HostGitEndpoint,
            ProfileName = request.SandboxPolicy.NetworkProfile,
        };

        return SandboxConventions.WithTimingEnvironment(new SandboxSpec
        {
            ImageReference = _pipelineOptions.SandboxImageReference,
            Mounts =
            [
                .. access.Mounts,
                new SandboxMount { SandboxPath = SandboxConventions.WorkDir, Tmpfs = true },
            ],
            Environment = new Dictionary<string, string>(),
            Network = net,
            Flavor = SandboxProfileFlavor.Headless,
            WorkingDirectory = SandboxConventions.WorkDir,
            TimingWorkItemId = request.WorkItemId,
            TimingPhase = request.Phase,
            BaselineImageRef = request.SandboxPolicy.BaselineImageRef,
        });
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

    private string BuildTimeoutExceededOutput() =>
        $"build exceeded the required-build verification timeout of {_pipelineOptions.RequiredBuildVerificationTimeout.TotalMinutes:0.##} minutes";

    private sealed record DotnetBuildMarkerInspection(
        RequiredBuildProbeStatus Status,
        bool WorkBranchHasMarkers,
        bool BaseBranchHasMarkers,
        IReadOnlyList<string> MissingRequiredMarkers,
        string? BaseBranch = null,
        string? Reason = null)
    {
        public static DotnetBuildMarkerInspection NotApplicable() =>
            new(RequiredBuildProbeStatus.NotApplicable, false, false, Array.Empty<string>());

        public static DotnetBuildMarkerInspection Applies(
            bool workBranchHasMarkers,
            bool baseBranchHasMarkers,
            IReadOnlyList<string> missingRequiredMarkers,
            string? baseBranch = null,
            string? reason = null) =>
            new(
                RequiredBuildProbeStatus.Applies,
                workBranchHasMarkers,
                baseBranchHasMarkers,
                missingRequiredMarkers,
                baseBranch,
                reason);

        public static DotnetBuildMarkerInspection Unavailable(string reason) =>
            new(RequiredBuildProbeStatus.Unavailable, false, false, Array.Empty<string>(), Reason: reason);

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
        if (!TrySplitMarkerSegments(path, out var segments))
            return false;

        var fileName = segments[^1];
        return fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Required" base markers are the ones whose deletion from the work branch
    /// would silently narrow the build gate. Always required: any
    /// <c>.sln</c>/<c>.slnx</c> solution file (at any depth) and any test
    /// project (filename prefixed with <c>test</c>, or any <c>.csproj</c> under
    /// a <c>test</c>/<c>tests</c> directory). Additionally required when the
    /// base carries no solution file: every base <c>.csproj</c>, because the
    /// build script then falls back to building each one and dropping any
    /// production project would silently narrow the build surface. With a
    /// solution present, non-test projects are protected by the .sln itself
    /// (a referenced project that goes missing fails the solution build).
    /// </summary>
    private static bool IsRequiredBaseMarkerPath(string path, bool baseHasSolution)
    {
        if (!TrySplitMarkerSegments(path, out var segments))
            return false;

        var fileName = segments[^1];
        var lowerFileName = fileName.ToLowerInvariant();
        if (lowerFileName.EndsWith(".sln", StringComparison.Ordinal)
            || lowerFileName.EndsWith(".slnx", StringComparison.Ordinal))
        {
            return true;
        }

        if (!lowerFileName.EndsWith(".csproj", StringComparison.Ordinal))
            return false;

        if (!baseHasSolution)
            return true;

        if (lowerFileName.StartsWith("test", StringComparison.Ordinal))
            return true;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("tests", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("test", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSolutionPath(string path)
    {
        if (!TrySplitMarkerSegments(path, out var segments))
            return false;

        var fileName = segments[^1];
        return fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplitMarkerSegments(string path, out string[] segments)
    {
        segments = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var parts = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        if (parts.Any(static s =>
                s.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || s.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || s.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || s.Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        segments = parts;
        return true;
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
