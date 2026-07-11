using System.Collections.ObjectModel;
using CodeyBox.Sandbox.Incus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Api;

/// <summary>
/// Maps the API configuration graph to the provider-owned immutable options
/// snapshot. Keeping this in one place ensures startup validation and runtime
/// provider creation apply identical independent-provider semantics.
/// </summary>
internal static class IncusSandboxConfigMapper
{
    public static IncusSandboxOptions Build(CodeyBoxOptions options) =>
        Build(options, NullLogger.Instance);

    public static IncusSandboxOptions Build(CodeyBoxOptions options, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        var incus = options.Incus ?? new IncusSandboxConfig();
        var diskGuard = SharedDiskGuardConfig.Resolve(options, log);
        var stagingDirectory = ResolveStagingDirectory(options, incus.StagingDirectory);
        return new IncusSandboxOptions
        {
            BinaryPath = incus.BinaryPath,
            ProjectName = incus.ProjectName,
            StoragePoolName = incus.StoragePoolName,
            DefaultImage = incus.DefaultImage,
            InstanceNamePrefix = incus.InstanceNamePrefix,
            BaselineNamePrefix = incus.BaselineNamePrefix,
            UseBaselineImages = incus.UseBaselineImages,
            NetworkProfiles = SnapshotNetworkProfiles(options.SandboxNetworkProfiles),
            AllowedHostMountRoots = SnapshotAllowedHostMountRoots(options),
            // Provider provisioning is independent: an empty Incus value
            // intentionally remains empty during a side-by-side cutover.
            ExtraRuncmd = SnapshotExtraRuncmd(incus.ExtraRuncmd),
            ExtraCloudInit = incus.ExtraCloudInit,
            StagingDirectory = stagingDirectory,
            GuestUserId = incus.GuestUserId,
            GuestGroupId = incus.GuestGroupId,
            GuestHome = incus.GuestHome,
            OperationTimeout = incus.OperationTimeout,
            ExecTimeout = incus.ExecTimeout,
            ImageProvisioningTimeout = incus.ImageProvisioningTimeout,
            VmStartTimeout = incus.VmStartTimeout,
            VmStopTimeout = incus.VmStopTimeout,
            CloudInitTimeout = incus.CloudInitTimeout,
            MountReadyTimeout = incus.MountReadyTimeout,
            ReadinessPollInterval = incus.ReadinessPollInterval,
            MaxConcurrentOperations = incus.MaxConcurrentOperations,
            MaxCliStdoutBytes = incus.MaxCliStdoutBytes,
            MaxCliStderrBytes = incus.MaxCliStderrBytes,
            CaptureResourceMetrics = incus.CaptureResourceMetrics,
            ResourceMetricsCaptureTimeout = incus.ResourceMetricsCaptureTimeout,
            ResourceMetricsSampleInterval = incus.ResourceMetricsSampleInterval,
            DiskGuard = diskGuard is null
                ? null
                : new IncusDiskGuardOptions
                {
                    MinFreeBytes = diskGuard.MinFreeBytes,
                    RecheckIn = diskGuard.RecheckIn,
                    HostPaths = SnapshotDiskGuardHostPaths(diskGuard, stagingDirectory),
                },
            BaselineCpus = incus.BaselineCpus,
            BaselineMemoryBytes = incus.BaselineMemoryBytes,
            BaselineDiskBytes = incus.BaselineDiskBytes,
            MaxSnapshotBytes = incus.MaxSnapshotBytes,
            MaxSnapshotEntries = incus.MaxSnapshotEntries,
            MaxReadinessProbeEntries = incus.MaxReadinessProbeEntries,
            MaxTmpfsDeviceBytes = incus.MaxTmpfsDeviceBytes,
            MaxAggregateTmpfsBytes = incus.MaxAggregateTmpfsBytes,
        };
    }

