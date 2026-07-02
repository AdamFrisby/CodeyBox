using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Review-agent sandbox profile that prevents redundant dotnet build/test
/// runs after the deterministic gate has already produced the trusted signal.
/// Tool auditors, including build/test gates, must not use this profile.
/// </summary>
internal sealed class AuditReviewDotnetShim
{
    public const string Directory = "/codeybox/bin";
    public const string Path = Directory + "/dotnet";
    public const string Notice =
        "build and tests already executed by the deterministic gate before this review; skipped to avoid slow redundant re-runs";

    private const long TmpfsBytes = 64 * 1024;
    private const string DefaultSandboxPath =
        "/codeybox/bin:/home/ubuntu/.local/bin:/home/ubuntu/.dotnet/tools:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin";

    // The absolute locations the multipass baseline may install the real dotnet
    // at. The hardening script moves any of these aside and drops the shim in
    // their place so an auditor invoking dotnet via an absolute path (rather
    // than resolving it through PATH) is still intercepted.
    private const string DefaultAbsoluteDotnetCandidates =
        "/usr/bin/dotnet /usr/local/bin/dotnet /snap/bin/dotnet /usr/share/dotnet/dotnet";

    internal static readonly string ShimScript = $$"""
        #!/bin/sh
        if [ "${1:-}" = "build" ] || [ "${1:-}" = "test" ]; then
            printf '%s\n' "{{Notice}}"
            exit 0
        fi

        real_sibling="${0}.codeybox-real"
        if [ -x "$real_sibling" ]; then
            exec "$real_sibling" "$@"
        fi

        shim_dir=$(CDPATH= cd -- "$(dirname -- "$0")" 2>/dev/null && pwd)
        shim_path="$shim_dir/$(basename -- "$0")"
        old_ifs=$IFS
        IFS=:
        for path_entry in ${PATH:-}; do
            [ -n "$path_entry" ] || path_entry=.
            [ "$path_entry" = "$shim_dir" ] && continue
            candidate="$path_entry/dotnet"
            [ "$candidate" = "$shim_path" ] && continue
            [ -x "$candidate" ] || continue
            IFS=$old_ifs
            exec "$candidate" "$@"
        done
        IFS=$old_ifs

        echo "dotnet passthrough target not found" >&2
        exit 127
        """;

    private static readonly string PrivilegedHardeningScript =
        BuildPrivilegedHardeningScript(Path, Directory, DefaultAbsoluteDotnetCandidates);

    // Extracted as a builder so the paths (shim location, shim directory, and
    // the absolute dotnet candidate list) are injectable. Production always
    // calls it with the compiled-in constants; the behavioral test drives it
    // against a throwaway fixture tree so the load-bearing absolute-path
    // hardening (move real dotnet aside, drop the shim in its place) is
    // exercised without needing root or the /codeybox/bin mount.
    internal static string BuildPrivilegedHardeningScript(
        string shimPath, string shimDirectory, string absoluteCandidates) => $$"""
        shim={{shimPath}}

        case "${CODEYBOX_AUDIT_DOTNET_SHIM_HARDEN_ABSOLUTE:-}" in
            1|true|TRUE|yes|YES) ;;
            *) exit 0 ;;
        esac

        [ -x "$shim" ] || exit 0

        if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
            sudo_prefix=sudo
        else
            sudo_prefix=
        fi

        if [ -n "$sudo_prefix" ]; then
            $sudo_prefix chown root:root "$shim" "$(dirname -- "$shim")" 2>/dev/null || true
            $sudo_prefix chmod 0755 "$shim" 2>/dev/null || true
            $sudo_prefix chmod 0555 "$(dirname -- "$shim")" 2>/dev/null || true
        else
            chmod 0755 "$shim" 2>/dev/null || true
        fi

        candidates="${1:-} $(command -v dotnet 2>/dev/null || true)"
        candidates="$candidates {{absoluteCandidates}}"
        for target in $candidates; do
            [ -n "$target" ] || continue
            [ "$target" = "$shim" ] && continue
            case "$target" in
                {{shimDirectory}}/*) continue ;;
            esac
            [ -e "$target" ] || continue
            [ ! -d "$target" ] || continue

            real="${target}.codeybox-real"
            if [ -x "$real" ]; then
                if [ -n "$sudo_prefix" ]; then
                    $sudo_prefix cp "$shim" "$target" 2>/dev/null || true
                    $sudo_prefix chmod 0755 "$target" 2>/dev/null || true
                else
                    cp "$shim" "$target" 2>/dev/null || true
                    chmod 0755 "$target" 2>/dev/null || true
                fi
                continue
            fi

            if [ -n "$sudo_prefix" ]; then
                $sudo_prefix sh -c '
                    target=$1
                    real=$2
                    shim=$3
                    [ -e "$target" ] || exit 0
                    [ ! -d "$target" ] || exit 0
                    mv "$target" "$real"
                    cp "$shim" "$target"
                    chmod 0755 "$target"
                ' sh "$target" "$real" "$shim" 2>/dev/null || true
            else
                mv "$target" "$real" 2>/dev/null && \
                    cp "$shim" "$target" 2>/dev/null && \
                    chmod 0755 "$target" 2>/dev/null || true
            fi
        done
        """;

