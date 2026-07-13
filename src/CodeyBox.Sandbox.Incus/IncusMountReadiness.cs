using System.Globalization;

namespace CodeyBox.Sandbox.Incus;

internal static class IncusMountReadiness
{
    internal static async Task WaitAsync(
        IncusCliRunner cli,
        IncusSandboxOptions options,
        string name,
        string stagingRoot,
        IReadOnlyList<IncusPreparedMount> mounts,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(timeProvider);
        for (var index = 0; index < mounts.Count; index++)
        {
            var mount = mounts[index];
            VerifyPinnedMountSource(options, stagingRoot, mount);
            var deadline = timeProvider.GetUtcNow() + options.MountReadyTimeout;
            var lastReadinessStage = "filesystem type";
            while (true)
            {
                if (mount.RootDiskDirectory)
                {
                    var rootTarget = await cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(
                            options,
                            name,
                            ["findmnt", "-n", "-o", "TARGET", "--target", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var ownership = await cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(
                            options,
                            name,
                            ["stat", "-Lc", "%u:%g:%a", "--", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var expectedOwnership =
                        $"{options.GuestUserId.ToString(CultureInfo.InvariantCulture)}:" +
                        $"{options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}:700";
                    var rootDirectoryReady = rootTarget.Success
                        && string.Equals(rootTarget.Stdout.Trim(), "/", StringComparison.Ordinal)
                        && ownership.Success
                        && string.Equals(ownership.Stdout.Trim(), expectedOwnership, StringComparison.Ordinal);
                    lastReadinessStage =
                        $"root-disk directory identity (targetExit={rootTarget.ExitCode}, " +
                        $"targetMatch={string.Equals(rootTarget.Stdout.Trim(), "/", StringComparison.Ordinal)}, " +
                        $"statExit={ownership.ExitCode}, ownershipMatch={string.Equals(ownership.Stdout.Trim(), expectedOwnership, StringComparison.Ordinal)})";
                    if (rootDirectoryReady)
                        break;
                    if (timeProvider.GetUtcNow() >= deadline)
                        throw MountTimeout(mount, lastReadinessStage);
                    await Task.Delay(options.ReadinessPollInterval, timeProvider, ct).ConfigureAwait(false);
                    continue;
                }

                var findMount = await cli.RunAllowFailureAsync(
                    options,
                    IncusCommandBuilder.BuildExec(
                        options,
                        name,
                        ["findmnt", "-n", "-o", "FSTYPE", "--target", mount.GuestPath]),
                    stdin: null,
                    options.OperationTimeout,
                    ct,
                    heavyOperation: false,
                    maxStdoutBytes: 4096,
                    maxStderrBytes: 4096).ConfigureAwait(false);
                var expectedFilesystem = mount.TmpfsSizeBytes.HasValue ? "tmpfs" : "virtiofs";
                var ready = findMount.Success
                    && string.Equals(findMount.Stdout.Trim(), expectedFilesystem, StringComparison.Ordinal);
                lastReadinessStage = $"filesystem type (exit={findMount.ExitCode}, match={ready})";
                if (ready && mount.HostSource is not null)
                {
                    var readable = await cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(options, name, ["test", "-r", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var traversable = await cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(options, name, ["test", "-x", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    ready = readable.Success && traversable.Success;
                    lastReadinessStage =
                        $"configured guest access (readExit={readable.ExitCode}, traverseExit={traversable.ExitCode})";
                }
                if (ready && mount.HostSource is not null)
                {
                    var mountOptions = await cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.BuildExec(
                            options,
                            name,
                            ["findmnt", "-n", "-o", "OPTIONS", "--target", mount.GuestPath]),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 4096,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var optionSet = mountOptions.Stdout
                        .Trim()
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToHashSet(StringComparer.Ordinal);
                    var expectedAccess = mount.ReadOnly ? "ro" : "rw";
                    ready = mountOptions.Success && optionSet.Contains(expectedAccess);
                    lastReadinessStage =
                        $"mount access mode (exit={mountOptions.ExitCode}, expected={expectedAccess}, match={ready})";
                }
                if (ready && mount.HostSource is { } hostSource)
                {
                    var deviceName = IncusSandboxProvider.BuildMountDeviceNameForVerification(index);
                    var source = await cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.Prefix(
                            options,
                            "config", "device", "get", name, deviceName, "source"),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: options.MaxCliStdoutBytes,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    var bus = await cli.RunAllowFailureAsync(
                        options,
                        IncusCommandBuilder.Prefix(
                            options,
                            "config", "device", "get", name, deviceName, "io.bus"),
                        stdin: null,
                        options.OperationTimeout,
                        ct,
                        heavyOperation: false,
                        maxStdoutBytes: 128,
                        maxStderrBytes: 4096).ConfigureAwait(false);
                    ready = source.Success
                        && bus.Success
                        && string.Equals(source.Stdout.Trim(), hostSource, StringComparison.Ordinal)
                        && string.Equals(bus.Stdout.Trim(), "virtiofs", StringComparison.Ordinal);
                    lastReadinessStage =
                        $"device metadata (sourceExit={source.ExitCode}, sourceMatch={string.Equals(source.Stdout.Trim(), hostSource, StringComparison.Ordinal)}, " +
                        $"busExit={bus.ExitCode}, busMatch={string.Equals(bus.Stdout.Trim(), "virtiofs", StringComparison.Ordinal)})";
                    if (ready && mount.ReadinessProbe is { } probe)
                    {
                        var guestProbePath = $"{mount.GuestPath.TrimEnd('/')}/{probe.RelativeFilePath}";
                        var guestHash = await cli.RunAllowFailureAsync(
                            options,
                            IncusCommandBuilder.BuildExec(options, name, ["sha256sum", "--", guestProbePath]),
                            stdin: null,
                            options.OperationTimeout,
                            ct,
                            heavyOperation: false,
                            maxStdoutBytes: 512,
                            maxStderrBytes: 4096).ConfigureAwait(false);
                        var guestHashText = guestHash.Stdout
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault();
                        ready = guestHash.Success
                            && string.Equals(guestHashText, probe.ExpectedSha256, StringComparison.Ordinal);
                        lastReadinessStage =
                            $"host-to-guest identity hash (guestExit={guestHash.ExitCode}, match={string.Equals(guestHashText, probe.ExpectedSha256, StringComparison.Ordinal)})";
                    }
                    if (ready && mount.PinnedHostDirectory is { } pinnedDirectory)
                    {
                        var inode = await cli.RunAllowFailureAsync(
                            options,
                            IncusCommandBuilder.BuildExec(
                                options,
                                name,
                                ["stat", "-Lc", "%i", "--", mount.GuestPath]),
                            stdin: null,
                            options.OperationTimeout,
                            ct,
                            heavyOperation: false,
                            maxStdoutBytes: 128,
                            maxStderrBytes: 4096).ConfigureAwait(false);
                        ready = inode.Success
                            && ulong.TryParse(
                                inode.Stdout.Trim(),
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out var guestInode)
                            && guestInode == pinnedDirectory.Identity.Inode;
                        lastReadinessStage =
                            $"host-to-guest directory identity (guestExit={inode.ExitCode}, match={ready})";
                    }
                }
                if (ready)
                {
                    VerifyPinnedMountSource(options, stagingRoot, mount);
                    break;
                }
                if (timeProvider.GetUtcNow() >= deadline)
                    throw MountTimeout(mount, lastReadinessStage);
                await Task.Delay(options.ReadinessPollInterval, timeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    private static void VerifyPinnedMountSource(
        IncusSandboxOptions options,
        string stagingRoot,
        IncusPreparedMount mount)
    {
        if (mount.PinnedHostDirectory is not { } pinnedDirectory)
            return;
        var source = mount.HostSource
            ?? throw new InvalidOperationException("A pinned Incus mount has no host source.");
        var authorizedSource = IncusMountStaging.ReauthorizeHostSource(options, stagingRoot, source);
        if (!string.Equals(authorizedSource, source, StringComparison.Ordinal))
            throw new IOException("The authorized Incus host mount source changed canonical path.");
        IncusMountStaging.EnsurePinnedHostSourceMatches(authorizedSource, pinnedDirectory);
    }

    private static TimeoutException MountTimeout(
        IncusPreparedMount mount,
        string readinessStage) =>
        new($"Incus mount '{mount.GuestPath}' did not pass its {readinessStage} readiness check within the configured deadline.");
}
