using System.Collections.Concurrent;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.MultipassRemote;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="MultipassRemoteSandboxProvider"/> — the SSH-driven
/// remote multipass provider that lets the single-orchestrator-brain deployment
/// run sandboxes on a different machine.
///
/// We cannot drive a real remote multipass daemon in CI, so the tests use a
/// scriptable <see cref="FakeRemoteHostTransport"/> that records the argv
/// passed to it and returns pre-canned <see cref="ProcessRunResult"/>s or
/// synthesised transport drops. This exercises the contract end-to-end:
/// CreateAsync staging + launch, ExecAsync streaming, transport-drop
/// classification, list, dispose.
/// </summary>
public sealed class MultipassRemoteSandboxProviderTests
{
    private static MultipassRemoteSandboxOptions DefaultOptions() => new()
    {
        SshTarget = "codeybox@remote.example",
        SshBinary = "ssh",
        RemoteMultipassPath = "/snap/bin/multipass",
        RemoteStagingRoot = "/home/codeybox/snap/multipass/common/codeybox-remote-staging",
        VmNamePrefix = "codeybox-r-",
    };

    [Fact]
    public async Task CreateAsync_launches_remote_vm_and_returns_sandbox()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "launch")) return ProcessRunOk();
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            if (Contains(argv, "delete")) return ProcessRunOk();
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var sb = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "24.04",
            Mounts = [],
            WorkingDirectory = "/work",
        });

        Assert.NotNull(sb);
        Assert.StartsWith(opts.VmNamePrefix, sb.Id);
        Assert.Contains(transport.RecordedCalls, c =>
            c.Argv.Contains("launch") && c.Argv.Contains("--name") && c.Argv.Contains(sb.Id));
        Assert.Contains(transport.RecordedCalls, c =>
            c.Argv.Contains("info") && c.Argv.Contains(sb.Id));
        await sb.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_with_baseline_ref_clones_remote_baseline_instead_of_launching()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        const string baseline = "cb-e2e-baseline";
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info"))
            {
                var vm = VmNameFromInfo(argv);
                return InfoJson(vm, vm == baseline ? "Stopped" : "Running");
            }
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var sb = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            BaselineImageRef = baseline,
            WorkingDirectory = "/work",
        });

        var calls = transport.RecordedCalls.Select(c => c.Argv).ToList();
        var stopBaselineIndex = calls.FindIndex(argv =>
            argv.SequenceEqual([opts.RemoteMultipassPath, "stop", baseline]));
        var cloneIndex = calls.FindIndex(argv =>
            argv.SequenceEqual([opts.RemoteMultipassPath, "clone", baseline, "--name", sb.Id]));
        var startCloneIndex = calls.FindIndex(argv =>
            argv.SequenceEqual([opts.RemoteMultipassPath, "start", sb.Id]));
        var infoCloneIndex = calls.FindIndex(argv =>
            argv.SequenceEqual([opts.RemoteMultipassPath, "info", sb.Id, "--format", "json"]));

        Assert.True(stopBaselineIndex >= 0, "baseline VM must be stopped before clone");
        Assert.True(cloneIndex > stopBaselineIndex, "clone must happen after stopping the baseline");
        Assert.True(startCloneIndex > cloneIndex, "cloned VM must be started after clone");
        Assert.True(infoCloneIndex > startCloneIndex, "running-state check must happen after starting the clone");
        Assert.DoesNotContain(transport.RecordedCalls, c => c.Argv.Contains("launch"));

        await sb.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_streams_stdout_chunks_to_callback_in_order()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        var capturedStream = new List<string>();
        transport.OnRun = (argv, stream) =>
        {
            if (Contains(argv, "launch")) return ProcessRunOk();
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            if (Contains(argv, "delete")) return ProcessRunOk();
            if (Contains(argv, "exec"))
            {
                // Simulate three async stdout chunks as the agent CLI
                // writes its progress.
                stream.Stdout?.Invoke("chunk-a\n");
                stream.Stdout?.Invoke("chunk-b\n");
                stream.Stdout?.Invoke("chunk-c\n");
                return new ProcessRunResult(0, "chunk-a\nchunk-b\nchunk-c\n", "");
            }
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var sb = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "24.04",
            WorkingDirectory = "/work",
        });

        var result = await sb.ExecAsync(new SandboxExec
        {
            Argv = ["echo", "hello"],
            StdoutChunkCallback = chunk => capturedStream.Add(chunk),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "chunk-a\n", "chunk-b\n", "chunk-c\n" }, capturedStream);
        await sb.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_surfaces_nonzero_remote_exit_code_as_result_not_exception()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "launch")) return ProcessRunOk();
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            if (Contains(argv, "delete")) return ProcessRunOk();
            if (Contains(argv, "exec")) return new ProcessRunResult(42, "", "boom");
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var sb = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });

        var result = await sb.ExecAsync(new SandboxExec { Argv = ["false"] });

        Assert.Equal(42, result.ExitCode);
        Assert.Contains("boom", result.Stderr);
        await sb.DisposeAsync();
    }

    [Fact]
    public async Task ExecAsync_propagates_transport_drop_as_RemoteSshTransportException()
    {
        // An SSH transport drop must NOT silently become a "command exited
        // non-zero" — the orchestrator needs to classify it as recoverable
        // sandbox failure (re-pickup) rather than an agent crash.
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        var execCalls = 0;
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "launch")) return ProcessRunOk();
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            if (Contains(argv, "delete")) return ProcessRunOk();
            if (Contains(argv, "exec"))
            {
                execCalls++;
                throw new RemoteSshTransportException("ssh: connection refused");
            }
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var sb = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });

        var ex = await Assert.ThrowsAsync<RemoteSshTransportException>(async () =>
            await sb.ExecAsync(new SandboxExec { Argv = ["echo", "x"] }));
        Assert.Contains("connection refused", ex.Message);
        Assert.Equal(1, execCalls);
        await sb.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_cleans_up_remote_state_when_launch_fails()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "launch")) return new ProcessRunResult(1, "", "image not found");
            if (Contains(argv, "delete")) return ProcessRunOk();
            if (Contains(argv, "info")) return new ProcessRunResult(2, "", "not found");
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.CreateAsync(new SandboxSpec { ImageReference = "bogus" }));

        // Cleanup must have tried to delete the would-be VM.
        Assert.Contains(transport.RecordedCalls, c =>
            c.Argv.Contains("delete") && c.Argv.Contains("--purge"));
    }

    [Fact]
    public async Task ListAllManagedAsync_filters_to_provider_prefix_and_returns_empty_on_transport_drop()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        var json = """
            {
              "list": [
                { "name": "codeybox-r-aaaaaaaaaaaaaaa", "state": "Running" },
                { "name": "primary",                "state": "Running" }
              ]
            }
            """;
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "list")) return new ProcessRunResult(0, json, "");
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var infos = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Single(infos);
        Assert.StartsWith(opts.VmNamePrefix, infos[0].Name);

        // Now simulate a transport drop on the next call — must not throw.
        transport.OnRun = (argv, _) => throw new RemoteSshTransportException("network down");
        var infosOnDrop = await provider.ListAllManagedAsync(CancellationToken.None);
        Assert.Empty(infosOnDrop);
    }

    [Fact]
    public async Task DisposeAsync_attempts_stop_then_delete_and_syncback_for_writable_mounts()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "launch")) return ProcessRunOk();
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };

        // Set up a writable host bind mount.
        var hostTemp = Path.Combine(Path.GetTempPath(), "codeybox-remote-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostTemp);
        try
        {
            var provider = new MultipassRemoteSandboxProvider(
                opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
            var sb = await provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "24.04",
                Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = hostTemp, ReadOnly = false }],
            });

            Assert.True(transport.StageInCalls.Count >= 1);
            Assert.Contains(transport.StageInCalls, c => c.HostPath == hostTemp);

            await sb.DisposeAsync();

            // Stop, delete, and stage-out for writable mount must all have run.
            Assert.Contains(transport.RecordedCalls, c => c.Argv.Contains("stop"));
            Assert.Contains(transport.RecordedCalls, c => c.Argv.Contains("delete") && c.Argv.Contains("--purge"));
            Assert.Contains(transport.StageOutCalls, c => c.HostPath == hostTemp);
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_skips_syncback_for_readonly_mounts()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };

        var hostTemp = Path.Combine(Path.GetTempPath(), "codeybox-remote-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostTemp);
        try
        {
            var provider = new MultipassRemoteSandboxProvider(
                opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
            var sb = await provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "24.04",
                Mounts = [new SandboxMount { SandboxPath = "/ro", HostPath = hostTemp, ReadOnly = true }],
            });
            await sb.DisposeAsync();
            Assert.Empty(transport.StageOutCalls);
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CreateAsync_throws_SandboxMountSourceMissingException_when_host_path_absent()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (_, _) => ProcessRunOk();

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        await Assert.ThrowsAsync<SandboxMountSourceMissingException>(async () =>
            await provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "24.04",
                Mounts = [new SandboxMount
                {
                    SandboxPath = "/repo",
                    HostPath = "/does/not/exist/" + Guid.NewGuid(),
                    ReadOnly = false,
                }],
            }));
    }

    [Fact]
    public void OpenSshCliTransport_QuoteShellArgv_quotes_specials_correctly()
    {
        // Direct exercise of the quoting helper. Single quotes, dollar signs,
        // backticks must all survive an unbroken round-trip when the remote
        // shell parses the resulting command.
        var argv = new[] { "echo", "hello world", "weird's $stuff", "`back`" };
        var quoted = OpenSshCliTransport.QuoteShellArgv(argv);
        Assert.Equal("'echo' 'hello world' 'weird'\\''s $stuff' '`back`'", quoted);
    }

    [Fact]
    public void OpenSshCliTransport_QuoteShellWord_empty_string_yields_empty_quotes()
    {
        Assert.Equal("''", OpenSshCliTransport.QuoteShellWord(""));
    }

    // ----- helpers ---------------------------------------------------

    private static bool Contains(IReadOnlyList<string> argv, string token)
    {
        foreach (var a in argv) if (a == token) return true;
        return false;
    }

    private static ProcessRunResult ProcessRunOk() => new(0, "", "");

    private static ProcessRunResult RunningInfoJson(string vm) => new(
        0,
        $"{{\"info\":{{\"{vm}\":{{\"state\":\"Running\"}}}}}}",
        "");

    private static ProcessRunResult InfoJson(string vm, string state) => new(
        0,
        $"{{\"info\":{{\"{vm}\":{{\"state\":\"{state}\"}}}}}}",
        "");

    private static string VmNameFromInfo(IReadOnlyList<string> argv)
    {
        var idx = argv.ToList().IndexOf("info");
        return idx >= 0 && idx + 1 < argv.Count ? argv[idx + 1] : "unknown";
    }

    private static string VmNameFromLastLaunch(FakeRemoteHostTransport transport)
    {
        for (var i = transport.RecordedCalls.Count - 1; i >= 0; i--)
        {
            var call = transport.RecordedCalls[i];
            if (!call.Argv.Contains("launch")) continue;
            var idx = call.Argv.ToList().IndexOf("--name");
            if (idx >= 0 && idx + 1 < call.Argv.Count) return call.Argv[idx + 1];
        }
        // Fallback — should not happen in well-formed tests.
        return "unknown";
    }

    internal sealed record StreamSinks(Action<string>? Stdout, Action<string>? Stderr);
    internal sealed record RecordedCall(IReadOnlyList<string> Argv, string? Stdin);
    internal sealed record StageInCall(string HostPath, string RemotePath);
    internal sealed record StageOutCall(string RemotePath, string HostPath);

    internal sealed class FakeRemoteHostTransport : IRemoteHostTransport
    {
        public string DiagnosticId => "fake";
        public ConcurrentQueue<RecordedCall> RecordedCallsQueue { get; } = new();
        public List<RecordedCall> RecordedCalls { get; } = new();
        public List<StageInCall> StageInCalls { get; } = new();
        public List<StageOutCall> StageOutCalls { get; } = new();
        public Func<IReadOnlyList<string>, StreamSinks, ProcessRunResult> OnRun { get; set; } =
            (_, _) => new ProcessRunResult(0, "", "");

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null)
        {
            var call = new RecordedCall(argv.ToArray(), stdin);
            RecordedCalls.Add(call);
            RecordedCallsQueue.Enqueue(call);
            var sinks = new StreamSinks(stdoutChunkCallback, stderrChunkCallback);
            var result = OnRun(argv, sinks);
            return Task.FromResult(result);
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
        {
            StageInCalls.Add(new StageInCall(hostPath, remotePath));
            return Task.CompletedTask;
        }

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
        {
            StageOutCalls.Add(new StageOutCall(remotePath, hostPath));
            return Task.CompletedTask;
        }
    }
}
