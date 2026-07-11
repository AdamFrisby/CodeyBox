using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;
using Xunit;

namespace CodeyBox.Tests;

public sealed class MultipassBaselineSeedingTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-seeding-tests-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            try { Directory.Delete(_workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ComputeBaselineHash_IncludesPackageCacheSeeds()
    {
        var opts1 = new MultipassSandboxOptions
        {
            UseBaselineImages = true,
            PackageCacheSeeds = new[]
            {
                new BaselinePackageCacheSeed { HostSourcePath = "~/path1", VmDestPath = "/dest1", MaxSizeMB = 100 }
            }
        };

        var opts2 = new MultipassSandboxOptions
        {
            UseBaselineImages = true,
            PackageCacheSeeds = new[]
            {
                new BaselinePackageCacheSeed { HostSourcePath = "~/path1", VmDestPath = "/dest1", MaxSizeMB = 200 }
            }
        };

        var hash1 = MultipassSandboxProvider.ComputeBaselineHash(opts1, "isolated", SandboxProfileFlavor.Headless);
        var hash2 = MultipassSandboxProvider.ComputeBaselineHash(opts2, "isolated", SandboxProfileFlavor.Headless);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeBaselineHash_IncludesExecutableProvisions()
    {
        // Provisioning a different host binary, or relocating the VM dest,
        // must invalidate the cached baseline. Without this the operator would
        // swap binaries (or fix a wrong path) and never see a rebake.
        var hostBin = Path.Combine(_workspace, "agy");
        File.WriteAllText(hostBin, "agy-v1");
        var otherHostBin = Path.Combine(_workspace, "agy-v2");
        File.WriteAllText(otherHostBin, "agy-v2");

        var opts1 = new MultipassSandboxOptions
        {
            UseBaselineImages = true,
            ExecutableProvisions = new[]
            {
                new BaselineExecutableProvision
                {
                    HostSourcePath = hostBin,
                    VmDestPath = "/home/ubuntu/.local/bin/agy",
                    VmSymlinks = new[] { "/usr/local/bin/agy" },
                },
            },
        };
        string HashFor(BaselineExecutableProvision provision) =>
            MultipassSandboxProvider.ComputeBaselineHash(
                opts1 with { ExecutableProvisions = new[] { provision } },
                "isolated",
                SandboxProfileFlavor.Headless);

        var baselineHash = HashFor(opts1.ExecutableProvisions[0]);
        File.WriteAllText(hostBin, "agy-v1-replaced");
        var hostContentHash = HashFor(opts1.ExecutableProvisions[0]);
        var hostPathHash = HashFor(opts1.ExecutableProvisions[0] with { HostSourcePath = otherHostBin });
        var vmDestHash = HashFor(opts1.ExecutableProvisions[0] with { VmDestPath = "/opt/agy" });
        var symlinkHash = HashFor(opts1.ExecutableProvisions[0] with { VmSymlinks = new[] { "/opt/bin/agy" } });

        Assert.NotEqual(baselineHash, hostContentHash);
        Assert.NotEqual(baselineHash, hostPathHash);
        Assert.NotEqual(baselineHash, vmDestHash);
        Assert.NotEqual(baselineHash, symlinkHash);
    }

    [Fact]
    public async Task BakeBaselineAsync_ProvisionsExecutableBeforeBaselineVerification()
    {
        var hostBin = Path.Combine(_workspace, "agy");
        await File.WriteAllBytesAsync(hostBin, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/multipass-mock",
            StagingDirectory = Path.Combine(_workspace, "staging"),
            NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
            UseBaselineImages = true,
            ExecutableProvisions = new[]
            {
                new BaselineExecutableProvision
                {
                    HostSourcePath = hostBin,
                    VmDestPath = "/home/ubuntu/.local/bin/agy",
                    VmSymlinks = new[] { "/usr/local/bin/agy" },
                    Label = "antigravity",
                },
            },
            BaselineVerificationCommands = new[]
            {
                new BaselineVerificationCommand(
                    "antigravity",
                    ["agy", "--version"],
                    "agy must be present after executable provisioning"),
            },
        };

        var installed = false;
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var runner = NewBaselineBakeRunner(states, calls, argv =>
        {
            if (argv is [_, "exec", _, "--", "sudo", "bash", "-c", var script]
                && script.Contains("install -m 0755 -o root -g root", StringComparison.Ordinal))
            {
                installed = true;
                return new ProcessRunResult(0, "", "");
            }

            if (argv is [_, "exec", _, "--", "agy", "--version"])
            {
                return installed
                    ? new ProcessRunResult(0, "agy version 1.0.7", "")
                    : new ProcessRunResult(127, "", "agy: command not found");
            }

            return null;
        });

        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var baselineName = await ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
            "claude", SandboxProfileFlavor.Headless, null, CancellationToken.None);

        Assert.NotNull(baselineName);
        Assert.True(installed);

        var callList = calls.Select(c => c.ToArray()).ToList();
        var installIndex = callList.FindIndex(c =>
            c is [_, "exec", _, "--", "sudo", "bash", "-c", var script]
            && script.Contains("install -m 0755 -o root -g root", StringComparison.Ordinal));
        var verificationIndex = callList.FindIndex(c => c is [_, "exec", _, "--", "agy", "--version"]);

        Assert.True(installIndex >= 0, "Provisioning install command was not run.");
        Assert.True(verificationIndex > installIndex, "Baseline verification must run after executable provisioning.");
    }

    [Fact]
    public async Task BakeBaselineAsync_ExecutesSeedingCommands()
    {
        // Create a dummy file on host so host path exists
        var dummyCacheDir = Path.Combine(_workspace, "dummy-cache");
        Directory.CreateDirectory(dummyCacheDir);
        var dummyFile = Path.Combine(dummyCacheDir, "some-package.nupkg");
        await File.WriteAllTextAsync(dummyFile, "dummy nuget package content");

        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/multipass-mock",
            StagingDirectory = Path.Combine(_workspace, "staging"),
            NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
            UseBaselineImages = true,
            PackageCacheSeeds = new[]
            {
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = dummyCacheDir,
                    VmDestPath = "/home/ubuntu/.nuget/packages",
                    MaxSizeMB = 50
                }
            }
        };

        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            calls.Enqueue(argv);

