using System.Diagnostics;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

[CollectionDefinition("Incus integration", DisableParallelization = true)]
public sealed class IncusIntegrationCollection;

/// <summary>
/// Opt-in real-host coverage for the Incus VM path. The test owns uniquely-prefixed
/// instances, baselines, and a dedicated per-run restricted project. It never
/// creates, formats, or changes a storage pool.
/// </summary>
[Collection("Incus integration")]
[Trait("requires_incus", "true")]
public sealed class IncusIntegrationTests
{
    private const string EnableVariable = "CODEYBOX_RUN_INCUS_INTEGRATION";
    private const string KeepFailedVariable = "CODEYBOX_INCUS_KEEP_FAILED";
    private const int MaxCliOutputBytes = 4 * 1024 * 1024;
    private static readonly DefaultProcessRunner ProcessRunner = new();

    [SkippableFact]
    public async Task BaselineCowCopyExecAndVirtiofsMount_EndToEnd()
    {
        var settings = IncusIntegrationSettings.FromEnvironment();
        var skipReason = await GetSkipReasonAsync(settings);
        if (skipReason is not null)
            Skip.If(condition: true, skipReason);

        var runId = Guid.NewGuid().ToString("N");
        var token = runId[..10];
        settings = settings with { Project = CreateTestProjectName(settings.Project, token) };
        var profile = $"incus-it-{token}";
        var instancePrefix = $"codeybox-it-{token}-";
        var baselinePrefix = $"cb-incus-it-{token}-";
        var copyProbe = $"{instancePrefix}cow";
        var workspace = Path.Combine("/tmp", $"codeybox-incus-it-{runId}");
        var writableSource = Path.Combine(workspace, "writable");
        var readOnlySource = Path.Combine(workspace, "readonly");
        var emptyReadOnlySource = Path.Combine(workspace, "readonly-empty");
        var isolatedSource = Path.Combine(workspace, "isolated");
        var singleFileSource = Path.Combine(workspace, "single-file.txt");
        var executableSource = Path.Combine(workspace, "codeybox-provisioned-tool");
        var cacheSeedSource = Path.Combine(workspace, "package-cache-seed");
        var staging = Path.Combine(workspace, "staging");
        var baselineMarkerPath = $"/var/lib/codeybox-incus-test/bake-{token}";
        Directory.CreateDirectory(writableSource);
        Directory.CreateDirectory(readOnlySource);
        Directory.CreateDirectory(emptyReadOnlySource);
        Directory.CreateDirectory(isolatedSource);
        Directory.CreateDirectory(cacheSeedSource);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                emptyReadOnlySource,
                UnixFileMode.UserRead | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        await File.WriteAllTextAsync(
            Path.Combine(writableSource, "from-host.txt"),
            "host-to-guest\n");
        await File.WriteAllTextAsync(
            Path.Combine(readOnlySource, "read-only.txt"),
            "immutable\n");
        await File.WriteAllTextAsync(
            Path.Combine(isolatedSource, "snapshot.txt"),
            "before-snapshot\n");
        await File.WriteAllTextAsync(singleFileSource, "single-file\n");
        await File.WriteAllTextAsync(
            executableSource,
            "#!/bin/sh\nif [ \"${1:-}\" = \"--version\" ]; then printf 'codeybox-provisioned-tool 1.0\\n'; else printf 'provisioned-exec-ok\\n'; fi\n");
        await File.WriteAllTextAsync(
            Path.Combine(cacheSeedSource, "seed.txt"),
            "package-cache-seed-ok\n");
        var hostIdentity = IncusHostIdentity.GetEffectiveIdentity();

        var options = new IncusSandboxOptions
        {
            BinaryPath = settings.IncusBinary,
            ProjectName = settings.Project,
            StoragePoolName = settings.Pool,
            DefaultImage = settings.Image,
            InstanceNamePrefix = instancePrefix,
            BaselineNamePrefix = baselinePrefix,
            UseBaselineImages = true,
            NetworkProfiles = new Dictionary<string, string>
            {
                [profile] = settings.Bridge,
            },
            AllowedHostMountRoots = [workspace],
            GuestUserId = hostIdentity.UserId,
            GuestGroupId = hostIdentity.GroupId,
            ExtraRuncmd =
            [
                $"install -d -m 0755 /var/lib/codeybox-incus-test && " +
                $"printf '{token}\\n' >> {baselineMarkerPath} && chmod 0644 {baselineMarkerPath}",
            ],
            ExecutableProvisions =
            [
                new BaselineExecutableProvision
                {
                    HostSourcePath = executableSource,
                    VmDestPath = "/home/ubuntu/.local/bin/codeybox-provisioned-tool",
                    VmSymlinks = ["/usr/local/bin/codeybox-provisioned-tool"],
                    Label = "integration executable",
                },
            ],
            BaselineVerificationCommands =
            [
                new BaselineVerificationCommand(
                    "integration executable",
                    ["codeybox-provisioned-tool", "--version"],
                    "the host-staged integration executable must be runnable on the sandbox PATH"),
            ],
            PackageCacheSeeds =
            [
                new BaselinePackageCacheSeed
                {
                    HostSourcePath = cacheSeedSource,
                    VmDestPath = "/home/ubuntu/.nuget/packages/codeybox-integration",
                    MaxSizeMB = 1,
                },
            ],
            StagingDirectory = staging,
            OperationTimeout = TimeSpan.FromMinutes(5),
            VmStartTimeout = TimeSpan.FromMinutes(5),
            VmStopTimeout = TimeSpan.FromMinutes(2),
            CloudInitTimeout = TimeSpan.FromMinutes(10),
            MountReadyTimeout = TimeSpan.FromSeconds(30),
            ReadinessPollInterval = TimeSpan.FromMilliseconds(500),
            BaselineCpus = 2,
            BaselineMemoryBytes = 2L * 1024 * 1024 * 1024,
            BaselineDiskBytes = 10L * 1024 * 1024 * 1024,
        };
        var recordingRunner = new CopyRecordingProcessRunner();
        var provider = new IncusSandboxProvider(
            () => options,
            NullLogger<IncusSandboxProvider>.Instance,
            timings: null,
            recordingRunner);
        var resolver = Assert.IsAssignableFrom<IBaselineImageResolver>(provider);
        var provisioner = Assert.IsAssignableFrom<IBaselineImageProvisioner>(provider);
        var baseline = resolver.ResolveBaselineRef(profile, SandboxProfileFlavor.Headless)
            ?? throw new InvalidOperationException("The Incus integration baseline resolver returned null.");
        Assert.False(string.IsNullOrWhiteSpace(baseline));

        ISandbox? sandbox = null;
        Exception? scenarioFailure = null;
        try
        {
            var bakedBaseline = await provisioner.EnsureBaselineImageAsync(
                profile,
                SandboxProfileFlavor.Headless,
                pinnedBaselineRef: null,
                CancellationToken.None)
                ?? throw new InvalidOperationException("The Incus integration baseline bake returned null.");
            Assert.Equal(baseline, bakedBaseline);
            baseline = bakedBaseline;

            var copy = await RunCheckedAsync(
                [
                    settings.IncusBinary, "--project", settings.Project,
                    "copy", baseline + "/ready", copyProbe,
                    "--storage", settings.Pool,
                    "--no-profiles",
                ],
                TimeSpan.FromMinutes(2));
            Assert.True(
                copy.Elapsed <= settings.MaximumCloneDuration,
                $"COW copy took {copy.Elapsed.TotalSeconds:F3}s; expected at most " +
                $"{settings.MaximumCloneDuration.TotalSeconds:F3}s. stdout={copy.Result.Stdout} stderr={copy.Result.Stderr}");
            await AssertZfsCowCloneAsync(settings, baseline, copyProbe);

            sandbox = await provider.CreateAsync(new SandboxSpec
            {
                ImageReference = settings.Image,
                BaselineImageRef = baseline,
                Network = new SandboxNetworkPolicy { ProfileName = profile },
                Mounts =
                [
                    new SandboxMount
                    {
                        SandboxPath = "/work",
                        Tmpfs = true,
                        SizeBytes = 128L * 1024 * 1024,
                    },
                    new SandboxMount
                    {
                        SandboxPath = SandboxConventions.AgentTurnScratchpadDir,
                        Tmpfs = true,
                        SizeBytes = 4L * 1024 * 1024,
                    },
                    new SandboxMount
                    {
                        HostPath = writableSource,
                        SandboxPath = "/integration-rw",
                        ReadOnly = false,
                    },
                    new SandboxMount
                    {
                        HostPath = readOnlySource,
                        SandboxPath = "/integration-ro",
                        ReadOnly = true,
                    },
                    new SandboxMount
                    {
                        HostPath = emptyReadOnlySource,
                        SandboxPath = "/integration-ro-empty",
                        ReadOnly = true,
                    },
                    new SandboxMount
                    {
                        HostPath = isolatedSource,
                        SandboxPath = "/integration-snapshot",
                        ReadOnly = true,
                        SnapshotForIsolation = true,
                    },
                    new SandboxMount
                    {
                        HostPath = singleFileSource,
                        SandboxPath = "/home/ubuntu/incus-mounted-file.txt",
                        ReadOnly = true,
                    },
                ],
                Limits = new SandboxResourceLimits
                {
                    CpuCount = 2,
                    MemoryBytes = 2L * 1024 * 1024 * 1024,
                    DiskBytes = 10L * 1024 * 1024 * 1024,
                },
                WorkingDirectory = "/work",
            });
            var providerCopy = Assert.Single(recordingRunner.CopyDurations);
            Assert.True(
                providerCopy <= settings.MaximumCloneDuration,
                $"Provider incus copy took {providerCopy.TotalSeconds:F3}s; expected at most " +
                $"{settings.MaximumCloneDuration.TotalSeconds:F3}s.");
            await AssertZfsCowCloneAsync(settings, baseline, sandbox.Id);
            Assert.Empty(Directory.GetFileSystemEntries(emptyReadOnlySource));

            await File.WriteAllTextAsync(
                Path.Combine(isolatedSource, "snapshot.txt"),
                "after-snapshot\n");

            var bakedOnce = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", $"grep -c '^{token}$' {baselineMarkerPath}"],
            });
            Assert.True(bakedOnce.Success, bakedOnce.Stderr);
            Assert.Equal("1", bakedOnce.Stdout.Trim());

