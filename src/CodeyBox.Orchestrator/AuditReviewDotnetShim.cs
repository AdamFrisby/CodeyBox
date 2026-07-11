using System.Text;
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
    internal const string PrivilegedHardeningEnvironmentVariable =
        "CODEYBOX_AUDIT_DOTNET_SHIM_HARDEN_ABSOLUTE";
    private const string DefaultSandboxPath =
        "/codeybox/bin:/home/ubuntu/.local/bin:/home/ubuntu/.dotnet/tools:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin";

    // Absolute locations a VM baseline may use for the real dotnet executable.
    // The hardening script moves any of these aside and drops the shim in their
    // place so an auditor invoking dotnet via an absolute path (rather than
    // resolving it through PATH) is still intercepted.
    private const int MaximumGuestExecutablePathUtf8Bytes = 4096;
    private const int MaximumAbsoluteDotnetCandidates = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly IReadOnlyList<string> DefaultAbsoluteDotnetCandidates =
        Array.AsReadOnly(
        [
            "/usr/bin/dotnet",
            "/usr/local/bin/dotnet",
            "/snap/bin/dotnet",
            "/usr/share/dotnet/dotnet",
        ]);

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

        # A moved-aside executable may be a symlink whose canonical target was
        # hardened separately. Follow that link once so passthrough reaches the
        # canonical target's preserved real sibling instead of recursing into
        # the shim through the moved symlink.
        resolved_self=$(readlink -f -- "$0" 2>/dev/null || true)
        if [ -n "$resolved_self" ] && [ "$resolved_self" != "$0" ]; then
            resolved_real_sibling="${resolved_self}.codeybox-real"
            if [ -x "$resolved_real_sibling" ]; then
                exec "$resolved_real_sibling" "$@"
            fi
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
        string shimPath,
        string shimDirectory,
        IReadOnlyList<string> absoluteCandidates)
    {
        ValidateCanonicalAbsoluteGuestPath(shimPath, nameof(shimPath));
        ValidateCanonicalAbsoluteGuestPath(shimDirectory, nameof(shimDirectory));
        if (!shimPath.StartsWith(shimDirectory + "/", StringComparison.Ordinal))
            throw new ArgumentException("The shim path must be contained by the shim directory.", nameof(shimPath));
        ArgumentNullException.ThrowIfNull(absoluteCandidates);
        if (absoluteCandidates.Count > MaximumAbsoluteDotnetCandidates)
        {
            throw new ArgumentException(
                $"No more than {MaximumAbsoluteDotnetCandidates} absolute dotnet candidates are allowed.",
                nameof(absoluteCandidates));
        }
        var staticCandidateCommands = new StringBuilder();
        for (var index = 0; index < absoluteCandidates.Count; index++)
        {
            var candidate = absoluteCandidates[index];
            ValidateCanonicalAbsoluteGuestPath(candidate, nameof(absoluteCandidates));
            staticCandidateCommands
                .Append("harden_target ")
                .Append(QuoteShellLiteral(candidate))
                .Append('\n');
        }

        return $$"""
        set -u

        shim={{QuoteShellLiteral(shimPath)}}
        shim_dir={{QuoteShellLiteral(shimDirectory)}}

        fail_hardening() {
            printf 'codeybox audit dotnet hardening failed: %s\n' "$1" >&2
            exit 74
        }

        run_privileged() {
            if [ -n "$sudo_prefix" ]; then
                sudo -n -- "$@"
            else
                "$@"
            fi
        }

        verify_hardened_target() {
            target=$1
            real=$2
            [ -x "$real" ] && [ -x "$target" ] && cmp -s -- "$shim" "$target"
        }

        harden_target() {
            target=$1
            case "$target" in
                /*) ;;
                *) fail_hardening "refusing non-absolute target" ;;
            esac
            case "$target" in
                "$shim_dir"/*) return 0 ;;
            esac
            [ -x "$target" ] || return 0
            [ ! -d "$target" ] || return 0

            real="${target}.codeybox-real"
            if [ -e "$real" ] || [ -L "$real" ]; then
                [ -x "$real" ] \
                    || fail_hardening "existing passthrough for $target is not executable"
                replacement_failed=0
                run_privileged chmod 0755 "$target" || replacement_failed=1
                if [ "$replacement_failed" -eq 0 ]; then
                    run_privileged cp -- "$shim" "$target" || replacement_failed=1
                fi
                if [ "$replacement_failed" -eq 0 ]; then
                    run_privileged chmod 0555 "$target" || replacement_failed=1
                fi
                if [ "$replacement_failed" -eq 0 ] \
                    && verify_hardened_target "$target" "$real"; then
                    return 0
                fi
                if ! run_privileged cp -p -- "$real" "$target" || [ ! -x "$target" ]; then
                    fail_hardening "replacement and rollback both failed for $target"
                fi
                fail_hardening "replacement failed for $target; the original was restored"
            fi

            run_privileged mv -- "$target" "$real" \
                || fail_hardening "could not preserve the original $target"
            replacement_failed=0
            run_privileged cp -- "$shim" "$target" || replacement_failed=1
            if [ "$replacement_failed" -eq 0 ]; then
                run_privileged chmod 0555 "$target" || replacement_failed=1
            fi
            if [ "$replacement_failed" -eq 0 ] \
                && verify_hardened_target "$target" "$real"; then
                return 0
            fi

            run_privileged rm -f -- "$target" || replacement_failed=1
            if run_privileged mv -- "$real" "$target" && [ -x "$target" ]; then
                fail_hardening "replacement failed for $target; the original was restored"
            else
                fail_hardening "replacement and rollback both failed for $target"
            fi
        }

        case "${{{PrivilegedHardeningEnvironmentVariable}}:-}" in
            1|true|TRUE|yes|YES) ;;
            *) exit 0 ;;
        esac

        [ -x "$shim" ] || fail_hardening "shim is missing or is not executable"

        if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
            sudo_prefix=sudo
        else
            sudo_prefix=
        fi

        if [ -n "$sudo_prefix" ]; then
            run_privileged chown root:root "$shim" "$shim_dir" \
                || fail_hardening "could not assign the shim to root"
        fi
        run_privileged chmod 0555 "$shim" \
            || fail_hardening "could not make the shim read-only and executable"
        run_privileged chmod 0555 "$shim_dir" \
            || fail_hardening "could not make the shim directory read-only"

        if [ -n "${1:-}" ]; then
            harden_target "$1"
        fi
        {{staticCandidateCommands.ToString().TrimEnd()}}
        """;
    }

    private static string QuoteShellLiteral(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void ValidateCanonicalAbsoluteGuestPath(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is < 2 or > MaximumGuestExecutablePathUtf8Bytes
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.EndsWith("/", StringComparison.Ordinal)
            || value.Contains("//", StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Guest executable paths must be bounded canonical absolute paths.", parameterName);
        }
        try
        {
            if (StrictUtf8.GetByteCount(value) > MaximumGuestExecutablePathUtf8Bytes)
                throw new ArgumentException("Guest executable path exceeds the UTF-8 size bound.", parameterName);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Guest executable path is not valid Unicode.", parameterName, ex);
        }
        foreach (var segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
                throw new ArgumentException("Guest executable paths cannot contain traversal segments.", parameterName);
        }
    }

    private AuditReviewDotnetShim(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }

    public static AuditReviewDotnetShim From(PipelineTuningOptions tuning) =>
        new(tuning.BlockRedundantDotnetBuildTestInAuditSandbox);

    public SandboxSpec Apply(SandboxSpec spec)
    {
        if (!Enabled)
            return spec;

        var environment = new Dictionary<string, string>(spec.Environment, StringComparer.Ordinal);
        var path = environment.TryGetValue("PATH", out var existingPath) && !string.IsNullOrWhiteSpace(existingPath)
            ? PrependShimDirectory(existingPath)
            : DefaultSandboxPath;
        environment["PATH"] = path;

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

        var supportsPrivilegedHardening =
            SandboxCapability.Find<IPrivilegedGuestFileHardeningSandbox>(sandbox) is not null;
        var resolvedDotnetPath = supportsPrivilegedHardening
            ? await TryResolveDotnetPathAsync(sandbox, ct).ConfigureAwait(false)
            : null;

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

        if (!supportsPrivilegedHardening)
            return;

        var harden = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-s", "--", resolvedDotnetPath ?? ""],
            ExtraEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PrivilegedHardeningEnvironmentVariable] = "1",
            },
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
            MaxStdoutBytes = MaximumGuestExecutablePathUtf8Bytes,
            MaxStderrBytes = MaximumGuestExecutablePathUtf8Bytes,
        }, ct).ConfigureAwait(false);
        if (!result.Success)
            return null;
        if (result.StdoutLimitExceeded || result.StderrLimitExceeded)
            throw new InvalidOperationException("Resolving the guest dotnet path exceeded its bounded output allowance.");

        var firstLine = result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (firstLine is null)
            return null;
        try
        {
            ValidateCanonicalAbsoluteGuestPath(firstLine, nameof(result));
            return firstLine;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("The guest returned a non-canonical dotnet executable path.", ex);
        }
    }

    private static async Task RunOrThrowAsync(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var result = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                $"command failed (exit {result.ExitCode}): {string.Join(' ', argv)}\n{result.Stderr}");
    }
}
