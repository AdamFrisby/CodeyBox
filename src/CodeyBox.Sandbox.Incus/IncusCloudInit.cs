using System.Text;
using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

internal static class IncusCloudInit
{
    internal const string ExecWrapperPath = "/usr/local/bin/codeybox-incus-exec";
    internal const string RuntimeDirectory = "/run/codeybox";
    internal const string DotnetCliHomeEnvironmentVariable = "DOTNET_CLI_HOME";
    internal const string DotnetCliHome = RuntimeDirectory + "/dotnet-cli-home";
    internal const string ControlDirectory = "/run/codeybox-control";
    internal const string PeakRamPath = "/run/codeybox-peak-ram-bytes";
    internal const string PeakRamSamplerPath = "/usr/local/sbin/codeybox-peak-ram-sampler";
    internal const string PeakRamServicePath = "/etc/systemd/system/codeybox-peak-ram-sampler.service";
    internal const string NonLoginPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    internal const string ExecWrapper = $$"""
        #!/bin/bash
        set -uo pipefail
        if [ "$#" -lt 8 ]; then
            echo "codeybox-incus-exec: invalid invocation" >&2
            exit 64
        fi
        env_file=$1
        pid_file=$2
        completion_file=$3
        working_directory=$4
        guest_home=$5
        guest_uid=$6
        guest_gid=$7
        shift 7
        case "$guest_uid:$guest_gid" in
          *[!0-9:]*|0:*|*:0) echo "codeybox-incus-exec: invalid guest identity" >&2; exit 64 ;;
        esac
        if [ ! -r "$env_file" ]; then
            echo "codeybox-incus-exec: environment file is unavailable" >&2
            exit 74
        fi
        environment=(
          "HOME=$guest_home"
          "{{DotnetCliHomeEnvironmentVariable}}={{DotnetCliHome}}"
          "PATH={{NonLoginPath}}"
          "LANG=C.UTF-8"
        )
        while IFS= read -r -d '' entry; do
          environment+=("$entry")
        done < "$env_file"
        rm -f -- "$env_file" || {
          echo "codeybox-incus-exec: environment cleanup failed" >&2
          exit 74
        }
        cd -- "$working_directory" || exit 72
        umask 077
        setsid -- setpriv \
          --no-new-privs \
          --reuid="$guest_uid" \
          --regid="$guest_gid" \
          --clear-groups \
          -- env -i -- "${environment[@]}" "$@" <&0 &
        child=$!
        printf '%s\n' "$child" > "$pid_file" || {
          kill -TERM -- "-$child" 2>/dev/null || true
          exit 73
        }
        wait "$child"
        result=$?
        rm -f -- "$pid_file" || exit 73
        printf '%s\n' "$result" > "$completion_file" || exit 73
        exit "$result"
        """;

    private const string PeakRamService = """
        [Unit]
        Description=CodeyBox peak RAM sampler
        After=multi-user.target

        [Service]
        Type=simple
        ExecStart=/usr/local/sbin/codeybox-peak-ram-sampler
        Restart=always
        RestartSec=1

        [Install]
        WantedBy=multi-user.target
        """;