            var exec = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf 'incus-exec-ok\\n'"],
            });
            Assert.True(exec.Success, exec.Stderr);
            Assert.Equal("incus-exec-ok\n", exec.Stdout);

            var provisionedExecutable = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["codeybox-provisioned-tool"],
            });
            Assert.True(provisionedExecutable.Success, provisionedExecutable.Stderr);
            Assert.Equal("provisioned-exec-ok\n", provisionedExecutable.Stdout);

            var provisionedExecutableMetadata = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "stat", "-Lc", "%u:%g:%a",
                    "/home/ubuntu/.local/bin/codeybox-provisioned-tool",
                ],
            });
            Assert.True(provisionedExecutableMetadata.Success, provisionedExecutableMetadata.Stderr);
            Assert.Equal("0:0:755", provisionedExecutableMetadata.Stdout.Trim());

            var provisioningParentsWritable = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "set -eu; printf sibling > /home/ubuntu/.local/bin/codeybox-write-probe; " +
                    "mkdir -p /home/ubuntu/.nuget/packages/codeybox-write-probe; " +
                    "rm -rf /home/ubuntu/.local/bin/codeybox-write-probe /home/ubuntu/.nuget/packages/codeybox-write-probe",
                ],
            });
            Assert.True(provisioningParentsWritable.Success, provisioningParentsWritable.Stderr);

            var provisionedCache = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", "/home/ubuntu/.nuget/packages/codeybox-integration/seed.txt"],
            });
            Assert.True(provisionedCache.Success, provisionedCache.Stderr);
            Assert.Equal("package-cache-seed-ok\n", provisionedCache.Stdout);

            var provisionedCacheWritable = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "printf 'package-cache-updated\\n' > " +
                    "/home/ubuntu/.nuget/packages/codeybox-integration/seed.txt && " +
                    "cat /home/ubuntu/.nuget/packages/codeybox-integration/seed.txt",
                ],
            });
            Assert.True(provisionedCacheWritable.Success, provisionedCacheWritable.Stderr);
            Assert.Equal("package-cache-updated\n", provisionedCacheWritable.Stdout);

            var secret = $"incus-secret-{token}";
            var secretEnvironment = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["printenv", "CODEYBOX_INCUS_TEST_SECRET"],
                ExtraEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["CODEYBOX_INCUS_TEST_SECRET"] = secret,
                },
                EnvironmentContainsSecrets = true,
            });
            Assert.True(secretEnvironment.Success, secretEnvironment.Stderr);
            Assert.Equal(secret, secretEnvironment.Stdout.Trim());

            var identity = await sandbox.ExecAsync(new SandboxExec { Argv = ["id", "-u"] });
            Assert.True(identity.Success, identity.Stderr);
            Assert.Equal(options.GuestUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), identity.Stdout.Trim());

            var hostToGuest = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", "/integration-rw/from-host.txt"],
            });
            Assert.True(hostToGuest.Success, hostToGuest.Stderr);
            Assert.Equal("host-to-guest\n", hostToGuest.Stdout);

            var guestToHost = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf 'guest-to-host\\n' > /integration-rw/from-guest.txt"],
            });
            Assert.True(guestToHost.Success, guestToHost.Stderr);
            Assert.Equal(
                "guest-to-host\n",
                await File.ReadAllTextAsync(Path.Combine(writableSource, "from-guest.txt")));

            var privilegedMountWrite = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sudo", "-n", "sh", "-c",
                    "install -m 4755 /bin/true /integration-rw/root-setuid",
                ],
            });
            Assert.False(privilegedMountWrite.Success);
            Assert.False(File.Exists(Path.Combine(writableSource, "root-setuid")));

            var readOnlyWrite = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf x > /integration-ro/must-not-exist"],
            });
            Assert.False(readOnlyWrite.Success);
            Assert.False(File.Exists(Path.Combine(readOnlySource, "must-not-exist")));

            var emptyReadOnly = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "test -d /integration-ro-empty && test -z \"$(find /integration-ro-empty -mindepth 1 -maxdepth 1 -print -quit)\""],
            });
            Assert.True(emptyReadOnly.Success, emptyReadOnly.Stderr);

            var isolatedRead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", "/integration-snapshot/snapshot.txt"],
            });
            Assert.True(isolatedRead.Success, isolatedRead.Stderr);
            Assert.Equal("before-snapshot\n", isolatedRead.Stdout);

            var singleFileRead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", "/home/ubuntu/incus-mounted-file.txt"],
            });
            Assert.True(singleFileRead.Success, singleFileRead.Stderr);
            Assert.Equal("single-file\n", singleFileRead.Stdout);

            var workBacking = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["findmnt", "-n", "-o", "TARGET", "--target", "/work"],
            });
            Assert.True(workBacking.Success, workBacking.Stderr);
            Assert.Equal("/", workBacking.Stdout.Trim());
            var persistentWorkMarker = $"incus-work-persists-{token}\n";
            var writePersistentWork = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf '%s' \"$1\" > /work/incus-stop-start-marker", "--", persistentWorkMarker],
            });
            Assert.True(writePersistentWork.Success, writePersistentWork.Stderr);

            var devices = await RunCheckedAsync(
                [
                    settings.IncusBinary, "--project", settings.Project,
                    "config", "device", "show", sandbox.Id,
                ],
                TimeSpan.FromSeconds(30));
            Assert.Equal(5, CountOccurrences(devices.Result.Stdout, "io.bus: virtiofs"));
            Assert.Contains("readonly: \"true\"", devices.Result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("9p", devices.Result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("codeybox-net:", devices.Result.Stdout, StringComparison.Ordinal);
            Assert.Contains("nictype: bridged", devices.Result.Stdout, StringComparison.Ordinal);
            Assert.Contains($"parent: {settings.Bridge}", devices.Result.Stdout, StringComparison.Ordinal);
            Assert.Contains("name: eth0", devices.Result.Stdout, StringComparison.Ordinal);

            var privateTmpfsMarker = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    $"printf private > {SandboxConventions.AgentTurnScratchpadDir}/must-not-survive-restart",
                ],
            });
            Assert.True(privateTmpfsMarker.Success, privateTmpfsMarker.Stderr);

            var interruptedExec = sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    "printf durable > /work/interrupted-exec-marker; sleep 300; printf should-not-run",
                ],
            });
            await WaitForGuestCommandAsync(
                settings,
                sandbox.Id,
                ["test", "-f", "/work/interrupted-exec-marker"],
                TimeSpan.FromMinutes(1));
            await RunCheckedAsync(
                [
                    settings.IncusBinary, "--project", settings.Project,
                    "stop", sandbox.Id, "--force",
                ],
                TimeSpan.FromMinutes(2));

            var interruptedResult = await interruptedExec.WaitAsync(TimeSpan.FromMinutes(7));
            Assert.False(interruptedResult.Success);
            Assert.True(
                interruptedResult.ExecutionUnavailable,
                $"Forced VM stop was not surfaced as execution-unavailable: exit={interruptedResult.ExitCode} stderr={interruptedResult.Stderr}");

            var recoveredExec = await sandbox.ExecAsync(new SandboxExec
            {
                Argv =
                [
                    "sh", "-c",
                    $"set -eu; test \"$(cat /work/interrupted-exec-marker)\" = durable; " +
                    $"test ! -e {SandboxConventions.AgentTurnScratchpadDir}/must-not-survive-restart; " +
                    $"test \"$(findmnt -n -o FSTYPE --target {SandboxConventions.AgentTurnScratchpadDir})\" = tmpfs",
                ],
            });
            Assert.True(
                recoveredExec.Success,
                $"First exec after interrupted-exec recovery failed: exit={recoveredExec.ExitCode} stderr={recoveredExec.Stderr}");

            var recoveredHostMountRead = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["cat", "/integration-rw/from-host.txt"],
            });
            Assert.True(recoveredHostMountRead.Success, recoveredHostMountRead.Stderr);
            Assert.Equal("host-to-guest\n", recoveredHostMountRead.Stdout);

            var recoveredHostMountWrite = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "printf 'after-recovery\\n' > /integration-rw/after-recovery.txt"],
            });
            Assert.True(recoveredHostMountWrite.Success, recoveredHostMountWrite.Stderr);
            Assert.Equal(
                "after-recovery\n",
                await File.ReadAllTextAsync(Path.Combine(writableSource, "after-recovery.txt")));

            var preemptible = Assert.IsAssignableFrom<IPreemptibleSandbox>(sandbox);
            await preemptible.StopAndPreserveAsync();
            await RunCheckedAsync(
                [settings.IncusBinary, "--project", settings.Project, "start", sandbox.Id],
                TimeSpan.FromMinutes(2));
            var persistedWork = await WaitForGuestCommandAsync(
                settings,
                sandbox.Id,
                ["cat", "/work/incus-stop-start-marker"],
                TimeSpan.FromMinutes(2));
            Assert.Equal(persistentWorkMarker, persistedWork.Stdout);
            var preserved = Assert.Single(
                await provider.ListAllManagedAsync(CancellationToken.None),
                managed => string.Equals(managed.Name, sandbox.Id, StringComparison.Ordinal));
            Assert.True(preserved.HasPreemptMarker);

            // Preserve makes normal disposal a no-op. The lifecycle reaper must
            // still be able to verify ownership and explicitly remove it.
            await sandbox.DisposeAsync();
            Assert.Contains(
                await provider.ListAllManagedAsync(CancellationToken.None),
                managed => string.Equals(managed.Name, sandbox.Id, StringComparison.Ordinal));
            await provider.DisposeLeakedAsync(sandbox.Id, CancellationToken.None);
            Assert.DoesNotContain(
                await provider.ListAllManagedAsync(CancellationToken.None),
                managed => string.Equals(managed.Name, sandbox.Id, StringComparison.Ordinal));
            sandbox = null;
        }
        catch (Exception ex)
        {
            scenarioFailure = ex;
        }

        if (scenarioFailure is not null
            && string.Equals(Environment.GetEnvironmentVariable(KeepFailedVariable), "1", StringComparison.Ordinal))
        {
            throw new AggregateException(
                $"Incus integration failed; explicit {KeepFailedVariable}=1 retained project '{settings.Project}' for diagnosis.",
                scenarioFailure);
        }

        var cleanupFailures = new List<Exception>();
        if (sandbox is not null)
            await CaptureCleanupFailureAsync(() => sandbox.DisposeAsync().AsTask(), cleanupFailures);
        await CaptureCleanupFailureAsync(
            () => DeleteIfPresentAsync(settings, copyProbe),
            cleanupFailures);
        await CaptureCleanupFailureAsync(
            () => DeleteInstancesWithPrefixAsync(settings, instancePrefix),
            cleanupFailures);
        await CaptureCleanupFailureAsync(
            () => DisposeBaselineIfPresentAsync(resolver, baseline),
            cleanupFailures);
        await CaptureCleanupFailureAsync(
            () => DeleteProjectIfPresentAsync(settings),
            cleanupFailures);
        try
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
        catch (Exception ex)
        {
            cleanupFailures.Add(ex);
        }

        if (scenarioFailure is not null)
            cleanupFailures.Insert(0, scenarioFailure);
        if (cleanupFailures.Count != 0)
            throw new AggregateException("Incus integration scenario or cleanup failed.", cleanupFailures);
    }

    private static async Task<string?> GetSkipReasonAsync(IncusIntegrationSettings settings)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
            return $"Set {EnableVariable}=1 to run the destructive real-VM integration test.";

        try
        {
            var identityOptions = new IncusSandboxOptions
            {
                BinaryPath = settings.IncusBinary,
                ProjectName = settings.Project,
                StoragePoolName = settings.Pool,
            };
            IncusInputValidation.ValidateOptionsIdentity(identityOptions);
            IncusInputValidation.ValidateOpaqueArgument(settings.ZfsBinary, nameof(settings.ZfsBinary));
            IncusInputValidation.ValidateOpaqueArgument(settings.Image, nameof(settings.Image));
            IncusInputValidation.ValidateBridgeName(settings.Bridge, nameof(settings.Bridge));

            var version = await RunAllowFailureAsync(
                [settings.IncusBinary, "--version"],
                TimeSpan.FromSeconds(10));
            if (!version.Result.Success)
                return $"Incus CLI probe failed: {DiagnosticText(version.Result.Stderr)}";

            var pool = await RunAllowFailureAsync(
                [settings.IncusBinary, "storage", "show", settings.Pool],
                TimeSpan.FromSeconds(10));
            if (!pool.Result.Success)
                return $"Incus pool '{settings.Pool}' is unavailable: {DiagnosticText(pool.Result.Stderr)}";
            if (!pool.Result.Stdout.Contains("driver: zfs", StringComparison.Ordinal))
                return $"Incus pool '{settings.Pool}' is not backed by ZFS.";
            var cloneCopy = await RunAllowFailureAsync(
                [settings.IncusBinary, "storage", "get", settings.Pool, "zfs.clone_copy"],
                TimeSpan.FromSeconds(10));
            if (!cloneCopy.Result.Success)
                return $"Cannot read zfs.clone_copy for Incus pool '{settings.Pool}': {DiagnosticText(cloneCopy.Result.Stderr)}";
            var cloneMode = cloneCopy.Result.Stdout.Trim();
            if (cloneMode.Length != 0
                && !string.Equals(cloneMode, "true", StringComparison.OrdinalIgnoreCase))
                return $"Incus pool '{settings.Pool}' configures non-COW zfs.clone_copy mode '{DiagnosticText(cloneMode)}'.";

            if (settings.Image.Contains(':', StringComparison.Ordinal))
                return "CODEYBOX_INCUS_TEST_IMAGE must name a pre-cached local alias or fingerprint, not a remote image.";
            var image = await RunAllowFailureAsync(
                [
                    settings.IncusBinary, "image", "info", settings.Image,
                ],
                TimeSpan.FromSeconds(30));
            if (!image.Result.Success)
            {
                return $"Pre-cached local VM image '{settings.Image}' is unavailable. " +
                    "Import it before opting into the integration test.";
            }
            if (!image.Result.Stdout.Contains("Type: virtual-machine", StringComparison.OrdinalIgnoreCase))
                return $"Pre-cached local image '{settings.Image}' is not a virtual-machine image.";

            var zfs = await RunAllowFailureAsync(
                [settings.ZfsBinary, "list", "-H", "-p", "-o", "name"],
                TimeSpan.FromSeconds(10));
            if (!zfs.Result.Success)
                return $"ZFS dataset introspection is unavailable: {DiagnosticText(zfs.Result.Stderr)}";
            if (!Directory.Exists(Path.Combine("/sys/class/net", settings.Bridge)))
                return $"Required host bridge '{settings.Bridge}' does not exist.";
        }
        catch (Exception ex)
        {
            return $"Incus integration prerequisite probe failed: {ex.Message}";
        }

        return null;
    }

    private static async Task AssertZfsCowCloneAsync(
        IncusIntegrationSettings settings,
        string baseline,
        string clone)
    {
        var datasetList = await RunCheckedAsync(
            [settings.ZfsBinary, "list", "-H", "-p", "-o", "name"],
            TimeSpan.FromSeconds(30));
        var datasets = datasetList.Result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var baselineDatasets = datasets
            .Where(name => DatasetBelongsToInstance(name, settings.Project, baseline))
            .ToArray();
        var cloneDatasets = datasets
            .Where(name => DatasetBelongsToInstance(name, settings.Project, clone))
            .ToArray();
        Assert.NotEmpty(baselineDatasets);
        Assert.NotEmpty(cloneDatasets);

        var propertyArgv = new List<string>
        {
            settings.ZfsBinary,
            "get", "-H", "-p", "-o", "name,property,value",
            "origin,usedbydataset,referenced",
        };
        Assert.All(baselineDatasets, AssertSafeZfsDatasetName);
        Assert.All(cloneDatasets, AssertSafeZfsDatasetName);
        propertyArgv.AddRange(baselineDatasets);
        propertyArgv.AddRange(cloneDatasets);
        var properties = await RunCheckedAsync(propertyArgv, TimeSpan.FromSeconds(30));
        var rows = properties.Result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseZfsProperty)
            .ToArray();

        var cloneOrigins = rows
            .Where(row => cloneDatasets.Contains(row.Dataset, StringComparer.Ordinal)
                && row.Property == "origin"
                && row.Value != "-")
            .Select(row => row.Value)
            .ToArray();
        Assert.NotEmpty(cloneOrigins);
        Assert.Contains(
            cloneOrigins,
            origin => origin.Contains(baseline, StringComparison.Ordinal));

        var baselineReferenced = SumNumericProperty(rows, baselineDatasets, "referenced");
        var cloneExclusive = SumNumericProperty(rows, cloneDatasets, "usedbydataset");
        Assert.True(baselineReferenced > 0, "baseline ZFS datasets reported no referenced bytes");
        Assert.True(
            cloneExclusive < baselineReferenced / 4,
            $"clone owns {cloneExclusive} bytes exclusively, which is not materially smaller than " +
            $"the baseline's {baselineReferenced} referenced bytes; this resembles a full copy");
    }

    private static ZfsProperty ParseZfsProperty(string line)
    {
        var fields = line.Split('\t');
        Assert.True(fields.Length == 3, $"Unexpected `zfs get` row: {line}");
        return new ZfsProperty(fields[0], fields[1], fields[2]);
    }

    private static long SumNumericProperty(
        IEnumerable<ZfsProperty> rows,
        IReadOnlyCollection<string> datasets,
        string property)
    {
        long total = 0;
        foreach (var row in rows.Where(row => datasets.Contains(row.Dataset) && row.Property == property))
        {
            Assert.True(long.TryParse(row.Value, out var value), $"Invalid numeric ZFS value '{row.Value}'.");
            total = checked(total + value);
        }
        return total;
    }

    private static bool DatasetBelongsToInstance(string dataset, string project, string instance)
    {
        var finalSegment = dataset[(dataset.LastIndexOf('/') + 1)..];
        var projectScopedName = $"{project}_{instance}";
        return string.Equals(finalSegment, projectScopedName, StringComparison.Ordinal)
            || string.Equals(finalSegment, projectScopedName + ".block", StringComparison.Ordinal);
    }

    private static async Task DeleteIfPresentAsync(IncusIntegrationSettings settings, string name)
    {
        if (!await VerifyOwnedInstanceForCleanupAsync(settings, name))
            return;
        var result = await RunAllowFailureAsync(
            [
                settings.IncusBinary, "--project", settings.Project,
                "delete", name, "--force",
            ],
            TimeSpan.FromMinutes(2));
        if (result.Result.Success || IsMissingInstance(result.Result.Stderr))
            return;
        throw new InvalidOperationException(
            $"Could not delete Incus integration instance '{name}': {DiagnosticText(result.Result.Stderr)}");
    }

    private static async Task DeleteProjectIfPresentAsync(IncusIntegrationSettings settings)
    {
        var query = await RunAllowFailureAsync(
            [settings.IncusBinary, "query", $"/1.0/projects/{settings.Project}"],
            TimeSpan.FromSeconds(30));
        if (!query.Result.Success)
        {
            if (IsMissingInstance(query.Result.Stderr))
                return;
            throw new InvalidOperationException("Could not verify Incus integration project ownership before cleanup.");
        }
        var project = IncusProjectSecurity.ParseProjectQuery(query.Result.Stdout, settings.Project);
        IncusProjectSecurity.EnsureDedicatedShape(project);
        _ = GetRunToken(settings.Project);
        var result = await RunAllowFailureAsync(
            [settings.IncusBinary, "project", "delete", settings.Project],
            TimeSpan.FromSeconds(30));
        if (result.Result.Success || IsMissingInstance(result.Result.Stderr))
            return;
        throw new InvalidOperationException(
            $"Could not delete Incus integration project '{settings.Project}': {DiagnosticText(result.Result.Stderr)}");
    }

    private static async Task<bool> VerifyOwnedInstanceForCleanupAsync(
        IncusIntegrationSettings settings,
        string name)
    {
        var listed = await RunAllowFailureAsync(
            [
                settings.IncusBinary, "--project", settings.Project,
                "list", name, "--format=json",
            ],
            TimeSpan.FromSeconds(30));
        if (!listed.Result.Success)
        {
            if (IsMissingInstance(listed.Result.Stderr))
                return false;
            throw new InvalidOperationException("Could not verify Incus integration instance ownership before cleanup.");
        }
        using var document = JsonDocument.Parse(listed.Result.Stdout, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Incus integration cleanup received a malformed instance inventory.");
        var exact = document.RootElement.EnumerateArray()
            .Where(instance => instance.TryGetProperty("name", out var value)
                && string.Equals(value.GetString(), name, StringComparison.Ordinal))
            .ToArray();
        if (exact.Length == 0)
            return false;
        if (exact.Length != 1
            || !exact[0].TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "virtual-machine", StringComparison.Ordinal)
            || !exact[0].TryGetProperty("config", out var config)
            || config.ValueKind != JsonValueKind.Object
            || !HasConfig(config, IncusSandboxProvider.ManagedKey, "true")
            || !(HasConfig(config, IncusSandboxProvider.KindKey, IncusSandboxProvider.SandboxKind)
                || HasConfig(config, IncusSandboxProvider.KindKey, IncusSandboxProvider.BaselineKind)))
        {
            throw new InvalidOperationException("Refusing to delete an Incus integration instance without exact provider ownership.");
        }
        var runToken = GetRunToken(settings.Project);
        var boundToRun = name.Contains(runToken, StringComparison.Ordinal)
            || ConfigContains(config, IncusSandboxProvider.BaselineProfileKey, runToken)
            || ConfigContains(config, IncusSandboxProvider.BaselineRefKey, runToken);
        if (!boundToRun)
            throw new InvalidOperationException("Refusing to delete an Incus integration instance not bound to this test run.");
        return true;
    }

    private static bool HasConfig(JsonElement config, string key, string expected) =>
        config.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool ConfigContains(JsonElement config, string key, string expectedFragment) =>
        config.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Contains(expectedFragment, StringComparison.Ordinal) == true;

    private static string GetRunToken(string projectName)
    {
        var marker = projectName.LastIndexOf("-it-", StringComparison.Ordinal);
        var token = marker >= 0 ? projectName[(marker + 4)..] : string.Empty;
        if (token.Length != 10 || token.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidOperationException("Incus integration project lacks the expected run token.");
        return token;
    }

    private static string CreateTestProjectName(string configuredBase, string token)
    {
        const int maximumProjectNameLength = 42;
        var suffix = "-it-" + token;
        var maximumBaseLength = maximumProjectNameLength - suffix.Length;
        var boundedBase = configuredBase[..Math.Min(configuredBase.Length, maximumBaseLength)]
            .TrimEnd('-', '_', '.');
        if (boundedBase.Length == 0)
            boundedBase = "cb";
        return boundedBase + suffix;
    }

    private static async Task DeleteInstancesWithPrefixAsync(
        IncusIntegrationSettings settings,
        string prefix)
    {
        var list = await RunCheckedAsync(
            [
                settings.IncusBinary, "--project", settings.Project,
                "list", "--format=csv", "-c", "n",
            ],
            TimeSpan.FromSeconds(30));
        var names = list.Result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        foreach (var name in names)
            await DeleteIfPresentAsync(settings, name);
    }

    private static bool IsMissingInstance(string stderr) =>
        stderr.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static async Task CaptureCleanupFailureAsync(
        Func<Task> cleanup,
        ICollection<Exception> failures)
    {
        try
        {
            await cleanup();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static async Task DisposeBaselineIfPresentAsync(
        IBaselineImageResolver resolver,
        string baseline)
    {
        var images = await resolver.ListBaselineImagesAsync(CancellationToken.None);
        if (images.Any(image => string.Equals(image.Name, baseline, StringComparison.Ordinal)))
            await resolver.DisposeBaselineImageAsync(baseline, CancellationToken.None);
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }
        return count;
    }

    private static async Task<TimedProcessResult> RunCheckedAsync(
        IReadOnlyList<string> argv,
        TimeSpan timeout)
    {
        var result = await RunAllowFailureAsync(argv, timeout);
        if (!result.Result.Success)
        {
            throw new InvalidOperationException(
                $"Host command failed with exit code {result.Result.ExitCode}: " +
                $"stderr={DiagnosticText(result.Result.Stderr)}; stdout={DiagnosticText(result.Result.Stdout)}");
        }
        if (result.Result.StdoutLimitExceeded || result.Result.StderrLimitExceeded)
            throw new InvalidOperationException("Host command exceeded the integration test's output bound.");
        return result;
    }

    private static async Task<ProcessRunResult> WaitForGuestCommandAsync(
        IncusIntegrationSettings settings,
        string instanceName,
        IReadOnlyList<string> guestArgv,
        TimeSpan timeout)
    {
        IncusInputValidation.ValidateInstanceName(instanceName, nameof(instanceName));
        if (guestArgv.Count == 0)
            throw new ArgumentException("Guest integration command must not be empty.", nameof(guestArgv));
        var deadline = Stopwatch.StartNew();
        ProcessRunResult last = default;
        while (deadline.Elapsed < timeout)
        {
            var argv = new List<string>(guestArgv.Count + 6)
            {
                settings.IncusBinary,
                "--project",
                settings.Project,
                "exec",
                instanceName,
                "--",
            };
            argv.AddRange(guestArgv);
            var attempt = await RunAllowFailureAsync(argv, TimeSpan.FromSeconds(10));
            last = attempt.Result;
            if (last.Success)
                return last;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException(
            $"Incus guest command did not become available within {timeout.TotalSeconds:F0} seconds: " +
            $"stderr={DiagnosticText(last.Stderr)}; stdout={DiagnosticText(last.Stdout)}");
    }

    private static async Task<TimedProcessResult> RunAllowFailureAsync(
        IReadOnlyList<string> argv,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await ProcessRunner.RunAsync(
                argv,
                stdin: null,
                timeoutSource.Token,
                maxStdoutBytes: MaxCliOutputBytes,
                maxStderrBytes: MaxCliOutputBytes);
            return new TimedProcessResult(result, stopwatch.Elapsed);
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Host integration command exceeded its {timeout.TotalSeconds:F0}-second deadline.",
                ex);
        }
    }

    private static void AssertSafeZfsDatasetName(string name)
    {
        Assert.True(
            !string.IsNullOrWhiteSpace(name)
            && name.Length <= 1024
            && name[0] != '-'
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or ':' or '@'),
            $"Unsafe ZFS dataset name returned by the host: {DiagnosticText(name)}");
    }

    private static string DiagnosticText(string value)
    {
        const int maximumCharacters = 4096;
        var bounded = value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "...";
        return new string(bounded.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }

    private sealed record IncusIntegrationSettings(
        string IncusBinary,
        string ZfsBinary,
        string Project,
        string Pool,
        string Image,
        string Bridge,
        TimeSpan MaximumCloneDuration)
    {
        internal static IncusIntegrationSettings FromEnvironment()
        {
            var cloneSecondsText = Environment.GetEnvironmentVariable("CODEYBOX_INCUS_MAX_CLONE_SECONDS");
            var maximumCloneDuration = string.IsNullOrWhiteSpace(cloneSecondsText)
                ? TimeSpan.FromSeconds(2.5)
                : double.TryParse(
                    cloneSecondsText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var seconds)
                    && seconds is >= 0.1 and <= 60
                        ? TimeSpan.FromSeconds(seconds)
                        : throw new InvalidOperationException(
                            "CODEYBOX_INCUS_MAX_CLONE_SECONDS must be between 0.1 and 60.");
            return new IncusIntegrationSettings(
                Environment.GetEnvironmentVariable("CODEYBOX_INCUS_TEST_BINARY") ?? "incus",
                Environment.GetEnvironmentVariable("CODEYBOX_INCUS_TEST_ZFS_BINARY") ?? "zfs",
                Environment.GetEnvironmentVariable("CODEYBOX_INCUS_TEST_PROJECT") ?? "codeybox",
                Environment.GetEnvironmentVariable("CODEYBOX_INCUS_TEST_POOL") ?? "codeybox-zfs",
                Environment.GetEnvironmentVariable("CODEYBOX_INCUS_TEST_IMAGE")
                    ?? "codeybox-incus-test-ubuntu-24.04",
                Environment.GetEnvironmentVariable("CODEYBOX_INCUS_TEST_BRIDGE") ?? "cb-net",
                maximumCloneDuration);
        }
    }

    private sealed record ZfsProperty(string Dataset, string Property, string Value);
    private sealed record TimedProcessResult(ProcessRunResult Result, TimeSpan Elapsed);

    private sealed class CopyRecordingProcessRunner : IProcessRunner
    {
        private readonly DefaultProcessRunner _inner = new();
        private readonly List<TimeSpan> _copyDurations = [];
        private readonly Lock _gate = new();

        internal IReadOnlyList<TimeSpan> CopyDurations
        {
            get
            {
                lock (_gate)
                    return _copyDurations.ToArray();
            }
        }

        public async Task<ProcessRunResult> RunAsync(
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
            var isCopy = argv.Contains("copy", StringComparer.Ordinal);
            var stopwatch = isCopy ? Stopwatch.StartNew() : null;
            try
            {
                return await _inner.RunAsync(
                    argv,
                    stdin,
                    ct,
                    stdoutChunkCallback,
                    stderrChunkCallback,
                    maxStdoutBytes,
                    maxStderrBytes,
                    environment,
                    killOnOutputLimit).ConfigureAwait(false);
            }
            finally
            {
                if (stopwatch is not null)
                {
                    lock (_gate)
                        _copyDurations.Add(stopwatch.Elapsed);
                }
            }
        }
    }
}
