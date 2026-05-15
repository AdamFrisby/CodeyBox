using System.Diagnostics;

namespace CodeyBox.Tests;

public sealed class HostNetworkSetupScriptTests
{
    [Fact]
    public void SetupHostNetworks_EmitsInboundBridgeChainsForEveryProfile()
    {
        var script = File.ReadAllText(SetupScriptPath());
        var inboundChain = ExtractBetween(
            script,
            "    chain cb-${name}-in {",
            "    chain cb-${name} {");

        Assert.Contains("jump cb-${name}-in", script, StringComparison.Ordinal);
        Assert.Contains("chain cb-${name}-in {", script, StringComparison.Ordinal);
        Assert.Contains("Host-originated", inboundChain, StringComparison.Ordinal);
        Assert.Contains("ct state established,related accept", inboundChain, StringComparison.Ordinal);
        Assert.Contains("        drop", inboundChain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedVncHelper_ValidatesArgsAndBuildsLoopbackProxy()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempDir("codeybox-vnc-helper-");
        var helper = Path.Combine(temp.Path, "codeybox-vnc-loopback");
        var callLog = Path.Combine(temp.Path, "socket-activate.args");
        File.WriteAllText(helper, ExtractVncHelperScript(File.ReadAllText(SetupScriptPath())));
        MakeExecutable(helper);

        var tools = Path.Combine(temp.Path, "tools");
        Directory.CreateDirectory(tools);
        WriteExecutable(Path.Combine(tools, "multipass"), """
#!/usr/bin/env bash
set -euo pipefail
if [[ "$1" == "exec" ]]; then
    if [[ "$*" == *"codeybox-vnc-password"* ]]; then
        echo "secret123"
    else
        echo "10.99.6.42"
    fi
    exit 0
fi
echo "unexpected multipass argv: $*" >&2
exit 9
""");
        WriteExecutable(Path.Combine(tools, "systemd-socket-activate"), """
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$@" > "$CALL_LOG"
""");
        WriteExecutable(Path.Combine(tools, "systemd-socket-proxyd"), """
#!/usr/bin/env bash
set -euo pipefail
exit 0
""");

        var env = new Dictionary<string, string?>
        {
            ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["CALL_LOG"] = callLog,
            ["CODEYBOX_GRAPHICAL_VNC_PORT"] = "5999",
        };

        var invalid = await RunAsync("/usr/bin/env", ["bash", helper, "gui-vm", "not-a-port"], env);
        Assert.Equal(2, invalid.ExitCode);
        Assert.Contains("invalid local port", invalid.Stderr, StringComparison.OrdinalIgnoreCase);

        var invalidRemoteEnv = new Dictionary<string, string?>(env)
        {
            ["CODEYBOX_GRAPHICAL_VNC_PORT"] = "bad-port",
        };
        var invalidRemote = await RunAsync("/usr/bin/env", ["bash", helper, "gui-vm", "5901"], invalidRemoteEnv);
        Assert.Equal(2, invalidRemote.ExitCode);
        Assert.Contains("invalid remote VNC port", invalidRemote.Stderr, StringComparison.OrdinalIgnoreCase);

        var ok = await RunAsync("/usr/bin/env", ["bash", helper, "gui-vm", "5901"], env);
        Assert.Equal(0, ok.ExitCode);
        Assert.Contains("Forwarding VNC: 127.0.0.1:5901 -> gui-vm:10.99.6.42:5999", ok.Stderr, StringComparison.Ordinal);
        Assert.Contains("VNC password: secret123", ok.Stderr, StringComparison.Ordinal);

        var socketArgs = await File.ReadAllLinesAsync(callLog);
        Assert.Equal("-l", socketArgs[0]);
        Assert.Equal("127.0.0.1:5901", socketArgs[1]);
        Assert.Equal("--inetd", socketArgs[2]);
        Assert.Equal("--", socketArgs[3]);
        Assert.EndsWith("systemd-socket-proxyd", socketArgs[4], StringComparison.Ordinal);
        Assert.Equal("10.99.6.42:5999", socketArgs[5]);
    }

    [Fact]
    public async Task GeneratedVncHelper_RejectsMissingToolsAndMissingGuestBridgeAddress()
    {
        if (OperatingSystem.IsWindows()) return;

        using var temp = new TempDir("codeybox-vnc-helper-errors-");
        var helper = Path.Combine(temp.Path, "codeybox-vnc-loopback");
        File.WriteAllText(helper, ExtractVncHelperScript(File.ReadAllText(SetupScriptPath())));
        MakeExecutable(helper);

        var emptyTools = Path.Combine(temp.Path, "empty-tools");
        Directory.CreateDirectory(emptyTools);
        var missingTools = await RunAsync("/bin/bash", [helper, "gui-vm"], new Dictionary<string, string?>
        {
            ["PATH"] = emptyTools,
        });
        Assert.Equal(1, missingTools.ExitCode);
        Assert.Contains("missing required tool: multipass", missingTools.Stderr, StringComparison.OrdinalIgnoreCase);

        var tools = Path.Combine(temp.Path, "tools");
        Directory.CreateDirectory(tools);
        WriteExecutable(Path.Combine(tools, "multipass"), """
#!/usr/bin/env bash
set -euo pipefail
echo "192.168.1.50"
""");
        WriteExecutable(Path.Combine(tools, "systemd-socket-activate"), """
#!/usr/bin/env bash
set -euo pipefail
exit 0
""");
        WriteExecutable(Path.Combine(tools, "systemd-socket-proxyd"), """
#!/usr/bin/env bash
set -euo pipefail
exit 0
""");

        var noGuestBridge = await RunAsync("/usr/bin/env", ["bash", helper, "gui-vm"], new Dictionary<string, string?>
        {
            ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        });
        Assert.Equal(1, noGuestBridge.ExitCode);
        Assert.Contains("could not find CodeyBox bridge IPv4 address", noGuestBridge.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static string SetupScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "setup-host-networks.sh");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate scripts/setup-host-networks.sh from the test output directory.");
    }

    private static string ExtractVncHelperScript(string setupScript)
    {
        const string startMarker = "cat > \"$VNC_HELPER\" <<'EOF'";
        const string endMarker = "\nEOF\nchmod 0755 \"$VNC_HELPER\"";
        var start = setupScript.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "VNC helper heredoc start marker was not found.");
        var contentStart = setupScript.IndexOf('\n', start) + 1;
        var end = setupScript.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
        Assert.True(end > contentStart, "VNC helper heredoc end marker was not found.");
        return setupScript[contentStart..end];
    }

    private static string ExtractBetween(string text, string startMarker, string endMarker)
    {
        var start = text.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker '{startMarker}' was not found.");
        start += startMarker.Length;
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker '{endMarker}' was not found.");
        return text[start..end];
    }

    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents);
        MakeExecutable(path);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in env)
            psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
