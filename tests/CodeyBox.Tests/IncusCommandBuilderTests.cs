using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class IncusCommandBuilderTests
{
    private static readonly IncusSandboxOptions Options = new()
    {
        BinaryPath = "/opt/incus/bin/incus",
        ProjectName = "codeybox-tests",
        StoragePoolName = "codeybox-zfs",
    };

    [Fact]
    public void BuildInit_ConstructsVmOnConfiguredProjectAndPool()
    {
        var argv = IncusCommandBuilder.BuildInit(
            Options,
            "images:ubuntu/24.04/cloud",
            "codeybox-test",
            new SandboxResourceLimits
            {
                CpuCount = 4,
                MemoryBytes = 8L * 1024 * 1024 * 1024,
                DiskBytes = 24L * 1024 * 1024 * 1024,
            });

        Assert.Equal(
            [
                "/opt/incus/bin/incus", "--project", "codeybox-tests",
                "init", "images:ubuntu/24.04/cloud", "codeybox-test",
                "--vm", "--storage", "codeybox-zfs", "--no-profiles",
                "--config", "limits.cpu=4",
                "--config", "limits.memory=8589934592B",
                "--device", "root,size=25769803776B",
            ],
            argv);
    }

    [Fact]
    public void BuildCopy_ConstructsPoolLocalCowCopy()
    {
        var argv = IncusCommandBuilder.BuildCopy(
            Options,
            "cb-incus-baseline-internet-headless-123456789abc/ready",
            "codeybox-test");

        Assert.Equal(
            [
                "/opt/incus/bin/incus", "--project", "codeybox-tests",
                "copy", "cb-incus-baseline-internet-headless-123456789abc/ready",
                "codeybox-test", "--storage", "codeybox-zfs", "--no-profiles",
            ],
            argv);
    }

    [Fact]
    public void BuildCopy_RejectsMutableBaselineInstance()
    {
        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildCopy(
            Options,
            "cb-incus-baseline-internet-headless-123456789abc",
            "codeybox-test"));
    }

    [Fact]
    public void PoolLocalCloneGuard_RejectsActualRootOnDifferentPool()
    {
        IncusSandboxProvider.EnsurePoolLocalClone("codeybox-zfs\n", "codeybox-zfs");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            IncusSandboxProvider.EnsurePoolLocalClone("retired-pool\n", "codeybox-zfs"));

        Assert.Contains("cross-pool full copy", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildDeviceAdd_ForcesVirtiofsAndPreservesReadOnly(bool readOnly)
    {
        var argv = IncusCommandBuilder.BuildDeviceAdd(
            Options,
            "codeybox-test",
            "mount-0",
            "/host/repo",
            "/repo",
            readOnly);

        var expected = new List<string>
        {
            "/opt/incus/bin/incus", "--project", "codeybox-tests",
            "config", "device", "add", "codeybox-test", "mount-0", "disk",
            "source=/host/repo", "path=/repo", "io.bus=virtiofs",
        };
        if (readOnly)
            expected.Add("readonly=true");

        Assert.Equal(expected, argv);
        Assert.DoesNotContain(argv, argument => argument.Contains("9p", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildDeviceRemove_UsesValidatedArgumentVector()
    {
        var argv = IncusCommandBuilder.BuildDeviceRemove(
            Options,
            "codeybox-test",
            "m000");

        Assert.Equal(
            [
                "/opt/incus/bin/incus", "--project", "codeybox-tests",
                "config", "device", "remove", "codeybox-test", "m000",
            ],
            argv);
    }

    [Fact]
    public void BuildExec_PreservesEachArgumentAndWorkingDirectory()
    {
        var argv = IncusCommandBuilder.BuildExec(
            Options,
            "codeybox-test",
            ["printf", "%s\\n", "value with spaces;$(touch /host/pwned)"],
            "/work tree");

        Assert.Equal(
            [
                "/opt/incus/bin/incus", "--project", "codeybox-tests",
                "exec", "codeybox-test", "--cwd", "/work tree",
                "--user", "1000", "--group", "1000", "--",
                "printf", "%s\\n", "value with spaces;$(touch /host/pwned)",
            ],
            argv);
    }

    [Fact]
    public void BuildExec_RejectsEmptyCommand()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            IncusCommandBuilder.BuildExec(Options, "codeybox-test", []));

        Assert.Contains("must not be empty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInit_RejectsInvalidNameAndNonPositiveLimits()
    {
        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildInit(
            Options,
            "images:ubuntu/24.04/cloud",
            "--force",
            SandboxResourceLimits.Default));

        Assert.Throws<ArgumentOutOfRangeException>(() => IncusCommandBuilder.BuildInit(
            Options,
            "images:ubuntu/24.04/cloud",
            "codeybox-test",
            new SandboxResourceLimits { CpuCount = 0 }));

        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildInit(
            Options,
            new string('i', 4097),
            "codeybox-test",
            SandboxResourceLimits.Default));
    }

    [Theory]
    [InlineData("relative/host", "/repo")]
    [InlineData("/host/repo", "relative/guest")]
    [InlineData("/host/repo", "/repo/../root")]
    public void BuildDeviceAdd_RejectsNonCanonicalPaths(string hostPath, string guestPath)
    {
        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildDeviceAdd(
            Options,
            "codeybox-test",
            "mount-0",
            hostPath,
            guestPath,
            readOnly: true));
    }

    [Fact]
    public void BuildNicAdd_RejectsInvalidBridgeName()
    {
        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildNicAdd(
            Options,
            "codeybox-test",
            "bridge-name-is-too-long"));
    }

    [Fact]
    public void BuildNicAdd_ConstructsUnmanagedHostBridgeDevice()
    {
        var argv = IncusCommandBuilder.BuildNicAdd(
            Options,
            "codeybox-test",
            "cb-net");

        Assert.Equal(
            [
                "/opt/incus/bin/incus", "--project", "codeybox-tests",
                "config", "device", "add", "codeybox-test", "codeybox-net", "nic",
                "nictype=bridged", "parent=cb-net", "name=eth0",
            ],
            argv);
    }

    [Fact]
    public void BuildExec_RejectsNulInCommandArgument()
    {
        Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildExec(
            Options,
            "codeybox-test",
            ["printf", "unsafe\0argument"]));
    }

    [Fact]
    public void ComputeConfigHash_IsStableAndSensitiveToBaselineContent()
    {
        var first = new IncusSandboxOptions
        {
            DefaultImage = "images:ubuntu/24.04/cloud",
            StoragePoolName = "codeybox-zfs",
            NetworkProfiles = new Dictionary<string, string>
            {
                ["internet-only"] = "cb-internet",
            },
            ExtraRuncmd = ["apt-get update", "apt-get install -y git"],
        };
        var equivalent = first with
        {
            NetworkProfiles = new Dictionary<string, string>
            {
                ["internet-only"] = "cb-internet",
            },
            ExtraRuncmd = ["apt-get update", "apt-get install -y git"],
        };
        var changed = first with
        {
            ExtraRuncmd = ["apt-get update", "apt-get install -y git jq"],
        };

        var firstHash = IncusBaselineNaming.ComputeConfigHash(
            first,
            "internet-only",
            SandboxProfileFlavor.Headless);
        var equivalentHash = IncusBaselineNaming.ComputeConfigHash(
            equivalent,
            "internet-only",
            SandboxProfileFlavor.Headless);
        var changedHash = IncusBaselineNaming.ComputeConfigHash(
            changed,
            "internet-only",
            SandboxProfileFlavor.Headless);

        Assert.Equal(64, firstHash.Length);
        Assert.Equal(firstHash, equivalentHash);
        Assert.NotEqual(firstHash, changedHash);
    }

    [Fact]
    public void DeriveBaselineName_NormalizesInputAndHonorsIncusNameLimit()
    {
        var options = Options with
        {
            BaselineNamePrefix = "CODEYBOX baseline prefix that is deliberately far too long",
        };

        var name = IncusBaselineNaming.DeriveBaselineName(
            options,
            "Internet / Strict",
            SandboxProfileFlavor.Graphical);

        Assert.Matches("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", name);
        Assert.True(name.Length <= 63, $"derived name had {name.Length} characters: {name}");
        Assert.Contains("internet-strict-gui-", name, StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineNamespace_AcceptsDerivedRefAndRejectsLookalikes()
    {
        var options = Options with
        {
            NetworkProfiles = new Dictionary<string, string>
            {
                ["internet-only"] = "cb-net",
            },
        };
        var derived = IncusBaselineNaming.DeriveBaselineName(
            options,
            "internet-only",
            SandboxProfileFlavor.Headless);

        Assert.True(IncusSandboxProvider.IsOwnedBaselineRef(options, derived));
        Assert.False(IncusSandboxProvider.IsOwnedBaselineRef(options, derived[..^1] + "g"));
        Assert.False(IncusSandboxProvider.IsOwnedBaselineRef(options, "foreign-" + derived));
        Assert.False(IncusSandboxProvider.IsOwnedBaselineRef(options, derived + "0"));
        Assert.False(IncusSandboxProvider.IsOwnedBaselineRef("unsafe/path", derived));
        Assert.False(IncusSandboxProvider.IsOwnedBaselineRef("cb-bake-", derived));
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void Decide_UsesCowOnlyWhenBaselineFeatureAndInstanceAreAvailable(
        bool useBaselines,
        bool baselineExists,
        int expected)
    {
        var options = Options with { UseBaselineImages = useBaselines };
        var spec = new SandboxSpec
        {
            ImageReference = "images:ubuntu/24.04/cloud",
            Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
        };

        var decision = IncusProvisioningDecision.Decide(options, spec, baselineExists);

        Assert.Equal((IncusProvisioningPath)expected, decision);
    }

    [Fact]
    public void Decide_DoesNotCloneProfilelessBaseline()
    {
        var options = Options with { UseBaselineImages = true };
        var spec = new SandboxSpec
        {
            ImageReference = "images:ubuntu/24.04/cloud",
            Network = new SandboxNetworkPolicy(),
        };

        var decision = IncusProvisioningDecision.Decide(options, spec, baselineExists: true);

        Assert.Equal(IncusProvisioningPath.FullLaunch, decision);
    }

    [Fact]
    public void IgnoredImageSentinel_UsesConfiguredDefaultAndRemainsBaselineEligible()
    {
        var options = Options with
        {
            DefaultImage = "local-ubuntu",
            UseBaselineImages = true,
        };
        var spec = new SandboxSpec
        {
            ImageReference = "ignored",
            Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
        };

        Assert.Equal("local-ubuntu", IncusSandboxProvider.ResolveImage(options, spec.ImageReference));
        Assert.Equal(
            IncusProvisioningPath.CowCopy,
            IncusProvisioningDecision.Decide(options, spec, baselineExists: true));
    }

    [Fact]
    public void Decide_PinnedBaselineOverridesAStaleImageReference()
    {
        var options = Options with
        {
            DefaultImage = "images:ubuntu/26.04/cloud",
            UseBaselineImages = true,
        };
        var spec = new SandboxSpec
        {
            ImageReference = "images:ubuntu/24.04/cloud",
            BaselineImageRef = "cb-incus-baseline-internet-headless-123456789abc",
            Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
        };

        Assert.Equal(
            IncusProvisioningPath.CowCopy,
            IncusProvisioningDecision.Decide(options, spec, baselineExists: true));
    }

    [Fact]
    public async Task EnsureBaselineImage_AcceptsExistingSameTargetPinAfterConfigDrift()
    {
        var original = Options with
        {
            DiskGuard = null,
            DefaultImage = "images:ubuntu/24.04/cloud",
            NetworkProfiles = new Dictionary<string, string> { ["internet-only"] = "cb-net" },
            ExtraRuncmd = ["install git"],
        };
        var stalePin = IncusBaselineNaming.DeriveBaselineName(
            original,
            "internet-only",
            SandboxProfileFlavor.Headless);
        var live = original with
        {
            DefaultImage = "images:ubuntu/26.04/cloud",
            ExtraRuncmd = ["install git jq"],
        };
        Assert.NotEqual(
            stalePin,
            IncusBaselineNaming.DeriveBaselineName(live, "internet-only", SandboxProfileFlavor.Headless));
        var runner = new ExistingBaselineProcessRunner(
            stalePin,
            "internet-only",
            SandboxProfileFlavor.Headless,
            live.StoragePoolName);
        var provider = new IncusSandboxProvider(
            () => live,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        var resolved = await provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            stalePin,
            CancellationToken.None);

        Assert.Equal(stalePin, resolved);
        Assert.DoesNotContain(runner.Commands, command => command.Contains("init", StringComparer.Ordinal));
    }

    [Fact]
    public async Task EnsureBaselineImage_RejectsStalePinBoundToDifferentTarget()
    {
        var options = Options with
        {
            DiskGuard = null,
            NetworkProfiles = new Dictionary<string, string> { ["internet-only"] = "cb-net" },
            ExtraRuncmd = ["install changed"],
        };
        const string stalePin = "cb-incus-baseline-audit-headless-123456789abc";
        var runner = new ExistingBaselineProcessRunner(
            stalePin,
            "audit",
            SandboxProfileFlavor.Headless,
            options.StoragePoolName);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                stalePin,
                CancellationToken.None));

        Assert.Contains("not owned and bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureBaselineImage_RejectsPinFromDifferentStoragePool()
    {
        var options = Options with
        {
            DiskGuard = null,
            NetworkProfiles = new Dictionary<string, string> { ["internet-only"] = "cb-net" },
        };
        const string stalePin = "cb-incus-baseline-internet-headless-123456789abc";
        var runner = new ExistingBaselineProcessRunner(
            stalePin,
            "internet-only",
            SandboxProfileFlavor.Headless,
            "retired-pool");
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                stalePin,
                CancellationToken.None));

        Assert.Contains("storage pool", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, command => command.Contains("copy", StringComparer.Ordinal));
    }

    private sealed class ExistingBaselineProcessRunner(
        string baselineName,
        string profileName,
        SandboxProfileFlavor flavor,
        string storagePoolName) : IProcessRunner
    {
        private string? _restrictedDiskPaths;
        internal List<IReadOnlyList<string>> Commands { get; } = [];

        public Task<ProcessRunResult> RunAsync(
            IReadOnlyList<string> argv,
            string? stdin,
            CancellationToken ct,
            Action<string>? stdoutChunkCallback = null,
            Action<string>? stderrChunkCallback = null,
            int? maxStdoutBytes = null,
            int? maxStderrBytes = null,
            IReadOnlyDictionary<string, string>? environment = null,
            bool killOnOutputLimit = true)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(argv.ToArray());
            string stdout;
            if (argv.SequenceEqual(["/opt/incus/bin/incus", "project", "list", "--format=json"]))
            {
                stdout = "[{\"name\":\"codeybox-tests\"}]";
            }
            else if (argv.SequenceEqual(["/opt/incus/bin/incus", "query", "/1.0"]))
            {
                stdout = "{\"metadata\":{\"api_extensions\":[\"disk_io_bus_cache_filesystem\",\"projects_restrictions\"],\"environment\":{\"kernel_version\":\"6.14.0-test\"}}}";
            }
            else if (argv.SequenceEqual(["/opt/incus/bin/incus", "query", "/1.0/projects/codeybox-tests"]))
            {
                var config = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [IncusProjectSecurity.FeaturesImagesKey] = "false",
                    [IncusProjectSecurity.FeaturesProfilesKey] = "true",
                    [IncusProjectSecurity.ManagedKey] = "true",
                    [IncusProjectSecurity.SchemaKey] = "1",
                };
                if (_restrictedDiskPaths is not null)
                {
                    config[IncusProjectSecurity.RestrictedKey] = "true";
                    config[IncusProjectSecurity.RestrictedDiskKey] = "allow";
                    config[IncusProjectSecurity.RestrictedDiskPathsKey] = _restrictedDiskPaths;
                    config[IncusProjectSecurity.RestrictedNicKey] = "allow";
                    config[IncusProjectSecurity.RestrictedSnapshotsKey] = "allow";
                    config[IncusProjectSecurity.RestrictedVmLowLevelKey] = "block";
                }
                stdout = System.Text.Json.JsonSerializer.Serialize(new
                {
                    metadata = new { name = "codeybox-tests", config },
                });
            }
            else if (argv.Take(4).SequenceEqual(
                ["/opt/incus/bin/incus", "project", "set", "codeybox-tests"]))
            {
                _restrictedDiskPaths = argv
                    .Single(argument => argument.StartsWith(
                        IncusProjectSecurity.RestrictedDiskPathsKey + "=",
                        StringComparison.Ordinal))
                    [(IncusProjectSecurity.RestrictedDiskPathsKey.Length + 1)..];
                stdout = string.Empty;
            }
            else if (argv.Contains("storage", StringComparer.Ordinal)
                && argv.Contains("list", StringComparer.Ordinal))
            {
                stdout = "[{\"name\":\"codeybox-zfs\",\"driver\":\"zfs\",\"config\":{}}]";
            }
            else if (argv.Contains("snapshot", StringComparer.Ordinal)
                && argv.Contains("list", StringComparer.Ordinal))
            {
                stdout = "[{\"name\":\"ready\"}]";
            }
            else if (argv.Contains("list", StringComparer.Ordinal))
            {
                stdout = $$"""
                    [{
                      "name": "{{baselineName}}",
                      "type": "virtual-machine",
                      "status": "STOPPED",
                      "config": {
                        "{{IncusSandboxProvider.ManagedKey}}": "true",
                        "{{IncusSandboxProvider.KindKey}}": "{{IncusSandboxProvider.BaselineKind}}",
                        "{{IncusSandboxProvider.BaselineProfileKey}}": "{{profileName}}",
                        "{{IncusSandboxProvider.BaselineFlavorKey}}": "{{flavor}}",
                        "{{IncusSandboxProvider.BaselinePoolKey}}": "{{storagePoolName}}"
                      }
                    }]
                    """;
            }
            else
            {
                throw new InvalidOperationException($"Unexpected Incus test command: {string.Join(' ', argv)}");
            }
            return Task.FromResult(new ProcessRunResult(0, stdout, string.Empty));
        }
    }
}