            if (argv.Contains("info"))
            {
                var name = argv.ElementAtOrDefault(2);
                if (name != null && states.TryGetValue(name, out var state))
                {
                    return Task.FromResult(new ProcessRunResult(0, $"Name,State,IPv4\n{name},{state},1.2.3.4", ""));
                }
                return Task.FromResult(new ProcessRunResult(1, "", "does not exist"));
            }
            if (argv.Contains("launch"))
            {
                var nameIndex = argv.ToList().IndexOf("--name");
                if (nameIndex >= 0 && nameIndex + 1 < argv.Count)
                {
                    states[argv[nameIndex + 1]] = "Running";
                }
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv.Contains("stop"))
            {
                var stopName = argv.ElementAtOrDefault(2);
                if (stopName != null)
                {
                    states[stopName] = "Stopped";
                }
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv.Contains("exec") || argv.Contains("transfer"))
            {
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv[0] == "tar")
            {
                // Mock tar file creation
                var tarPath = argv[2];
                File.WriteAllText(tarPath, "mock tar content");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });

        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var baselineName = await ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync("claude", SandboxProfileFlavor.Headless, null, CancellationToken.None);

        Assert.NotNull(baselineName);

        // Verify that calls contain our expected tar, mkdir, transfer, tar extract, chown, rm
        var flatCalls = calls.Select(argv => string.Join(" ", argv)).ToList();

        // 1. Local tar execution (RunHostProcessAsync)
        Assert.Contains(flatCalls, c => c.StartsWith("tar -cf"));

        // 2. mkdir -p
        Assert.Contains(flatCalls, c => c.Contains("exec") && c.Contains("mkdir -p /home/ubuntu/.nuget/packages"));

        // 3. transfer
        Assert.Contains(flatCalls, c => c.Contains("transfer") && c.Contains(".tar"));

        // 4. tar extract
        Assert.Contains(flatCalls, c => c.Contains("exec") && c.Contains("tar -xf") && c.Contains("-C /home/ubuntu/.nuget/packages"));

        // 5. chown
        Assert.Contains(flatCalls, c => c.Contains("exec") && c.Contains("sudo chown -R ubuntu:ubuntu /home/ubuntu/.nuget/packages"));

        // 6. rm
        Assert.Contains(flatCalls, c => c.Contains("exec") && c.Contains("rm -f"));
    }

    [Fact]
    public async Task BakeBaselineAsync_ProvisionsExecutableWithChmodAndSymlink()
    {
        // Stage a host-side dummy binary; agy in production is ~168MB, but the
        // bytes don't matter for the provisioning steps the bake runs.
        var hostDir = Path.Combine(_workspace, "agy seed's dir");
        Directory.CreateDirectory(hostDir);
        var hostBin = Path.Combine(hostDir, "ag y's");
        await File.WriteAllBytesAsync(hostBin, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/multipass-mock",
            StagingDirectory = Path.Combine(_workspace, "staging"),
            NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
            UseBaselineImages = true,
            ExecutableProvisions = new[]
            {
                new BaselineExecutableProvision
                {
                    HostSourcePath = hostBin,
                    VmDestPath = "/home/ubuntu/.local/bin/ag y's",
                    VmSymlinks = new[] { "/usr/local/bin/ag y's", "/opt/codeybox tools/ag y's" },
                    Label = "antigravity",
                },
            },
        };

        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            calls.Enqueue(argv);

            if (argv.Contains("info"))
            {
                var name = argv.ElementAtOrDefault(2);
                if (name != null && states.TryGetValue(name, out var state))
                {
                    return Task.FromResult(new ProcessRunResult(0, $"Name,State,IPv4\n{name},{state},1.2.3.4", ""));
                }
                return Task.FromResult(new ProcessRunResult(1, "", "does not exist"));
            }
            if (argv.Contains("launch"))
            {
                var nameIndex = argv.ToList().IndexOf("--name");
                if (nameIndex >= 0 && nameIndex + 1 < argv.Count)
                {
                    states[argv[nameIndex + 1]] = "Running";
                }
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv.Contains("stop"))
            {
                var stopName = argv.ElementAtOrDefault(2);
                if (stopName != null)
                {
                    states[stopName] = "Stopped";
                }
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            // All exec / transfer steps succeed.
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });

        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var baselineName = await ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
            "claude", SandboxProfileFlavor.Headless, null, CancellationToken.None);

        Assert.NotNull(baselineName);

        var callList = calls.Select(argv => argv.ToArray()).ToList();
        var flatCalls = callList.Select(argv => string.Join(" ", argv)).ToList();

        // 1. multipass transfer of the host binary to a /home/ubuntu staging path.
        var transferCall = callList.FirstOrDefault(c => c is [_, "transfer", _, _]);
        Assert.NotNull(transferCall);
        Assert.Equal(hostBin, transferCall![2]);
        Assert.Contains("/home/ubuntu/codeybox-exe-", transferCall[3]);

        // 2. The install script must create home parents as ubuntu-owned and use
        //    `install -m 0755 -o root` to land the binary deterministically.
        var installScript = flatCalls.FirstOrDefault(c =>
            c.Contains("exec") && c.Contains("install -m 0755 -o root -g root"));
        Assert.NotNull(installScript);
        Assert.Contains("install -d -m 0755 -o ubuntu -g ubuntu '/home/ubuntu/.local/bin'", installScript);
        Assert.Contains("'/home/ubuntu/.local/bin/ag y'\"'\"'s'", installScript);

        // 3. Requested symlinks are created, including paths that need shell quoting.
        Assert.Contains("ln -sf '/home/ubuntu/.local/bin/ag y'\"'\"'s' '/usr/local/bin/ag y'\"'\"'s'", installScript);
        Assert.Contains("mkdir -p '/opt/codeybox tools'", installScript);
        Assert.Contains("ln -sf '/home/ubuntu/.local/bin/ag y'\"'\"'s' '/opt/codeybox tools/ag y'\"'\"'s'", installScript);

        // 4. Staging copy is removed from /home/ubuntu so it does not leak into clones.
        Assert.Contains("rm -f '/home/ubuntu/codeybox-exe-", installScript);
    }

    [Fact]
    public async Task BakeBaselineAsync_FailsLoudly_WhenExecutableHostFileMissing()
    {
        // Pointing at a missing host binary must fail the bake immediately — the
        // entire point of this option is to replace a silent `curl|bash || true`.
        var opts = new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/multipass-mock",
            StagingDirectory = Path.Combine(_workspace, "staging"),
            NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
            UseBaselineImages = true,
            ExecutableProvisions = new[]
            {
                new BaselineExecutableProvision
                {
                    HostSourcePath = Path.Combine(_workspace, "does-not-exist"),
                    VmDestPath = "/home/ubuntu/.local/bin/agy",
                    Label = "antigravity",
                },
            },
        };

        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv.Contains("info"))
            {
                var name = argv.ElementAtOrDefault(2);
                if (name != null && states.TryGetValue(name, out var state))
                    return Task.FromResult(new ProcessRunResult(0, $"Name,State,IPv4\n{name},{state},1.2.3.4", ""));
                return Task.FromResult(new ProcessRunResult(1, "", "does not exist"));
            }
            if (argv.Contains("launch"))
            {
                var nameIndex = argv.ToList().IndexOf("--name");
                if (nameIndex >= 0 && nameIndex + 1 < argv.Count)
                    states[argv[nameIndex + 1]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });

        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
                "claude", SandboxProfileFlavor.Headless, null, CancellationToken.None));

        Assert.Contains("antigravity", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidExecutableProvisionCases()
    {
        yield return
        [
            new Func<string, BaselineExecutableProvision>(hostBin => new BaselineExecutableProvision
            {
                HostSourcePath = " ",
                VmDestPath = "/home/ubuntu/.local/bin/agy",
                Label = "blank-host",
            }),
            "HostSourcePath and VmDestPath are both required",
        ];
        yield return
        [
            new Func<string, BaselineExecutableProvision>(hostBin => new BaselineExecutableProvision
            {
                HostSourcePath = hostBin,
                VmDestPath = " ",
                Label = "blank-dest",
            }),
            "HostSourcePath and VmDestPath are both required",
        ];
        yield return
        [
            new Func<string, BaselineExecutableProvision>(hostBin => new BaselineExecutableProvision
            {
                HostSourcePath = hostBin,
                VmDestPath = "agy",
                Label = "relative-dest",
            }),
            "VmDestPath must be absolute",
        ];
        yield return
        [
            new Func<string, BaselineExecutableProvision>(hostBin => new BaselineExecutableProvision
            {
                HostSourcePath = hostBin,
                VmDestPath = "/",
                Label = "root-dest",
            }),
            "VmDestPath has no directory component",
        ];
        yield return
        [
            new Func<string, BaselineExecutableProvision>(hostBin => new BaselineExecutableProvision
            {
                HostSourcePath = hostBin,
                VmDestPath = "/home/ubuntu/.local/bin/agy",
                VmSymlinks = new[] { "usr/local/bin/agy" },
                Label = "relative-symlink",
            }),
            "VmSymlink must be absolute",
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidExecutableProvisionCases))]
    public async Task BakeBaselineAsync_RejectsInvalidExecutableProvisionConfig(
        Func<string, BaselineExecutableProvision> makeProvision,
        string expectedMessage)
    {
        var hostBin = Path.Combine(_workspace, "agy");
        await File.WriteAllBytesAsync(hostBin, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        var opts = MakeExecutableProvisionOptions(makeProvision(hostBin));
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var runner = NewBaselineBakeRunner(states);
        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
                "claude", SandboxProfileFlavor.Headless, null, CancellationToken.None));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BakeBaselineAsync_FailsAndPurgesBaseline_WhenExecutableTransferFails()
    {
        var hostBin = Path.Combine(_workspace, "agy");
        await File.WriteAllBytesAsync(hostBin, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        var opts = MakeExecutableProvisionOptions(new BaselineExecutableProvision
        {
            HostSourcePath = hostBin,
            VmDestPath = "/home/ubuntu/.local/bin/agy",
            Label = "antigravity",
        });
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var deleteNames = new ConcurrentQueue<string>();
        var runner = NewBaselineBakeRunner(states, deleteNames: deleteNames, onCall: argv =>
            argv is [_, "transfer", _, _]
                ? new ProcessRunResult(23, "", "transfer stderr: permission denied")
                : null);
        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
                "claude", SandboxProfileFlavor.Headless, null, CancellationToken.None));

        Assert.Contains("transfer failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(deleteNames, name => name.StartsWith("cb-baseline-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BakeBaselineAsync_FailsAndPurgesBaseline_WhenExecutableInstallFails()
    {
        var hostBin = Path.Combine(_workspace, "agy");
        await File.WriteAllBytesAsync(hostBin, [0x7f, (byte)'E', (byte)'L', (byte)'F']);

        var opts = MakeExecutableProvisionOptions(new BaselineExecutableProvision
        {
            HostSourcePath = hostBin,
            VmDestPath = "/home/ubuntu/.local/bin/agy",
            Label = "antigravity",
        });
        var states = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var deleteNames = new ConcurrentQueue<string>();
        var runner = NewBaselineBakeRunner(states, deleteNames: deleteNames, onCall: argv =>
            argv is [_, "exec", _, "--", "sudo", "bash", "-c", var script]
            && script.Contains("install -m 0755 -o root -g root", StringComparison.Ordinal)
                ? new ProcessRunResult(24, "", "install stderr: read-only filesystem")
                : null);
        var provider = new MultipassSandboxProvider(opts, NullLogger<MultipassSandboxProvider>.Instance, null, runner);

        var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            ((IBaselineImageProvisioner)provider).EnsureBaselineImageAsync(
                "claude", SandboxProfileFlavor.Headless, null, CancellationToken.None));

        Assert.Contains("install failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only filesystem", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(deleteNames, name => name.StartsWith("cb-baseline-", StringComparison.Ordinal));
    }

    private MultipassSandboxOptions MakeExecutableProvisionOptions(BaselineExecutableProvision provision) => new()
    {
        MultipassBinary = "/bin/multipass-mock",
        StagingDirectory = Path.Combine(_workspace, "staging-" + Guid.NewGuid().ToString("N")),
        NetworkProfiles = new Dictionary<string, string> { ["claude"] = "cb-claude" },
        UseBaselineImages = true,
        ExecutableProvisions = new[] { provision },
    };

    private static RecordingMultipassRunner NewBaselineBakeRunner(
        ConcurrentDictionary<string, string> states,
        ConcurrentQueue<IReadOnlyList<string>>? calls = null,
        Func<IReadOnlyList<string>, ProcessRunResult?>? onCall = null,
        ConcurrentQueue<string>? deleteNames = null)
    {
        return new RecordingMultipassRunner((argv, _, _) =>
        {
            calls?.Enqueue(argv.ToArray());

            var custom = onCall?.Invoke(argv);
            if (custom is not null)
                return Task.FromResult(custom.Value);

            if (argv.Contains("info"))
            {
                var name = argv.ElementAtOrDefault(2);
                if (name != null && states.TryGetValue(name, out var state))
                    return Task.FromResult(new ProcessRunResult(0, $"Name,State,IPv4\n{name},{state},1.2.3.4", ""));
                return Task.FromResult(new ProcessRunResult(1, "", "does not exist"));
            }

            if (argv.Contains("launch"))
            {
                var nameIndex = argv.ToList().IndexOf("--name");
                if (nameIndex >= 0 && nameIndex + 1 < argv.Count)
                    states[argv[nameIndex + 1]] = "Running";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "stop", var stopName])
            {
                states[stopName] = "Stopped";
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv is [_, "delete", "--purge", var deleteName])
            {
                deleteNames?.Enqueue(deleteName);
                states.TryRemove(deleteName, out _);
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }

            if (argv.Contains("exec") || argv.Contains("transfer"))
                return Task.FromResult(new ProcessRunResult(0, "", ""));

            return Task.FromResult(new ProcessRunResult(0, "", ""));
        });
    }
}
