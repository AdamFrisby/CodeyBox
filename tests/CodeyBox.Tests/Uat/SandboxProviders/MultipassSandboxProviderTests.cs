using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;

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
        Assert.Contains("systemctl enable --now codeybox-route.service", cloudInit);
        Assert.Contains("apt-get update", cloudInit);
        Assert.Contains("npm install -g @anthropic-ai/claude-code", cloudInit);
        Assert.Contains("packages:\n  - git", cloudInit);
        Assert.DoesNotContain("iptables", cloudInit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ufw", cloudInit, StringComparison.OrdinalIgnoreCase);
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
            Task.FromResult(new RunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        await sandbox.ExecAsync(new SandboxExec { Argv = ["printenv", "DISPLAY"] });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.Contains($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task ExecAsync_GraphicalSandboxPreservesCallerDisplayOverride()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new RunResult(0, "", "")));
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
            Task.FromResult(new RunResult(0, "", "")));
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
            Task.FromResult(new RunResult(0, "", "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, runner);

        await sandbox.ExecAsync(new SandboxExec { Argv = ["printenv", "DISPLAY"] });

        var argv = Assert.Single(runner.Calls).Argv;
        Assert.DoesNotContain($"DISPLAY={SandboxConventions.GraphicalDisplay}", argv);
    }

    [Fact]
    public async Task GraphicalCapabilities_RejectHeadlessSandbox()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Headless, (_, _, _) =>
            Task.FromResult(new RunResult(0, "", "")));

        await Assert.ThrowsAsync<NotSupportedException>(() => sandbox.GetScreenshotAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = SandboxInputEventType.Click }]));
    }

    [Fact]
    public async Task GetScreenshotAsync_ThrowsWhenScrotFails()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new RunResult(2, "", "scrot failed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("graphical screenshot failed", ex.Message);
        Assert.Contains("scrot failed", ex.Message);
    }

    [Fact]
    public async Task GetScreenshotAsync_ThrowsWhenCommandReturnsInvalidBase64()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new RunResult(0, "not base64", "")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("invalid base64", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetScreenshotAsync_ReturnsDecodedPngBytes()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new RunResult(0, Convert.ToBase64String(TinyPng), "")));
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, runner);

        var screenshot = await sandbox.GetScreenshotAsync();

        Assert.Equal(TinyPng, screenshot);
        AssertPngSignature(screenshot);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsDecodedNonPngBytes()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new RunResult(0, Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]), "")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.GetScreenshotAsync());

        Assert.Contains("non-PNG", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetScreenshotAsync_RejectsOutputPastCaptureLimit()
    {
        var runner = new RecordingMultipassRunner((_, _, _) =>
            Task.FromResult(new RunResult(
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
            Task.FromResult(new RunResult(
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
            Task.FromResult(new RunResult(0, Convert.ToBase64String(new byte[5]), "")));
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

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
            Task.FromResult(new RunResult(1, "", "xdotool failed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.SynthesizeInputAsync([new SandboxInputEvent { Type = SandboxInputEventType.Key, Key = "Return" }]));

        Assert.Contains("graphical input event 'Key' failed", ex.Message);
        Assert.Contains("xdotool failed", ex.Message);
    }

    [Fact]
    public async Task SynthesizeInputAsync_RejectsMalformedInputEvents()
    {
        var sandbox = NewMultipassSandbox(SandboxProfileFlavor.Graphical, (_, _, _) =>
            Task.FromResult(new RunResult(0, "", "")));
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
            Task.FromResult(new RunResult(0, "", "")));

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
            Task.FromResult(new RunResult(0, "", "")));
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
            Task.FromResult(new RunResult(0, "", "")));
        var provider = NewProvider(runner: runner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            provider.DisposeLeakedAsync("codeybox-bad/../../../escape", CancellationToken.None));

        Assert.Empty(runner.Calls);
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
                return Task.FromResult(new RunResult(0, """
                    {"list":[
                      {"name":"primary"},
                      {"name":"cb-baseline-claude"},
                      {"name":"codeybox-alpha"},
                      {"name":"codeybox-beta"},
                      {"name":"codeybox-invalid.name"}
                    ]}
                    """, ""));
            }

            if (argv.Count >= 4 && argv[1] == "info")
            {
                infoCalls++;
                return Task.FromResult(new RunResult(0, """
                    {"info":{
                      "codeybox-alpha":{"disks":{"sda1":{"used":"1048576"}}},
                      "codeybox-beta":{"disks":{"sda1":{"used":"2097152"}}}
                    }}
                    """, ""));
            }

            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + string.Join(' ', argv)));
        });
        var provider = NewProvider(stagingDirectory: staging, runner: runner);

        var first = await provider.ListAllManagedAsync(CancellationToken.None);
        var second = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, listCalls);
        Assert.Equal(1, infoCalls);
        Assert.Equal(["codeybox-alpha", "codeybox-beta"], first.Select(s => s.Name).ToArray());
        Assert.All(first, s => Assert.NotNull(s.CreatedAt));
        Assert.Equal(1024 * 1024, first.Single(s => s.Name == "codeybox-alpha").DiskBytes);
        Assert.True(first.Single(s => s.Name == "codeybox-beta").HasPreemptMarker);
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
                    return new RunResult(0, state, "");
                return new RunResult(1, "", "not found");
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
                return new RunResult(0, "", "");
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return new RunResult(states.ContainsKey(execName) ? 0 : 1, "", "");

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", ..]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref installCount);
                return new RunResult(0, "", "");
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return new RunResult(0, "", "");
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                Assert.StartsWith("cb-baseline-", source, StringComparison.Ordinal);
                states[cloneName] = "Stopped";
                Interlocked.Increment(ref cloneCount);
                return new RunResult(0, "", "");
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return new RunResult(0, "", "");
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return new RunResult(0, "", "");

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return new RunResult(0, "", "");

            return new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv));
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
                    return Task.FromResult(new RunResult(0, state, ""));
                return Task.FromResult(new RunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                baselineLaunchName = argv[3];
                var networkIndex = argv.ToList().IndexOf("--network");
                baselineLaunchNetwork = networkIndex >= 0 ? argv[networkIndex + 1] : null;
                var cloudInitIndex = argv.ToList().IndexOf("--cloud-init");
                baselineCloudInit = cloudInitIndex >= 0 ? File.ReadAllText(argv[cloudInitIndex + 1]) : null;
                states[baselineLaunchName] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new RunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", var command]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
            {
                installCommands.Add(command);
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSource = source;
                states[cloneName] = "Stopped";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new RunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new RunResult(0, "", ""));

            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
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

        Assert.Equal("cb-baseline-graphical", baselineLaunchName);
        Assert.Equal("name=cb-graphical,mode=auto", baselineLaunchNetwork);
        Assert.Equal("cb-baseline-graphical", cloneSource);
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
                    return Task.FromResult(new RunResult(0, state, ""));
                return Task.FromResult(new RunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                baselineLaunchName = argv[3];
                var networkIndex = argv.ToList().IndexOf("--network");
                baselineLaunchNetwork = networkIndex >= 0 ? argv[networkIndex + 1] : null;
                states[baselineLaunchName] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
                return Task.FromResult(new RunResult(states.ContainsKey(execName) ? 0 : 1, "", ""));

            if (argv is [_, "exec", var installName, "--", "sudo", "bash", "-c", _]
                && installName.StartsWith("cb-baseline-", StringComparison.Ordinal))
                return Task.FromResult(new RunResult(0, "", ""));

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "clone", var source, "--name", var cloneName])
            {
                cloneSource = source;
                states[cloneName] = "Stopped";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "start", var startName])
            {
                states[startName] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "transfer", _, var destination]
                && destination.EndsWith(":.codeybox-env", StringComparison.Ordinal))
                return Task.FromResult(new RunResult(0, "", ""));

            if (argv is [_, "exec", _, "--", "chmod", "0600", "/home/ubuntu/.codeybox-env"])
                return Task.FromResult(new RunResult(0, "", ""));

            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
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

        Assert.Equal("cb-baseline-graphical-ci", baselineLaunchName);
        Assert.Equal("name=cb-ci,mode=auto", baselineLaunchNetwork);
        Assert.Equal("cb-baseline-graphical-ci", cloneSource);
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
                    ? Task.FromResult(new RunResult(0, state, ""))
                    : Task.FromResult(new RunResult(1, "", "not found"));
            }

            if (argv.Count >= 4 && argv[1] == "launch" && argv[2] == "--name")
            {
                launchedName = argv[3];
                states[launchedName] = "Running";
                return Task.FromResult(new RunResult(0, "", ""));
            }

            if (argv is [_, "exec", var execName, "--", "cloud-init", "status", "--wait"])
            {
                cloudInitTarget = execName;
                return Task.FromResult(new RunResult(3, "", "schema validation failed: bad runcmd"));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deletedName = deleteName;
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new RunResult(0, "", ""));
            }

            return Task.FromResult(new RunResult(99, "", "unexpected argv: " + JsonSerializer.Serialize(argv)));
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
    public async Task RetryHelper_RetriesTransientSshReadinessWithoutRealDelays()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        var result = await MultipassRetry.RunWithRetryAsync(
            action: _ => Task.FromResult(++attempts == 1
                ? new RunResult(1, "", "ssh connection failed: Connection refused")
                : new RunResult(0, "ok", "")),
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
        RecordingMultipassRunner? runner = null)
    {
        var options = new MultipassSandboxOptions
        {
            MultipassBinary = runner is null ? "multipass" : "/bin/false",
            StagingDirectory = stagingDirectory,
            NetworkProfiles = networkProfiles ?? new Dictionary<string, string>(),
            UseBaselineImages = useBaselineImages,
            ExtraRuncmd = extraRuncmd ?? [],
        };

        return runner is null
            ? new MultipassSandboxProvider(options, NullLogger<MultipassSandboxProvider>.Instance)
            : new MultipassSandboxProvider(options, NullLogger<MultipassSandboxProvider>.Instance, null, runner);
    }

    private MultipassSandbox NewMultipassSandbox(
        SandboxProfileFlavor flavor,
        Func<IReadOnlyList<string>, string?, CancellationToken, Task<RunResult>> handler,
        int? maxScreenshotPngBytes = null)
    {
        return NewMultipassSandbox(flavor, new RecordingMultipassRunner(handler), maxScreenshotPngBytes);
    }

    private MultipassSandbox NewMultipassSandbox(
        SandboxProfileFlavor flavor,
        RecordingMultipassRunner runner,
        int? maxScreenshotPngBytes = null)
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
            new MultipassSandboxOptions { MultipassBinary = "/bin/true" },
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
}
