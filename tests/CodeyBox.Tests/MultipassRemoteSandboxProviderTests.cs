using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
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
    public async Task CreateAsync_attaches_configured_network_profile_bridge()
    {
        var opts = DefaultOptions() with
        {
            NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = "cb-claude",
            },
        };
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        await using var sb = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "24.04",
            Network = new SandboxNetworkPolicy { ProfileName = "claude" },
        });

        var launch = Assert.Single(transport.RecordedCalls, c => c.Argv.Contains("launch"));
        var networkIndex = launch.Argv.ToList().IndexOf("--network");
        Assert.True(networkIndex >= 0);
        Assert.Equal("name=cb-claude,mode=auto", launch.Argv[networkIndex + 1]);
    }

    [Fact]
    public async Task CreateAsync_rejects_missing_network_profile_before_unprofiled_launch()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "24.04",
                Network = new SandboxNetworkPolicy { ProfileName = "missing-profile" },
            }));

        Assert.Contains("Network profile 'missing-profile' is not configured", ex.Message);
        Assert.DoesNotContain(transport.RecordedCalls, c => c.Argv.Contains("launch"));
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
    public async Task ExecAsync_wraps_transport_drop_as_infrastructure_deferral()
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

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await sb.ExecAsync(new SandboxExec { Argv = ["echo", "x"] }));
        Assert.Equal("exec", ex.Operation);
        Assert.Equal("remote-host-unreachable", ex.ErrorClass);
        Assert.IsType<RemoteSshTransportException>(ex.InnerException);
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

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
            await provider.CreateAsync(new SandboxSpec { ImageReference = "bogus" }));
        Assert.Equal("placement", ex.Operation);
        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);

        // Cleanup must have tried to delete the would-be VM.
        Assert.Contains(transport.RecordedCalls, c =>
            c.Argv.Contains("delete") && c.Argv.Contains("--purge"));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("clone")]
    [InlineData("start")]
    public async Task CreateAsync_with_baseline_ref_cleans_up_remote_state_when_clone_path_fails(string failingCommand)
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        const string baseline = "cb-e2e-baseline";
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, failingCommand))
                return new ProcessRunResult(1, "", $"{failingCommand} failed");
            if (Contains(argv, "info"))
            {
                var vm = VmNameFromInfo(argv);
                return InfoJson(vm, vm == baseline ? "Stopped" : "Running");
            }
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                BaselineImageRef = baseline,
                WorkingDirectory = "/work",
            }));
        Assert.Equal("placement", ex.Operation);
        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
        var hostFailure = Assert.IsType<RemoteHostProvisioningException>(ex.InnerException);
        Assert.Equal(failingCommand, hostFailure.Operation);

        Assert.Contains(transport.RecordedCalls, c =>
            c.Argv.Contains("delete") && c.Argv.Contains("--purge"));
    }

    [Fact]
    public async Task CreateAsync_with_baseline_ref_cleans_up_remote_state_when_waiting_for_stopped_baseline_times_out()
    {
        var opts = DefaultOptions() with
        {
            VmStopTimeout = TimeSpan.FromMilliseconds(20),
            VmStateCheckInterval = TimeSpan.FromMilliseconds(1),
        };
        var transport = new FakeRemoteHostTransport();
        const string baseline = "cb-e2e-baseline";
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info"))
            {
                var vm = VmNameFromInfo(argv);
                return InfoJson(vm, vm == baseline ? "Running" : "Running");
            }
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(() =>
            provider.CreateAsync(new SandboxSpec
            {
                ImageReference = "ignored",
                BaselineImageRef = baseline,
                WorkingDirectory = "/work",
            }));
        Assert.Equal("placement", ex.Operation);
        Assert.Equal("all-hosts-unavailable", ex.ErrorClass);
        var hostFailure = Assert.IsType<RemoteHostProvisioningException>(ex.InnerException);
        Assert.Equal("wait-state", hostFailure.Operation);

        Assert.Contains(transport.RecordedCalls, c =>
            c.Argv.Contains("delete") && c.Argv.Contains("--purge"));
    }

    [Fact]
    public async Task ListAllManagedAsync_marks_remote_clone_active_before_clone_completes()
    {
        var opts = DefaultOptions();
        var cloneStarted = new ManualResetEventSlim(false);
        var releaseClone = new ManualResetEventSlim(false);
        var cloneVmName = "";
        var transport = new FakeRemoteHostTransport();
        const string baseline = "cb-e2e-baseline";
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "clone"))
            {
                cloneVmName = argv[^1];
                cloneStarted.Set();
                Assert.True(releaseClone.Wait(TimeSpan.FromSeconds(5)));
                return ProcessRunOk();
            }
            if (Contains(argv, "list"))
            {
                return new ProcessRunResult(0, $$"""
                    { "list": [ { "name": "{{cloneVmName}}", "state": "Running" } ] }
                    """, "");
            }
            if (Contains(argv, "info"))
            {
                var vm = VmNameFromInfo(argv);
                return InfoJson(vm, vm == baseline ? "Stopped" : "Running");
            }
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var createTask = Task.Run(() => provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "ignored",
            BaselineImageRef = baseline,
            WorkingDirectory = "/work",
        }));
        Assert.True(cloneStarted.Wait(TimeSpan.FromSeconds(5)));

        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        var active = Assert.Single(managed);
        Assert.Equal(cloneVmName, active.Name);
        Assert.True(active.IsTrackedActive);
        releaseClone.Set();
        await using var sandbox = await createTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateAsync_serializes_parallel_remote_heavy_multipass_operations()
    {
        var opts = DefaultOptions();
        const string baseline = "cb-e2e-baseline";
        var activeHeavy = 0;
        var maxHeavy = 0;
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (IsRemoteHeavy(argv, opts.RemoteMultipassPath))
            {
                var current = Interlocked.Increment(ref activeHeavy);
                UpdateMax(ref maxHeavy, current);
                Thread.Sleep(25);
                Interlocked.Decrement(ref activeHeavy);
            }
            if (Contains(argv, "info"))
            {
                var vm = VmNameFromInfo(argv);
                return InfoJson(vm, vm == baseline ? "Stopped" : "Running");
            }
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var first = Task.Run(() => provider.CreateAsync(new SandboxSpec { ImageReference = "ignored", BaselineImageRef = baseline }));
        var second = Task.Run(() => provider.CreateAsync(new SandboxSpec { ImageReference = "ignored", BaselineImageRef = baseline }));
        await using var firstSandbox = await first.WaitAsync(TimeSpan.FromSeconds(5));
        await using var secondSandbox = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, maxHeavy);
    }

    [Fact]
    public async Task DisposeAsync_serializes_parallel_remote_stop_and_delete_operations()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var firstSandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });
        var secondSandbox = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });

        var activeHeavy = 0;
        var maxHeavy = 0;
        transport.OnRun = (argv, _) =>
        {
            if (IsRemoteHeavy(argv, opts.RemoteMultipassPath))
            {
                var current = Interlocked.Increment(ref activeHeavy);
                UpdateMax(ref maxHeavy, current);
                Thread.Sleep(25);
                Interlocked.Decrement(ref activeHeavy);
            }
            return ProcessRunOk();
        };

        await Task.WhenAll(firstSandbox.DisposeAsync().AsTask(), secondSandbox.DisposeAsync().AsTask());

        Assert.Equal(1, maxHeavy);
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
    public async Task ListAllManagedAsync_returns_empty_without_a_transport_call_when_SshTarget_is_blank()
    {
        // An unconfigured (blank SshTarget) E2E provider must NOT enumerate a
        // fleet it is not attached to — otherwise the leak reaper could sweep
        // another fleet's VMs. The guard returns [] before touching the transport.
        var opts = new MultipassRemoteSandboxOptions
        {
            SshTarget = "   ",
            SshBinary = "ssh",
            RemoteMultipassPath = "/snap/bin/multipass",
            RemoteStagingRoot = "/home/codeybox/snap/multipass/common/codeybox-remote-staging",
            VmNamePrefix = "codeybox-r-",
        };
        var transport = new FakeRemoteHostTransport
        {
            OnRun = (_, _) => throw new InvalidOperationException("transport must not be reached when SshTarget is blank"),
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Empty(managed);
        Assert.Empty(transport.RecordedCalls);
    }

    [Fact]
    public async Task ListAllManagedAsync_reads_created_at_from_remote_staging_metadata()
    {
        var opts = DefaultOptions();
        var createdAt = DateTimeOffset.Parse("2026-06-20T10:15:30.0000000+00:00");
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "list"))
            {
                return new ProcessRunResult(
                    0,
                    """{"list":[{"name":"codeybox-r-created","state":"Running"}]}""",
                    "");
            }
            if (argv.Count >= 3
                && argv[0] == "sh"
                && argv[1] == "-c"
                && argv[2].Contains(".codeybox-created-at", StringComparison.Ordinal))
            {
                return new ProcessRunResult(0, $"codeybox-r-created\t{createdAt:O}\n", "");
            }
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var info = Assert.Single(await provider.ListAllManagedAsync(CancellationToken.None));

        Assert.Equal(createdAt, info.CreatedAt);
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
    public async Task SyncStateToHostAsync_stages_writable_mount_without_deleting_vm()
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
                Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = hostTemp, ReadOnly = false }],
            });

            await sb.SyncStateToHostAsync(CancellationToken.None);

            Assert.Contains(transport.StageOutCalls, c => c.HostPath == hostTemp);
            Assert.DoesNotContain(transport.RecordedCalls, c => c.Argv.Contains("delete"));

            await sb.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SyncStateToHostAsync_PropagatesCallerCancellationFromStageOut()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };
        using var cts = new CancellationTokenSource();
        transport.OnStageOut = (_, _, ct) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(ct);
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
                Mounts = [new SandboxMount { SandboxPath = "/repo", HostPath = hostTemp, ReadOnly = false }],
            });

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await sb.SyncStateToHostAsync(cts.Token));

            transport.OnStageOut = null;
            await sb.DisposeAsync();
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
    public async Task DisposeAsync_syncback_failure_throws_and_keeps_host_reservation()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };
        transport.ThrowOnStageOut = true;

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

            var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
                await sb.DisposeAsync());

            Assert.Equal("sync-back", ex.Operation);
            Assert.Equal("remote-syncback-failed", ex.ErrorClass);
            Assert.Equal(1, Assert.Single(provider.SnapshotHostPool()).Reserved);
            Assert.DoesNotContain(transport.RecordedCalls, c => c.Argv.Contains("delete"));
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_syncback_content_validation_does_not_mark_host_unhealthy()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };
        transport.OnStageOut = (_, _, _) =>
            throw new RemoteSshTransportException(
                "unsafe tar entry",
                RemoteSshTransportFailureKind.ContentValidation);

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

            var ex = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
                await sb.DisposeAsync());

            Assert.Equal("remote-syncback-invalid-content", ex.ErrorClass);
            Assert.Equal(sb.Id, ex.RetainedSandboxName);
            var host = Assert.Single(provider.SnapshotHostPool());
            Assert.True(host.RuntimeHealthy);
            Assert.Equal(1, host.Reserved);
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeLeakedAsync_active_sandbox_skips_failed_syncback_and_releases_host_reservation()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };
        transport.ThrowOnStageOut = true;

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

            await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(async () =>
                await sb.DisposeAsync());
            var stageOutAttempts = transport.StageOutCalls.Count;

            await provider.DisposeLeakedAsync(new ManagedSandboxInfo(
                sb.Id,
                DateTimeOffset.UtcNow,
                DiskBytes: null,
                IsTrackedActive: true,
                HostId: opts.HostId.Length == 0 ? "default" : opts.HostId), CancellationToken.None);

            Assert.Equal(stageOutAttempts, transport.StageOutCalls.Count);
            Assert.Contains(transport.RecordedCalls, c => c.Argv.Contains("delete") && c.Argv.Contains("--purge"));
            Assert.Contains(transport.RecordedCalls, c => c.Argv.Count >= 2 && c.Argv[0] == "rm" && c.Argv[1] == "-rf");
            Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
        }
        finally
        {
            try { Directory.Delete(hostTemp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_delete_failure_is_best_effort_and_releases_host_reservation()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
        var failDelete = false;
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            if (Contains(argv, "delete") && failDelete) return new ProcessRunResult(1, "", "delete failed");
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var sb = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });
        failDelete = true;

        await sb.DisposeAsync();

        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task DisposeAsync_delete_not_found_still_removes_remote_staging()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
        var deleteAttempted = false;
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "delete"))
            {
                deleteAttempted = true;
                return new ProcessRunResult(1, "", "instance not found");
            }
            if (Contains(argv, "info") && deleteAttempted)
                return new ProcessRunResult(2, "", "instance not found");
            if (Contains(argv, "info"))
                return RunningInfoJson(VmNameFromLastLaunch(transport));
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var sb = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });

        await sb.DisposeAsync();

        Assert.Contains(transport.RecordedCalls, c => c.Argv.Count >= 2 && c.Argv[0] == "rm" && c.Argv[1] == "-rf");
        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task DisposeAsync_staging_cleanup_failure_is_best_effort_and_releases_host_reservation()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
        var failCleanup = false;
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            if (argv.Count >= 2 && argv[0] == "rm" && argv[1] == "-rf" && failCleanup)
                return new ProcessRunResult(1, "", "rm failed");
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var sb = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });
        failCleanup = true;

        await sb.DisposeAsync();

        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task DisposeAsync_staging_cleanup_transport_failure_is_best_effort_and_releases_host_reservation()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
        var failCleanup = false;
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "info")) return RunningInfoJson(VmNameFromLastLaunch(transport));
            if (argv.Count >= 2 && argv[0] == "rm" && argv[1] == "-rf" && failCleanup)
                throw new RemoteSshTransportException("ssh dropped during rm");
            return ProcessRunOk();
        };

        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);
        var sb = await provider.CreateAsync(new SandboxSpec { ImageReference = "24.04" });
        failCleanup = true;

        await sb.DisposeAsync();

        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task CreateAsync_throws_SandboxMountSourceMissingException_when_host_path_absent()
    {
        var opts = DefaultOptions() with { MaxConcurrentSandboxes = 1 };
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
        Assert.Equal(0, Assert.Single(provider.SnapshotHostPool()).Reserved);
    }

    [Fact]
    public async Task ListAllManagedAsync_malformed_json_marks_host_unhealthy_without_throwing()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "list")) return new ProcessRunResult(0, "{ not json", "");
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var managed = await provider.ListAllManagedAsync(CancellationToken.None);

        Assert.Empty(managed);
        var host = Assert.Single(provider.SnapshotHostPool());
        Assert.False(host.RuntimeHealthy);
        Assert.Contains("failed to parse", host.RuntimeUnhealthyReason);
    }

    [Fact]
    public async Task ListAllManagedAsync_metadata_transport_failure_marks_host_unhealthy_without_throwing()
    {
        var opts = DefaultOptions();
        var transport = new FakeRemoteHostTransport();
        transport.OnRun = (argv, _) =>
        {
            if (Contains(argv, "list"))
                return new ProcessRunResult(0, "{\"list\":[{\"name\":\"codeybox-r-one\",\"state\":\"Running\"}]}", "");
            if (argv.Count >= 3 && argv[0] == "sh" && argv[1] == "-c" && argv[2].Contains(".codeybox-created-at", StringComparison.Ordinal))
                throw new RemoteSshTransportException("metadata ssh dropped");
            return ProcessRunOk();
        };
        var provider = new MultipassRemoteSandboxProvider(
            opts, transport, NullLogger<MultipassRemoteSandboxProvider>.Instance);

        var inventory = await provider.ListManagedInventoryAsync(CancellationToken.None);

        Assert.Empty(inventory);
        var host = Assert.Single(provider.SnapshotHostPool());
        Assert.False(host.RuntimeHealthy);
        Assert.Contains("metadata ssh dropped", host.RuntimeUnhealthyReason);
        Assert.False(inventory.IsComplete);
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

    [Fact]
    public async Task OpenSshCliTransport_StageOut_rejects_symlink_entries_before_replacing_host_path()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stageout-tar-").FullName;
        try
        {
            var sourceParent = Path.Combine(root, "source");
            var sourceRoot = Path.Combine(sourceParent, "repo");
            Directory.CreateDirectory(sourceRoot);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "safe.txt"), "safe\n");
            File.CreateSymbolicLink(Path.Combine(sourceRoot, "escape"), "/tmp/codeybox-stageout-escape");

            var archivePath = Path.Combine(root, "archive.tar");
            await RunProcessOrThrowAsync("tar", root, ["-C", sourceParent, "-cf", archivePath, "repo"]);

            var fakeSsh = Path.Combine(root, "fake-ssh");
            await File.WriteAllTextAsync(
                fakeSsh,
                "#!/usr/bin/env bash\ncat " + OpenSshCliTransport.QuoteShellWord(archivePath) + "\n");
            File.SetUnixFileMode(
                fakeSsh,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var hostTarget = Path.Combine(root, "host-target");
            Directory.CreateDirectory(hostTarget);
            await File.WriteAllTextAsync(Path.Combine(hostTarget, "existing.txt"), "existing\n");

            var opts = DefaultOptions() with { SshBinary = fakeSsh, SshTarget = "ignored" };
            var transport = new OpenSshCliTransport(
                () => opts,
                new DefaultProcessRunner(),
                NullLogger<OpenSshCliTransport>.Instance);

            var ex = await Assert.ThrowsAsync<RemoteSshTransportException>(async () =>
                await transport.StageOutAsync("/remote/staged/repo", hostTarget, CancellationToken.None));

            Assert.Contains("Unsafe tar entry", ex.Message);
            Assert.Equal(RemoteSshTransportFailureKind.ContentValidation, ex.Kind);
            Assert.Equal("existing\n", await File.ReadAllTextAsync(Path.Combine(hostTarget, "existing.txt")));
            Assert.False(File.Exists(Path.Combine(hostTarget, "safe.txt")));
            Assert.False(File.Exists(Path.Combine(hostTarget, "escape")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenSshCliTransport_StageOut_replaces_host_path_with_valid_archive()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stageout-success-").FullName;
        try
        {
            var archivePath = Path.Combine(root, "archive.tar");
            await WriteTarArchiveAsync(
                archivePath,
                TarFile("repo/new.txt", "new\n"),
                TarFile("repo/nested/file.txt", "nested\n"));
            var fakeSsh = await WriteFakeSshCatArchiveAsync(root, archivePath);

            var hostTarget = Path.Combine(root, "host-target");
            Directory.CreateDirectory(hostTarget);
            await File.WriteAllTextAsync(Path.Combine(hostTarget, "old.txt"), "old\n");

            var opts = DefaultOptions() with { SshBinary = fakeSsh, SshTarget = "ignored" };
            var transport = new OpenSshCliTransport(
                () => opts,
                new DefaultProcessRunner(),
                NullLogger<OpenSshCliTransport>.Instance);

            await transport.StageOutAsync("/remote/staged/repo", hostTarget, CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(hostTarget, "old.txt")));
            Assert.Equal("new\n", await File.ReadAllTextAsync(Path.Combine(hostTarget, "new.txt")));
            Assert.Equal("nested\n", await File.ReadAllTextAsync(Path.Combine(hostTarget, "nested", "file.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public static IEnumerable<object[]> UnsafeStageOutArchives()
    {
        yield return
        [
            "parent-segment",
            new TarSpec[] { TarFile("repo/../escape.txt", "x\n") },
            "Unsafe tar entry path",
            RemoteSshTransportFailureKind.ContentValidation
        ];
        yield return
        [
            "absolute-path",
            new TarSpec[] { TarFile("/repo/file.txt", "x\n") },
            "Unsafe tar entry path",
            RemoteSshTransportFailureKind.ContentValidation
        ];
        yield return
        [
            "wrong-root",
            new TarSpec[] { TarFile("other/file.txt", "x\n") },
            "outside expected root",
            RemoteSshTransportFailureKind.ContentValidation
        ];
        yield return
        [
            "empty-archive",
            Array.Empty<TarSpec>(),
            "contained no extractable entries",
            RemoteSshTransportFailureKind.ContentValidation
        ];
        yield return
        [
            "too-many-entries",
            new TarSpec[] { TarFile("repo/a.txt", "a\n"), TarFile("repo/b.txt", "b\n") },
            "StageOutMaxEntries",
            RemoteSshTransportFailureKind.ResourceLimit
        ];
    }

    [Theory]
    [MemberData(nameof(UnsafeStageOutArchives))]
    public async Task OpenSshCliTransport_StageOut_rejects_unsafe_archives_before_replacing_host_path(
        string scenario,
        TarSpec[] entries,
        string expectedMessage,
        RemoteSshTransportFailureKind expectedKind)
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stageout-invalid-").FullName;
        try
        {
            var archivePath = Path.Combine(root, "archive.tar");
            await WriteTarArchiveAsync(archivePath, entries);
            var fakeSsh = await WriteFakeSshCatArchiveAsync(root, archivePath);

            var hostTarget = Path.Combine(root, "host-target");
            Directory.CreateDirectory(hostTarget);
            await File.WriteAllTextAsync(Path.Combine(hostTarget, "existing.txt"), scenario + "\n");

            var opts = DefaultOptions() with
            {
                SshBinary = fakeSsh,
                SshTarget = "ignored",
                StageOutMaxEntries = scenario == "too-many-entries" ? 1 : MultipassRemoteSandboxOptions.DefaultStageOutMaxEntries,
            };
            var transport = new OpenSshCliTransport(
                () => opts,
                new DefaultProcessRunner(),
                NullLogger<OpenSshCliTransport>.Instance);

            var ex = await Assert.ThrowsAsync<RemoteSshTransportException>(async () =>
                await transport.StageOutAsync("/remote/staged/repo", hostTarget, CancellationToken.None));

            Assert.Contains(expectedMessage, ex.Message);
            Assert.Equal(expectedKind, ex.Kind);
            Assert.Equal(scenario + "\n", await File.ReadAllTextAsync(Path.Combine(hostTarget, "existing.txt")));
            Assert.False(File.Exists(Path.Combine(hostTarget, "a.txt")));
            Assert.False(File.Exists(Path.Combine(hostTarget, "b.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenSshCliTransport_StageOut_rejects_archive_byte_cap_before_replacing_host_path()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stageout-cap-").FullName;
        try
        {
            var archivePath = Path.Combine(root, "archive.tar");
            await WriteTarArchiveAsync(archivePath, TarFile("repo/file.txt", "payload\n"));
            var fakeSsh = await WriteFakeSshCatArchiveAsync(root, archivePath);

            var hostTarget = Path.Combine(root, "host-target");
            Directory.CreateDirectory(hostTarget);
            await File.WriteAllTextAsync(Path.Combine(hostTarget, "existing.txt"), "existing\n");

            var opts = DefaultOptions() with
            {
                SshBinary = fakeSsh,
                SshTarget = "ignored",
                StageOutMaxArchiveBytes = 1,
            };
            var transport = new OpenSshCliTransport(
                () => opts,
                new DefaultProcessRunner(),
                NullLogger<OpenSshCliTransport>.Instance);

            var ex = await Assert.ThrowsAsync<RemoteSshTransportException>(async () =>
                await transport.StageOutAsync("/remote/staged/repo", hostTarget, CancellationToken.None));

            Assert.Equal(RemoteSshTransportFailureKind.ResourceLimit, ex.Kind);
            Assert.Contains("StageOutMaxArchiveBytes", ex.Message);
            Assert.Equal("existing\n", await File.ReadAllTextAsync(Path.Combine(hostTarget, "existing.txt")));
            Assert.False(File.Exists(Path.Combine(hostTarget, "file.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenSshCliTransport_StageOut_rejects_expansion_ratio_before_replacing_host_path()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stageout-ratio-").FullName;
        try
        {
            var archivePath = Path.Combine(root, "archive.tar");
            await WriteOverDeclaredTarArchiveAsync(archivePath, "repo/huge.bin", declaredSize: 4096);
            var fakeSsh = await WriteFakeSshCatArchiveAsync(root, archivePath);

            var hostTarget = Path.Combine(root, "host-target");
            Directory.CreateDirectory(hostTarget);
            await File.WriteAllTextAsync(Path.Combine(hostTarget, "existing.txt"), "existing\n");

            var opts = DefaultOptions() with
            {
                SshBinary = fakeSsh,
                SshTarget = "ignored",
                StageOutMaxExpansionRatio = 1.0d,
            };
            var transport = new OpenSshCliTransport(
                () => opts,
                new DefaultProcessRunner(),
                NullLogger<OpenSshCliTransport>.Instance);

            var ex = await Assert.ThrowsAsync<RemoteSshTransportException>(async () =>
                await transport.StageOutAsync("/remote/staged/repo", hostTarget, CancellationToken.None));

            Assert.Equal(RemoteSshTransportFailureKind.ResourceLimit, ex.Kind);
            Assert.Contains("StageOutMaxExpansionRatio", ex.Message);
            Assert.Equal("existing\n", await File.ReadAllTextAsync(Path.Combine(hostTarget, "existing.txt")));
            Assert.False(File.Exists(Path.Combine(hostTarget, "huge.bin")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenSshCliTransport_StageIn_broken_pipe_is_classified_as_transport_failure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stagein-broken-pipe-").FullName;
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var payload = new byte[8 * 1024 * 1024];
            Array.Fill<byte>(payload, 0x41);
            await File.WriteAllBytesAsync(Path.Combine(source, "large.bin"), payload);

            var fakeSsh = Path.Combine(root, "fake-ssh");
            await WriteExecutableScriptAsync(fakeSsh, "#!/usr/bin/env bash\nexit 255\n");

            var opts = DefaultOptions() with { SshBinary = fakeSsh, SshTarget = "ignored" };
            var transport = new OpenSshCliTransport(
                () => opts,
                new DefaultProcessRunner(),
                NullLogger<OpenSshCliTransport>.Instance);

            var ex = await Assert.ThrowsAsync<RemoteSshTransportException>(async () =>
                await transport.StageInAsync(source, "/remote/source", CancellationToken.None));

            Assert.Equal(RemoteSshTransportFailureKind.Transport, ex.Kind);
            Assert.Contains("SSH transport failure", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenSshCliTransport_StageIn_remote_extract_failure_is_not_transport_failure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stagein-remote-command-").FullName;
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "payload.txt"), "payload\n");

            var fakeSsh = Path.Combine(root, "fake-ssh");
            await WriteExecutableScriptAsync(fakeSsh, "#!/usr/bin/env bash\ncat >/dev/null\nexit 7\n");

            var opts = DefaultOptions() with { SshBinary = fakeSsh, SshTarget = "ignored" };
            var transport = new OpenSshCliTransport(
                () => opts,
                new DefaultProcessRunner(),
                NullLogger<OpenSshCliTransport>.Instance);

            var ex = await Assert.ThrowsAsync<RemoteSshTransportException>(async () =>
                await transport.StageInAsync(source, "/remote/source", CancellationToken.None));

            Assert.Equal(RemoteSshTransportFailureKind.RemoteCommand, ex.Kind);
            Assert.False(ex.IsHostTransportFailure);
            Assert.Contains("Remote tar-extract failed", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void OpenSshCliTransport_NormalizeTarEntryName_rejects_nul_as_content_failure()
    {
        var ex = Assert.Throws<RemoteSshTransportException>(() =>
            OpenSshCliTransport.NormalizeTarEntryName("repo/\0evil"));

        Assert.Equal(RemoteSshTransportFailureKind.ContentValidation, ex.Kind);
        Assert.False(ex.IsHostTransportFailure);
    }

    [Fact]
    public void OpenSshCliTransport_ValidateExtractedTree_rejects_post_extract_reparse_points()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Directory.CreateTempSubdirectory("codeybox-stageout-reparse-").FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "link"), "target");

            var ex = Assert.Throws<RemoteSshTransportException>(() =>
                OpenSshCliTransport.ValidateExtractedTree(root));

            Assert.Equal(RemoteSshTransportFailureKind.ContentValidation, ex.Kind);
            Assert.Contains("reparse point", ex.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ----- helpers ---------------------------------------------------

    private static bool Contains(IReadOnlyList<string> argv, string token)
    {
        foreach (var a in argv) if (a == token) return true;
        return false;
    }

    private static TarSpec TarFile(string name, string content) =>
        new(TarEntryType.RegularFile, name, content);

    private static async Task WriteTarArchiveAsync(string archivePath, params TarSpec[] entries)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var spec in entries)
        {
            var entry = new PaxTarEntry(spec.Type, spec.Name);
            if (spec.LinkName is not null)
                entry.LinkName = spec.LinkName;
            if (spec.Content is not null)
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(spec.Content));
            writer.WriteEntry(entry);
        }
    }

    private static async Task WriteOverDeclaredTarArchiveAsync(string archivePath, string entryName, long declaredSize)
    {
        var header = new byte[512];
        WriteTarString(header, 0, 100, entryName);
        WriteTarOctal(header, 100, 8, 420);
        WriteTarOctal(header, 108, 8, 0);
        WriteTarOctal(header, 116, 8, 0);
        WriteTarOctal(header, 124, 12, declaredSize);
        WriteTarOctal(header, 136, 12, 0);
        for (var i = 148; i < 156; i++)
            header[i] = (byte)' ';
        header[156] = (byte)'0';
        WriteTarString(header, 257, 6, "ustar");
        WriteTarString(header, 263, 2, "00");

        var checksum = header.Sum(static b => (int)b);
        var checksumText = Convert.ToString(checksum, 8)!.PadLeft(6, '0');
        WriteTarString(header, 148, 6, checksumText);
        header[154] = 0;
        header[155] = (byte)' ';

        await using var stream = new FileStream(
            archivePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        await stream.WriteAsync(header);
        await stream.WriteAsync(new byte[1024]);
    }

    private static void WriteTarString(byte[] header, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, header, offset, Math.Min(bytes.Length, length));
    }

    private static void WriteTarOctal(byte[] header, int offset, int length, long value)
    {
        var text = Convert.ToString(value, 8) ?? "0";
        if (text.Length > length - 1)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value does not fit in tar octal field.");
        text = text.PadLeft(length - 1, '0');
        WriteTarString(header, offset, length - 1, text);
        header[offset + length - 1] = 0;
    }

    private static async Task<string> WriteFakeSshCatArchiveAsync(string root, string archivePath)
    {
        var fakeSsh = Path.Combine(root, "fake-ssh");
        await WriteExecutableScriptAsync(
            fakeSsh,
            "#!/usr/bin/env bash\ncat " + OpenSshCliTransport.QuoteShellWord(archivePath) + "\n");
        return fakeSsh;
    }

    private static async Task WriteExecutableScriptAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                tmp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.Move(tmp, path);
    }

    private static ProcessRunResult ProcessRunOk() => new(0, "", "");

    private static bool IsRemoteHeavy(IReadOnlyList<string> argv, string multipassPath) =>
        argv.Count >= 2
        && argv[0] == multipassPath
        && argv[1] is "launch" or "start" or "stop" or "clone" or "mount" or "delete";

    private static void UpdateMax(ref int target, int value)
    {
        int existing;
        do { existing = Volatile.Read(ref target); }
        while (value > existing
            && Interlocked.CompareExchange(ref target, value, existing) != existing);
    }

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
    public sealed record TarSpec(TarEntryType Type, string Name, string? Content = null, string? LinkName = null);

    internal sealed class FakeRemoteHostTransport : IRemoteHostTransport
    {
        public string DiagnosticId => "fake";
        public ConcurrentQueue<RecordedCall> RecordedCallsQueue { get; } = new();
        public List<RecordedCall> RecordedCalls { get; } = new();
        public List<StageInCall> StageInCalls { get; } = new();
        public List<StageOutCall> StageOutCalls { get; } = new();
        public bool ThrowOnStageOut { get; set; }
        public Action<string, string, CancellationToken>? OnStageOut { get; set; }
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
            if (Contains(argv, "list") && result.ExitCode == 0 && string.IsNullOrWhiteSpace(result.Stdout))
                result = new ProcessRunResult(0, "{\"list\":[]}", result.Stderr);
            return Task.FromResult(result);
        }

        public Task StageInAsync(string hostPath, string remotePath, CancellationToken ct)
        {
            StageInCalls.Add(new StageInCall(hostPath, remotePath));
            return Task.CompletedTask;
        }

        public Task StageOutAsync(string remotePath, string hostPath, CancellationToken ct)
        {
            if (OnStageOut is not null)
            {
                OnStageOut(remotePath, hostPath, ct);
                return Task.CompletedTask;
            }
            if (ThrowOnStageOut)
                throw new RemoteSshTransportException("stage-out failed");
            StageOutCalls.Add(new StageOutCall(remotePath, hostPath));
            return Task.CompletedTask;
        }
    }

    private static async Task RunProcessOrThrowAsync(string fileName, string workingDirectory, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}{stdout}");
    }
}