    private static IReadOnlyDictionary<string, string> SnapshotNetworkProfiles(
        IReadOnlyDictionary<string, string>? profiles)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profiles is null)
            return new ReadOnlyDictionary<string, string>(copy);
        if (profiles.Count > IncusSandboxOptions.MaximumNetworkProfiles)
        {
            throw new InvalidOperationException(
                $"SandboxNetworkProfiles cannot contain more than {IncusSandboxOptions.MaximumNetworkProfiles} entries for Incus.");
        }

        long bytes = 0;
        foreach (var (profile, bridge) in profiles)
        {
            if (profile is null || bridge is null)
                throw new InvalidOperationException("SandboxNetworkProfiles cannot contain null keys or values.");
            var entryBytes = (long)System.Text.Encoding.UTF8.GetByteCount(profile)
                + System.Text.Encoding.UTF8.GetByteCount(bridge);
            if (entryBytes > IncusSandboxOptions.MaximumNetworkProfileUtf8Bytes - bytes)
                throw new InvalidOperationException("SandboxNetworkProfiles exceeds 64 KiB in aggregate for Incus.");
            bytes += entryBytes;
            copy.Add(profile, bridge);
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static IReadOnlyList<string> SnapshotExtraRuncmd(IReadOnlyList<string>? commands)
    {
        if (commands is null || commands.Count == 0)
            return Array.Empty<string>();
        if (commands.Count > IncusSandboxOptions.MaximumExtraRuncmdCount)
        {
            throw new InvalidOperationException(
                $"Incus:ExtraRuncmd cannot contain more than {IncusSandboxOptions.MaximumExtraRuncmdCount} commands.");
        }

        var copy = new string[commands.Count];
        long bytes = 0;
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index]
                ?? throw new InvalidOperationException("Incus:ExtraRuncmd cannot contain null commands.");
            var commandBytes = System.Text.Encoding.UTF8.GetByteCount(command);
            if (commandBytes > IncusSandboxOptions.MaximumExtraRuncmdUtf8Bytes - bytes)
                throw new InvalidOperationException("Incus:ExtraRuncmd exceeds 1 MiB in aggregate.");
            bytes += commandBytes;
            copy[index] = command;
        }
        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyList<string> SnapshotAllowedHostMountRoots(CodeyBoxOptions options)
    {
        var configuredRoots = options.Incus?.AllowedHostMountRoots ?? [];
        if (configuredRoots.Count > 64)
            throw new InvalidOperationException("Incus:AllowedHostMountRoots cannot contain more than 64 entries.");
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Add(options.GitRootDirectory);
        if (options.EnableSharedUpstreamMirror)
        {
            var mirror = string.IsNullOrWhiteSpace(options.SharedUpstreamMirrorDirectory)
                ? "_upstream-mirror"
                : options.SharedUpstreamMirrorDirectory;
            Add(Path.IsPathRooted(mirror) || string.IsNullOrWhiteSpace(options.GitRootDirectory)
                ? mirror
                : Path.Combine(options.GitRootDirectory, mirror));
        }
        foreach (var root in configuredRoots)
            Add(root);

        return Array.AsReadOnly(roots.ToArray());

        void Add(string root)
        {
            var normalized = NormalizeHostPath(root);
            if (seen.Add(normalized))
                roots.Add(normalized);
        }
    }

    internal static string ResolveStagingDirectory(CodeyBoxOptions options, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return NormalizeHostPath(configured);

        var statePath = Path.GetFullPath(options.StateDatabasePath);
        var stateDirectory = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException(
                "CodeyBox:StateDatabasePath must have a parent directory for Incus staging.");
        return Path.Combine(stateDirectory, "incus-staging");
    }

    private static IReadOnlyList<string> SnapshotDiskGuardHostPaths(
        ResolvedDiskGuardConfig diskGuard,
        string stagingDirectory)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in diskGuard.AdditionalPaths)
            Add(path);
        Add(stagingDirectory);
        return Array.AsReadOnly(paths.ToArray());

        void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = NormalizeHostPath(path);
            if (seen.Add(normalized))
                paths.Add(normalized);
        }
    }

    private static string NormalizeHostPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Preserve invalid input so IncusSandboxOptions.Validate can report
            // the operator-facing field error through IOptions validation.
            return path;
        }
    }
}
