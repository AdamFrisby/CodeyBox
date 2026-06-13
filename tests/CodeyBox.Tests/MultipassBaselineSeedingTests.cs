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

        var calls = new ConcurrentQueue<IReadOnlyList<string>>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            calls.Enqueue(argv);

            // Simulate command responses to get baseline bake to succeed:
            // info, launch, exec, stop, info (WaitForStopped), etc.
            if (argv.Contains("info"))
            {
                // To mock the VM status being Stopped or Running appropriately
                if (argv.Contains("stop"))
                {
                    return Task.FromResult(new ProcessRunResult(0, "Name,State,IPv4\nbaseline-name,Stopped,1.2.3.4", ""));
                }
                // When called at start to see if VM exists: return exit code 1 to say it does not exist
                // so BakeBaselineAsync is triggered.
                if (calls.Count(c => c.Contains("info")) == 1)
                {
                    return Task.FromResult(new ProcessRunResult(1, "", "does not exist"));
                }
                return Task.FromResult(new ProcessRunResult(0, "Name,State,IPv4\nbaseline-name,Stopped,1.2.3.4", ""));
            }
            if (argv.Contains("launch") || argv.Contains("exec") || argv.Contains("transfer") || argv.Contains("stop"))
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
}
