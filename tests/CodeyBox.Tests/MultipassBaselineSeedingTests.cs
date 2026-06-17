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
                new PackageCacheSeedOptions { HostSourcePath = "~/path1", VmDestPath = "/dest1", MaxSizeMB = 100 }
            }
        };

        var opts2 = new MultipassSandboxOptions
        {
            UseBaselineImages = true,
            PackageCacheSeeds = new[]
            {
                new PackageCacheSeedOptions { HostSourcePath = "~/path1", VmDestPath = "/dest1", MaxSizeMB = 200 }
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
        var opts1 = new MultipassSandboxOptions
        {
            UseBaselineImages = true,
            ExecutableProvisions = new[]
            {
                new ExecutableProvisionOptions
                {
                    HostSourcePath = "/host/agy",
                    VmDestPath = "/home/ubuntu/.local/bin/agy",
                    VmSymlinks = new[] { "/usr/local/bin/agy" },
                },
            },
        };
        var opts2 = opts1 with
        {
            ExecutableProvisions = new[]
            {
                new ExecutableProvisionOptions
                {
                    HostSourcePath = "/host/agy-v2",
                    VmDestPath = "/home/ubuntu/.local/bin/agy",
                    VmSymlinks = new[] { "/usr/local/bin/agy" },
                },
            },
        };

        var hash1 = MultipassSandboxProvider.ComputeBaselineHash(opts1, "isolated", SandboxProfileFlavor.Headless);
        var hash2 = MultipassSandboxProvider.ComputeBaselineHash(opts2, "isolated", SandboxProfileFlavor.Headless);

        Assert.NotEqual(hash1, hash2);
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
                new PackageCacheSeedOptions
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
                new ExecutableProvisionOptions
                {
                    HostSourcePath = hostBin,
                    VmDestPath = "/home/ubuntu/.local/bin/agy",
                    VmSymlinks = new[] { "/usr/local/bin/agy" },
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

        var flatCalls = calls.Select(argv => string.Join(" ", argv)).ToList();

        // 1. multipass transfer of the host binary to a /home/ubuntu staging path.
        Assert.Contains(flatCalls, c => c.Contains("transfer") && c.Contains(hostBin)
            && c.Contains("/home/ubuntu/codeybox-exe-"));

        // 2. The install script must mkdir the parent and use `install -m 0755 -o root`
        //    to land the binary deterministically.
        var installScript = flatCalls.FirstOrDefault(c =>
            c.Contains("exec") && c.Contains("install -m 0755 -o root -g root"));
        Assert.NotNull(installScript);
        Assert.Contains("mkdir -p '/home/ubuntu/.local/bin'", installScript);
        Assert.Contains("'/home/ubuntu/.local/bin/agy'", installScript);

        // 3. The requested symlink at /usr/local/bin/agy is created.
        Assert.Contains("ln -sf '/home/ubuntu/.local/bin/agy' '/usr/local/bin/agy'", installScript);

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
                new ExecutableProvisionOptions
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
}