    private AuditReviewDotnetShim(bool enabled, bool hardenAbsolutePaths)
    {
        Enabled = enabled;
        HardenAbsolutePaths = hardenAbsolutePaths;
    }

    public bool Enabled { get; }
    private bool HardenAbsolutePaths { get; }

    public static AuditReviewDotnetShim From(PipelineTuningOptions tuning, string sandboxProviderName)
    {
        var hardenAbsolutePaths = string.Equals(sandboxProviderName, "multipass", StringComparison.Ordinal)
                                  || string.Equals(sandboxProviderName, "multipass-remote", StringComparison.Ordinal);
        return new AuditReviewDotnetShim(
            tuning.BlockRedundantDotnetBuildTestInAuditSandbox,
            hardenAbsolutePaths);
    }

    public SandboxSpec Apply(SandboxSpec spec)
    {
        if (!Enabled)
            return spec;

        var environment = new Dictionary<string, string>(spec.Environment, StringComparer.Ordinal);
        var path = environment.TryGetValue("PATH", out var existingPath) && !string.IsNullOrWhiteSpace(existingPath)
            ? PrependShimDirectory(existingPath)
            : DefaultSandboxPath;
        environment["PATH"] = path;
        if (HardenAbsolutePaths)
            environment["CODEYBOX_AUDIT_DOTNET_SHIM_HARDEN_ABSOLUTE"] = "1";

        var mounts = spec.Mounts.Any(m => string.Equals(m.SandboxPath.TrimEnd('/'), Directory, StringComparison.Ordinal))
            ? spec.Mounts
            :
            [
                .. spec.Mounts,
                new SandboxMount
                {
                    SandboxPath = Directory,
                    Tmpfs = true,
                    SizeBytes = TmpfsBytes,
                },
            ];

        return spec with
        {
            Environment = environment,
            Mounts = mounts,
        };
    }

    public async Task InstallAsync(ISandbox sandbox, CancellationToken ct)
    {
        if (!Enabled)
            return;

        var resolvedDotnetPath = await TryResolveDotnetPathAsync(sandbox, ct).ConfigureAwait(false);

        await RunOrThrowAsync(sandbox, ct, "mkdir", "-p", Directory).ConfigureAwait(false);
        var write = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$1\" && chmod 0755 \"$1\"", "sh", Path],
            Stdin = ShimScript,
        }, ct).ConfigureAwait(false);
        if (!write.Success)
        {
            throw new InvalidOperationException(
                $"Failed to install audit dotnet shim at {Path}: {write.Stderr}{write.Stdout}");
        }

        var harden = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-s", "--", resolvedDotnetPath ?? ""],
            Stdin = PrivilegedHardeningScript,
        }, ct).ConfigureAwait(false);
        if (!harden.Success)
        {
            throw new InvalidOperationException(
                $"Failed to harden audit dotnet shim at {Path}: {harden.Stderr}{harden.Stdout}");
        }
    }

    private static string PrependShimDirectory(string existingPath)
    {
        var entries = existingPath
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(static p => !string.Equals(p, Directory, StringComparison.Ordinal));
        return string.Join(':', [Directory, .. entries]);
    }

    private static async Task<string?> TryResolveDotnetPathAsync(ISandbox sandbox, CancellationToken ct)
    {
        var result = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "command -v dotnet 2>/dev/null || true"],
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            return null;

        var firstLine = result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return firstLine is not null && firstLine.StartsWith("/", StringComparison.Ordinal)
            ? firstLine
            : null;
    }

    private static async Task RunOrThrowAsync(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"command failed (exit {result.ExitCode}): {string.Join(' ', argv)}\n{result.Stderr}");
    }
}
