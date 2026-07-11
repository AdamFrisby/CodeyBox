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
    private const int MaximumHostPathCharacters = 4096;

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
            CliProcessCleanupTimeout = incus.CliProcessCleanupTimeout,
            CliProcessGroupExitPollInterval = incus.CliProcessGroupExitPollInterval,
            ExecPidPollAttempts = incus.ExecPidPollAttempts,
            ExecControlFileCleanupAttempts = incus.ExecControlFileCleanupAttempts,
            ExecCompletionProbeAttempts = incus.ExecCompletionProbeAttempts,
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

    internal static IReadOnlyDictionary<string, string> SnapshotNetworkProfiles(
        IEnumerable<KeyValuePair<string, string>>? profiles)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profiles is null)
            return new ReadOnlyDictionary<string, string>(copy);

        long bytes = 0;
        foreach (var (profile, bridge) in profiles)
        {
            if (copy.Count >= IncusSandboxOptions.MaximumNetworkProfiles)
            {
                throw new InvalidOperationException(
                    $"SandboxNetworkProfiles cannot contain more than {IncusSandboxOptions.MaximumNetworkProfiles} entries for Incus.");
            }
            if (profile is null || bridge is null)
                throw new InvalidOperationException("SandboxNetworkProfiles cannot contain null keys or values.");
            ConfigurationInputBounds.EnsureCharacterBound(
                profile,
                IncusSandboxOptions.MaximumNetworkProfileUtf8Bytes,
                "SandboxNetworkProfiles key");
            ConfigurationInputBounds.EnsureCharacterBound(
                bridge,
                IncusSandboxOptions.MaximumNetworkProfileUtf8Bytes,
                "SandboxNetworkProfiles value");
            var entryBytes = (long)System.Text.Encoding.UTF8.GetByteCount(profile)
                + System.Text.Encoding.UTF8.GetByteCount(bridge);
            if (entryBytes > IncusSandboxOptions.MaximumNetworkProfileUtf8Bytes - bytes)
                throw new InvalidOperationException("SandboxNetworkProfiles exceeds 64 KiB in aggregate for Incus.");
            bytes += entryBytes;
            copy.Add(profile, bridge);
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    internal static IReadOnlyList<string> SnapshotExtraRuncmd(IEnumerable<string>? commands)
    {
        if (commands is null)
            return Array.Empty<string>();

        var copy = new List<string>(Math.Min(IncusSandboxOptions.MaximumExtraRuncmdCount, 16));
        long bytes = 0;
        foreach (var candidate in commands)
        {
            if (copy.Count >= IncusSandboxOptions.MaximumExtraRuncmdCount)
            {
                throw new InvalidOperationException(
                    $"Incus:ExtraRuncmd cannot contain more than {IncusSandboxOptions.MaximumExtraRuncmdCount} commands.");
            }
            var command = candidate
                ?? throw new InvalidOperationException("Incus:ExtraRuncmd cannot contain null commands.");
            ConfigurationInputBounds.EnsureCharacterBound(
                command,
                IncusSandboxOptions.MaximumExtraRuncmdCommandUtf8Bytes,
                "Incus:ExtraRuncmd command");
            var commandBytes = System.Text.Encoding.UTF8.GetByteCount(command);
            if (commandBytes > IncusSandboxOptions.MaximumAggregateExtraRuncmdUtf8Bytes - bytes)
                throw new InvalidOperationException("Incus:ExtraRuncmd exceeds 1 MiB in aggregate.");
            bytes += commandBytes;
            copy.Add(command);
        }
        return copy.Count == 0
            ? Array.Empty<string>()
            : Array.AsReadOnly(copy.ToArray());
    }

    private static IReadOnlyList<string> SnapshotAllowedHostMountRoots(CodeyBoxOptions options)
    {
        var configuredRoots = options.Incus?.AllowedHostMountRoots ?? [];
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Add(options.GitRootDirectory);
        if (options.EnableSharedUpstreamMirror)
        {
            var configuredMirror = options.SharedUpstreamMirrorDirectory;
            ConfigurationInputBounds.EnsureCharacterBound(
                configuredMirror,
                MaximumHostPathCharacters,
                "CodeyBox:SharedUpstreamMirrorDirectory");
            var mirror = string.IsNullOrWhiteSpace(configuredMirror)
                ? "_upstream-mirror"
                : configuredMirror;
            ConfigurationInputBounds.EnsureCharacterBound(
                options.GitRootDirectory,
                MaximumHostPathCharacters,
                "CodeyBox:GitRootDirectory");
            Add(Path.IsPathRooted(mirror) || string.IsNullOrWhiteSpace(options.GitRootDirectory)
                ? mirror
                : Path.Combine(options.GitRootDirectory, mirror));
        }
        var configuredRootCount = 0;
        foreach (var root in configuredRoots)
        {
            if (configuredRootCount++ >= IncusSandboxOptions.MaximumConfiguredHostPathEntries)
            {
                throw new InvalidOperationException(
                    $"Incus:AllowedHostMountRoots cannot contain more than {IncusSandboxOptions.MaximumConfiguredHostPathEntries} entries.");
            }
            Add(root);
        }

        return Array.AsReadOnly(roots.ToArray());

        void Add(string root)
        {
            var normalized = NormalizeHostPath(root, "Incus host mount root");
            if (seen.Add(normalized))
                roots.Add(normalized);
        }
    }

    internal static string ResolveStagingDirectory(CodeyBoxOptions options, string? configured)
    {
        if (configured is not null)
        {
            ConfigurationInputBounds.EnsureCharacterBound(
                configured,
                MaximumHostPathCharacters,
                "Incus:StagingDirectory");
            if (!string.IsNullOrWhiteSpace(configured))
                return NormalizeHostPath(configured, "Incus:StagingDirectory");
        }

        ConfigurationInputBounds.EnsureCharacterBound(
            options.StateDatabasePath,
            MaximumHostPathCharacters,
            "CodeyBox:StateDatabasePath");
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
        var observedPathCount = 0;
        foreach (var path in diskGuard.AdditionalPaths)
        {
            if (observedPathCount++ >= IncusSandboxOptions.MaximumEffectiveHostPathEntries)
                throw new InvalidOperationException("Incus disk-guard host paths exceed the bounded entry limit.");
            Add(path);
        }
        Add(stagingDirectory);
        return Array.AsReadOnly(paths.ToArray());

        void Add(string path)
        {
            ConfigurationInputBounds.EnsureCharacterBound(
                path,
                MaximumHostPathCharacters,
                "Incus disk-guard host path");
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = NormalizeHostPath(path, "Incus disk-guard host path");
            if (!seen.Add(normalized)) return;
            if (paths.Count == IncusSandboxOptions.MaximumEffectiveHostPathEntries)
                throw new InvalidOperationException("Incus disk-guard host paths exceed the bounded entry limit.");
            paths.Add(normalized);
        }
    }

    private static string NormalizeHostPath(string path, string fieldName)
    {
        ConfigurationInputBounds.EnsureCharacterBound(path, MaximumHostPathCharacters, fieldName);
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
