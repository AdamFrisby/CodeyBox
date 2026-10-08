using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CodeyBox.Build.MSBuild;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Opted-in MSBuild binary-log capture inside the existing isolated required-build
/// execution path. No second build is ever executed for diagnostics: the same
/// sandbox session that ran the build carries the <c>-bl</c> capture, and the
/// bytes are parsed locally in this process through the capability-based
/// <see cref="IBuildDiagnosticsProducer"/> seam.
///
/// The enrichment never alters the authoritative build outcome. Every capture,
/// transfer, parse, or binding problem yields explicit
/// <see cref="BuildDiagnosticsStatus.InsufficientDiagnostics"/> evidence while
/// the Failed result stands; caller cancellation still propagates.
/// </summary>
internal static class MSBuildDiagnosticsEnrichment
{
    internal const string BinlogDirectoryName = "codeybox-msbuild-binlogs";
    internal const string BinlogConfigVariable = "CODEYBOX_MSBUILD_CONFIG";

    private const int BinlogFileNameMaxIndex = 9999;

    private static readonly Regex BinlogFileNamePattern = new(
        "^target-([0-9]{1,4})\\.binlog$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CommitShaPattern = new(
        "^[0-9a-f]{40}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Capture variant of SandboxRequiredBuildVerifier.BuildScript: identical
    // target discovery, but each `dotnet build` also writes a least-data
    // binary log (ProjectImports=None: no embedded imported-project content)
    // into a fixed directory this file's fetch step knows. Kept as a separate
    // script so the default path stays byte-identical when enrichment is off.
    // The build configuration arrives via CODEYBOX_MSBUILD_CONFIG (validated
    // operator config, re-checked by the case guard below); binlog file names
    // are index-derived, never taken from branch-controlled paths.
    internal static readonly string BuildScriptWithBinlogCapture = """
        set -eu
        dotnet_command_not_found_exit=127
        no_required_build_target_exit=125

        if ! command -v dotnet >/dev/null 2>&1; then
          echo "dotnet is not available in the sandbox PATH" >&2
          exit "$dotnet_command_not_found_exit"
        fi

        tmp_root="${TMPDIR:-/tmp}"
        targets_file="$tmp_root/codeybox-required-build-targets-$$"
        binlog_dir="$tmp_root/codeybox-msbuild-binlogs"

        msbuild_config="${CODEYBOX_MSBUILD_CONFIG:-Debug}"
        case "$msbuild_config" in
          ''|*[!A-Za-z0-9_.-]*)
            echo "invalid CODEYBOX_MSBUILD_CONFIG value" >&2
            exit "$no_required_build_target_exit"
            ;;
        esac

        cleanup() { rm -f "$targets_file"; }
        trap cleanup EXIT INT TERM

        mkdir -p "$binlog_dir"
        # Purge logs from any earlier attempt in this sandbox so a failed build
        # can never attach a stale binlog from a previous run.
        rm -f "$binlog_dir"/target-*.binlog

        find . -maxdepth 1 -type f \( -name '*.slnx' -o -name '*.sln' \) | sort > "$targets_file"

        if [ ! -s "$targets_file" ]; then
          find . \( -type d \( -name '.git' -o -name 'bin' -o -name 'obj' -o -name 'node_modules' \) -prune \) -o \( -type f \( -name '*.slnx' -o -name '*.sln' \) -print \) | sort > "$targets_file"
        fi

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

        idx=0
        while IFS= read -r target; do
          [ -n "$target" ] || continue
          idx=$((idx+1))
          echo "CodeyBox required build: dotnet build $target"
          dotnet build "$target" --disable-build-servers --maxcpucount:1 -c "$msbuild_config" -bl:"$binlog_dir/target-$idx.binlog;ProjectImports=None"
        done < "$targets_file"
        """;

    /// <summary>
    /// Reads the live options once per verification (hot-reloadable, no
    /// restart). Fail-safe: if options cannot be read, capture stays off and
    /// the build runs exactly as it does today.
    /// </summary>
    internal static MSBuildDiagnosticsOptions ReadOptions(Func<MSBuildDiagnosticsOptions>? optionsAccessor)
    {
        try
        {
            return optionsAccessor?.Invoke() ?? new MSBuildDiagnosticsOptions();
        }
        catch (Exception)
        {
            return new MSBuildDiagnosticsOptions();
        }
    }

    /// <summary>
    /// Captures the binlogs written by the failed build in this sandbox
    /// session, parses them locally, and returns attempt-bound evidence.
    /// Returns null when enrichment is disabled; otherwise always returns
    /// evidence (Enriched or explicit InsufficientDiagnostics). Only caller
    /// cancellation propagates as an exception. The options are the single
    /// per-verification read taken by the caller, so the capture script,
    /// fetch caps, and evidence binding all agree even if the operator
    /// reloads configuration mid-build.
    /// </summary>
    internal static async Task<BuildDiagnosticsEvidence?> EnrichFailedBuildAsync(
        ISandbox sandbox,
        string workBranch,
        MSBuildDiagnosticsOptions? options,
        IBuildDiagnosticsProducer? producer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(workBranch);

        options ??= new MSBuildDiagnosticsOptions();
        if (!options.Enabled || producer is null || !producer.IsEnabled)
            return null;

        var attempt = Guid.NewGuid().ToString("N");
        BuildDiagnosticsSourceBinding FallbackBinding() => new()
        {
            SourceRef = workBranch,
            Configuration = options.BuildConfiguration,
            Attempt = attempt,
        };

        if (!MSBuildDiagnosticsOptions.IsValidConfiguration(options.BuildConfiguration))
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId,
                FallbackBinding(),
                "invalid-configuration: the configured build configuration is not an allowlisted value");

        try
        {
            var commitSha = await ReadCommitShaAsync(sandbox, ct).ConfigureAwait(false);
            var binding = new BuildDiagnosticsSourceBinding
            {
                SourceRef = commitSha ?? workBranch,
                Configuration = options.BuildConfiguration,
                Attempt = attempt,
            };

            var names = await ListBinlogNamesAsync(sandbox, options, ct).ConfigureAwait(false);
            if (names.Count == 0)
                return BuildDiagnosticsEvidence.Insufficient(
                    MSBuildDiagnosticsOptions.ProviderId,
                    binding,
                    "missing: the failed build wrote no binary log");

            var parts = new List<BuildDiagnosticsEvidence>(names.Count);
            foreach (var name in names)
            {
                var part = await FetchAndProduceAsync(sandbox, name, binding, options, producer, ct)
                    .ConfigureAwait(false);
                if (part is not null)
                    parts.Add(part);
            }

            var merged = MSBuildEvidenceMerger.Merge(parts, binding, options);
            if (!merged.SourceBinding.Matches(binding))
                return BuildDiagnosticsEvidence.Insufficient(
                    MSBuildDiagnosticsOptions.ProviderId,
                    binding,
                    "source-mismatch: merged evidence is not bound to this source/configuration/attempt");
            return merged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Justification: enrichment is best-effort. Any capture/transfer
            // failure (including sandbox transport loss after the build
            // already failed) must not escalate into infrastructure handling
            // or mask the definitive Failed outcome, so it becomes explicit
            // insufficient-diagnostics. Redacted and single-lined.
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId,
                FallbackBinding(),
                $"capture-failure: {SingleLine(RawOutputRedactor.Redact(ex.GetType().Name + ": " + ex.Message))}");
        }
    }

    private static async Task<string?> ReadCommitShaAsync(ISandbox sandbox, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["git", "rev-parse", "HEAD"],
            WorkingDirectory = SandboxConventions.WorkDir,
            MaxStdoutBytes = 256,
            MaxStderrBytes = 1024,
        }, ct).ConfigureAwait(false);
        if (!result.Success || result.ExecutionUnavailable)
            return null;
        var sha = result.Stdout.Trim();
        return CommitShaPattern.IsMatch(sha) ? sha : null;
    }

    private static async Task<IReadOnlyList<string>> ListBinlogNamesAsync(
        ISandbox sandbox,
        MSBuildDiagnosticsOptions options,
        CancellationToken ct)
    {
        // Fixed directory (no PID interpolation: each exec is a fresh shell
        // with its own $$). Structured argv only; the listing is filtered by
        // an exact file-name allowlist below.
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [
                "sh", "-c",
                "ls -1 -- \"$1\" 2>/dev/null",
                "codeybox-binlog-list",
                "${TMPDIR:-/tmp}/" + BinlogDirectoryName,
            ],
            WorkingDirectory = SandboxConventions.WorkDir,
            MaxStdoutBytes = 4096,
            MaxStderrBytes = 1024,
        }, ct).ConfigureAwait(false);
        if (!result.Success || result.ExecutionUnavailable)
            return [];

        var names = new List<string>();
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = line.Trim();
            var match = BinlogFileNamePattern.Match(name);
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, out var index)
                || index < 1
                || index > BinlogFileNameMaxIndex)
                continue;
            names.Add(name);
            if (names.Count >= options.MaxBinlogsPerBuild) break;
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<BuildDiagnosticsEvidence?> FetchAndProduceAsync(
        ISandbox sandbox,
        string fileName,
        BuildDiagnosticsSourceBinding binding,
        MSBuildDiagnosticsOptions options,
        IBuildDiagnosticsProducer producer,
        CancellationToken ct)
    {
        // One bounded exec prints "<byte-size>\n<base64>". The file name was
        // allowlisted by ListBinlogNamesAsync and travels as "$1" (never
        // interpolated), so branch-controlled content cannot reach a shell.
        var fetchCap = checked((int)Math.Min(
            options.MaxBinlogBytes / 3 * 4 + 128,
            int.MaxValue / 2));
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = [
                "sh", "-c",
                "size=$(stat -c %s -- \"$1\" 2>/dev/null) || exit 3; printf '%s\\n' \"$size\"; base64 -w0 -- \"$1\"",
                "codeybox-binlog-fetch",
                "${TMPDIR:-/tmp}/" + BinlogDirectoryName + "/" + fileName,
            ],
            WorkingDirectory = SandboxConventions.WorkDir,
            MaxStdoutBytes = fetchCap,
            MaxStderrBytes = 1024,
        }, ct).ConfigureAwait(false);
        if (!result.Success || result.ExecutionUnavailable || result.StdoutLimitExceeded)
            return null;

        var newline = result.Stdout.IndexOf('\n');
        if (newline <= 0) return null;
        if (!long.TryParse(result.Stdout.AsSpan(0, newline).Trim(), out var size)
            || size <= 0
            || size > options.MaxBinlogBytes)
            return null;

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(result.Stdout[(newline + 1)..].Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        if (payload.Length == 0 || payload.Length > options.MaxBinlogBytes)
            return null;

        var evidence = await producer.ProduceAsync(new BuildDiagnosticsProductionRequest
        {
            ExpectedBinding = binding,
            PayloadFormat = MSBuildDiagnosticsOptions.PayloadFormat,
            PayloadBytes = payload,
        }, ct).ConfigureAwait(false);

        using var sha = SHA256.Create();
        return evidence with
        {
            ArtifactRefs =
            [
                new BuildDiagnosticsArtifactRef
                {
                    Name = fileName,
                    ContentHashSha256 = Convert.ToHexString(sha.ComputeHash(payload)).ToLowerInvariant(),
                    ByteSize = payload.Length,
                },
            ],
        };
    }

    private static string SingleLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
