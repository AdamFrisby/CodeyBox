using System.Formats.Tar;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

public sealed class IncusBaselineProvisioningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codeybox-incus-provision-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void BaselineHash_CoversProvisioningConfigBoundsAndExactExecutableBytes()
    {
        var executable = Path.Combine(_root, "tool");
        File.WriteAllText(executable, "first");
        var baseline = BaseOptions() with
        {
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/home/ubuntu/.local/bin/tool",
                    VmSymlinks = ["/usr/local/bin/tool"],
                    Label = "tool",
                },
            ],
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = "/var/cache/source",
                    VmDestPath = "/var/cache/codeybox",
                    MaxSizeMB = 64,
                },
            ],
            BaselineVerificationCommands =
            [
                new BaselineVerificationCommand("tool", ["tool", "--version"], "tool unavailable"),
            ],
        };
        var environment = EnvironmentReader(_root);
        var original = IncusBaselineNaming.ComputeConfigHash(
            baseline,
            "internet-only",
            SandboxProfileFlavor.Headless,
            environment);

        File.WriteAllText(executable, "other");
        var changedContent = IncusBaselineNaming.ComputeConfigHash(
            baseline,
            "internet-only",
            SandboxProfileFlavor.Headless,
            environment);
        Assert.NotEqual(original, changedContent);
        File.WriteAllText(executable, "first");

        IncusSandboxOptions[] variants =
        [
            baseline with
            {
                PackageCacheSeeds =
                [
                    baseline.PackageCacheSeeds[0] with { MaxSizeMB = 65 },
                ],
            },
            baseline with
            {
                ExecutableProvisions =
                [
                    baseline.ExecutableProvisions[0] with { VmDestPath = "/opt/tool" },
                ],
            },
            baseline with
            {
                BaselineVerificationCommands =
                [
                    new BaselineVerificationCommand("tool", ["tool", "version"], "tool unavailable"),
                ],
            },
            baseline with { MaxExecutableProvisionBytes = baseline.MaxExecutableProvisionBytes + 1 },
            baseline with
            {
                MaxAggregateExecutableProvisionBytes =
                    baseline.MaxAggregateExecutableProvisionBytes + 1,
            },
            baseline with { MaxPackageCacheSeedBytes = baseline.MaxPackageCacheSeedBytes + 1 },
            baseline with
            {
                MaxAggregatePackageCacheSeedBytes =
                    baseline.MaxAggregatePackageCacheSeedBytes + 1,
            },
            baseline with { MaxPackageCacheSeedEntries = baseline.MaxPackageCacheSeedEntries + 1 },
        ];

        Assert.All(variants, variant => Assert.NotEqual(
            original,
            IncusBaselineNaming.ComputeConfigHash(
                variant,
                "internet-only",
                SandboxProfileFlavor.Headless,
                environment)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("sha512:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void BaselineHash_RejectsMalformedInjectedExecutableDigest(string digest)
    {
        var options = BaseOptions() with
        {
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/unused",
                    VmDestPath = "/usr/local/bin/tool",
                },
            ],
        };

        Assert.Throws<ArgumentException>(() => IncusBaselineNaming.ComputeConfigHash(
            options,
            "internet-only",
            SandboxProfileFlavor.Headless,
            executableContentSha256: [digest]));
    }

    [Fact]
    public void Options_RejectAmbiguousProvisioningDestinationsBeforeIncusRuns()
    {
        var options = BaseOptions() with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = "/cache/one",
                    VmDestPath = "/var/cache/codeybox",
                },
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = "/cache/two",
                    VmDestPath = "/var/cache/codeybox",
                },
            ],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/tools/one",
                    VmDestPath = "/usr/local/bin/tool",
                    VmSymlinks = ["/opt/tool"],
                },
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/tools/two",
                    VmDestPath = "/opt/tool",
                },
            ],
        };
        var errors = IncusSandboxOptions.Validate(options);

        Assert.Contains(errors, error => error.Contains("PackageCacheSeeds", StringComparison.Ordinal)
            && error.Contains("overlaps", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("ExecutableProvisions", StringComparison.Ordinal)
            && error.Contains("overlaps", StringComparison.Ordinal));
        var runner = new NeverRunner();
        Assert.Throws<InvalidOperationException>(() => new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner));
        Assert.False(runner.Called);
    }

    [Fact]
    public void Options_RejectProviderOwnedGuestControlPathOverlapBeforeIncusRuns()
    {
        var options = BaseOptions() with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = "/cache/source",
                    VmDestPath = $"{IncusCloudInit.RuntimeDirectory}/cache",
                },
            ],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/tools/source",
                    VmDestPath = IncusCloudInit.ExecWrapperPath,
                    VmSymlinks = [$"{IncusCloudInit.ControlDirectory}/tool"],
                },
            ],
        };
        var errors = IncusSandboxOptions.Validate(options);

        Assert.Equal(3, errors.Count(error => error.Contains("provider-owned guest control path", StringComparison.Ordinal)));
        var runner = new NeverRunner();
        Assert.Throws<InvalidOperationException>(() => new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner));
        Assert.False(runner.Called);
    }

    [Fact]
    public void Options_RejectCacheExecutableOverlapAndVolatileProvisioningDestinations()
    {
        var collision = BaseOptions() with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = "/cache/source",
                    VmDestPath = "/opt/codeybox",
                },
            ],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/tools/source",
                    VmDestPath = "/opt/codeybox/bin/tool",
                },
            ],
        };
        var collisionErrors = IncusSandboxOptions.Validate(collision);
        Assert.Contains(collisionErrors, error => error.Contains(
            "package seeding after verification",
            StringComparison.Ordinal));

        var volatileDestinations = BaseOptions() with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = "/cache/source",
                    VmDestPath = "/run/codeybox-cache",
                },
            ],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/tools/source",
                    VmDestPath = "/proc/codeybox-tool",
                    VmSymlinks = ["/dev/codeybox-tool", "/sys/codeybox-tool"],
                },
            ],
        };
        var volatileErrors = IncusSandboxOptions.Validate(volatileDestinations);
        Assert.Equal(4, volatileErrors.Count(error => error.Contains(
            "volatile or pseudo-filesystem",
            StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GuestCanonicalization_RejectsImageAliasesAndAliasedMountOverlap()
    {
        var executable = Path.Combine(_root, "alias-tool");
        var cache = Path.Combine(_root, "alias-cache");
        File.WriteAllText(executable, "tool");
        File.WriteAllText(cache, "cache");
        var stagingRoot = Path.Combine(_root, "alias-staging");
        var aliasOptions = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = cache,
                    VmDestPath = "/bin",
                },
            ],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/usr/bin/alias-tool",
                },
            ],
        };
        var aliasRunner = new BaselineBakeRunner(
            stagingRoot,
            verificationExitCode: 0,
            canonicalizeGuestPath: path => path switch
            {
                "/bin" => "/usr/bin",
                _ when path.StartsWith("/var/run/", StringComparison.Ordinal) =>
                    "/run/" + path["/var/run/".Length..],
                _ => path,
            });
        var aliasProvider = new IncusSandboxProvider(
            () => aliasOptions,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            aliasRunner,
            environmentVariableReader: EnvironmentReader(_root));

        var binAlias = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            aliasProvider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));
        Assert.Contains("filesystem alias", binAlias.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(aliasRunner.Invocations, invocation => IsFilePush(invocation.Argv));
        Assert.DoesNotContain(aliasRunner.Invocations, invocation =>
            invocation.Argv.Contains("snapshot", StringComparer.Ordinal)
            || invocation.Argv.Contains("move", StringComparer.Ordinal));

        var volatileAliasOptions = BaseOptions() with
        {
            StagingDirectory = Path.Combine(_root, "volatile-alias-staging"),
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = cache,
                    VmDestPath = "/var/run/codeybox-cache",
                },
            ],
        };
        var volatileRunner = new BaselineBakeRunner(
            volatileAliasOptions.StagingDirectory!,
            verificationExitCode: 0,
            canonicalizeGuestPath: path => path.StartsWith("/var/run/", StringComparison.Ordinal)
                ? "/run/" + path["/var/run/".Length..]
                : path);
        var volatileProvider = new IncusSandboxProvider(
            () => volatileAliasOptions,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            volatileRunner,
            environmentVariableReader: EnvironmentReader(_root));
        var varRunAlias = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            volatileProvider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));
        Assert.Contains("filesystem alias", varRunAlias.Message, StringComparison.Ordinal);

        var mountOptions = BaseOptions() with
        {
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = cache,
                    VmDestPath = "/mnt/real/cache",
                },
            ],
        };
        var mountRunner = new BaselineBakeRunner(
            Path.Combine(_root, "mount-alias-staging"),
            verificationExitCode: 0,
            canonicalizeGuestPath: path => path switch
            {
                "/opt/alias" => "/mnt/real",
                "/opt/protected-alias" => "/etc",
                "/opt/run-alias" => "/run/codeybox-alias",
                "/opt/a" => "/mnt/shared",
                "/opt/b" => "/mnt/shared",
                "/opt/exec-alias/tool" => "/usr/local/bin/tool",
                "/opt/link-alias/tool" => "/usr/local/bin/tool",
                _ => path,
            });
        var mountProvider = new IncusSandboxProvider(
            () => mountOptions,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            mountRunner);
        var mountAlias = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mountProvider.ValidateCanonicalProvisioningPathsAsync(
                mountOptions,
                "codeybox-test-instance",
                ["/opt/alias"],
                CancellationToken.None));
        Assert.Contains("mount destinations", mountAlias.Message, StringComparison.Ordinal);

        foreach (var protectedAlias in new[] { "/opt/protected-alias", "/opt/run-alias" })
        {
            var protectedFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                mountProvider.ValidateCanonicalProvisioningPathsAsync(
                    BaseOptions(),
                    "codeybox-test-instance",
                    [protectedAlias],
                    CancellationToken.None));
            Assert.Contains("mount destinations", protectedFailure.Message, StringComparison.Ordinal);
        }
        var duplicateCanonicalMount = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mountProvider.ValidateCanonicalProvisioningPathsAsync(
                BaseOptions(),
                "codeybox-test-instance",
                ["/opt/a", "/opt/b"],
                CancellationToken.None));
        Assert.Contains("mount destinations", duplicateCanonicalMount.Message, StringComparison.Ordinal);

        IncusSandboxOptions[] executableAliasVariants =
        [
            BaseOptions() with
            {
                ExecutableProvisions =
                [
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = executable,
                        VmDestPath = "/opt/exec-alias/tool",
                    },
                ],
            },
            BaseOptions() with
            {
                ExecutableProvisions =
                [
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = executable,
                        VmDestPath = "/home/ubuntu/.local/bin/tool",
                        VmSymlinks = ["/opt/link-alias/tool"],
                    },
                ],
            },
        ];
        foreach (var variant in executableAliasVariants)
        {
            var executableAlias = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                mountProvider.ValidateCanonicalProvisioningPathsAsync(
                    variant,
                    "codeybox-test-instance",
                    mountGuestPaths: [],
                    CancellationToken.None));
            Assert.Contains("filesystem alias", executableAlias.Message, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(mountRunner.Invocations, invocation =>
            IsFilePush(invocation.Argv)
            || invocation.Argv.Contains("install", StringComparer.Ordinal)
            || invocation.Argv.Contains("ln", StringComparer.Ordinal));
    }

    [Fact]
    public async Task FullLaunch_RejectsProvisioningMountOverlapBeforeIncusRuns()
    {
        var options = BaseOptions() with
        {
            UseBaselineImages = false,
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = "/cache/source",
                    VmDestPath = "/home/ubuntu/.nuget/packages",
                },
            ],
        };
        var mount = new SandboxMount { SandboxPath = "/home/ubuntu", Tmpfs = true };
        IncusSandboxOptions[] targetVariants =
        [
            options,
            options with
            {
                PackageCacheSeeds = [],
                ExecutableProvisions =
                [
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = "/tools/source",
                        VmDestPath = "/home/ubuntu/.local/bin/tool",
                    },
                ],
            },
            options with
            {
                PackageCacheSeeds = [],
                ExecutableProvisions =
                [
                    new BaselineExecutableProvision
                    {
                        HostSourcePath = "/tools/source",
                        VmDestPath = "/usr/local/lib/codeybox/tool",
                        VmSymlinks = ["/home/ubuntu/.local/bin/tool"],
                    },
                ],
            },
        ];
        Assert.All(targetVariants, variant =>
        {
            var direct = Assert.Throws<InvalidOperationException>(() =>
                IncusSandboxProvider.ValidateProvisioningMountSeparation(variant, [mount]));
            Assert.Contains("root provisioning writes", direct.Message, StringComparison.Ordinal);
        });

        var runner = new NeverRunner();
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(
            new SandboxSpec
            {
                ImageReference = options.DefaultImage,
                Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
                Mounts =
                [
                    new SandboxMount
                    {
                        SandboxPath = "/home/ubuntu/.nuget",
                        Tmpfs = true,
                        SizeBytes = 1024 * 1024,
                    },
                ],
            }));

        Assert.Contains("root provisioning writes", exception.Message, StringComparison.Ordinal);
        Assert.False(runner.Called);
    }

    [Fact]
    public async Task CowClone_RejectsCanonicalMountAliasThatHidesProvisionedExecutable()
    {
        const string pinned = "cb-incus-baseline-internet-headless-123456789abc";
        var stagingRoot = Path.Combine(_root, "cow-alias-staging");
        var mountSource = CreateDirectory("cow-alias-host-source");
        var hostIdentity = IncusHostIdentity.GetEffectiveIdentity();
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            AllowedHostMountRoots = [_root],
            GuestUserId = hostIdentity.UserId,
            GuestGroupId = hostIdentity.GroupId,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "/host/source-not-read-for-pin",
                    VmDestPath = "/home/ubuntu/.local/bin/tool",
                },
            ],
        };
        var runner = new BaselineBakeRunner(
            stagingRoot,
            verificationExitCode: 0,
            existingPinnedName: pinned,
            canonicalizeGuestPath: path => path == "/home/ubuntu/bin"
                ? "/home/ubuntu/.local/bin"
                : path);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(
            new SandboxSpec
            {
                ImageReference = options.DefaultImage,
                BaselineImageRef = pinned,
                Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
                Mounts =
                [
                    new SandboxMount
                    {
                        HostPath = mountSource,
                        SandboxPath = "/home/ubuntu/bin",
                        ReadOnly = true,
                    },
                ],
            }));

        Assert.Contains("mount destinations", exception.Message, StringComparison.Ordinal);
        AssertMountAliasRejectedBeforeHostDeviceAttachment(runner.Invocations, "copy");
    }

    [Fact]
    public async Task FullLaunch_RejectsCanonicalMountAliasBeforeHostDeviceAttachmentWithoutProvisioning()
    {
        var stagingRoot = Path.Combine(_root, "full-launch-alias-staging");
        var mountSource = CreateDirectory("full-launch-alias-host-source");
        var hostIdentity = IncusHostIdentity.GetEffectiveIdentity();
        var options = BaseOptions() with
        {
            UseBaselineImages = false,
            StagingDirectory = stagingRoot,
            AllowedHostMountRoots = [_root],
            GuestUserId = hostIdentity.UserId,
            GuestGroupId = hostIdentity.GroupId,
            PackageCacheSeeds = [],
            ExecutableProvisions = [],
            BaselineVerificationCommands = [],
        };
        var runner = new BaselineBakeRunner(
            stagingRoot,
            verificationExitCode: 0,
            canonicalizeGuestPath: path => path == "/opt/alias" ? "/etc" : path);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(
            new SandboxSpec
            {
                ImageReference = options.DefaultImage,
                Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
                Mounts =
                [
                    new SandboxMount
                    {
                        HostPath = mountSource,
                        SandboxPath = "/opt/alias",
                        ReadOnly = true,
                    },
                ],
            }));

        Assert.Contains("mount destinations", exception.Message, StringComparison.Ordinal);
        AssertMountAliasRejectedBeforeHostDeviceAttachment(runner.Invocations, "init");
    }

    [Fact]
    public void ExecutableStaging_HashesTheExactPrivateBytesAndRejectsSymlinksBoundsAndCancellation()
    {
        var source = Path.Combine(_root, "source-tool");
        File.WriteAllText(source, "trusted-tool-v1");
        var stagingRoot = CreateDirectory("staging-executable");
        var options = BaseOptions() with
        {
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "~/source-tool",
                    VmDestPath = "/usr/local/bin/tool",
                },
            ],
        };
        using (var workspace = IncusProvisioningWorkspace.Create(
                   options,
                   stagingRoot,
                   EnvironmentReader(_root),
                   Guid.NewGuid,
                   CancellationToken.None))
        {
            var staged = Assert.Single(workspace.Executables);
            var expected = "sha256:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes("trusted-tool-v1")));
            Assert.Equal(expected, staged.ContentSha256);
            File.WriteAllText(source, "mutated-after-stage");
            Assert.Equal("trusted-tool-v1", File.ReadAllText(staged.StagedPath));
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(staged.StagedPath));
            }
        }
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(stagingRoot),
            path => Path.GetFileName(path).StartsWith(
                IncusProvisioningWorkspace.DirectoryPrefix,
                StringComparison.Ordinal));

        var symlink = Path.Combine(_root, "tool-link");
        File.CreateSymbolicLink(symlink, source);
        var symlinkOptions = options with
        {
            ExecutableProvisions = [options.ExecutableProvisions[0] with { HostSourcePath = symlink }],
        };
        Assert.ThrowsAny<IOException>(() => IncusBaselineProvisioning.FingerprintExecutables(
            symlinkOptions,
            EnvironmentReader(_root),
            CancellationToken.None));

        File.WriteAllBytes(source, [1, 2]);
        Assert.ThrowsAny<IOException>(() => IncusBaselineProvisioning.FingerprintExecutables(
            options with { MaxExecutableProvisionBytes = 1, MaxAggregateExecutableProvisionBytes = 1 },
            EnvironmentReader(_root),
            CancellationToken.None));

        var secondSource = Path.Combine(_root, "second-tool");
        File.WriteAllBytes(secondSource, [3, 4]);
        var aggregateOptions = options with
        {
            ExecutableProvisions =
            [
                options.ExecutableProvisions[0] with { HostSourcePath = source },
                options.ExecutableProvisions[0] with { HostSourcePath = secondSource },
            ],
            MaxExecutableProvisionBytes = 2,
            MaxAggregateExecutableProvisionBytes = 3,
        };
        Assert.ThrowsAny<IOException>(() => IncusBaselineProvisioning.FingerprintExecutables(
            aggregateOptions,
            EnvironmentReader(_root),
            CancellationToken.None));

        var aggregateStagingRoot = CreateDirectory("staging-executable-aggregate-bound");
        Assert.ThrowsAny<IOException>(() => IncusProvisioningWorkspace.Create(
            aggregateOptions,
            aggregateStagingRoot,
            EnvironmentReader(_root),
            Guid.NewGuid,
            CancellationToken.None));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(aggregateStagingRoot),
            path => Path.GetFileName(path).StartsWith(
                IncusProvisioningWorkspace.DirectoryPrefix,
                StringComparison.Ordinal));

        var perFileStagingRoot = CreateDirectory("staging-executable-per-file-bound");
        Assert.ThrowsAny<IOException>(() => IncusProvisioningWorkspace.Create(
            options with
            {
                MaxExecutableProvisionBytes = 1,
                MaxAggregateExecutableProvisionBytes = 2,
            },
            perFileStagingRoot,
            EnvironmentReader(_root),
            Guid.NewGuid,
            CancellationToken.None));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(perFileStagingRoot),
            path => Path.GetFileName(path).StartsWith(
                IncusProvisioningWorkspace.DirectoryPrefix,
                StringComparison.Ordinal));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => IncusBaselineProvisioning.FingerprintExecutables(
            options,
            EnvironmentReader(_root),
            cancelled.Token));
    }

    [Fact]
    public void BoundedProvisioningReads_ObserveCancellationBetweenContentChunks()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelAfterFirstReadStream(
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray(),
            cancellation);
        var buffer = new byte[8];

        Assert.Equal(
            buffer.Length,
            IncusBaselineProvisioning.ReadCancellable(
                source,
                buffer,
                cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            IncusBaselineProvisioning.ReadCancellable(
                source,
                buffer,
                cancellation.Token));
        Assert.True(source.Position < source.Length);
    }

    [Fact]
    public void PackageArchive_PreservesDirectorySemanticsAndRejectsSpecialFilesAndBounds()
    {
        var sourceDirectory = CreateDirectory("cache-source");
        File.WriteAllText(Path.Combine(sourceDirectory, "package.txt"), "package-content");
        var sourceFile = Path.Combine(_root, "single.nupkg");
        File.WriteAllText(sourceFile, "single-content");
        var stagingRoot = CreateDirectory("staging-cache");
        var options = BaseOptions();
        using var workspace = IncusProvisioningWorkspace.Create(
            options,
            stagingRoot,
            EnvironmentReader(_root),
            Guid.NewGuid,
            CancellationToken.None);
        var aggregateBytes = 0L;
        var directoryArchive = workspace.CreatePackageArchive(
            options,
            new BaselinePackageCacheSeed
            {
                HostSourcePath = sourceDirectory,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 0,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None);
        var fileArchive = workspace.CreatePackageArchive(
            options,
            new BaselinePackageCacheSeed
            {
                HostSourcePath = sourceFile,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 1,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None);

        Assert.Equal("package-content", ReadSingleTarFile(directoryArchive, "package.txt"));
        Assert.Equal("single-content", ReadSingleTarFile(fileArchive, "single.nupkg"));

        Assert.ThrowsAny<IOException>(() => workspace.CreatePackageArchive(
            options with { MaxPackageCacheSeedBytes = 1, MaxAggregatePackageCacheSeedBytes = 1 },
            new BaselinePackageCacheSeed
            {
                HostSourcePath = sourceFile,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 2,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None));

        if (OperatingSystem.IsLinux())
        {
            var socketPath = Path.Combine(sourceDirectory, "cache.sock");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            var specialAggregate = 0L;
            Assert.ThrowsAny<IOException>(() => workspace.CreatePackageArchive(
                options,
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = sourceDirectory,
                    VmDestPath = "/var/cache/codeybox",
                },
                index: 3,
                EnvironmentReader(_root),
                ref specialAggregate,
                CancellationToken.None));
        }
    }

    [Fact]
    public void PackageArchive_EnforcesPerSeedAggregateEntryAndCancellationBounds()
    {
        var first = Path.Combine(_root, "first-cache");
        var second = Path.Combine(_root, "second-cache");
        File.WriteAllBytes(first, [1, 2]);
        File.WriteAllBytes(second, [3, 4]);
        var entries = CreateDirectory("bounded-cache-entries");
        File.WriteAllText(Path.Combine(entries, "one"), "1");
        File.WriteAllText(Path.Combine(entries, "two"), "2");
        var stagingRoot = CreateDirectory("staging-cache-bounds");
        var options = BaseOptions() with
        {
            MaxPackageCacheSeedBytes = 16,
            MaxAggregatePackageCacheSeedBytes = 16,
            MaxPackageCacheSeedEntries = 16,
        };
        using var workspace = IncusProvisioningWorkspace.Create(
            options,
            stagingRoot,
            EnvironmentReader(_root),
            Guid.NewGuid,
            CancellationToken.None);

        var aggregateBytes = 0L;
        Assert.ThrowsAny<IOException>(() => workspace.CreatePackageArchive(
            options,
            new BaselinePackageCacheSeed
            {
                HostSourcePath = first,
                VmDestPath = "/var/cache/codeybox",
                MaxSizeMB = 1d / (1024d * 1024d),
            },
            index: 0,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None));

        aggregateBytes = 0;
        var aggregateOptions = options with
        {
            MaxPackageCacheSeedBytes = 2,
            MaxAggregatePackageCacheSeedBytes = 3,
        };
        _ = workspace.CreatePackageArchive(
            aggregateOptions,
            new BaselinePackageCacheSeed
            {
                HostSourcePath = first,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 1,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None);
        Assert.ThrowsAny<IOException>(() => workspace.CreatePackageArchive(
            aggregateOptions,
            new BaselinePackageCacheSeed
            {
                HostSourcePath = second,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 2,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None));

        aggregateBytes = 0;
        Assert.ThrowsAny<IOException>(() => workspace.CreatePackageArchive(
            options with { MaxPackageCacheSeedEntries = 1 },
            new BaselinePackageCacheSeed
            {
                HostSourcePath = entries,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 3,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None));

        aggregateBytes = 0;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.CreatePackageArchive(
            options,
            new BaselinePackageCacheSeed
            {
                HostSourcePath = entries,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 4,
            EnvironmentReader(_root),
            ref aggregateBytes,
            cancelled.Token));

        var deep = CreateDirectory("bounded-cache-depth");
        var cursor = deep;
        for (var depth = 0; depth <= 512; depth++)
        {
            cursor = Path.Combine(cursor, "d");
            Directory.CreateDirectory(cursor);
        }
        aggregateBytes = 0;
        var depthFailure = Assert.ThrowsAny<IOException>(() => workspace.CreatePackageArchive(
            options with { MaxPackageCacheSeedEntries = 1024 },
            new BaselinePackageCacheSeed
            {
                HostSourcePath = deep,
                VmDestPath = "/var/cache/codeybox",
            },
            index: 5,
            EnvironmentReader(_root),
            ref aggregateBytes,
            CancellationToken.None));
        Assert.Contains("depth", depthFailure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProvisioningWorkspaceRecovery_DeletesOwnedStaleTreeAndRejectsDeceptiveEntries()
    {
        Assert.Throws<ArgumentException>(() => IncusInputValidation.ValidateInstanceName(
            $"{IncusProvisioningWorkspace.DirectoryPrefix}{Guid.NewGuid():N}",
            "workspace"));
        var stagingRoot = CreateDirectory("staging-recovery");
        using (var workspace = IncusProvisioningWorkspace.Create(
                   BaseOptions(),
                   stagingRoot,
                   EnvironmentReader(_root),
                   Guid.NewGuid,
                   CancellationToken.None))
        {
            var activeRoot = workspace.Root;
            File.WriteAllText(Path.Combine(activeRoot, "large-partial"), "partial");
            Assert.False(IncusProvisioningWorkspace.RecoverStaleWorkspaces(
                stagingRoot,
                CancellationToken.None));
            Assert.True(Directory.Exists(activeRoot));
        }

        var external = CreateDirectory("external-recovery");
        var sentinel = Path.Combine(external, "sentinel");
        File.WriteAllText(sentinel, "keep");
        var deceptive = Path.Combine(stagingRoot, $"{IncusProvisioningWorkspace.DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(deceptive, external);
        Assert.Throws<InvalidOperationException>(() =>
            IncusProvisioningWorkspace.RecoverStaleWorkspaces(stagingRoot, CancellationToken.None));
        Assert.True(File.Exists(sentinel));
        Directory.Delete(deceptive);

        var mismatchedMarker = Path.Combine(
            stagingRoot,
            $"{IncusProvisioningWorkspace.DirectoryPrefix}{Guid.NewGuid():N}");
        Assert.True(IncusSafeFile.TryCreateDirectoryExclusive(mismatchedMarker));
        var marker = Path.Combine(mismatchedMarker, ".codeybox-incus-provision-v1");
        File.WriteAllText(marker, "not-this-workspace\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<InvalidOperationException>(() =>
            IncusProvisioningWorkspace.RecoverStaleWorkspaces(stagingRoot, CancellationToken.None));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(mismatchedMarker),
            path => Path.GetFileName(path).Contains("lease", StringComparison.Ordinal));
        Directory.Delete(mismatchedMarker, recursive: true);

        var foreign = Path.Combine(stagingRoot, $"{IncusProvisioningWorkspace.DirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(foreign);
        Assert.ThrowsAny<Exception>(() =>
            IncusProvisioningWorkspace.RecoverStaleWorkspaces(stagingRoot, CancellationToken.None));
        Assert.True(Directory.Exists(foreign));
        Directory.Delete(foreign);

        var partial = Path.Combine(stagingRoot, $"{IncusProvisioningWorkspace.DirectoryPrefix}{Guid.NewGuid():N}");
        Assert.True(IncusSafeFile.TryCreateDirectoryExclusive(partial));
        Assert.True(IncusProvisioningWorkspace.RecoverStaleWorkspaces(
            stagingRoot,
            CancellationToken.None));
        Assert.False(Directory.Exists(partial));
    }

    [Fact]
    public void ProvisioningWorkspace_DisposeCanRetryAfterValidationFailure()
    {
        var stagingRoot = CreateDirectory("staging-dispose-retry");
        var workspace = IncusProvisioningWorkspace.Create(
            BaseOptions(),
            stagingRoot,
            EnvironmentReader(_root),
            Guid.NewGuid,
            CancellationToken.None);
        var root = workspace.Root;
        var marker = Path.Combine(root, ".codeybox-incus-provision-v1");
        File.WriteAllText(marker, "mismatched\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Throws<InvalidOperationException>(workspace.Dispose);
        Assert.True(Directory.Exists(root));

        File.WriteAllText(marker, Path.GetFileName(root) + "\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        workspace.Dispose();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task BaselineBake_VerificationFailureRunsAfterInstallAsGuestAndPreventsPublish()
    {
        var executable = Path.Combine(_root, "probe-tool-host");
        File.WriteAllText(executable, "probe-bytes");
        var cache = Path.Combine(_root, "cache.nupkg");
        File.WriteAllText(cache, "cache-bytes");
        var stagingRoot = Path.Combine(_root, "provider-staging");
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            ExtraRuncmd = ["echo extra-runcmd"],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/home/ubuntu/.local/bin/probe-tool",
                    VmSymlinks = ["/usr/local/bin/probe-tool"],
                    Label = "probe",
                },
            ],
            BaselineVerificationCommands =
            [
                new BaselineVerificationCommand("probe", ["probe-tool", "--version"], "probe unavailable"),
            ],
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = cache,
                    VmDestPath = "/var/cache/codeybox",
                },
            ],
        };
        var runner = new BaselineBakeRunner(stagingRoot, verificationExitCode: 37);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            newGuid: () => Guid.Parse("11111111-2222-3333-4444-555555555555"),
            environmentVariableReader: EnvironmentReader(_root));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        Assert.Contains("probe unavailable", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret-output", exception.ToString(), StringComparison.Ordinal);
        var commands = runner.Invocations.Select(static invocation => invocation.Argv).ToList();
        var runcmdIndex = runner.Invocations.FindIndex(invocation =>
            invocation.Stdin?.Contains("extra-runcmd", StringComparison.Ordinal) == true);
        var pushIndex = commands.FindIndex(command => IsFilePush(command));
        var installIndex = commands.FindIndex(command =>
            command.Contains("install", StringComparer.Ordinal)
            && command.Contains("/home/ubuntu/.local/bin/probe-tool", StringComparer.Ordinal));
        var verificationIndex = commands.FindIndex(command =>
            command.Contains("setpriv", StringComparer.Ordinal)
            && command.Contains("probe-tool", StringComparer.Ordinal));
        Assert.True(runcmdIndex >= 0 && runcmdIndex < pushIndex);
        Assert.True(pushIndex < installIndex && installIndex < verificationIndex);
        var push = commands[pushIndex];
        var operandBoundary = IndexOf(push, "--");
        Assert.True(operandBoundary >= 0 && operandBoundary + 2 < push.Count);
        Assert.NotEqual(executable, push[operandBoundary + 1]);
        Assert.StartsWith(stagingRoot, push[operandBoundary + 1], StringComparison.Ordinal);
        var verification = commands[verificationIndex];
        Assert.Contains("--cwd", verification);
        Assert.Contains(options.GuestHome, verification);
        Assert.Contains($"--reuid={options.GuestUserId}", verification);
        Assert.Contains($"--regid={options.GuestGroupId}", verification);
        Assert.Contains($"PATH={IncusCloudInit.NonLoginPath}", verification);
        var verificationBoundary = IndexOf(verification, "--");
        Assert.True(verificationBoundary >= 0);
        Assert.Equal(
            [
                "setsid",
                "--",
                "setpriv",
                "--no-new-privs",
                $"--reuid={options.GuestUserId}",
                $"--regid={options.GuestGroupId}",
                "--clear-groups",
                "--",
                "env",
                "-i",
                "--",
                $"HOME={options.GuestHome}",
                $"PATH={IncusCloudInit.NonLoginPath}",
                "LANG=C.UTF-8",
                "probe-tool",
                "--version",
            ],
            verification.Skip(verificationBoundary + 1));
        Assert.Contains(commands, command =>
            command.Contains("install", StringComparer.Ordinal)
            && command.Contains("-d", StringComparer.Ordinal)
            && command.Contains(options.GuestUserId.ToString(), StringComparer.Ordinal)
            && command.Contains("/home/ubuntu/.local/bin", StringComparer.Ordinal));
        Assert.DoesNotContain(commands, command =>
            command.Contains("snapshot", StringComparer.Ordinal)
            && command.Contains("create", StringComparer.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("move", StringComparer.Ordinal));
        Assert.DoesNotContain(commands, command =>
            IsFilePush(command)
            && command.Any(argument => argument.Contains("package-cache", StringComparison.Ordinal)));
        Assert.Contains(commands, command => command.Contains("delete", StringComparer.Ordinal));
        Assert.False(Directory.Exists(stagingRoot)
            && Directory.EnumerateFileSystemEntries(stagingRoot)
                .Any(path => Path.GetFileName(path).StartsWith(
                    IncusProvisioningWorkspace.DirectoryPrefix,
                    StringComparison.Ordinal)));
    }

    [Fact]
    public async Task HostPreflight_RecoversStaleWorkspaceEvenWhenProvisioningListsAreEmpty()
    {
        var stagingRoot = Path.Combine(_root, "empty-config-staging");
        IncusMountStaging.EnsureOwnedStagingRoot(stagingRoot);
        var staleRoot = Path.Combine(
            stagingRoot,
            $"{IncusProvisioningWorkspace.DirectoryPrefix}{Guid.NewGuid():N}");
        Assert.True(IncusSafeFile.TryCreateDirectoryExclusive(staleRoot));
        var options = BaseOptions() with { StagingDirectory = stagingRoot };
        var runner = new BaselineBakeRunner(stagingRoot, verificationExitCode: 0);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        var baseline = await provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.False(Directory.Exists(staleRoot));
        Assert.Contains(runner.Invocations, invocation =>
            invocation.Argv.Contains("snapshot", StringComparer.Ordinal)
            && invocation.Argv.Contains("create", StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkspaceCreateCleanupFailure_IsRecoveredOnNextPreflight()
    {
        var executable = Path.Combine(_root, "create-cleanup-tool");
        File.WriteAllText(executable, "tool");
        var stagingRoot = Path.Combine(_root, "create-cleanup-staging");
        var currentOptions = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "~/create-cleanup-tool",
                    VmDestPath = "/usr/local/bin/create-cleanup-tool",
                },
            ],
        };
        var homeReads = 0;
        string? EnvironmentWithCreateFailure(string name)
        {
            if (!string.Equals(name, "HOME", StringComparison.Ordinal))
                return null;
            if (++homeReads == 3)
            {
                var workspaceRoot = Assert.Single(
                    Directory.EnumerateDirectories(stagingRoot),
                    path => Path.GetFileName(path).StartsWith(
                        IncusProvisioningWorkspace.DirectoryPrefix,
                        StringComparison.Ordinal));
                var marker = Path.Combine(workspaceRoot, ".codeybox-incus-provision-v1");
                File.WriteAllText(marker, "mismatched\n");
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(executable);
            }
            return _root;
        }
        var runner = new BaselineBakeRunner(stagingRoot, verificationExitCode: 0);
        var provider = new IncusSandboxProvider(
            () => currentOptions,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentWithCreateFailure);

        _ = await Assert.ThrowsAnyAsync<Exception>(() => provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None));
        var leakedWorkspace = Assert.Single(
            Directory.EnumerateDirectories(stagingRoot),
            path => Path.GetFileName(path).StartsWith(
                IncusProvisioningWorkspace.DirectoryPrefix,
                StringComparison.Ordinal));
        var repairedMarker = Path.Combine(leakedWorkspace, ".codeybox-incus-provision-v1");
        File.WriteAllText(repairedMarker, Path.GetFileName(leakedWorkspace) + "\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(repairedMarker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        currentOptions = BaseOptions() with { StagingDirectory = stagingRoot };

        var baseline = await provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.False(Directory.Exists(leakedWorkspace));
    }

    [Fact]
    public async Task WorkspaceDisposeFailure_ReleasesLeaseForNextPreflightRecovery()
    {
        var executable = Path.Combine(_root, "dispose-cleanup-tool");
        File.WriteAllText(executable, "tool");
        var stagingRoot = Path.Combine(_root, "dispose-cleanup-staging");
        var currentOptions = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/usr/local/bin/dispose-cleanup-tool",
                },
            ],
        };
        var corrupted = false;
        var runner = new BaselineBakeRunner(
            stagingRoot,
            verificationExitCode: 0,
            onCanonicalization: () =>
            {
                if (corrupted)
                    return;
                corrupted = true;
                var workspaceRoot = Assert.Single(
                    Directory.EnumerateDirectories(stagingRoot),
                    path => Path.GetFileName(path).StartsWith(
                        IncusProvisioningWorkspace.DirectoryPrefix,
                        StringComparison.Ordinal));
                var marker = Path.Combine(workspaceRoot, ".codeybox-incus-provision-v1");
                File.WriteAllText(marker, "mismatched\n");
                if (OperatingSystem.IsLinux())
                    File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            });
        var provider = new IncusSandboxProvider(
            () => currentOptions,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        _ = await Assert.ThrowsAnyAsync<Exception>(() => provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None));
        var leakedWorkspace = Assert.Single(
            Directory.EnumerateDirectories(stagingRoot),
            path => Path.GetFileName(path).StartsWith(
                IncusProvisioningWorkspace.DirectoryPrefix,
                StringComparison.Ordinal));
        var repairedMarker = Path.Combine(leakedWorkspace, ".codeybox-incus-provision-v1");
        File.WriteAllText(repairedMarker, Path.GetFileName(leakedWorkspace) + "\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(repairedMarker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        currentOptions = BaseOptions() with { StagingDirectory = stagingRoot };

        var baseline = await provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None);

        Assert.NotNull(baseline);
        Assert.False(Directory.Exists(leakedWorkspace));
    }

    [Fact]
    public async Task ExistingPinnedBaseline_DoesNotReadNowMissingExecutableSource()
    {
        var stagingRoot = Path.Combine(_root, "pinned-staging");
        var missingSource = Path.Combine(_root, "deleted-after-bake");
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = missingSource,
                    VmDestPath = "/usr/local/bin/tool",
                },
            ],
        };
        const string pinned = "cb-incus-baseline-internet-headless-123456789abc";
        var runner = new BaselineBakeRunner(stagingRoot, verificationExitCode: 0, existingPinnedName: pinned);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        var resolved = await provider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinned,
            CancellationToken.None);

        Assert.Equal(pinned, resolved);
        Assert.DoesNotContain(runner.Invocations, invocation => invocation.Argv.Contains("init", StringComparer.Ordinal));
    }

    [Fact]
    public async Task MissingPinnedBaseline_MatchingLiveName_BakesInsteadOfRefusing()
    {
        // A pin that names the CURRENT content-addressed baseline (identical to
        // the live name) but does not yet exist must be baked, not refused as a
        // stale ref — otherwise a fresh cutover or a config change that shifts the
        // baseline hash can never bake its own baseline (dispatch pins to the
        // computed name, which does not exist until something bakes it).
        var options = BaseOptions() with { StagingDirectory = Path.Combine(_root, "pin-live-discover") };

        // Discover the live name by baking it unpinned.
        var discoverRunner = new BaselineBakeRunner(options.StagingDirectory!, verificationExitCode: 0);
        var discoverProvider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            discoverRunner,
            environmentVariableReader: EnvironmentReader(_root));
        var liveName = await discoverProvider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            pinnedBaselineRef: null,
            CancellationToken.None);
        Assert.NotNull(liveName);

        // A fresh provider with the same config where that baseline does NOT exist
        // yet, asked for the exact live name as a pin, must bake it.
        var freshRunner = new BaselineBakeRunner(options.StagingDirectory!, verificationExitCode: 0);
        var freshProvider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            freshRunner,
            environmentVariableReader: EnvironmentReader(_root));

        var resolved = await freshProvider.EnsureBaselineImageAsync(
            "internet-only",
            SandboxProfileFlavor.Headless,
            liveName,
            CancellationToken.None);

        Assert.Equal(liveName, resolved);
        Assert.Contains(freshRunner.Invocations, invocation => invocation.Argv.Contains("init", StringComparer.Ordinal));
    }

    [Fact]
    public async Task BaselineBake_RejectsExecutableMutationBetweenNameAndBakeFingerprint()
    {
        var executable = Path.Combine(_root, "name-race-tool");
        File.WriteAllText(executable, "version-one");
        var stagingRoot = Path.Combine(_root, "name-race-staging");
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "~/name-race-tool",
                    VmDestPath = "/usr/local/bin/name-race-tool",
                },
            ],
        };
        var homeReads = 0;
        string? EnvironmentWithMutation(string name)
        {
            if (!string.Equals(name, "HOME", StringComparison.Ordinal))
                return null;
            if (++homeReads == 2)
                File.WriteAllText(executable, "version-two");
            return _root;
        }
        var runner = new BaselineBakeRunner(stagingRoot, verificationExitCode: 0);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentWithMutation);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        Assert.Contains("content changed", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Invocations, invocation =>
            invocation.Argv.Contains("init", StringComparer.Ordinal));
    }

    [Fact]
    public async Task BaselineBake_RejectsExecutableMutationBeforePrivateStaging()
    {
        var executable = Path.Combine(_root, "stage-race-tool");
        File.WriteAllText(executable, "version-one");
        var stagingRoot = Path.Combine(_root, "stage-race-staging");
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/usr/local/bin/stage-race-tool",
                },
            ],
        };
        var runner = new BaselineBakeRunner(
            stagingRoot,
            verificationExitCode: 0,
            onStart: () => File.WriteAllText(executable, "version-two"));
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        Assert.Contains("source changed", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Invocations, invocation =>
            invocation.Argv.Contains("snapshot", StringComparer.Ordinal)
            || invocation.Argv.Contains("move", StringComparer.Ordinal));
        Assert.Contains(runner.Invocations, invocation =>
            invocation.Argv.Contains("delete", StringComparer.Ordinal));
        Assert.False(Directory.Exists(stagingRoot)
            && Directory.EnumerateFileSystemEntries(stagingRoot)
                .Any(path => Path.GetFileName(path).StartsWith(
                    IncusProvisioningWorkspace.DirectoryPrefix,
                    StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FullLaunch_RunsAllProvisioningStagesWithoutBaselineOperations()
    {
        var executable = Path.Combine(_root, "full-launch-tool");
        var cache = Path.Combine(_root, "full-launch-cache");
        File.WriteAllText(executable, "tool");
        File.WriteAllText(cache, "cache");
        var stagingRoot = Path.Combine(_root, "full-launch-staging");
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            UseBaselineImages = false,
            ExtraRuncmd = ["echo full-launch-runcmd"],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/home/ubuntu/.local/bin/full-tool",
                    VmSymlinks = ["/usr/local/bin/full-tool"],
                },
            ],
            BaselineVerificationCommands =
            [
                new BaselineVerificationCommand("full-tool", ["full-tool", "--version"]),
            ],
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = cache,
                    VmDestPath = "/home/ubuntu/.nuget/packages",
                },
            ],
        };
        var runner = new BaselineBakeRunner(stagingRoot, verificationExitCode: 0);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        await using var sandbox = await provider.CreateAsync(new SandboxSpec
        {
            ImageReference = options.DefaultImage,
            Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
        });

        var commands = runner.Invocations.Select(static invocation => invocation.Argv).ToList();
        Assert.Equal(2, commands.Count(IsFilePush));
        Assert.Contains(commands, command =>
            command.Contains("/home/ubuntu/.local/bin/full-tool", StringComparer.Ordinal));
        Assert.Contains(commands, command => command.Contains("setpriv", StringComparer.Ordinal)
            && command.Contains("full-tool", StringComparer.Ordinal));
        Assert.Contains(commands, command => command.Contains("tar", StringComparer.Ordinal)
            && command.Contains("--extract", StringComparer.Ordinal));
        Assert.Contains(commands, command => command.Contains("install", StringComparer.Ordinal)
            && command.Contains("-d", StringComparer.Ordinal)
            && command.Contains("/home/ubuntu/.nuget/packages", StringComparer.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("copy", StringComparer.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("snapshot", StringComparer.Ordinal));
        Assert.DoesNotContain(commands, command => command.Contains("move", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ProvisioningDeadline_ThrowsTimeoutAndCleansGuestHostAndVm()
    {
        var executable = Path.Combine(_root, "timeout-tool");
        File.WriteAllText(executable, "tool");
        var stagingRoot = Path.Combine(_root, "timeout-staging");
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            UseBaselineImages = false,
            ImageProvisioningTimeout = TimeSpan.FromMilliseconds(50),
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/usr/local/bin/timeout-tool",
                },
            ],
        };
        var time = new ControllableTimeProvider(
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
        var runner = new BaselineBakeRunner(
            stagingRoot,
            verificationExitCode: 0,
            hangFilePush: true,
            onFilePush: () => time.Advance(TimeSpan.FromMilliseconds(100)));
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            timeProvider: time,
            environmentVariableReader: EnvironmentReader(_root));

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => provider.CreateAsync(new SandboxSpec
        {
            ImageReference = options.DefaultImage,
            Network = new SandboxNetworkPolicy { ProfileName = "internet-only" },
        }));

        Assert.Contains("provisioning", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Invocations, invocation =>
            invocation.Argv.Contains("rm", StringComparer.Ordinal)
            && invocation.Argv.Any(argument => argument.Contains("/provision-", StringComparison.Ordinal)));
        Assert.Contains(runner.Invocations, invocation => invocation.Argv.Contains("delete", StringComparer.Ordinal));
        Assert.False(Directory.Exists(stagingRoot)
            && Directory.EnumerateFileSystemEntries(stagingRoot)
                .Any(path => Path.GetFileName(path).StartsWith(
                    IncusProvisioningWorkspace.DirectoryPrefix,
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void ExecutableFingerprinting_UsesTheProvisioningDeadline()
    {
        var executable = Path.Combine(_root, "fingerprint-timeout-tool");
        File.WriteAllText(executable, "tool");
        var time = new ControllableTimeProvider(
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
        var options = BaseOptions() with
        {
            StagingDirectory = Path.Combine(_root, "fingerprint-timeout-staging"),
            ImageProvisioningTimeout = TimeSpan.FromMilliseconds(50),
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = "~/fingerprint-timeout-tool",
                    VmDestPath = "/usr/local/bin/fingerprint-timeout-tool",
                },
            ],
        };
        string? EnvironmentWithTimeout(string name)
        {
            if (!string.Equals(name, "HOME", StringComparison.Ordinal))
                return null;
            time.Advance(TimeSpan.FromMilliseconds(100));
            return _root;
        }
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            new NeverRunner(),
            timeProvider: time,
            environmentVariableReader: EnvironmentWithTimeout);

        var exception = Assert.Throws<TimeoutException>(() =>
            provider.ResolveBaselineRef("internet-only", SandboxProfileFlavor.Headless));

        Assert.Contains("fingerprinting", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerificationAndGuestCleanupFailures_PreserveBothCauses()
    {
        var executable = Path.Combine(_root, "cleanup-tool");
        File.WriteAllText(executable, "tool");
        var stagingRoot = Path.Combine(_root, "cleanup-staging");
        var options = BaseOptions() with
        {
            StagingDirectory = stagingRoot,
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executable,
                    VmDestPath = "/usr/local/bin/probe-tool",
                },
            ],
            BaselineVerificationCommands =
            [
                new BaselineVerificationCommand("probe", ["probe-tool", "--version"], "verification-primary"),
            ],
        };
        var runner = new BaselineBakeRunner(
            stagingRoot,
            verificationExitCode: 12,
            failGuestCleanup: true);
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner,
            environmentVariableReader: EnvironmentReader(_root));

        var aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            provider.EnsureBaselineImageAsync(
                "internet-only",
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None));

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains("verification-primary", aggregate.InnerExceptions[0].Message, StringComparison.Ordinal);
        Assert.Contains("clean", aggregate.InnerExceptions[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Invocations, invocation => invocation.Argv.Contains("delete", StringComparer.Ordinal));
    }

    private IncusSandboxOptions BaseOptions() => new()
    {
        ProjectName = "codeybox-tests",
        StoragePoolName = "codeybox-zfs",
        DefaultImage = "images:ubuntu/24.04/cloud",
        UseBaselineImages = true,
        DiskGuard = null,
        NetworkProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["internet-only"] = "cb-net",
        },
    };

    private string CreateDirectory(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private static Func<string, string?> EnvironmentReader(string home) =>
        name => string.Equals(name, "HOME", StringComparison.Ordinal) ? home : null;

    private static string ReadSingleTarFile(string archivePath, string expectedName)
    {
        using var archive = File.OpenRead(archivePath);
        using var reader = new TarReader(archive);
        var entry = reader.GetNextEntry(copyData: false);
        Assert.NotNull(entry);
        Assert.Equal(expectedName, entry.Name);
        Assert.NotNull(entry.DataStream);
        using var text = new StreamReader(entry.DataStream, Encoding.UTF8, leaveOpen: false);
        var payload = text.ReadToEnd();
        Assert.Null(reader.GetNextEntry(copyData: false));
        return payload;
    }

    private static bool IsFilePush(IReadOnlyList<string> argv) =>
        argv.Contains("file", StringComparer.Ordinal) && argv.Contains("push", StringComparer.Ordinal);

    private static void AssertMountAliasRejectedBeforeHostDeviceAttachment(
        IReadOnlyList<Invocation> invocations,
        string creationVerb)
    {
        var creationIndex = FindInvocation(invocations, argv => argv.Contains(creationVerb, StringComparer.Ordinal));
        var startIndex = FindInvocation(invocations, argv => argv.Contains("start", StringComparer.Ordinal));
        var canonicalizationIndex = FindInvocation(
            invocations,
            argv => argv.Contains("/usr/bin/realpath", StringComparer.Ordinal));
        var deleteIndex = FindInvocation(invocations, argv => argv.Contains("delete", StringComparer.Ordinal));

        Assert.True(
            creationIndex >= 0
            && creationIndex < startIndex
            && startIndex < canonicalizationIndex
            && canonicalizationIndex < deleteIndex,
            "VM creation, isolated start, canonicalization, and failure deletion must remain ordered.");
        Assert.DoesNotContain(invocations, invocation =>
            invocation.Argv.Contains("config", StringComparer.Ordinal)
            && invocation.Argv.Contains("device", StringComparer.Ordinal)
            && invocation.Argv.Contains("add", StringComparer.Ordinal)
            && invocation.Argv.Contains("disk", StringComparer.Ordinal));
    }

    private static int FindInvocation(
        IReadOnlyList<Invocation> invocations,
        Func<IReadOnlyList<string>, bool> predicate)
    {
        for (var i = 0; i < invocations.Count; i++)
        {
            if (predicate(invocations[i].Argv))
                return i;
        }
        return -1;
    }

    private static int IndexOf(IReadOnlyList<string> values, string expected)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], expected, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private sealed class NeverRunner : IProcessRunner
    {
        internal bool Called { get; private set; }

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
            Called = true;
            throw new InvalidOperationException("Incus must not run for invalid provisioning configuration.");
        }
    }

    private sealed class CancelAfterFirstReadStream(
        byte[] buffer,
        CancellationTokenSource cancellation) : MemoryStream(buffer, writable: false)
    {
        private bool _cancelled;

        public override int Read(Span<byte> destination)
        {
            var read = base.Read(destination);
            if (!_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }
            return read;
        }
    }

    private sealed record Invocation(IReadOnlyList<string> Argv, string? Stdin);

    private sealed class BaselineBakeRunner : IProcessRunner
    {
        private readonly string _stagingRoot;
        private readonly int _verificationExitCode;
        private readonly bool _hangFilePush;
        private readonly bool _failGuestCleanup;
        private readonly Action? _onFilePush;
        private readonly Action? _onStart;
        private readonly Func<string, string> _canonicalizeGuestPath;
        private readonly Action? _onCanonicalization;
        private string? _instanceName;
        private string _instanceStatus = "STOPPED";
        private Dictionary<string, string> _instanceConfig = new(StringComparer.Ordinal);
        private bool _projectExists;
        private Dictionary<string, string> _projectConfig = new(StringComparer.Ordinal);

        internal BaselineBakeRunner(
            string stagingRoot,
            int verificationExitCode,
            string? existingPinnedName = null,
            bool hangFilePush = false,
            bool failGuestCleanup = false,
            Action? onFilePush = null,
            Action? onStart = null,
            Func<string, string>? canonicalizeGuestPath = null,
            Action? onCanonicalization = null)
        {
            _stagingRoot = stagingRoot;
            _verificationExitCode = verificationExitCode;
            _hangFilePush = hangFilePush;
            _failGuestCleanup = failGuestCleanup;
            _onFilePush = onFilePush;
            _onStart = onStart;
            _canonicalizeGuestPath = canonicalizeGuestPath ?? (static path => path);
            _onCanonicalization = onCanonicalization;
            if (existingPinnedName is not null)
            {
                _instanceName = existingPinnedName;
                _instanceConfig = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [IncusSandboxProvider.ManagedKey] = "true",
                    [IncusSandboxProvider.KindKey] = IncusSandboxProvider.BaselineKind,
                    [IncusSandboxProvider.BaselineProfileKey] = "internet-only",
                    [IncusSandboxProvider.BaselineFlavorKey] = SandboxProfileFlavor.Headless.ToString(),
                    [IncusSandboxProvider.BaselinePoolKey] = "codeybox-zfs",
                };
            }
        }

        internal List<Invocation> Invocations { get; } = [];

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
            Invocations.Add(new Invocation(argv.ToArray(), stdin));
            if (argv.SequenceEqual(["incus", "query", "/1.0"]))
            {
                return Success("{\"metadata\":{\"api_extensions\":[\"disk_io_bus_cache_filesystem\",\"projects_restrictions\"],\"environment\":{\"kernel_version\":\"6.14.0-test\"}}}");
            }
            if (argv.SequenceEqual(["incus", "project", "list", "--format=json"]))
                return Success(_projectExists ? "[{\"name\":\"codeybox-tests\"}]" : "[]");
            if (argv.Count >= 4 && argv.Take(3).SequenceEqual(["incus", "project", "create"]))
            {
                _projectExists = true;
                _projectConfig = ParseConfigArguments(argv);
                return Success();
            }
            if (argv.SequenceEqual(["incus", "query", "/1.0/projects/codeybox-tests"]))
                return Success(ProjectJson());
            if (argv.Contains("storage", StringComparer.Ordinal) && argv.Contains("list", StringComparer.Ordinal))
                return Success("[{\"name\":\"codeybox-zfs\",\"driver\":\"zfs\",\"config\":{}}]");
            if (argv.Contains("snapshot", StringComparer.Ordinal)
                && argv.Contains("list", StringComparer.Ordinal))
                return Success("[{\"name\":\"ready\"}]");
            if (argv.Contains("list", StringComparer.Ordinal) && argv.Contains("--format=json", StringComparer.Ordinal))
                return Success(InstanceListJson());

            var initIndex = IndexOf(argv, "init");
            if (initIndex >= 0)
            {
                _instanceName = argv[initIndex + 2];
                _instanceStatus = "STOPPED";
                _instanceConfig = ParseConfigArguments(argv);
                return Success();
            }
            var copyIndex = IndexOf(argv, "copy");
            if (copyIndex >= 0)
            {
                _instanceName = argv[copyIndex + 2];
                _instanceStatus = "STOPPED";
                _instanceConfig = ParseConfigArguments(argv);
                return Success();
            }
            if (argv.Contains("query", StringComparer.Ordinal)
                && argv.Any(argument => argument.StartsWith("/1.0/instances/", StringComparison.Ordinal)))
            {
                return Success(TopologyJson());
            }
            if (argv.Contains("start", StringComparer.Ordinal))
            {
                _onStart?.Invoke();
                _instanceStatus = "RUNNING";
                return Success();
            }
            if (argv.Contains("stop", StringComparer.Ordinal))
            {
                _instanceStatus = "STOPPED";
                return Success();
            }
            if (argv.Contains("exec", StringComparer.Ordinal))
            {
                if (argv.Contains("cloud-init", StringComparer.Ordinal)
                    && argv.Contains("status", StringComparer.Ordinal))
                {
                    return Success("{\"status\":\"done\",\"extended_status\":\"done\",\"errors\":[]}");
                }
                var realpathIndex = IndexOf(argv, "/usr/bin/realpath");
                if (realpathIndex >= 0
                    && realpathIndex + 1 < argv.Count
                    && string.Equals(argv[realpathIndex + 1], "-m", StringComparison.Ordinal))
                {
                    _onCanonicalization?.Invoke();
                    return Success(_canonicalizeGuestPath(argv[^1]) + "\n");
                }
                if (argv.Contains("setpriv", StringComparer.Ordinal))
                {
                    return Task.FromResult(new ProcessRunResult(
                        _verificationExitCode,
                        "raw-secret-output",
                        "raw-secret-output"));
                }
                if (_failGuestCleanup
                    && argv.Contains("rm", StringComparer.Ordinal)
                    && argv.Any(argument => argument.Contains("/provision-", StringComparison.Ordinal)))
                {
                    return Task.FromResult(new ProcessRunResult(44, string.Empty, "cleanup failed"));
                }
                return Success();
            }
            if (argv.Contains("file", StringComparer.Ordinal) && argv.Contains("push", StringComparer.Ordinal))
            {
                var boundary = IndexOf(argv, "--");
                if (boundary < 0
                    || boundary + 2 >= argv.Count
                    || !argv[boundary + 1].StartsWith(_stagingRoot, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Incus provisioning file push did not use the private staging root and operand boundary.");
                }
                _onFilePush?.Invoke();
                return _hangFilePush ? HangUntilCancelledAsync(ct) : Success();
            }
            if (argv.Contains("snapshot", StringComparer.Ordinal)
                && argv.Contains("create", StringComparer.Ordinal))
                return Success();
            if (argv.Contains("move", StringComparer.Ordinal))
            {
                var moveIndex = IndexOf(argv, "move");
                _instanceName = argv[moveIndex + 2];
                return Success();
            }
            if (argv.Contains("config", StringComparer.Ordinal)
                && argv.Contains("device", StringComparer.Ordinal)
                && argv.Contains("get", StringComparer.Ordinal)
                && string.Equals(argv[^1], "pool", StringComparison.Ordinal))
            {
                return Success("codeybox-zfs\n");
            }
            if (argv.Contains("config", StringComparer.Ordinal)
                && argv.Contains("get", StringComparer.Ordinal))
            {
                var key = argv[^1];
                return Success(_instanceConfig.GetValueOrDefault(key, string.Empty));
            }
            if (argv.Contains("config", StringComparer.Ordinal)
                && argv.Contains("unset", StringComparer.Ordinal))
            {
                _instanceConfig.Remove(argv[^1]);
                return Success();
            }
            if (argv.Contains("config", StringComparer.Ordinal))
                return Success();
            if (argv.Contains("delete", StringComparer.Ordinal))
            {
                _instanceName = null;
                _instanceConfig.Clear();
                return Success();
            }
            throw new InvalidOperationException($"Unexpected Incus bake test command: {string.Join(' ', argv)}");
        }

        private string ProjectJson()
        {
            if (!_projectExists)
                throw new InvalidOperationException("Project query preceded project creation.");
            return JsonSerializer.Serialize(new
            {
                metadata = new
                {
                    name = "codeybox-tests",
                    config = _projectConfig,
                },
            });
        }

        private string InstanceListJson()
        {
            if (_instanceName is null)
                return "[]";
            return JsonSerializer.Serialize(new[]
            {
                new
                {
                    name = _instanceName,
                    type = "virtual-machine",
                    status = _instanceStatus,
                    config = _instanceConfig,
                },
            });
        }

        private string TopologyJson() => JsonSerializer.Serialize(new
        {
            metadata = new
            {
                type = "virtual-machine",
                profiles = Array.Empty<string>(),
                config = new Dictionary<string, string>(),
                expanded_config = new Dictionary<string, string>(),
                expanded_devices = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["root"] = new(StringComparer.Ordinal)
                    {
                        ["type"] = "disk",
                        ["path"] = "/",
                        ["pool"] = "codeybox-zfs",
                    },
                    ["codeybox-net"] = new(StringComparer.Ordinal)
                    {
                        ["type"] = "nic",
                        ["nictype"] = "bridged",
                        ["parent"] = "cb-net",
                        ["name"] = "eth0",
                    },
                },
            },
        });

        private static Dictionary<string, string> ParseConfigArguments(IReadOnlyList<string> argv)
        {
            var config = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i + 1 < argv.Count; i++)
            {
                if (!string.Equals(argv[i], "--config", StringComparison.Ordinal))
                    continue;
                var field = argv[++i];
                var separator = field.IndexOf('=');
                if (separator > 0)
                    config[field[..separator]] = field[(separator + 1)..];
            }
            return config;
        }

        private static Task<ProcessRunResult> Success(string stdout = "") =>
            Task.FromResult(new ProcessRunResult(0, stdout, string.Empty));

        private static async Task<ProcessRunResult> HangUntilCancelledAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ProcessRunResult(0, string.Empty, string.Empty);
        }
    }
}
