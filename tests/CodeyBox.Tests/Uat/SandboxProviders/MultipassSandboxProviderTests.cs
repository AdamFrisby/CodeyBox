using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests.Uat.SandboxProviders;

/// <summary>
/// UAT coverage for <c>Multipass sandbox provider - Runs agents in isolated Ubuntu VMs with host-enforced network profiles</c>.
/// Plan anchor: docs/uat/00-plan.md#multipass-sandbox-provider---runs-agents-in-isolated-ubuntu-vms-with-host-enforced-network-profiles
/// </summary>
public sealed class MultipassSandboxProviderTests : IDisposable
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAAB9Wl9WAAAAC0lEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");

    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-multipass-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public void CloudInit_InstallsExecWrapperAndRouteServiceWithoutGuestFirewallRules()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            ["apt-get update", "npm install -g @anthropic-ai/claude-code"],
            "packages:\n  - git\n");

        Assert.StartsWith("#cloud-config", cloudInit, StringComparison.Ordinal);
        Assert.Contains("path: /usr/local/bin/codeybox-exec", cloudInit);
        Assert.Contains("path: /usr/local/sbin/codeybox-route", cloudInit);
        Assert.Contains("path: /etc/systemd/system/codeybox-route.service", cloudInit);
        Assert.Contains("mkdir -p /work", cloudInit);
        Assert.Contains("systemctl enable --now codeybox-route.service", cloudInit);
        Assert.Contains("apt-get update", cloudInit);
        Assert.Contains("npm install -g @anthropic-ai/claude-code", cloudInit);
        Assert.Contains("packages:\n  - git", cloudInit);
        Assert.DoesNotContain("iptables", cloudInit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ufw", cloudInit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CloudInit_InstallsTcpKeepaliveSysctlForSuspendResumeRecovery()
    {
        // R8-core: after a multipass suspend/start cycle the in-VM agent's
        // long-lived TCP connections may be hanging in the kernel waiting for
        // a peer that doesn't know the suspend happened. The keepalive
        // sysctl makes that detection fast (~45s worst-case) instead of the
        // OS default of ~2h.
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(extraRuncmd: [], extraCloudInit: null);

        Assert.Contains("path: /etc/sysctl.d/99-codeybox-keepalive.conf", cloudInit);
        Assert.Contains("net.ipv4.tcp_keepalive_time = 30", cloudInit);
        Assert.Contains("net.ipv4.tcp_keepalive_intvl = 5", cloudInit);
        Assert.Contains("net.ipv4.tcp_keepalive_probes = 3", cloudInit);
        // Applied immediately on first boot so the first agent run benefits.
        Assert.Contains("sysctl --system", cloudInit);
    }

    [Fact]
    public void CloudInit_GraphicalFlavor_InstallsDesktopVncAndInputTools()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            ["echo project setup"],
            extraCloudInit: null,
            SandboxProfileFlavor.Graphical);

        Assert.Contains("path: /etc/systemd/system/codeybox-xvfb.service", cloudInit);
        Assert.Contains("path: /etc/systemd/system/codeybox-xfce.service", cloudInit);
        Assert.Contains("path: /etc/systemd/system/codeybox-vnc.service", cloudInit);
        Assert.Contains("xvfb x11vnc xfce4", cloudInit);
        Assert.Contains("xdotool scrot ffmpeg", cloudInit);
        Assert.Contains("x11-utils socat", cloudInit);
        Assert.Contains($"-rfbport {SandboxConventions.GraphicalVncPort}", cloudInit);
        Assert.Contains("-listen \"$listen_addr\"", cloudInit);
        Assert.Contains("-allow \"$host_addr\"", cloudInit);
        Assert.Contains("-rfbauth \"$password_file\"", cloudInit);
        Assert.DoesNotContain("-listen 127.0.0.1", cloudInit);
        Assert.DoesNotContain("-allow 127.0.0.1", cloudInit);
        Assert.DoesNotContain("-nopw", cloudInit);
        Assert.Contains("echo project setup", cloudInit);
        Assert.True(
            cloudInit.IndexOf("systemctl enable --now codeybox-route.service", StringComparison.Ordinal)
            < cloudInit.IndexOf("apt-get update", StringComparison.Ordinal),
            "graphical package install must run after route swap");

        var headlessCloudInit = MultipassSandboxProvider.BuildCloudInit(
            ["echo project setup"],
            extraCloudInit: null,
            SandboxProfileFlavor.Headless);
        Assert.DoesNotContain("codeybox-xvfb.service", headlessCloudInit);
        Assert.DoesNotContain("codeybox-vnc.service", headlessCloudInit);
        Assert.DoesNotContain("xvfb x11vnc xfce4", headlessCloudInit);
        Assert.DoesNotContain("xdotool scrot ffmpeg", headlessCloudInit);
        Assert.DoesNotContain("x11-utils socat", headlessCloudInit);
    }

    [Fact]
    public void CloudInit_GraphicalFlavor_InstallsDesktopToolsWithEmptyRuncmd()
    {
        var cloudInit = MultipassSandboxProvider.BuildCloudInit(
            [],
            extraCloudInit: null,
            SandboxProfileFlavor.Graphical);

        Assert.Contains("xvfb x11vnc xfce4", cloudInit);
        Assert.Contains("xdotool scrot ffmpeg", cloudInit);
        Assert.True(
            cloudInit.IndexOf("systemctl enable --now codeybox-route.service", StringComparison.Ordinal)
            < cloudInit.IndexOf("apt-get update", StringComparison.Ordinal),
            "graphical package install must run after route swap");
    }

    [Fact]
    public async Task ExecAsync_GraphicalSandboxInjectsDisplayByDefault()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.ExecAsync(new SandboxExec { Argv = ["printenv", "DISPLAY"] });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.Contains($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task ExecAsync_GraphicalSandboxPreservesCallerDisplayOverride()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv", "DISPLAY"],
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["DISPLAY"] = ":7",
                ["OTHER"] = "value",
            },
        });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.Contains("DISPLAY=:7", argv);
        Assert.Contains("OTHER=value", argv);
        Assert.DoesNotContain($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task ExecAsync_GraphicalSandboxMergesDisplayIntoCallerEnvironment()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["printenv"],
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["OTHER"] = "value",
            },
        });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.Contains("OTHER=value", argv);
        Assert.Contains($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task ExecAsync_HeadlessSandboxDoesNotInjectDisplay()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await sandbox.ExecAsync(new SandboxExec { Argv = ["printenv", "DISPLAY"] });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.DoesNotContain($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task GraphicalCapabilities_RejectHeadlessSandbox()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<NotSupportedException>(() => sandbox.GetScreenshotAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = SandboxInputEventType.Click }]));
    }

    [Fact]
    public async Task GetScreenshotAsync_ThrowsWhenScrotFails()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(2, "", "scrot failed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("graphical screenshot failed", ex.Message);
        Assert.Contains("scrot failed", ex.Message);
    }

    [Fact]
    public async Task GetScreenshotAsync_ThrowsWhenCommandReturnsInvalidBase64()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "not base64", "")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("invalid base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetScreenshotAsync_ReturnsDecodedPngBytes()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, Convert.ToBase64String(TinyPng), "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        var screenshot = await sandbox.GetScreenshotAsync();

        Assert.Equal(TinyPng, screenshot);
        AssertPngSignature(screenshot);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsDecodedNonPngBytes()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]), "")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("non-PNG", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsOutputPastCaptureLimit()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                137,
                new string('A', 1024),
                "",
                StdoutLimitExceeded: true)));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("maximum capture size", ex.Message, StringComparison.OrdinalIgnoreCase);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(MultipassSandbox.MaxScreenshotBase64StdoutBytes, call.MaxStdoutBytes);
        Assert.Equal(MultipassSandbox.MaxScreenshotStderrBytes, call.MaxStderrBytes);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsStderrPastCaptureLimit()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(
                137,
                "",
                new string('e', 1024),
                StderrLimitExceeded: true)));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("stderr", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("maximum capture size", ex.Message, StringComparison.OrdinalIgnoreCase);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(MultipassSandbox.MaxScreenshotBase64StdoutBytes, call.MaxStdoutBytes);
        Assert.Equal(MultipassSandbox.MaxScreenshotStderrBytes, call.MaxStderrBytes);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsDecodedPngPastLimit()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, Convert.ToBase64String(new byte[5]), "")));
        var sandbox = NewMultipassSandbox(
            SandboxProfileFlavor.Graphical,
            runner,
            maxScreenshotPngBytes: 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("PNG exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultProcessRunner_StopsReadingWhenStdoutLimitIsExceeded()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new DefaultProcessRunner();
        // The sh busy-loop competes for CPU with the .NET reader. On a fast
        // host the kill fires in milliseconds, but in a CPU-constrained
        // sandbox (e.g. an audit Multipass VM) the reader can be starved
        // long enough for a 5s cap to fire WaitForExitAsync via OCE before
        // the reader observes the limit. The ct here is only a backstop
        // against hangs — it must NOT be tight enough to race the actual
        // limit-detection path.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(
            ["sh", "-c", "while :; do printf 1234567890; done"],
            stdin: null,
            ct: timeout.Token,
            maxStdoutBytes: 1024,
            maxStderrBytes: 1024);

        Assert.True(result.StdoutLimitExceeded);
        Assert.True(result.Stdout.Length <= 1024, $"captured {result.Stdout.Length} bytes");
    }

    [Fact]
    public async Task DefaultProcessRunner_StopsReadingWhenStderrLimitIsExceeded()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new DefaultProcessRunner();
        // See sibling stdout test for the rationale: the cap is a hang
        // backstop, not a tight bound on limit-detection latency.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(
            ["sh", "-c", "while :; do printf 1234567890 >&2; done"],
            stdin: null,
            ct: timeout.Token,
            maxStdoutBytes: 1024,
            maxStderrBytes: 1024);

        Assert.True(result.StderrLimitExceeded);
        Assert.True(result.Stderr.Length <= 1024, $"captured {result.Stderr.Length} bytes");
    }

    [Fact]
    public async Task SynthesizeInputAsync_ThrowsWhenXdotoolFails()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(1, "", "xdotool failed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Return" }]));

        Assert.Contains("graphical input event 'Key' failed", ex.Message);
        Assert.Contains("xdotool failed", ex.Message);
    }

    [Fact]
    public async Task SynthesizeInputAsync_RejectsMalformedInputEvents()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var invalidEvents = new[]
        {
            new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 10 },
            new SandboxInputEvent { Type = SandboxInputEventType.Click, X = -1, Y = 0 },
            new SandboxInputEvent { Type = SandboxInputEventType.Key },
            new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 10 },
            new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 0, Y = -1 },
            new SandboxInputEvent { Type = SandboxInputEventType.Scroll },
            new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 1, Y = 1 },
            new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = 1001 },
            new SandboxInputEvent { Type = SandboxInputEventType.Type },
        };

        foreach (var inputEvent in invalidEvents)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() =>
                sandbox.SynthesizeInputAsync([inputEvent]));
        }
    }

    [Fact]
    public async Task SynthesizeInputAsync_RejectsNullEventListAndUnknownEventType()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            sandbox.SynthesizeInputAsync(null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            sandbox.SynthesizeInputAsync([]));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            sandbox.SynthesizeInputAsync([null!]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = (SandboxInputEventType)999 }]));
    }

    [Fact]
    public async Task SynthesizeInputAsync_BuildsExpectedXdotoolCommands()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.SynthesizeInputAsync(
            [
                new SandboxInputEvent { Type = SandboxInputEventType.Click, X = 12, Y = 34 },
                new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Return" },
                new SandboxInputEvent { Type = SandboxInputEventType.Move, X = 45, Y = 56 },
                new SandboxInputEvent { Type = SandboxInputEventType.Scroll, Y = -2 },
                new SandboxInputEvent { Type = SandboxInputEventType.Scroll, X = 3 },
                new SandboxInputEvent { Type = SandboxInputEventType.Type, Text = "typed text" },
            ]);

        var commands = runner.Calls.Select(c => ExtractXdotoolCommand(c.Argv)).ToArray();
        Assert.Equal(
            [
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "mousemove", "--sync", "12", "34", "click", "1"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "key", "--clearmodifiers", "Return"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "mousemove", "--sync", "45", "56"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "click", "--repeat", "2", "4"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "click", "--repeat", "3", "7"],
                ["env", $"DISPLAY={SandboxConventions.GraphicalDisplay}", "xdotool", "type", "--clearmodifiers", "--delay", "0", "--", "typed text"],
            ],
            commands);
    }

    [Fact]
    public void LaunchArgv_MapsConfiguredProfileToHostBridgeAndRejectsUnknownProfiles()
    {
        var provider = NewProvider(networkProfiles: new Dictionary<string, string>
        {
            ["claude"] = "cb-claude",
        });
        var spec = new SandboxSpec
        {
            ImageReference = "24.04",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            Limits = new SandboxResourceLimits
            {
                CpuCount = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,
                DiskBytes = 20L * 1024 * 1024 * 1024,
            },
        };

        var argv = provider.BuildLaunchArgv("codeybox-test", spec, "/staging/cloud-init.yaml");

        Assert.Equal("multipass", argv[0]);
        Assert.Contains("--cpus", argv);
        Assert.Contains("4", argv);
        var argvList = argv.ToList();
        var networkIndex = argvList.IndexOf("--network");
        Assert.True(networkIndex > 0, string.Join(' ', argv));
        Assert.Equal("name=cb-claude,mode=auto", argv[networkIndex + 1]);
        Assert.True(argvList.IndexOf("24.04") > networkIndex);

        var missing = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "missing" },
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.BuildLaunchArgv("codeybox-test", missing, "/staging/cloud-init.yaml"));
        Assert.Contains("missing", ex.Message);
        Assert.Contains("claude", ex.Message);
    }

    [Fact]
    public void StagingRoot_IsCreatedWithOperatorOnlyPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var staging = Path.Combine(_workspace, "staging");

        _ = NewProvider(stagingDirectory: staging);

        var mode = File.GetUnixFileMode(staging);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
        Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead), mode.ToString());
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead), mode.ToString());
    }

    [Fact]
    public async Task DisposeLeakedAsync_RejectsUnsafeVmNamesBeforeShellingOut()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProvider(runner: runner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.DisposeLeakedAsync("codeybox-bad/../../../escape", CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task DisposeAsync_DeleteFailureUntracksActiveSandboxWithoutMaskingCompletedPhase()
    {
        var disposedNames = new List<string>();
        var noLongerActiveNames = new List<string>();
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "delete", "--purge", "codeybox-deletefail"])
            {
                deleteCalls++;
                return Task.FromResult(deleteCalls == 1
                    ? new ProcessRunResult(17, "", "still running")
                    : new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var sandbox = new MultipassSandbox(
            "codeybox-deletefail",
            Path.Combine(_workspace, "delete-fail-root"),
            new SandboxSpec { ImageReference = "ignored" },
            new MultipassSandboxOptions { MultipassBinary = "/bin/false" },
            NullLogger<MultipassSandboxProvider>.Instance,
            onDisposed: disposedNames.Add,
            onNoLongerTrackedActive: noLongerActiveNames.Add,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        await sandbox.DisposeAsync();

        Assert.Equal(1, deleteCalls);
        Assert.Empty(disposedNames);
        Assert.Equal(["codeybox-deletefail"], noLongerActiveNames);

        await sandbox.DisposeAsync();

        Assert.Equal(1, deleteCalls);
        Assert.Empty(disposedNames);
    }

    [Fact]
    public async Task ProviderCreatedSandbox_DeleteFailureUntracksActiveCacheAndReaperDisposes()
    {
        var staging = Path.Combine(_workspace, "provider-delete-fail-staging");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var deleteCalls = 0;
        var listCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(states.TryGetValue(name, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found"));

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "list", "--format", "json"])
            {
                listCalls++;
                var list = states.Keys
                    .OrderBy(vmName => vmName, StringComparer.Ordinal)
                    .Select(vmName => new { name = vmName })
                    .ToArray();
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { list }), ""));
            }

            if (argv is [_, "info", "--format", "json", ..])
            {
                var info = states.Keys.ToDictionary(
                    vmName => vmName,
                    vmName => new
                    {
                        created = "2026-05-18T00:00:00Z",
                        disks = new Dictionary<string, object>
                        {
                            ["sda1"] = new { used = "1048576" },
                        },
                    },
                    StringComparer.Ordinal);
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deleteCalls++;
                if (deleteCalls == 1)
                    return Task.FromResult(new ProcessRunResult(17, "", "transient delete failure"));

                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        var name = sandbox.Id;
        var active = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.True(active.IsTrackedActive);

        await sandbox.DisposeAsync();

        Assert.Equal(1, deleteCalls);
        var untracked = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.Equal(name, untracked.Name);
        Assert.False(untracked.IsTrackedActive);
        Assert.Equal(2, listCalls);

        var reaper = new SandboxLeakReaper(
            provider,
            new NullWebhookDispatcher(),
            new SandboxLeakOptions
            {
                Enabled = true,
                AutoDispose = true,
                CheckInterval = TimeSpan.FromHours(1),
                LeakAgeThreshold = TimeSpan.Zero,
                MaxConcurrentAutoDispose = 1,
            },
            NullLogger<SandboxLeakReaper>.Instance);
        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(2, deleteCalls);
        Assert.Empty(states);
        Assert.Empty(reaper.GetLatestLeaks());
    }

    [Fact]
    public async Task CreateAsync_TracksVmAsActiveWhileLaunchIsStillInProgress()
    {
        var staging = Path.Combine(_workspace, "provider-inflight-launch-staging");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLaunchToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                launchEntered.TrySetResult();
                await allowLaunchToFinish.Task.WaitAsync(ct);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return states.TryGetValue(name, out var state)
                    ? new ProcessRunResult(0, state, "")
                    : new ProcessRunResult(1, "", "not found");

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "list", "--format", "json"])
            {
                var list = states.Keys
                    .OrderBy(vmName => vmName, StringComparer.Ordinal)
                    .Select(vmName => new { name = vmName })
                    .ToArray();
                return new ProcessRunResult(0, JsonSerializer.Serialize(new { list }), "");
            }

            if (argv is [_, "info", "--format", "json", ..])
            {
                var info = states.Keys.ToDictionary(
                    vmName => vmName,
                    vmName => new
                    {
                        created = "2026-05-18T00:00:00Z",
                        disks = new Dictionary<string, object>(),
                    },
                    StringComparer.Ordinal);
                return new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), "");
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var createTask = provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        await launchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var inFlight = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));
        Assert.True(inFlight.IsTrackedActive);

        allowLaunchToFinish.SetResult();
        await using var sandbox = await createTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ListAllManagedAsync_FiltersCodeyboxVmsAddsDiskInfoAndUsesTtlCache()
    {
        var staging = Path.Combine(_workspace, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "codeybox-alpha"));
        Directory.CreateDirectory(Path.Combine(staging, "codeybox-beta"));
        await File.WriteAllTextAsync(Path.Combine(staging, "codeybox-beta", ".codeybox-preempt"), "preserved");

        var listCalls = 0;
        var infoCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
            {
                listCalls++;
                return Task.FromResult(new ProcessRunResult(0, """
                    {"list":[
                      {"name":"primary"},
                      {"name":"cb-baseline-claude"},
                      {"name":"codeybox-alpha"},
                      {"name":"codeybox-beta"},
                      {"name":"codeybox-orphan"},
                      {"name":"codeybox-invalid.name"}
                    ]}
                    """, ""));
            }

            if (argv.Count >= 4 && argv[1] == "info")
            {
                infoCalls++;
                return Task.FromResult(new ProcessRunResult(0, """
                    {"info":{
                      "codeybox-alpha":{"disks":{"sda1":{"used":"1048576"}}},
                      "codeybox-beta":{"disks":{"sda1":{"used":"2097152"}}},
                      "codeybox-orphan":{"created":"2026-05-18T00:00:00Z","disks":{"sda1":{"used":"3145728"}}}
                    }}
                    """, ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + string.Join(' ', argv)));
        });
        var provider = NewProvider(stagingDirectory: staging, runner: runner);

        var first = await provider.ListAllManagedAsync(CancellationToken.None);
        var second = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, listCalls);
        Assert.Equal(1, infoCalls);
        Assert.Equal(["codeybox-alpha", "codeybox-beta", "codeybox-orphan"], first.Select(s => s.Name).ToArray());
        Assert.All(first, s => Assert.NotNull(s.CreatedAt));
        Assert.Equal(1024 * 1024, first.Single(s => s.Name == "codeybox-alpha").DiskBytes);
        Assert.True(first.Single(s => s.Name == "codeybox-beta").HasPreemptMarker);
        Assert.Equal(
            DateTimeOffset.Parse("2026-05-18T00:00:00Z"),
            first.Single(s => s.Name == "codeybox-orphan").CreatedAt);
    }

    [Theory]
    [InlineData("created_at")]
    [InlineData("creation_time")]
    [InlineData("creationTimestamp")]
    public async Task ListAllManagedAsync_ReadsMultipassCreationTimestampAliases(string timestampProperty)
    {
        var expected = DateTimeOffset.Parse("2026-05-19T01:02:03Z");
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
                return Task.FromResult(new ProcessRunResult(0, """
                    {"list":[{"name":"codeybox-alias"}]}
                    """, ""));

            if (argv.Count >= 4 && argv[1] == "info")
            {
                var info = new Dictionary<string, object>
                {
                    ["codeybox-alias"] = new Dictionary<string, object?>
                    {
                        [timestampProperty] = "2026-05-19T01:02:03Z",
                        ["disks"] = new Dictionary<string, object>(),
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-" + timestampProperty),
            runner: runner);

        var managed = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));

        Assert.Equal(expected, managed.CreatedAt);
    }

    [Theory]
    [InlineData("Suspending", true)]
    [InlineData("Suspended", true)]
    [InlineData("Running", false)]
    [InlineData("Stopped", false)]
    public async Task ListAllManagedAsync_MapsMultipassStateToSuspendLifecycleFlag(
        string multipassState, bool expectedFlag)
    {
        // SandboxLeakReaper reads ManagedSandboxInfo.IsSuspendLifecycleOrFrozen to
        // spot VMs frozen/freezing with no live mapping. The multipass state
        // vocabulary stays inside the provider, which must map BOTH Suspending
        // (snapshot in progress) and Suspended (complete) to true, and running /
        // stopped states to false. This pins the JSON-parse → flag wiring so a
        // regression there can't silently blind the reaper.
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
                return Task.FromResult(new ProcessRunResult(0, """
                    {"list":[{"name":"codeybox-stateful"}]}
                    """, ""));

            if (argv.Count >= 4 && argv[1] == "info")
            {
                var info = new Dictionary<string, object>
                {
                    ["codeybox-stateful"] = new Dictionary<string, object?>
                    {
                        ["state"] = multipassState,
                        ["disks"] = new Dictionary<string, object>(),
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-state-" + multipassState),
            runner: runner);

        var managed = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));

        Assert.Equal(expectedFlag, managed.IsSuspendLifecycleOrFrozen);
    }

    [Fact]
    public async Task BaselineImages_BakeOncePerProfileUnderConcurrentCreatesThenCloneSandboxes()
    {
        var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var baselineLaunches = 0;
        var cloneCount = 0;
        var installCount = 0;

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "info", var name, "--format=csv"])
            {
                if (states.TryGetValue(name, out var state))
                    return new ProcessRunResult(0, state, "");
                return new ProcessRunResult(1, "", "not found");
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                var launchName = argv[3];
                if (launchName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref baselineLaunches);
                    states[launchName] = "Running";
                    launchEntered.TrySetResult();
                    await allowLaunch.Task.WaitAsync(ct);
                }
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", "");

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", ..]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref installCount);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                states[cloneName] = "Stopped";
                Interlocked.Increment(ref cloneCount);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            networkProfiles: new Dictionary<string, string> { ["claude"] = "cb-claude" },
            useBaselineImages: true,
            extraRuncmd: ["touch /opt/codeybox-baseline"],
            runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
            WorkingDirectory = "/work",
        };

        var firstCreate = provider.CreateAsync(spec, CancellationToken.None);
        await launchEntered.Task;
        var secondCreate = provider.CreateAsync(spec, CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref baselineLaunches));

        allowLaunch.SetResult();
        var sandboxes = await Task.WhenAll(firstCreate, secondCreate);
        await sandboxes[0].DisposeAsync();
        await sandboxes[1].DisposeAsync();

        Assert.Equal(1, baselineLaunches);
        Assert.Equal(1, installCount);
        Assert.Equal(2, cloneCount);
    }

    [Fact]
    public async Task BaselineImages_GraphicalFlavorUsesSharedGraphicalBaselineAndInstallsDesktop()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var installCommands = new List<string>();
        string? baselineLaunchName = null;
        string? baselineLaunchNetwork = null;
        string? baselineCloudInit = null;
        string? cloneSource = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var name, "--format=csv"])
            {
                if (states.TryGetValue(name, out var state))
                    return Task.FromResult(new ProcessRunResult(0, state, ""));
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                baselineLaunchName = argv[3];
                var networkIndex = argv.ToList().IndexOf("--network");
                baselineLaunchNetwork = networkIndex >= 0 ? argv[networkIndex + 1] : null;
                var cloudInitIndex = argv.ToList().IndexOf("--cloud-init");
                baselineCloudInit = cloudInitIndex >= 0 ? File.ReadAllText(argv[cloudInitIndex + 1]) : null;
                states[baselineLaunchName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", var command]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                installCommands.Add(command);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSource = source;
                states[cloneName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-graphical"),
            networkProfiles: new Dictionary<string, string>
            {
                ["claude"] = "cb-claude",
                [SandboxConventions.GraphicalNetworkProfile] = "cb-graphical",
            },
            useBaselineImages: true,
            extraRuncmd: ["touch /opt/codeybox-project-tools"],
            runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Flavor = SandboxProfileFlavor.Graphical,
            Network = new SandboxNetworkPolicy { ProfileName = SandboxConventions.GraphicalNetworkProfile },
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        // B1: baseline name is now content-hashed (cb-baseline-{12hex}); the
        // exact value depends on cloud-init + runcmd contents. Assert the
        // structure and that the same value is reused for the subsequent
        // clone — re-baking would have produced a different launch name.
        Assert.NotNull(baselineLaunchName);
        Assert.StartsWith("cb-baseline-", baselineLaunchName);
        Assert.Equal("name=cb-graphical,mode=auto", baselineLaunchNetwork);
        Assert.Equal(baselineLaunchName, cloneSource);
        var baselineCloudInitText = Assert.IsType<string>(baselineCloudInit);
        Assert.Contains("systemctl enable --now codeybox-route.service", baselineCloudInitText);
        Assert.DoesNotContain("apt-get install -y --no-install-recommends xvfb", baselineCloudInitText);
        Assert.Contains(installCommands, cmd =>
            cmd.Contains("xvfb x11vnc xfce4", StringComparison.Ordinal)
            && cmd.Contains("xdotool scrot ffmpeg", StringComparison.Ordinal));
        Assert.Contains(installCommands, cmd =>
            cmd.Contains("touch /opt/codeybox-project-tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BaselineImages_GraphicalFlavorPreservesSelectedNetworkProfile()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? baselineLaunchName = null;
        string? baselineLaunchNetwork = null;
        string? cloneSource = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var name, "--format=csv"])
            {
                if (states.TryGetValue(name, out var state))
                    return Task.FromResult(new ProcessRunResult(0, state, ""));
                return Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                baselineLaunchName = argv[3];
                var networkIndex = argv.ToList().IndexOf("--network");
                baselineLaunchNetwork = networkIndex >= 0 ? argv[networkIndex + 1] : null;
                states[baselineLaunchName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", _]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSource = source;
                states[cloneName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging-graphical-profile"),
            networkProfiles: new Dictionary<string, string>
            {
                ["ci"] = "cb-ci",
                [SandboxConventions.GraphicalNetworkProfile] = "cb-graphical",
            },
            useBaselineImages: true,
            runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Flavor = SandboxProfileFlavor.Graphical,
            Network = new SandboxNetworkPolicy { ProfileName = "ci" },
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        // B1: see other graphical-baseline test — name is content-hashed now.
        Assert.NotNull(baselineLaunchName);
        Assert.StartsWith("cb-baseline-", baselineLaunchName);
        Assert.Equal("name=cb-ci,mode=auto", baselineLaunchNetwork);
        Assert.Equal(baselineLaunchName, cloneSource);
    }

    [Fact]
    public async Task CreateAsync_RetriesCloudInitExitOneAndAcceptsRecoveredStatus()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-exit-one-retry");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloudInitCalls = 0;
        var probeCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitCalls++;
                return Task.FromResult(cloudInitCalls == 1
                    ? new ProcessRunResult(1, "", "")
                    : new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "bash", "-c", _])
            {
                probeCalls++;
                return Task.FromResult(new ProcessRunResult(99, "", "readiness probe should not run after recovered cloud-init status"));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            cloudInitReadyRetryDelay: TimeSpan.Zero);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.Equal(3, cloudInitCalls);
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public async Task CreateAsync_CloudInitExitOneAfterRetriesUsesReadinessProbe()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-probe-success");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var cloudInitCalls = 0;
        var probeCalls = 0;
        var logger = new RecordingLogger<MultipassSandboxProvider>();

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", ""));
            }

            if (argv is [_, "exec", _, "--", "bash", "-c", var command])
            {
                probeCalls++;
                Assert.Contains("test -e /work", command, StringComparison.Ordinal);
                Assert.Contains("test -e /usr/local/bin/codeybox-exec", command, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(
                    0,
                    "/work=present /usr/local/bin/codeybox-exec=present\n",
                    ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            logger: logger,
            cloudInitReadyRetryAttempts: 2,
            cloudInitReadyRetryDelay: TimeSpan.Zero);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        await using var _ = await provider.CreateAsync(spec, CancellationToken.None);

        Assert.Equal(4, cloudInitCalls);
        Assert.Equal(2, probeCalls);
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && e.Message.Contains("readiness probe passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_CloudInitExitOneAfterRetriesThrowsWhenReadinessProbeFails()
    {
        var staging = Path.Combine(_workspace, "staging-cloud-init-probe-failure");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? deletedName = null;
        var cloudInitCalls = 0;
        var probeCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", ""));
            }

            if (argv is [_, "exec", _, "--", "bash", "-c", var command])
            {
                probeCalls++;
                Assert.Contains("test -e /work", command, StringComparison.Ordinal);
                Assert.Contains("test -e /usr/local/bin/codeybox-exec", command, StringComparison.Ordinal);
                return Task.FromResult(new ProcessRunResult(
                    1,
                    "/work=missing /usr/local/bin/codeybox-exec=missing\n",
                    ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            cloudInitReadyRetryDelay: TimeSpan.Zero);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Equal(MultipassSandboxOptions.DefaultCloudInitReadyRetryAttempts, cloudInitCalls);
        Assert.Equal(1, probeCalls);
        Assert.Contains("readiness probe failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/work=missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/codeybox-exec=missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Expected /work and /usr/local/bin/codeybox-exec to exist", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(launchedName);
        Assert.Equal(launchedName, deletedName);
        Assert.False(
            Directory.Exists(Path.Combine(staging, launchedName!)),
            "staging directory for failed sandbox must be removed during cleanup");
    }

    [Fact]
    public async Task CreateAsync_ThrowsAndCleansUpWhenCloudInitWaitReturnsNonZero()
    {
        // WaitForVmReadyAsync surfaces cloud-init failures so a half-installed
        // graphical (or headless) VM doesn't return as a "ready" sandbox handle.
        // The catch in CreateAsync must also tear down the VM and staging dir
        // so a failed launch doesn't leak.
        var staging = Path.Combine(_workspace, "staging-cloud-init-failure");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? cloudInitTarget = null;
        string? deletedName = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitTarget = execName;
                return Task.FromResult(new ProcessRunResult(3, "", "schema validation failed: bad runcmd"));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var provider = NewProvider(stagingDirectory: staging, runner: runner);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Contains("cloud-init failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("schema validation failed: bad runcmd", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(launchedName);
        Assert.Contains(launchedName!, ex.Message, StringComparison.Ordinal);
        Assert.Equal(launchedName, cloudInitTarget);
        Assert.Equal(launchedName, deletedName);
        Assert.False(
            Directory.Exists(Path.Combine(staging, launchedName!)),
            "staging directory for failed sandbox must be removed during cleanup");
    }

    [Fact]
    public void LaunchArgv_GraphicalFlavorUsesConfiguredProfileBridge()
    {
        var provider = NewProvider(networkProfiles: new Dictionary<string, string>
        {
            ["claude"] = "cb-claude",
            [SandboxConventions.GraphicalNetworkProfile] = "cb-graphical",
        });
        var spec = new SandboxSpec
        {
            ImageReference = "24.04",
            Flavor = SandboxProfileFlavor.Graphical,
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
        };

        var argv = provider.BuildLaunchArgv("codeybox-test", spec, "/staging/cloud-init.yaml");
        var networkIndex = argv.ToList().IndexOf("--network");

        Assert.True(networkIndex > 0, string.Join(' ', argv));
        Assert.Equal("name=cb-claude,mode=auto", argv[networkIndex + 1]);
    }

    [Fact]
    public void MultipassSandboxOptions_VmTimeouts_DefaultToSpecValues()
    {
        // Defaults match the values previously hardcoded in WaitForRunningAsync
        // and WaitForStoppedAsync, so behaviour is unchanged when operators do
        // not override either knob.
        var options = new MultipassSandboxOptions();

        Assert.Equal(TimeSpan.FromMinutes(3), options.VmStartTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), options.VmStopTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), MultipassSandboxOptions.DefaultVmStartTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), MultipassSandboxOptions.DefaultVmStopTimeout);
    }

    [Fact]
    public async Task CreateAsync_VmStartTimeout_AppliesConfiguredDeadlineWhenInfoNeverReportsRunning()
    {
        var staging = Path.Combine(_workspace, "staging-vm-start-timeout");
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        string? launchedName = null;
        string? deletedName = null;

        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                // Stay in "Starting" forever so the WaitForRunning loop exhausts
                // the configured deadline rather than ever observing Running.
                return states.TryGetValue(infoName, out var state)
                    ? Task.FromResult(new ProcessRunResult(0, state, ""))
                    : Task.FromResult(new ProcessRunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Starting";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });

        var configuredTimeout = TimeSpan.FromMilliseconds(250);
        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            vmStartTimeout: configuredTimeout);
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy(),
            WorkingDirectory = "/work",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(spec, CancellationToken.None));

        Assert.Contains("did not reach Running state", ex.Message, StringComparison.Ordinal);
        Assert.Contains(configuredTimeout.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.NotNull(launchedName);
        Assert.Equal(launchedName, deletedName);
    }

    [Fact]
    public async Task RetryHelper_RetriesTransientSshReadinessWithoutRealDelays()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => Task.FromResult(++attempts == 1
                ? new ProcessRunResult(1, "", "ssh connection failed: Connection refused")
                : new ProcessRunResult(0, "ok", "")),
            log: NullLogger.Instance,
            description: "uat transfer",
            ct: CancellationToken.None,
            delay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Stdout);
        Assert.Equal(2, attempts);
        Assert.Equal([MultipassRetry.DefaultInitialDelay], delays);
    }

    private static MultipassSandboxProvider NewProvider(
        string? stagingDirectory = null,
        IReadOnlyDictionary<string, string>? networkProfiles = null,
        bool useBaselineImages = false,
        IReadOnlyList<string>? extraRuncmd = null,
        RecordingMultipassRunner? runner = null,
        RecordingLogger<MultipassSandboxProvider>? logger = null,
        MultipassDaemonRetryPolicy? daemonRetryPolicy = null,
        int? cloudInitReadyRetryAttempts = null,
        TimeSpan? cloudInitReadyRetryDelay = null,
        TimeSpan? vmStartTimeout = null,
        TimeSpan? vmStopTimeout = null,
        int? maxConcurrentBoots = null,
        TimeSpan? bootLaunchDelay = null)
    {
        var options = new MultipassSandboxOptions
        {
            MultipassBinary = runner is null ? "multipass" : "/bin/false",
            StagingDirectory = stagingDirectory,
            NetworkProfiles = networkProfiles ?? new Dictionary<string, string>(),
            UseBaselineImages = useBaselineImages,
            ExtraRuncmd = extraRuncmd ?? [],
            CloudInitReadyRetryAttempts = cloudInitReadyRetryAttempts
                ?? MultipassSandboxOptions.DefaultCloudInitReadyRetryAttempts,
            CloudInitReadyRetryDelay = cloudInitReadyRetryDelay
                ?? MultipassSandboxOptions.DefaultCloudInitReadyRetryDelay,
            VmStartTimeout = vmStartTimeout
                ?? MultipassSandboxOptions.DefaultVmStartTimeout,
            VmStopTimeout = vmStopTimeout
                ?? MultipassSandboxOptions.DefaultVmStopTimeout,
            MaxConcurrentBoots = maxConcurrentBoots
                ?? MultipassSandboxOptions.DefaultMaxConcurrentBoots,
            BootLaunchDelay = bootLaunchDelay
                ?? MultipassSandboxOptions.DefaultBootLaunchDelay,
        };
        Microsoft.Extensions.Logging.ILogger<MultipassSandboxProvider> resolvedLogger = logger is not null
            ? logger
            : NullLogger<MultipassSandboxProvider>.Instance;

        return runner is null
            ? new MultipassSandboxProvider(options, resolvedLogger)
            : new MultipassSandboxProvider(
                options,
                resolvedLogger,
                null,
                runner,
                daemonRetryPolicy);
    }

    private MultipassSandbox NewMultipassSandbox(
        SandboxProfileFlavor flavor,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>> handler,
        int? maxScreenshotPngBytes = null,
        TimeSpan? vmStopTimeout = null)
    {
        return NewMultipassSandbox(flavor, new RecordingMultipassRunner(handler), maxScreenshotPngBytes, vmStopTimeout);
    }

    private MultipassSandbox NewMultipassSandbox(
        SandboxProfileFlavor flavor,
        RecordingMultipassRunner runner,
        int? maxScreenshotPngBytes = null,
        TimeSpan? vmStopTimeout = null)
    {
        return new MultipassSandbox(
            "codeybox-test",
            _workspace,
            new SandboxSpec
            {
                ImageReference = "ignored",
                Flavor = flavor,
                WorkingDirectory = "/work",
            },
            new MultipassSandboxOptions
            {
                MultipassBinary = "/bin/true",
                VmStopTimeout = vmStopTimeout ?? MultipassSandboxOptions.DefaultVmStopTimeout,
            },
            NullLogger<MultipassSandboxProvider>.Instance,
            runner: runner,
            maxScreenshotPngBytes: maxScreenshotPngBytes);
    }

    private static string[] ExtractXdotoolCommand(IReadOnlyList<string> argv)
    {
        var envIndex = argv.ToList().IndexOf("env");
        Assert.True(envIndex >= 0, "missing xdotool env command in argv: " + JsonSerializer.Serialize(argv));
        return argv.Skip(envIndex).ToArray();
    }

    private static void AssertPngSignature(byte[] bytes)
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(
            bytes.Length >= signature.Length && bytes.AsSpan(0, signature.Length).SequenceEqual(signature),
            "screenshot bytes must start with the PNG signature");
    }

    [Fact]
    public async Task CreateAsync_RetriesTransientMultipassSocketLaunchFailureAndSucceeds()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var launchCalls = 0;
        var versionCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.15.0", ""));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchCalls++;
                if (launchCalls == 1)
                    return Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
            {
                var state = states.TryGetValue(name, out var current) ? current : "Running";
                return Task.FromResult(new ProcessRunResult(0, state, ""));
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = WorkItemId.New(),
        });
        await sandbox.DisposeAsync();

        Assert.Equal(2, launchCalls);
        Assert.Equal(1, versionCalls);
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("transient multipass daemon error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAsync_TransientMultipassSocketLaunchFailureExhaustsRetriesWithClearMessage()
    {
        var launchCalls = 0;
        var versionCalls = 0;

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
            }

            if (argv.Count >= 2 && argv[1] == "launch")
            {
                launchCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "cannot connect to the multipass socket"));
            }

            if (argv.Count >= 2 && argv[1] == "delete")
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                TimingWorkItemId = WorkItemId.New(),
            }));

        Assert.Contains("multipass daemon unreachable after 2 retries", ex.Message);
        Assert.Equal(3, launchCalls);
        Assert.Equal(3, versionCalls);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task SandboxExec_RetriesTransientMultipassSocketErrorAndSucceeds()
    {
        // Covers the sandbox-side retry wrapper (MultipassSandbox.RunMultipassAsync),
        // which is a second integration of MultipassDaemonRetry distinct from
        // the provider's CreateAsync path. A transient multipass-socket error
        // surfaced from `multipass exec` mid-lifetime must be retried, not
        // propagated as an exec failure to the caller.
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var execCalls = 0;
        var versionCalls = 0;
        SandboxExecResult? execResult = null;
        var workItemId = WorkItemId.New();

        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
            {
                versionCalls++;
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.15.0", ""));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(
                    0, states.TryGetValue(name, out var current) ? current : "Running", ""));

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out var removedState);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            // Exec of the user payload — first attempt returns a transient
            // socket failure; the second must succeed and produce "hello".
            if (argv.Count >= 4 && argv[1] == "exec" && argv[3] == "--")
            {
                execCalls++;
                if (execCalls == 1)
                    return Task.FromResult(new ProcessRunResult(
                        1, "", "cannot connect to the multipass socket"));
                return Task.FromResult(new ProcessRunResult(0, "hello\n", ""));
            }

            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
        });
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            logger: logger,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
        });
        try
        {
            execResult = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["echo", "hello"],
            });
        }
        finally
        {
            await sandbox.DisposeAsync();
        }

        Assert.NotNull(execResult);
        Assert.Equal(0, execResult.ExitCode);
        Assert.Equal("hello\n", execResult.Stdout);
        // Two exec attempts — first transient, second success.
        Assert.Equal(2, execCalls);
        // Sandbox-side wrapper must have probed multipass version between
        // attempts (proving the retry layer ran on the sandbox, not the
        // provider).
        Assert.True(versionCalls >= 1, $"expected at least one health probe; got {versionCalls}");
        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("transient multipass daemon error", StringComparison.Ordinal));
    }


    // ── R8-core SuspendAsync / ResumeSandboxAsync coverage ──────────────────

    [Fact]
    public async Task SuspendAsync_RunsMultipassSuspendAndWritesPreemptMarker()
    {
        // Covers: argv ordering, preempt marker write, _preserveOnDispose
        // being set only AFTER multipass suspend returns success.
        var suspendCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "suspend", "codeybox-test"])
            {
                suspendCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await ((ISuspendableSandbox)sandbox).SuspendAsync();

        Assert.Equal(1, suspendCalls);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")),
            "SuspendAsync must write the .codeybox-preempt marker so SandboxLeakReaper applies PreemptRetention to leaked suspended VMs.");

        // After success, DisposeAsync must be a no-op (preserve flag flipped).
        // We verify by counting multipass delete calls — there should be zero.
        var deleteCalls = 0;
        runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv.Count >= 2 && argv[1] == "delete") deleteCalls++;
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        // Build a second sandbox so we can verify the no-op-dispose contract
        // independently — the first sandbox above is still preserved in
        // memory and would tie up the workspace if disposed.
        var workspace2 = Directory.CreateTempSubdirectory("codeybox-suspend-").FullName;
        try
        {
            var sandbox2 = new MultipassSandbox(
                "codeybox-test-2", workspace2,
                new SandboxSpec { ImageReference = "ignored", Flavor = SandboxProfileFlavor.Headless },
                new MultipassSandboxOptions { MultipassBinary = "/bin/true" },
                NullLogger<MultipassSandboxProvider>.Instance,
                runner: runner);
            await ((ISuspendableSandbox)sandbox2).SuspendAsync();
            await sandbox2.DisposeAsync();
            Assert.Equal(0, deleteCalls);
        }
        finally
        {
            if (Directory.Exists(workspace2))
                Directory.Delete(workspace2, recursive: true);
        }
    }

    [Fact]
    public async Task StopAndPreserveAsync_RunsStopAndVerifiesStopped()
    {
        var stopCalls = 0;
        var infoCalls = 0;
        var deleteCalls = 0;
        var calls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                calls.Add("delete");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "stop", "codeybox-test"])
            {
                calls.Add("stop");
                stopCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "info", "codeybox-test", "--format=csv"])
            {
                calls.Add("info");
                infoCalls++;
                return Task.FromResult(new ProcessRunResult(
                    0,
                    stopCalls > 0 ? "Stopped" : "Running",
                    ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync();

        Assert.Equal(1, stopCalls);
        Assert.True(infoCalls >= 1);
        Assert.True(calls.IndexOf("stop") >= 0 && calls.IndexOf("stop") < calls.IndexOf("info"),
            $"expected stop before info verification, got: {string.Join(", ", calls)}");
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")));
        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task StopAndPreserveAsync_NonZeroStopExit_ThrowsAndPreservesOnDispose()
    {
        var stopCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "stop", "codeybox-test"])
            {
                stopCalls++;
                return Task.FromResult(new ProcessRunResult(3, "", "multipassd: stop failed"));
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync());

        Assert.True(stopCalls >= 1);
        Assert.Contains("multipass stop codeybox-test failed", ex.Message);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")),
            "stop/preserve must mark the VM as preempt-retained before multipass stop can fail");

        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task StopAndPreserveAsync_NotStoppedAfterSuccessfulStop_ThrowsAndPreservesOnDispose()
    {
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "stop", "codeybox-test"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "info", "codeybox-test", "--format=csv"])
                return Task.FromResult(new ProcessRunResult(0, "Running", ""));
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(
            SandboxProfileFlavor.Headless,
            runner,
            vmStopTimeout: TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync());

        Assert.Contains("did not reach Stopped state", ex.Message);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")),
            "an abandoned stop verification must still leave the VM preempt-retained");

        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task StopAndPreserveAsync_CanceledStop_PreservesVmOnDispose()
    {
        var stopCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, ct) =>
        {
            if (argv is [_, "stop", "codeybox-test"])
            {
                stopCalls++;
                throw new OperationCanceledException(ct);
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((IPreemptibleSandbox)sandbox).StopAndPreserveAsync());

        Assert.Equal(1, stopCalls);
        Assert.True(File.Exists(Path.Combine(_workspace, ".codeybox-preempt")));

        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public async Task SuspendAsync_NonZeroExit_ThrowsAndLeavesDisposeActive()
    {
        // Critical safety contract: a failed `multipass suspend` MUST NOT flip
        // _preserveOnDispose to true. Otherwise the subsequent DisposeAsync
        // becomes a no-op while the VM is still Running on disk — a silent
        // leak. The SandboxSuspendOnShutdownService caller persists the
        // SuspendedVmName mapping BEFORE awaiting suspend and CLEARS it again
        // when suspend throws a non-cancellation exception, so this failed,
        // still-Running VM is left with no resume bookkeeping and DisposeAsync
        // tears it down.
        var suspendCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "suspend", _])
            {
                suspendCalls++;
                return Task.FromResult(new ProcessRunResult(1, "", "multipassd: I/O error suspending VM"));
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            // multipass version probe (daemon health) returns 0 from the
            // retry layer; we want the suspend to fail through.
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((ISuspendableSandbox)sandbox).SuspendAsync());

        Assert.True(suspendCalls >= 1);

        // DisposeAsync now runs and MUST actually call multipass delete,
        // because _preserveOnDispose should still be false after the failed
        // suspend.
        await sandbox.DisposeAsync();
        Assert.Equal(1, deleteCalls);
    }

    [Fact]
    public async Task SuspendAsync_TimedOut_PreservesVmOnDispose()
    {
        // Critical safety contract for the per-VM suspend timeout: when
        // `multipass suspend` is abandoned by OperationCanceledException (the
        // SandboxSuspendOnShutdownService per-VM timeout fired while multipassd
        // was still writing the RAM snapshot), DisposeAsync MUST NOT run
        // `multipass delete --purge`. multipassd keeps freezing the VM after we
        // give up; the caller keeps the persisted SuspendedVmName mapping so the
        // next startup resumes it (or the leak reaper purges it after the
        // preempt grace). Deleting here would destroy the snapshot mid-write and
        // defeat the whole persist-before-await fix.
        var suspendCalls = 0;
        var deleteCalls = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "suspend", _])
            {
                suspendCalls++;
                throw new OperationCanceledException("per-VM suspend timeout");
            }
            if (argv.Count >= 2 && argv[1] == "delete")
            {
                deleteCalls++;
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ((ISuspendableSandbox)sandbox).SuspendAsync());

        Assert.True(suspendCalls >= 1);
        // IsSuspended stays false: the VM is not confirmed frozen, so
        // PipelineRunner relies on the persisted mapping gate, not IsSuspended.
        Assert.False(((ISuspendableSandbox)sandbox).IsSuspended);

        // DisposeAsync must be a no-op: the VM multipassd is still snapshotting
        // is owned by the orchestrator's resume/reaper path, not destroyed here.
        await sandbox.DisposeAsync();
        Assert.Equal(0, deleteCalls);
    }

    [Fact]
    public void MemoryBytes_ReflectsSpecLimits_ForSuspendTimeoutScaling()
    {
        // SandboxSuspendOnShutdownService.SuspendTimeoutFor scales the per-VM
        // suspend timeout by ISuspendableSandbox.MemoryBytes. MultipassSandbox
        // must surface the provisioned RAM from its SandboxSpec.Limits — and
        // specifically the *memory* field, not disk — so scaling is fed a real
        // value rather than the flat floor. DiskBytes is deliberately set to a
        // different value so a regression that read the wrong field is caught.
        const long sixGiB = 6L * 1024 * 1024 * 1024;
        const long fortyGiB = 40L * 1024 * 1024 * 1024;
        var sandbox = NewMemorySandbox(new SandboxResourceLimits
        {
            MemoryBytes = sixGiB,
            DiskBytes = fortyGiB,
            CpuCount = 4,
        });

        Assert.Equal(sixGiB, ((ISuspendableSandbox)sandbox).MemoryBytes);

        // No reported RAM → null, so SuspendTimeoutFor falls back to the flat
        // floor rather than scaling off a bogus value. A hardcoded non-null
        // would break this.
        var unsized = NewMemorySandbox(new SandboxResourceLimits { DiskBytes = fortyGiB });
        Assert.Null(((ISuspendableSandbox)unsized).MemoryBytes);
    }

    private MultipassSandbox NewMemorySandbox(SandboxResourceLimits limits) =>
        new(
            "codeybox-mem",
            _workspace,
            new SandboxSpec
            {
                ImageReference = "ignored",
                Flavor = SandboxProfileFlavor.Headless,
                Limits = limits,
            },
            new MultipassSandboxOptions { MultipassBinary = "/bin/true" },
            NullLogger<MultipassSandboxProvider>.Instance,
            runner: new RecordingMultipassRunner((_, _, _) => Task.FromResult(new ProcessRunResult(0, "", ""))));

    [Fact]
    public async Task ResumeSandboxAsync_WaitsForSuspendingToSettleBeforeStart()
    {
        // The previous process may have abandoned `multipass suspend` mid-flight,
        // leaving the VM in the transitional `Suspending` state. `multipass start`
        // fails against a Suspending instance, so ResumeSandboxAsync must poll
        // `multipass info` until the state settles before calling start.
        var infoCalls = 0;
        var startCalls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=json"])
            {
                infoCalls++;
                // First two polls report Suspending, then the snapshot completes.
                var state = infoCalls < 3 ? "Suspending" : "Suspended";
                var info = new Dictionary<string, object>
                {
                    [infoName] = new Dictionary<string, object?>
                    {
                        ["state"] = state,
                        ["memory"] = new Dictionary<string, object> { ["total"] = 1024L * 1024 * 1024 },
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-settling", CancellationToken.None);

        Assert.True(infoCalls >= 3, $"expected the resume to poll info until settled; saw {infoCalls} call(s)");
        Assert.Equal(["codeybox-settling"], startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_WaitLoop_HonoursCancellationWhileStillSuspending()
    {
        // The Suspending wait is bounded by the RAM-scaled SuspendTimeoutPolicy
        // budget (up to 30 min for the default 12 GiB VM), so the loop cannot just
        // ignore the caller's token and block for that long if shutdown / startup
        // is itself aborted. With the VM held permanently at Suspending, cancelling
        // the token must interrupt the wait promptly — the resume surfaces
        // OperationCanceledException and never reaches `multipass start`.
        var startCalls = new List<string>();
        using var cts = new CancellationTokenSource();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=json"])
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(20));
                var info = new Dictionary<string, object>
                {
                    [infoName] = new Dictionary<string, object?>
                    {
                        ["state"] = "Suspending",
                        ["memory"] = new Dictionary<string, object> { ["total"] = 12L * 1024 * 1024 * 1024 },
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-stuck", cts.Token));
        Assert.Empty(startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_StillSuspendingPastBudget_StartsAnyway()
    {
        // A VM that never leaves Suspending must not strand the resume: once the
        // RAM-scaled settle budget elapses, WaitWhileSuspendingAsync logs a
        // warning and proceeds to `multipass start` regardless, letting start
        // surface any real error into the standard recovery path. The budget is
        // floored at 10 min in production, so the test injects a tiny override to
        // drive the deadline-expiry branch without waiting out real time.
        var infoCalls = 0;
        var startCalls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", var infoName, "--format=json"])
            {
                infoCalls++;
                // Permanently Suspending — the snapshot never completes.
                var info = new Dictionary<string, object>
                {
                    [infoName] = new Dictionary<string, object?>
                    {
                        ["state"] = "Suspending",
                        ["memory"] = new Dictionary<string, object> { ["total"] = 4L * 1024 * 1024 * 1024 },
                    },
                };
                return Task.FromResult(new ProcessRunResult(0, JsonSerializer.Serialize(new { info }), ""));
            }
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));
        provider.SuspendSettleBudgetOverride = TimeSpan.FromMilliseconds(50);

        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-stillstuck", CancellationToken.None);

        Assert.True(infoCalls >= 1, $"expected at least the initial Suspending probe; saw {infoCalls} call(s)");
        Assert.Equal(["codeybox-stillstuck"], startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_RunsMultipassStartWithValidatedName()
    {
        var startCalls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "start", var name])
            {
                startCalls.Add(name);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));
        await ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-abc123", CancellationToken.None);

        Assert.Equal(["codeybox-abc123"], startCalls);
    }

    [Fact]
    public async Task ResumeSandboxAsync_RejectsInvalidName()
    {
        var runner = new RecordingMultipassRunner((_, _, _) => Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox/../../etc/passwd", CancellationToken.None));
    }

    [Fact]
    public async Task ResumeSandboxAsync_NonZeroExit_Throws()
    {
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "start", _])
                return Task.FromResult(new ProcessRunResult(2, "", "multipassd: instance not found"));
            // multipass version probe responses
            return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
        });
        var provider = NewProvider(runner: runner, stagingDirectory: Path.Combine(_workspace, "staging"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((ISuspendingSandboxProvider)provider).ResumeSandboxAsync("codeybox-zzz", CancellationToken.None));
        Assert.Contains("multipass start", ex.Message);
        Assert.Contains("instance not found", ex.Message);
    }

    [Fact]
    public async Task SuspendableOwners_Registry_PopulatesOnCreateWithWorkItemAndClearsOnDispose()
    {
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = BuildSuccessfulCreateRunner(states);
        var provider = NewProvider(
            stagingDirectory: Path.Combine(_workspace, "staging"),
            runner: runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        // CreateAsync WITHOUT TimingWorkItemId — must NOT register (no owner
        // to suspend back to). The snapshot stays empty.
        var sandboxNoOwner = await provider.CreateAsync(new SandboxSpec { ImageReference = "ignored" });
        Assert.Empty(((ISuspendingSandboxProvider)provider).SnapshotSuspendableActive());

        // CreateAsync WITH TimingWorkItemId — populated; snapshot returns one entry.
        var workItemId = WorkItemId.New();
        var sandboxWithOwner = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            TimingWorkItemId = workItemId,
        });
        var snapshot = ((ISuspendingSandboxProvider)provider).SnapshotSuspendableActive();
        Assert.Single(snapshot);
        Assert.Equal(workItemId, snapshot[0].WorkItemId);

        // Dispose removes the owner from the snapshot — defends against the
        // suspend handler trying to freeze a sandbox that just released.
        await sandboxWithOwner.DisposeAsync();
        Assert.Empty(((ISuspendingSandboxProvider)provider).SnapshotSuspendableActive());

        await sandboxNoOwner.DisposeAsync();
    }

    /// <summary>
    /// Build a RecordingMultipassRunner that satisfies the full CreateAsync
    /// happy path (launch → cloud-init wait → stop → start → transfer env →
    /// chmod → optional delete). Used by tests that exercise side effects of
    /// the lifecycle (registry population, etc.) rather than the lifecycle
    /// itself.
    /// </summary>
    private static RecordingMultipassRunner BuildSuccessfulCreateRunner(ConcurrentDictionary<string, string> states)
    {
        return new RecordingMultipassRunner((argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (argv is [_, "version"])
                return Task.FromResult(new ProcessRunResult(0, "multipass 1.16.0", ""));
            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                states[argv[3]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "info", var name, "--format=csv"])
                return Task.FromResult(new ProcessRunResult(
                    0, states.TryGetValue(name, out var current) ? current : "Running", ""));
            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            if (argv is [_, "delete", "--purge", var deleteName])
            {
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", "unexpected argv"));
        });
    }

    [Fact]
    public void ExecWrapper_TeesStdoutAndStderrWhenAgentLogFileEnvIsSet()
    {
        // R8-core: the codeybox-exec wrapper inside the VM honours
        // CODEYBOX_AGENT_LOG_FILE so the orchestrator can re-tail what the
        // agent emitted after a multipass suspend/start cycle. We verify the
        // wrapper script text directly because we can't shell-exec inside a
        // unit test.
        Assert.Contains("CODEYBOX_AGENT_LOG_FILE", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains("tee -a \"$CODEYBOX_AGENT_LOG_FILE\"", MultipassSandboxProvider.ExecWrapperScript);
        Assert.Contains(".exit", MultipassSandboxProvider.ExecWrapperScript);
        // Defence-in-depth: the existing stdin-close behaviour stays intact
        // when the agent log path is set.
        Assert.Contains("\"$@\" </dev/null 2>&1 | tee -a", MultipassSandboxProvider.ExecWrapperScript);
    }

    /// <summary>
    /// <c>ExecAsync</c> must retry when the underlying multipass exec fails with
    /// a transient SSH-not-ready error ("Connection refused"). The retry wrapper
    /// around <c>RunMultipassAsync</c> gives sshd one more opportunity to accept
    /// the connection before the work item sees a failure.
    /// </summary>
    [Fact]
    public async Task ExecAsync_RetriesOnSshNotReady_ReturnsSuccessOnRetry()
    {
        var attempts = 0;
        var runner = new RecordingMultipassRunner((_, _, _) =>
        {
            attempts++;
            if (attempts == 1)
                return Task.FromResult(new ProcessRunResult(1, "",
                    "ssh connection failed: 'Connection refused'"));
            return Task.FromResult(new ProcessRunResult(0, "ok", ""));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["git", "add", "-A"] });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Stdout);
        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// Non-SSH errors (e.g. "git: command not found") must NOT trigger a retry.
    /// The retry wrapper must fail fast so callers see the real diagnostic
    /// instead of burning the retry budget on what was never a transient failure.
    /// </summary>
    [Fact]
    public async Task ExecAsync_DoesNotRetryNonSshError()
    {
        var attempts = 0;
        var runner = new RecordingMultipassRunner((_, _, _) =>
        {
            attempts++;
            return Task.FromResult(new ProcessRunResult(127, "", "git: command not found"));
        });
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        var result = await sandbox.ExecAsync(new SandboxExec { Argv = ["git", "status"] });

        Assert.Equal(127, result.ExitCode);
        Assert.Contains("git: command not found", result.Stderr);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ProvisioningGate_CapsConcurrentBootsAtConfiguredMax(int maxBoots)
    {
        var provider = NewProvider(maxConcurrentBoots: maxBoots);
        var concurrentCount = 0;
        var maxObserved = 0;
        var lockObj = new object();
        var blocker = new SemaphoreSlim(0);
        var filledBarrier = new TaskCompletionSource();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var totalTasks = maxBoots + 4;
        var tasks = new Task[totalTasks];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var opts = new MultipassSandboxOptions { MaxConcurrentBoots = maxBoots };
                using var gate = await provider.EnterBootGateAsync(opts, cts.Token);
                var count = Interlocked.Increment(ref concurrentCount);
                lock (lockObj) { if (count > maxObserved) maxObserved = count; }
                if (count >= maxBoots)
                    filledBarrier.TrySetResult();
                await blocker.WaitAsync(cts.Token);
                Interlocked.Decrement(ref concurrentCount);
            });
        }

        // Wait for at least maxBoots tasks to reach the gate (deterministic
        // barrier, not a timeout polling loop).
        await filledBarrier.Task.WaitAsync(cts.Token);

        // Let any queued tasks settle so we can read the peak.
        await Task.Delay(200, cts.Token);

        // Read maxObserved under lock — workers write it under lock, so
        // the assert thread must synchronise to avoid a data race.
        int observed;
        lock (lockObj) { observed = maxObserved; }

        // The gate must prevent more than maxBoots concurrent entries.
        Assert.True(observed <= maxBoots,
            $"Expected at most {maxBoots} concurrent boots, observed {observed}");

        // The gate must allow exactly maxBoots concurrent entries to
        // confirm it's configured to the intended capacity.
        Assert.True(observed == maxBoots,
            $"Expected exactly {maxBoots} concurrent boots, observed {observed}");

        // Release all blockers so tasks can finish cleanly.
        blocker.Release(tasks.Length);

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task BootLaunchDelay_DelaysBeforeReturning()
    {
        const int delayMs = 200;
        var provider = NewProvider(bootLaunchDelay: TimeSpan.FromMilliseconds(delayMs));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opts = new MultipassSandboxOptions
        {
            BootLaunchDelay = TimeSpan.FromMilliseconds(delayMs)
        };

        var sw = Stopwatch.StartNew();
        using var gate = await provider.EnterBootGateAsync(opts, cts.Token);
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(delayMs - 50),
            $"Expected at least {delayMs - 50}ms delay, elapsed {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task BootLaunchDelay_CancellationDuringDelayReleasesSlot()
    {
        // Use capacity 1 so the slot release is observable: after the
        // cancelled task releases its slot, a new task can acquire.
        var provider = NewProvider(maxConcurrentBoots: 1, bootLaunchDelay: TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var opts = new MultipassSandboxOptions
        {
            MaxConcurrentBoots = 1,
            BootLaunchDelay = TimeSpan.FromSeconds(10)
        };

        // Acquire the gate — will succeed, then block on the 10s delay.
        // The CTS fires after 200ms, cancelling the delay.
        var ex = await Record.ExceptionAsync(async () =>
        {
            using var gate = await provider.EnterBootGateAsync(opts, cts.Token);
        });
        Assert.NotNull(ex);
        Assert.True(ex is OperationCanceledException || ex is TaskCanceledException);

        // The cancelled task's catch block must have released the slot, so a
        // new acquisition must succeed (not deadlock). Use zero delay for the
        // verification call so it doesn't get stuck in its own delay.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var optsNoDelay = new MultipassSandboxOptions { MaxConcurrentBoots = 1 };
        using var gate2 = await provider.EnterBootGateAsync(optsNoDelay, cts2.Token);
    }

    [Fact]
    public async Task BootLaunchDelay_NegativeDelayLogsWarning()
    {
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var mutableOpts = new MultipassSandboxOptions
        {
            MaxConcurrentBoots = 1,
            BootLaunchDelay = TimeSpan.FromMilliseconds(-500),
        };
        MultipassSandboxOptions ReadOpts() => mutableOpts;
        var provider = new MultipassSandboxProvider(
            ReadOpts,
            logger,
            timings: null,
            runner: new RecordingMultipassRunner((_, _, _) =>
                Task.FromResult(new ProcessRunResult(0, "", ""))),
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var gate = await provider.EnterBootGateAsync(mutableOpts, cts.Token);

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning);
        Assert.Contains("negative", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootGate_ResizesWhenMaxConcurrentBootsChanges()
    {
        // Create a provider with a mutable options delegate so we can
        // change MaxConcurrentBoots between gate acquisitions.
        var mutableOpts = new MultipassSandboxOptions { MaxConcurrentBoots = 3 };
        MultipassSandboxOptions ReadOpts() => mutableOpts;
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new ProcessRunResult(0, "", "")));
        var provider = new MultipassSandboxProvider(
            ReadOpts,
            NullLogger<MultipassSandboxProvider>.Instance,
            timings: null,
            runner,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Fill the old gate (capacity 3).
        var oldHolders = new List<IDisposable>();
        for (var i = 0; i < 3; i++)
            oldHolders.Add(await provider.EnterBootGateAsync(mutableOpts, cts.Token));

        // Increase capacity to 5. The new semaphore has 5 slots; the old
        // holders still reference the old semaphore and don't count
        // against the new one (transient exceedance window, as documented).
        mutableOpts = mutableOpts with { MaxConcurrentBoots = 5 };

        // Up to 5 new acquirers can enter the new semaphore.
        var midHolders = new List<IDisposable>();
        for (var i = 0; i < 5; i++)
            midHolders.Add(await provider.EnterBootGateAsync(mutableOpts, cts.Token));

        foreach (var h in midHolders) h.Dispose();
        foreach (var h in oldHolders) h.Dispose();

        // Downward resize: change to 1.
        mutableOpts = mutableOpts with { MaxConcurrentBoots = 1 };
        var downHolder = await provider.EnterBootGateAsync(mutableOpts, cts.Token);

        // Second acquisition must block (capacity 1, already held).
        var blockedTask = provider.EnterBootGateAsync(mutableOpts, cts.Token);
        await Task.Delay(200, cts.Token);
        Assert.False(blockedTask.IsCompleted, "Second acquisition should block on the 1-slot gate");

        downHolder.Dispose();
        using var released = await blockedTask;
    }

    [Fact]
    public async Task ProvisioningGate_CapsConcurrentLaunchesThroughCreateAsync()
    {
        const int maxBoots = 2;
        var staging = Path.Combine(_workspace, "provider-gate-createasync-staging");

        var concurrentLaunches = 0;
        var maxConcurrentLaunches = 0;
        var lockObj = new object();
        var launchEntered = new TaskCompletionSource();
        var allowLaunch = new TaskCompletionSource();
        var allLaunchesStarted = new TaskCompletionSource();

        // Runner that tracks concurrent in-flight launch operations and
        // blocks them until signalled. Also tracks VM states so the full
        // CreateAsync lifecycle (launch→stop→start→transfer→chmod) succeeds.
        var vmStates = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return new ProcessRunResult(0, "multipass 1.16.0", "");

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                var vmName = argv[3];
                vmStates[vmName] = "Running";

                var c = Interlocked.Increment(ref concurrentLaunches);
                lock (lockObj)
                {
                    if (c > maxConcurrentLaunches) maxConcurrentLaunches = c;
                    if (maxConcurrentLaunches >= maxBoots)
                        allLaunchesStarted.TrySetResult();
                }
                launchEntered.TrySetResult();
                await allowLaunch.Task.WaitAsync(ct);
                Interlocked.Decrement(ref concurrentLaunches);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "info", var infoName, "--format=csv"])
                return new ProcessRunResult(0,
                    vmStates.TryGetValue(infoName, out var state) ? state : "Running", "");

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "stop", var stopName])
            {
                vmStates[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                // start is also gated but the test focuses on launch
                // concurrency. Return immediately so the lifecycle proceeds.
                vmStates[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var dest]
                && dest.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                vmStates.TryRemove(deleteName, out _);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(0, "", "");
        });

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            maxConcurrentBoots: maxBoots,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start 6 concurrent CreateAsync calls (all non-baseline path).
        var createTasks = new Task<ISandbox>[6];
        for (var i = 0; i < createTasks.Length; i++)
        {
            createTasks[i] = Task.Run<ISandbox>(async () =>
            {
                return await provider.CreateAsync(
                    new SandboxSpec
                    {
                        ImageReference = "ignored",
                        WorkingDirectory = "/work",
                    },
                    ct: cts.Token);
            });
        }

        // Wait until at least maxBoots launches have entered, confirming
        // the gate is active and capping concurrency.
        await allLaunchesStarted.Task.WaitAsync(cts.Token);

        // Brief settle delay so tasks queued at the gate don't increase
        // the count further yet.
        await Task.Delay(200, cts.Token);

        int observedLaunches;
        lock (lockObj) { observedLaunches = maxConcurrentLaunches; }

        Assert.True(observedLaunches <= maxBoots,
            $"Expected at most {maxBoots} concurrent launches through CreateAsync, observed {observedLaunches}");

        Assert.True(observedLaunches == maxBoots,
            $"Expected exactly {maxBoots} concurrent launches through CreateAsync, observed {observedLaunches}");

        // Release all blocked launches.
        allowLaunch.SetResult();

        await Task.WhenAll(createTasks);
        foreach (var t in createTasks)
        {
            var sandbox = await t;
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProvisioningGate_CapsConcurrentBaselineBakes()
    {
        const int maxBoots = 1;
        var staging = Path.Combine(_workspace, "provider-gate-baseline-staging");

        var concurrentBakes = 0;
        var maxConcurrentBakes = 0;
        var lockObj = new object();
        var allowBake = new TaskCompletionSource();
        var allBakesStarted = new TaskCompletionSource();

        var vmStates = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var runner = new RecordingMultipassRunner(async (argv, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();

            if (argv is [_, "version"])
                return new ProcessRunResult(0, "multipass 1.16.0", "");

            if (argv is [_, "info", var infoName, "--format=csv"])
            {
                if (vmStates.TryGetValue(infoName, out var s))
                    return new ProcessRunResult(0, s, "");
                return new ProcessRunResult(1, "", "");
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name"
                && argv[3].StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                var vmName = argv[3];
                vmStates[vmName] = "Running";

                var c = Interlocked.Increment(ref concurrentBakes);
                lock (lockObj)
                {
                    if (c > maxConcurrentBakes) maxConcurrentBakes = c;
                    if (maxConcurrentBakes >= maxBoots)
                        allBakesStarted.TrySetResult();
                }
                await allowBake.Task.WaitAsync(ct);
                Interlocked.Decrement(ref concurrentBakes);
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "exec", _, "--", "cloud-init", "status", "--wait"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "stop", var stopName])
            {
                vmStates[stopName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "clone", var sourceName, "--name", var newName])
            {
                vmStates[newName] = "Stopped";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                vmStates[startName] = "Running";
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var dest]
                && dest.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new ProcessRunResult(0, "", "");

            if (argv is [_, "delete", "--purge", var delName])
            {
                vmStates.TryRemove(delName, out _);
                return new ProcessRunResult(0, "", "");
            }

            return new ProcessRunResult(0, "", "");
        });

        var networkProfiles = new Dictionary<string, string>
        {
            ["test-iso"] = "cb-iso",
            ["test-claude"] = "cb-claude",
        };

        var provider = NewProvider(
            stagingDirectory: staging,
            runner: runner,
            networkProfiles: networkProfiles,
            useBaselineImages: true,
            maxConcurrentBoots: maxBoots,
            daemonRetryPolicy: InstantDaemonRetryPolicy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var profiles = new[] { "test-iso", "test-claude" };
        var createTasks = new Task<ISandbox>[2];
        for (var i = 0; i < createTasks.Length; i++)
        {
            var profile = profiles[i];
            createTasks[i] = Task.Run<ISandbox>(async () =>
            {
                return await provider.CreateAsync(
                    new SandboxSpec
                    {
                        ImageReference = "ignored",
                        WorkingDirectory = "/work",
                        Network = new SandboxNetworkPolicy { ProfileName = profile },
                    },
                    ct: cts.Token);
            });
        }

        await allBakesStarted.Task.WaitAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        int observed;
        lock (lockObj) { observed = maxConcurrentBakes; }

        Assert.True(observed <= maxBoots,
            $"Expected at most {maxBoots} concurrent baseline bakes, observed {observed}");
        Assert.True(observed == maxBoots,
            $"Expected exactly {maxBoots} concurrent baseline bakes, observed {observed}");

        allowBake.SetResult();

        await Task.WhenAll(createTasks);
        foreach (var t in createTasks)
        {
            var sandbox = await t;
            await sandbox.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProvisioningGate_ClampsInvalidMaxConcurrentBootsToOne()
    {
        var logger = new RecordingLogger<MultipassSandboxProvider>();
        var provider = NewProvider(maxConcurrentBoots: 0, logger: logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var opts = new MultipassSandboxOptions { MaxConcurrentBoots = 0 };

        using var gate = await provider.EnterBootGateAsync(opts, cts.Token);

        var warning = logger.Entries.SingleOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warning);
        Assert.Contains("clamping to 1", warning.Message);
        Assert.Contains("0", warning.Message);
    }

    private static MultipassDaemonRetryPolicy InstantDaemonRetryPolicy() => new()
    {
        Delay = (_, _) => Task.CompletedTask,
        HealthProbeTimeout = TimeSpan.FromMilliseconds(100),
    };
}