    internal static string Build(IncusSandboxOptions options, SandboxProfileFlavor flavor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateExtraFragment(options.ExtraCloudInit);
        if (flavor == SandboxProfileFlavor.Graphical)
        {
            // Graphical service provisioning is deliberately not inferred. Operators may
            // install their chosen desktop stack through the independent Incus runcmd.
        }

        var wrapperBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ExecWrapper));
        var sampler = BuildPeakRamSampler(options.ResourceMetricsSampleInterval);
        var samplerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sampler));
        var samplerServiceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(PeakRamService));
        var result = new StringBuilder();
        result.AppendLine("#cloud-config");
        result.AppendLine("write_files:");
        result.AppendLine($"  - path: {ExecWrapperPath}");
        result.AppendLine("    owner: root:root");
        result.AppendLine("    permissions: '0755'");
        result.AppendLine("    encoding: b64");
        result.Append("    content: ").AppendLine(wrapperBase64);
        if (options.CaptureResourceMetrics)
        {
            result.Append("  - path: ").AppendLine(PeakRamSamplerPath);
            result.AppendLine("    owner: root:root");
            result.AppendLine("    permissions: '0755'");
            result.AppendLine("    encoding: b64");
            result.Append("    content: ").AppendLine(samplerBase64);
            result.Append("  - path: ").AppendLine(PeakRamServicePath);
            result.AppendLine("    owner: root:root");
            result.AppendLine("    permissions: '0644'");
            result.AppendLine("    encoding: b64");
            result.Append("    content: ").AppendLine(samplerServiceBase64);
        }
        result.AppendLine("runcmd:");
        result.Append("  - [ install, -d, -m, '0700', -o, '")
            .Append(options.GuestUserId)
            .Append("', -g, '")
            .Append(options.GuestGroupId)
            .Append("', ")
            .Append(RuntimeDirectory)
            .AppendLine(" ]");
        result.Append("  - [ install, -d, -m, '0700', -o, '")
            .Append(options.GuestUserId)
            .Append("', -g, '")
            .Append(options.GuestGroupId)
            .Append("', ")
            .Append(DotnetCliHome)
            .AppendLine(" ]");
        result.Append("  - [ install, -d, -m, '0700', -o, '0', -g, '0', ")
            .Append(ControlDirectory)
            .AppendLine(" ]");
        if (options.CaptureResourceMetrics)
            result.AppendLine("  - [ systemctl, enable, --now, codeybox-peak-ram-sampler.service ]");
        if (!string.IsNullOrWhiteSpace(options.ExtraCloudInit))
        {
            result.AppendLine();
            result.AppendLine(options.ExtraCloudInit.Trim());
        }
        return result.ToString();
    }

    private static string BuildPeakRamSampler(TimeSpan interval)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(interval.TotalSeconds));
        return $$"""
            #!/bin/sh
            set -eu
            peak=0
            while :; do
              total=$(awk '/^MemTotal:/ {print $2 * 1024; exit}' /proc/meminfo)
              available=$(awk '/^MemAvailable:/ {print $2 * 1024; exit}' /proc/meminfo)
              used=$((total - available))
              if [ "$used" -gt "$peak" ]; then
                peak=$used
                temporary="{{PeakRamPath}}.$$"
                printf '%s\n' "$peak" > "$temporary"
                mv -f "$temporary" "{{PeakRamPath}}"
              fi
              sleep {{seconds}}
            done
            """;
    }

    internal static void ValidateExtraFragment(string? fragment)
    {
        if (fragment is null || fragment.Length == 0)
            return;
        try
        {
            _ = IncusInputValidation.GetBoundedUtf8ByteCount(
                fragment,
                IncusSandboxOptions.MaximumExtraCloudInitUtf8Bytes,
                nameof(fragment),
                "ExtraCloudInit");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("ExtraCloudInit exceeds 1 MiB or is not valid Unicode.", ex);
        }
        if (string.IsNullOrWhiteSpace(fragment))
            return;
        var normalized = fragment.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Any(c => (char.IsControl(c) && c != '\n')
            || c is '\u0085' or '\u2028' or '\u2029'))
        {
            throw new InvalidOperationException(
                "ExtraCloudInit may contain only LF line endings and no alternate YAML line separators or control characters.");
        }
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "write_files",
            "runcmd",
        };
        var sawTopLevelMapping = false;
        foreach (var line in normalized.Split('\n'))
        {
            if (line.Contains('\t'))
                throw new InvalidOperationException("ExtraCloudInit cannot contain YAML tab indentation.");
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                continue;
            if (char.IsWhiteSpace(line[0]))
            {
                if (!sawTopLevelMapping)
                    throw new InvalidOperationException("ExtraCloudInit must begin with an unindented top-level mapping key.");
                continue;
            }
            var topLevel = line.Trim();
            if (topLevel.StartsWith('{') || topLevel.StartsWith('['))
                throw new InvalidOperationException("ExtraCloudInit must use block-style top-level YAML mappings.");
            if (topLevel.StartsWith('\'') || topLevel.StartsWith('"'))
                throw new InvalidOperationException("ExtraCloudInit top-level keys must be unquoted plain scalars.");
            var colon = FindUnquotedColon(topLevel);
            if (colon <= 0)
                throw new InvalidOperationException("Every ExtraCloudInit top-level entry must be a plain mapping key.");
            var key = topLevel[..colon].Trim();
            if (key.Length == 0
                || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
                throw new InvalidOperationException("ExtraCloudInit top-level keys must be untagged ASCII plain scalars.");
            if (key == "<<")
                throw new InvalidOperationException("ExtraCloudInit cannot use a top-level YAML merge key.");
            if (generatedKeys.Contains(key))
                throw new InvalidOperationException(
                    $"ExtraCloudInit cannot redefine generated top-level key '{key}'.");
            sawTopLevelMapping = true;
        }
        if (!sawTopLevelMapping)
            throw new InvalidOperationException("ExtraCloudInit must contain at least one top-level mapping key.");
    }

    internal static bool OverlapsProviderOwnedPath(string path)
    {
        string[] protectedPaths =
        [
            RuntimeDirectory,
            ControlDirectory,
            ExecWrapperPath,
            PeakRamPath,
            PeakRamSamplerPath,
            PeakRamServicePath,
        ];
        return protectedPaths.Any(protectedPath =>
            string.Equals(path, protectedPath, StringComparison.Ordinal)
            || path.StartsWith(protectedPath + "/", StringComparison.Ordinal)
            || protectedPath.StartsWith(path + "/", StringComparison.Ordinal));
    }

    private static int FindUnquotedColon(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (quote == '\0' && c is '\'' or '"')
            {
                quote = c;
                continue;
            }
            if (quote == c)
            {
                if (quote == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
                {
                    index++;
                    continue;
                }
                quote = '\0';
                continue;
            }
            if (quote == '\0' && c == ':')
                return index;
        }
        if (quote != '\0')
            throw new InvalidOperationException("ExtraCloudInit contains an unterminated quoted top-level key.");
        return -1;
    }

}
