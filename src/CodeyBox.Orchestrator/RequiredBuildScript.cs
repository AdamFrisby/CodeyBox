using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Single source of truth for the required-build POSIX shell. The default
/// build and the opted-in MSBuild binlog-capture variant are both composed
/// here from shared fragments, so target discovery and the isolated
/// build environment cannot drift between the two paths: opted-in builds
/// execute in exactly the same environment as the default path, plus the
/// least-data <c>-bl</c> capture. The capture variant only appends the
/// configuration guard, the fixed binlog directory setup, and per-target
/// log arguments — it never re-states discovery or isolation.
/// </summary>
internal static class RequiredBuildScript
{
    internal const string BinlogDirectoryName = "codeybox-msbuild-binlogs";
    internal const string BinlogConfigVariable = "CODEYBOX_MSBUILD_CONFIG";

    // POSIX shells conventionally use 127 for "command not found".
    internal const int DotnetCommandNotFoundExitCode = 127;
    // Internal script sentinel: marker inspection said this gate applies,
    // but no buildable .NET target was present after checkout.
    internal const int NoRequiredBuildTargetExitCode = 125;

    private static readonly string PrologueFragment = $$"""
        dotnet_command_not_found_exit={{DotnetCommandNotFoundExitCode}}
        no_required_build_target_exit={{NoRequiredBuildTargetExitCode}}

        if ! command -v dotnet >/dev/null 2>&1; then
          echo "dotnet is not available in the sandbox PATH" >&2
          exit "$dotnet_command_not_found_exit"
        fi

        tmp_root="${TMPDIR:-/tmp}"
        targets_file="$tmp_root/codeybox-required-build-targets-$$"
        """;

    // Capture-only head: the build configuration arrives via
    // CODEYBOX_MSBUILD_CONFIG (validated operator config, re-checked by the
    // case guard below); binlog file names are index-derived, never taken
    // from branch-controlled paths.
    private const string CaptureConfigFragment = """
        binlog_dir="$tmp_root/codeybox-msbuild-binlogs"

        msbuild_config="${CODEYBOX_MSBUILD_CONFIG:-Debug}"
        case "$msbuild_config" in
          ''|*[!A-Za-z0-9_.-]*)
            echo "invalid CODEYBOX_MSBUILD_CONFIG value" >&2
            exit "$no_required_build_target_exit"
            ;;
        esac
        """;

    private const string DotnetEnvIsolationFragment = """
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
        original_home="${HOME:-}"

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
        if [ -z "${NUGET_PACKAGES:-}" ] && [ -n "$original_home" ] && [ -d "$original_home/.nuget/packages" ]; then
          export NUGET_PACKAGES="$original_home/.nuget/packages"
        fi

        # Some NuGet builds resolve their user-config path from HOME even when
        # DOTNET_CLI_HOME is set. Point both variables at the isolated writable
        # directory, after preserving the original package-cache path above.
        export HOME="$dotnet_home"
        """;

    /// <summary>
    /// Exact target-discovery sequence shared verbatim by both script
    /// variants. Exposed so tests can assert the capture variant carries the
    /// same discovery without re-stating it.
    /// </summary>
    internal static readonly string TargetDiscoveryFragment = """
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
        """;

    private const string NuGetHealSourcingFragment = """
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
        """;

    // Capture-only setup: a fixed directory (no PID interpolation: each exec
    // is a fresh shell with its own $$, so the fetch step could never find a
    // PID-suffixed directory). Earlier attempts in this sandbox are purged so
    // a failed build can never attach a stale binlog from a previous run.
    private const string BinlogSetupFragment = """
        mkdir -p "$binlog_dir"
        # Purge logs from any earlier attempt in this sandbox so a failed build
        # can never attach a stale binlog from a previous run.
        rm -f "$binlog_dir"/target-*.binlog
        """;

    private const string PlainBuildLoopFragment = """
        while IFS= read -r target; do
          [ -n "$target" ] || continue
          echo "CodeyBox required build: dotnet build $target"
          dotnet build "$target" --disable-build-servers --maxcpucount:1
        done < "$targets_file"
        """;

    // Capture loop: identical iteration, but each `dotnet build` also writes
    // a least-data binary log (ProjectImports=None: no embedded
    // imported-project content) into the fixed directory the fetch step
    // knows. The configuration was validated by the case guard above.
    private const string CaptureBuildLoopFragment = """
        idx=0
        while IFS= read -r target; do
          [ -n "$target" ] || continue
          idx=$((idx+1))
          echo "CodeyBox required build: dotnet build $target"
          dotnet build "$target" --disable-build-servers --maxcpucount:1 -c "$msbuild_config" -bl:"$binlog_dir/target-$idx.binlog;ProjectImports=None"
        done < "$targets_file"
        """;

    /// <summary>
    /// Composes the required-build shell executed in the isolated sandbox.
    /// The default variant is the historical gate script; the capture variant
    /// adds least-data binlog logging to each build inside the same isolated
    /// execution. No second build is ever executed for diagnostics.
    /// </summary>
    /// <param name="captureBinlogs">When true, emit the capture variant.</param>
    internal static string Build(bool captureBinlogs)
    {
        var parts = new List<string>(8)
        {
            PrologueFragment,
        };
        if (captureBinlogs)
            parts.Add(CaptureConfigFragment);
        parts.Add(DotnetEnvIsolationFragment);
        parts.Add(TargetDiscoveryFragment);
        parts.Add(NuGetHealSourcingFragment);
        if (captureBinlogs)
            parts.Add(BinlogSetupFragment);
        parts.Add(captureBinlogs ? CaptureBuildLoopFragment : PlainBuildLoopFragment);

        // The NuGet-home self-heal preamble runs before any dotnet
        // invocation, exactly as in the historical gate script.
        return "set -eu\n"
            + NuGetHomeSelfHeal.Preamble + "\n"
            + string.Join("\n", parts);
    }
}
