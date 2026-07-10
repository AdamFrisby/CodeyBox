using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class IncusMountAndCloudInitTests
{
    [Fact]
    public void ExistingUnmarkedStagingRoot_IsRejectedWithoutChangingMode()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-unowned-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var originalMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(root, originalMode);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                IncusMountStaging.EnsureOwnedStagingRoot(root));
            Assert.Equal(originalMode, File.GetUnixFileMode(root));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [Fact]
    public void ReadinessProbe_StopsAtConfiguredEntryBoundAndHonorsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var oversized = Path.Combine(root, "oversized.bin");
        var sentinel = Path.Combine(root, "sentinel.txt");
        File.WriteAllBytes(oversized, new byte[64 * 1024 + 1]);
        File.WriteAllText(sentinel, "sentinel");
        try
        {
            Assert.Null(IncusMountStaging.FindReadinessProbe(
                [oversized, sentinel],
                maximumEntries: 1,
                CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                IncusMountStaging.FindReadinessProbe(
                    [sentinel],
                    maximumEntries: 1,
                    cancelled.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PinnedDirectoryTraversal_IgnoresPathReplacementAfterAuthorization()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Path.Combine(Path.GetTempPath(), $"codeybox-incus-pin-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var moved = Path.Combine(root, "moved");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "safe.txt"), "safe");
        File.WriteAllText(Path.Combine(outside, "evil.txt"), "evil");
        try
        {
            using var pinned = IncusSafeFile.PinDirectoryNoFollow(source);
            Directory.Move(source, moved);
            Directory.CreateSymbolicLink(source, outside);

            Assert.Equal(["safe.txt"], IncusSafeFile.EnumerateChildNames(pinned).ToArray());
            using var stream = IncusSafeFile.OpenChildFileReadNoFollow(pinned, "safe.txt");
            using var reader = new StreamReader(stream);
            Assert.Equal("safe", reader.ReadToEnd());
            Assert.ThrowsAny<IOException>(() => IncusSafeFile.OpenChildFileReadNoFollow(pinned, "evil.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task ExecWrapper_IsValidBashAndKeepsUtilityOptionBoundaries()
    {
        var result = await new DefaultProcessRunner().RunAsync(
            ["/bin/bash", "-n"],
            IncusCloudInit.ExecWrapper,
            CancellationToken.None,
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096);

        Assert.True(result.Success, result.Stderr);
        Assert.Contains("setsid -- setpriv", IncusCloudInit.ExecWrapper, StringComparison.Ordinal);
        Assert.Contains("-- env -i --", IncusCloudInit.ExecWrapper, StringComparison.Ordinal);
        Assert.Contains("--clear-groups", IncusCloudInit.ExecWrapper, StringComparison.Ordinal);
        var cleanup = IncusCloudInit.ExecWrapper.IndexOf("rm -f -- \"$env_file\"", StringComparison.Ordinal);
        var launch = IncusCloudInit.ExecWrapper.IndexOf("setsid -- setpriv", StringComparison.Ordinal);
        Assert.True(cleanup >= 0 && cleanup < launch, "secret environment file must be removed before the agent starts");
    }

    [Fact]
    public void Prepare_AuthorizesCanonicalRootWithoutMutatingSourceAndHashesExistingFile()
    {
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("allowed/source");
        File.WriteAllText(Path.Combine(source, "seed.txt"), "seed");
        var before = Directory.GetFileSystemEntries(source).Select(Path.GetFileName).ToArray();
        using var plan = IncusMountStaging.Prepare(
            fixture.Options(fixture.Path("allowed")),
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { HostPath = source, SandboxPath = "/repo", ReadOnly = true }],
            8L * 1024 * 1024 * 1024);

        Assert.Single(plan.Mounts);
        Assert.NotNull(plan.Mounts[0].PinnedHostDirectory);
        var probe = plan.Mounts[0].ReadinessProbe!;
        Assert.Equal("seed.txt", probe.RelativeFilePath);
        Assert.Equal(64, plan.Mounts[0].ReadinessProbe!.ExpectedSha256.Length);
        Assert.Equal(before, Directory.GetFileSystemEntries(source).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Prepare_AcceptsEmptyHostReadOnlyDirectoryWithoutMutatingIt()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("allowed/empty-readonly");
        var originalMode = File.GetUnixFileMode(source);
        File.SetUnixFileMode(
            source,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            using var plan = IncusMountStaging.Prepare(
                fixture.Options(fixture.Path("allowed")),
                fixture.StagingRoot,
                fixture.SandboxRoot,
                [new SandboxMount { HostPath = source, SandboxPath = "/readonly-empty", ReadOnly = true }],
                8L * 1024 * 1024 * 1024);

            var mount = Assert.Single(plan.Mounts);
            Assert.NotNull(mount.PinnedHostDirectory);
            Assert.Null(mount.ReadinessProbe);
            Assert.Empty(Directory.GetFileSystemEntries(source));
            IncusMountStaging.EnsurePinnedHostSourceMatches(source, mount.PinnedHostDirectory!);
        }
        finally
        {
            File.SetUnixFileMode(source, originalMode);
        }
    }

    [Fact]
    public void PinnedHostSource_RejectsDeleteAndRecreatePathSwap()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("allowed/source");
        var displaced = fixture.Path("allowed/source-original");
        using var plan = IncusMountStaging.Prepare(
            fixture.Options(fixture.Path("allowed")),
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { HostPath = source, SandboxPath = "/repo", ReadOnly = false }],
            8L * 1024 * 1024 * 1024);
        var mount = Assert.Single(plan.Mounts);
        IncusMountStaging.EnsurePinnedHostSourceMatches(source, mount.PinnedHostDirectory!);

        Directory.Move(source, displaced);
        Directory.CreateDirectory(source);

        Assert.Throws<IOException>(() =>
            IncusMountStaging.EnsurePinnedHostSourceMatches(source, mount.PinnedHostDirectory!));
    }

    [Fact]
    public void Prepare_RejectsSourceOutsideAllowedRoots()
    {
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("outside");

        Assert.Throws<UnauthorizedAccessException>(() => IncusMountStaging.Prepare(
            fixture.Options(fixture.CreateDirectory("allowed")),
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { HostPath = source, SandboxPath = "/repo" }],
            8L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void ProtectedVarPath_AllowsAuthorizedReadOnlyDirectoryAtIdenticalGuestPath()
    {
        const string mirrorObjects =
            "/var/lib/codeybox/repos/_upstream-mirror/project.git/objects";
        var mount = new SandboxMount
        {
            HostPath = mirrorObjects,
            SandboxPath = mirrorObjects,
            ReadOnly = true,
        };

        IncusMountStaging.ValidateAuthorizedMountGuestPath(
            mount,
            authorizedExistingHostSource: mirrorObjects,
            hostSourceIsDirectory: true);
    }

    [Fact]
    public void ProtectedVarPath_RejectsWritableFileMismatchRootAndOtherProtectedRoots()
    {
        const string mirrorObjects =
            "/var/lib/codeybox/repos/_upstream-mirror/project.git/objects";
        var allowedShape = new SandboxMount
        {
            HostPath = mirrorObjects,
            SandboxPath = mirrorObjects,
            ReadOnly = true,
        };
        (SandboxMount Mount, string? AuthorizedSource, bool SourceIsDirectory)[] rejected =
        [
            (allowedShape with { ReadOnly = false }, mirrorObjects, true),
            (allowedShape, mirrorObjects, false),
            (allowedShape, "/var/lib/codeybox/repos/_upstream-mirror/other.git/objects", true),
            (allowedShape with { HostPath = "/var", SandboxPath = "/var" }, "/var", true),
            (allowedShape with { HostPath = "/etc/codeybox", SandboxPath = "/etc/codeybox" }, "/etc/codeybox", true),
        ];

        Assert.All(rejected, item => Assert.Throws<InvalidOperationException>(() =>
            IncusMountStaging.ValidateAuthorizedMountGuestPath(
                item.Mount,
                item.AuthorizedSource,
                item.SourceIsDirectory)));
    }

    [Fact]
    public void Prepare_EnforcesTmpfsBoundsAndProtectsRuntimeControlPaths()
    {
        using var fixture = new MountFixture();
        var options = fixture.Options(fixture.Root) with
        {
            MaxTmpfsDeviceBytes = 8L * 1024 * 1024,
            MaxAggregateTmpfsBytes = 16L * 1024 * 1024,
        };

        Assert.Throws<InvalidOperationException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { SandboxPath = "/work", Tmpfs = true, SizeBytes = 9L * 1024 * 1024 }],
            8L * 1024 * 1024));
        Assert.Throws<InvalidOperationException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { SandboxPath = IncusCloudInit.RuntimeDirectory, Tmpfs = true, SizeBytes = 1024 }],
            8L * 1024 * 1024));

        using var credentials = IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { SandboxPath = SandboxConventions.CredentialsDir, Tmpfs = true, SizeBytes = 1024 }],
            8L * 1024 * 1024);
        Assert.Equal(1024, credentials.Mounts.Single().TmpfsSizeBytes);
    }

    [Fact]
    public void Prepare_StagesReadOnlyCredentialFileInsideCredentialsTmpfs()
    {
        using var fixture = new MountFixture();
        var credential = fixture.Path("credential.json");
        File.WriteAllText(credential, "{\"token\":\"test-only\"}");

        using var plan = IncusMountStaging.Prepare(
            fixture.Options(fixture.Root),
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [
                new SandboxMount
                {
                    SandboxPath = SandboxConventions.CredentialsDir,
                    Tmpfs = true,
                    SizeBytes = 1024 * 1024,
                },
                new SandboxMount
                {
                    HostPath = credential,
                    SandboxPath = $"{SandboxConventions.CredentialsDir}/credential.json",
                    ReadOnly = true,
                },
            ],
            8L * 1024 * 1024);

        Assert.Equal(2, plan.Mounts.Count);
        var link = Assert.Single(plan.GuestLinks);
        Assert.Equal($"{SandboxConventions.CredentialsDir}/credential.json", link.LinkPath);
        Assert.StartsWith(IncusCloudInit.RuntimeDirectory + "/file-mounts/", link.Target, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_SnapshotRejectsByteOverflow()
    {
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("allowed/source");
        File.WriteAllBytes(Path.Combine(source, "large.bin"), new byte[2048]);
        var options = fixture.Options(fixture.Path("allowed")) with { MaxSnapshotBytes = 1024 };

        Assert.Throws<IOException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { HostPath = source, SandboxPath = "/repo", SnapshotForIsolation = true }],
            8L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Prepare_SnapshotPreservesRelativeAndAbsoluteSymbolicLinksWithoutFollowingTargets()
    {
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("allowed/source");
        File.WriteAllText(Path.Combine(source, "target.txt"), "snapshot content");
        const string relativeTarget = "target.txt";
        var absoluteTarget = fixture.Path("outside/host-only.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteTarget)!);
        File.WriteAllText(absoluteTarget, "must not be copied through the link");
        File.CreateSymbolicLink(Path.Combine(source, "relative-link.txt"), relativeTarget);
        File.CreateSymbolicLink(Path.Combine(source, "absolute-link.txt"), absoluteTarget);

        using var plan = IncusMountStaging.Prepare(
            fixture.Options(fixture.Path("allowed")),
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { HostPath = source, SandboxPath = "/repo", SnapshotForIsolation = true }],
            8L * 1024 * 1024 * 1024);

        var staged = Assert.Single(plan.Mounts).HostSource!;
        Assert.Equal("snapshot content", File.ReadAllText(Path.Combine(staged, "target.txt")));
        Assert.Equal(relativeTarget, new FileInfo(Path.Combine(staged, "relative-link.txt")).LinkTarget);
        Assert.Equal(absoluteTarget, new FileInfo(Path.Combine(staged, "absolute-link.txt")).LinkTarget);
        Assert.True((File.GetAttributes(Path.Combine(staged, "relative-link.txt")) & FileAttributes.ReparsePoint) != 0);
        Assert.True((File.GetAttributes(Path.Combine(staged, "absolute-link.txt")) & FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public void Prepare_SnapshotCountsSymbolicLinkTargetsAgainstAggregateByteBound()
    {
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("allowed/source");
        File.CreateSymbolicLink(Path.Combine(source, "oversized-link"), new string('x', 128));
        var options = fixture.Options(fixture.Path("allowed")) with { MaxSnapshotBytes = 127 };

        Assert.Throws<IOException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount { HostPath = source, SandboxPath = "/repo", SnapshotForIsolation = true }],
            8L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void Prepare_BoundsAggregateBytesAcrossStagedIndividualFiles()
    {
        using var fixture = new MountFixture();
        var first = fixture.Path("first.bin");
        var second = fixture.Path("second.bin");
        File.WriteAllBytes(first, new byte[700]);
        File.WriteAllBytes(second, new byte[700]);
        var options = fixture.Options(fixture.Root) with { MaxSnapshotBytes = 1024 };

        Assert.Throws<IOException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [
                new SandboxMount { HostPath = first, SandboxPath = "/home/ubuntu/first.bin", ReadOnly = true },
                new SandboxMount { HostPath = second, SandboxPath = "/home/ubuntu/second.bin", ReadOnly = true },
            ],
            8L * 1024 * 1024));
    }

    [Fact]
    public void SnapshotForIsolation_ObservesCancellationBeforeCopying()
    {
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("cancelled-source");
        File.WriteAllBytes(Path.Combine(source, "large.bin"), new byte[1024 * 1024]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => IncusMountStaging.Prepare(
            fixture.Options(fixture.Root),
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount
            {
                HostPath = source,
                SandboxPath = "/repo",
                ReadOnly = true,
                SnapshotForIsolation = true,
            }],
            1024 * 1024,
            cancellation.Token));
    }

    [Fact]
    public void SnapshotForIsolation_CopiesChildrenBeforeApplyingReadOnlyDirectoryMode()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("readonly-source");
        var nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "child.txt"), "child");
        File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string? stagedNested = null;
        try
        {
            using var plan = IncusMountStaging.Prepare(
                fixture.Options(fixture.Root),
                fixture.StagingRoot,
                fixture.SandboxRoot,
                [new SandboxMount
                {
                    HostPath = source,
                    SandboxPath = "/repo",
                    ReadOnly = true,
                    SnapshotForIsolation = true,
                }],
                1024 * 1024);

            stagedNested = Path.Combine(Assert.Single(plan.Mounts).HostSource!, "nested");
            Assert.Equal("child", File.ReadAllText(Path.Combine(stagedNested, "child.txt")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserExecute,
                File.GetUnixFileMode(stagedNested));
        }
        finally
        {
            File.SetUnixFileMode(nested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (stagedNested is not null && Directory.Exists(stagedNested))
                File.SetUnixFileMode(stagedNested, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void DeleteTreeIfContained_DoesNotFollowDirectorySymlink()
    {
        using var fixture = new MountFixture();
        var external = fixture.CreateDirectory("external");
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var link = Path.Combine(fixture.StagingRoot, "linked-candidate");
        Directory.CreateSymbolicLink(link, external);

        IncusMountStaging.DeleteTreeIfContained(fixture.StagingRoot, link);

        Assert.True(File.Exists(sentinel));
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void DeleteTreeIfContained_RejectsTheStagingRootItself()
    {
        using var fixture = new MountFixture();

        Assert.Throws<InvalidOperationException>(() =>
            IncusMountStaging.DeleteTreeIfContained(fixture.StagingRoot, fixture.StagingRoot));

        Assert.True(Directory.Exists(fixture.StagingRoot));
    }

    [Fact]
    public void OwnedStagingTree_RoundTripsThroughInventoryAndCheckedCleanup()
    {
        using var fixture = new MountFixture();
        const string sandboxName = "codeybox-staging-orphan";
        var createdAt = new DateTimeOffset(2026, 7, 11, 1, 2, 3, TimeSpan.Zero);
        var sandboxRoot = fixture.CreateDirectory($"staging/{sandboxName}");
        IncusMountStaging.InitializeOwnedTree(sandboxRoot, sandboxName, createdAt);
        File.WriteAllText(Path.Combine(sandboxRoot, "credential-copy"), "sensitive");

        Assert.Contains(
            IncusMountStaging.EnumerateOwnedTrees(fixture.StagingRoot),
            tree => tree.Name == sandboxName && tree.CreatedAt == createdAt);
        IncusMountStaging.DeleteOwnedTreeIfContained(
            fixture.StagingRoot,
            sandboxRoot,
            sandboxName);

        Assert.False(Directory.Exists(sandboxRoot));
        Assert.DoesNotContain(
            IncusMountStaging.EnumerateOwnedTrees(fixture.StagingRoot),
            tree => tree.Name == sandboxName);
    }

    [Fact]
    public void Prepare_RejectsWritableIsolationSnapshotAndCallerStagingMount()
    {
        using var fixture = new MountFixture();
        var source = fixture.CreateDirectory("source");
        var options = fixture.Options(fixture.Root);

        Assert.Throws<InvalidOperationException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount
            {
                HostPath = source,
                SandboxPath = "/repo",
                ReadOnly = false,
                SnapshotForIsolation = true,
            }],
            1024));
        Assert.Throws<UnauthorizedAccessException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount
            {
                HostPath = fixture.StagingRoot,
                SandboxPath = "/staging",
                ReadOnly = true,
            }],
            1024));
        Assert.Throws<UnauthorizedAccessException>(() => IncusMountStaging.Prepare(
            options,
            fixture.StagingRoot,
            fixture.SandboxRoot,
            [new SandboxMount
            {
                HostPath = fixture.Root,
                SandboxPath = "/staging-parent",
                ReadOnly = true,
            }],
            1024));
    }

    [Theory]
    [InlineData("write_files:\n  - path: /tmp/bad")]
    [InlineData("runcmd:\n  - echo bad")]
    [InlineData("'write_files': []")]
    [InlineData("\"r\\u0075ncmd\": []")]
    [InlineData("{ write_files: [] }")]
    [InlineData("<<: *defaults")]
    [InlineData("  - [ touch, /tmp/bypass ]")]
    [InlineData("!!str runcmd: []")]
    [InlineData("---\nruncmd: []")]
    [InlineData("packages: []\rwrite_files: []")]
    [InlineData("packages: []\u0085runcmd: []")]
    [InlineData("packages: []\u2028write_files: []")]
    [InlineData("packages: []\u2029runcmd: []")]
    public void CloudInit_RejectsGeneratedOrAmbiguousTopLevelKeys(string fragment)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IncusCloudInit.Build(new IncusSandboxOptions { ExtraCloudInit = fragment }, SandboxProfileFlavor.Headless));
    }

    [Fact]
    public void CloudInit_AcceptsNestedValuesUnderPlainTopLevelKeys()
    {
        var cloudInit = IncusCloudInit.Build(
            new IncusSandboxOptions
            {
                ExtraCloudInit = "packages:\n  - git\napt:\n  preserve_sources_list: true",
            },
            SandboxProfileFlavor.Headless);

        Assert.Contains("packages:\n  - git", cloudInit, StringComparison.Ordinal);
        Assert.Contains("apt:\n  preserve_sources_list: true", cloudInit, StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineHash_CoversEveryBakedInput()
    {
        var baseline = new IncusSandboxOptions
        {
            NetworkProfiles = new Dictionary<string, string> { ["internet"] = "cb-net" },
            ExtraRuncmd = ["echo ready"],
            ExtraCloudInit = "packages: [git]",
            CaptureResourceMetrics = true,
        };
        var original = IncusBaselineNaming.ComputeConfigHash(baseline, "internet", SandboxProfileFlavor.Headless);
        IncusSandboxOptions[] variants =
        [
            baseline with { DefaultImage = "images:ubuntu/26.04/cloud" },
            baseline with { StoragePoolName = "other-pool" },
            baseline with { BaselineCpus = baseline.BaselineCpus + 1 },
            baseline with { BaselineMemoryBytes = baseline.BaselineMemoryBytes + 4096 },
            baseline with { BaselineDiskBytes = baseline.BaselineDiskBytes + 4096 },
            baseline with { NetworkProfiles = new Dictionary<string, string> { ["internet"] = "cb-other" } },
            baseline with { ExtraRuncmd = ["echo changed"] },
            baseline with { ExtraCloudInit = "packages: [jq]" },
            baseline with { CaptureResourceMetrics = false },
            baseline with { ResourceMetricsSampleInterval = TimeSpan.FromSeconds(11) },
            baseline with { GuestUserId = 1001 },
            baseline with { GuestGroupId = 1001 },
            baseline with { GuestHome = "/home/codeybox" },
        ];

        Assert.All(variants, variant => Assert.NotEqual(
            original,
            IncusBaselineNaming.ComputeConfigHash(variant, "internet", SandboxProfileFlavor.Headless)));
    }

    [Theory]
    [InlineData(0, "done", true)]
    [InlineData(0, "degraded done", false)]
    [InlineData(2, "degraded done", true)]
    [InlineData(2, "done", false)]
    [InlineData(1, "done", false)]
    [InlineData(2, "error - done", false)]
    public void CloudInitStatus_AcceptsOnlyCompletedStatesWithoutFatalErrors(
        int exitCode,
        string extendedStatus,
        bool expected)
    {
        var json = $$"""
            {
              "status": "done",
              "extended_status": "{{extendedStatus}}",
              "errors": []
            }
            """;

        var accepted = IncusSandboxProvider.TryAcceptCloudInitStatus(json, exitCode, out var degraded);

        Assert.Equal(expected, accepted);
        Assert.Equal(expected && extendedStatus == "degraded done", degraded);
    }

    [Theory]
    [InlineData("{ not-json")]
    [InlineData("{ \"status\": \"done\", \"extended_status\": \"done\" }")]
    [InlineData("{ \"status\": \"done\", \"extended_status\": \"degraded done\", \"errors\": [\"fatal\"] }")]
    public void CloudInitStatus_RejectsMalformedOrFatalReports(string json)
    {
        Assert.False(IncusSandboxProvider.TryAcceptCloudInitStatus(json, 2, out var degraded));
        Assert.False(degraded);
    }

    [Fact]
    public void OwnsBaselineRef_AcceptsDerivedRefAndRejectsLookalikes()
    {
        var options = new IncusSandboxOptions
        {
            NetworkProfiles = new Dictionary<string, string> { ["internet"] = "cb-net" },
        };
        var provider = new IncusSandboxProvider(options, NullLogger<IncusSandboxProvider>.Instance);
        var derived = IncusBaselineNaming.DeriveBaselineName(options, "internet", SandboxProfileFlavor.Headless);

        Assert.True(provider.OwnsBaselineRef(derived));
        Assert.False(provider.OwnsBaselineRef(derived + "0"));
        Assert.False(provider.OwnsBaselineRef("x" + derived));
        Assert.False(provider.OwnsBaselineRef(derived[..^1] + "g"));
    }

    [Fact]
    public async Task Create_RejectsInvalidResourceLimitsBeforeCallingIncus()
    {
        var runner = new NeverProcessRunner();
        var provider = new IncusSandboxProvider(
            () => new IncusSandboxOptions(),
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            runner);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.CreateAsync(new SandboxSpec
        {
            ImageReference = "images:ubuntu/24.04/cloud",
            Limits = new SandboxResourceLimits { CpuCount = 0 },
        }));
        Assert.False(runner.Called);
    }

    private sealed class NeverProcessRunner : IProcessRunner
    {
        public bool Called { get; private set; }

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
            throw new InvalidOperationException("Incus should not be called for invalid limits.");
        }
    }

    private sealed class MountFixture : IDisposable
    {
        internal MountFixture()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codeybox-incus-unit-{Guid.NewGuid():N}");
            StagingRoot = CreateDirectory("staging");
            SandboxRoot = CreateDirectory("staging/sandbox");
        }

        internal string Root { get; }
        internal string StagingRoot { get; }
        internal string SandboxRoot { get; }
        internal string Path(string relative) => System.IO.Path.Combine(Root, relative);
        internal string CreateDirectory(string relative)
        {
            var path = Path(relative);
            Directory.CreateDirectory(path);
            return path;
        }

        internal IncusSandboxOptions Options(string allowedRoot) => new()
        {
            AllowedHostMountRoots = [allowedRoot],
            StagingDirectory = StagingRoot,
        };

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
