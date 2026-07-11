using CodeyBox.Core;
using CodeyBox.HostProcess;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Hot-readable operational settings for <see cref="IncusSandboxProvider"/>.
/// <see cref="ProjectName"/> and the effective <see cref="StagingDirectory"/>
/// are lifecycle identities captured when the provider is constructed and
/// require a provider restart to change.
/// </summary>
public sealed record IncusSandboxOptions
{
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultExecTimeout = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultImageProvisioningTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultVmStartTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultVmStopTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultReadinessPollInterval = TimeSpan.FromSeconds(1);
    public const int DefaultMaxCliOutputBytes = 4 * 1024 * 1024;
    public const int MaximumNetworkProfiles = 128;
    public const int MaximumNetworkProfileUtf8Bytes = 64 * 1024;
    public const int MaximumExtraRuncmdCount = 256;
    public const int MaximumExtraRuncmdCommandUtf8Bytes = 64 * 1024;
    public const int MaximumAggregateExtraRuncmdUtf8Bytes = 1024 * 1024;
    public const int MaximumExtraCloudInitUtf8Bytes = 1024 * 1024;
    /// <summary>Maximum operator-supplied entries in either Incus host-path list.</summary>
    public const int MaximumConfiguredHostPathEntries = 64;
    /// <summary>Configured entries plus the two provider-managed paths each list can add.</summary>
    public const int MaximumEffectiveHostPathEntries = MaximumConfiguredHostPathEntries + 2;
    public const int MaximumExecRetryAttempts = 100;

    /// <summary>
    /// Path to an Incus 6.3-or-newer CLI. The recommended Incus 7.0 LTS
    /// release requires Linux 6.12 or newer and QEMU 8.2 or newer.
    /// </summary>
    public string BinaryPath { get; init; } = "incus";

    /// <summary>
    /// Dedicated non-default restricted Incus project used to contain
    /// CodeyBox-owned instances and enforce daemon-side host disk paths.
    /// </summary>
    public string ProjectName { get; init; } = "codeybox";

    /// <summary>
    /// Existing ZFS or Btrfs storage pool used for VM roots. The provider verifies the
    /// snapshot-capable driver and rejects any explicitly configured
    /// <c>zfs.clone_copy</c> mode other than <c>true</c>; it never creates or
    /// reformats storage. ZFS is strongly recommended for VM workloads.
    /// </summary>
    public string StoragePoolName { get; init; } = "codeybox-zfs";

    /// <summary>Fallback Incus VM image when the sandbox spec does not name one.</summary>
    public string DefaultImage { get; init; } = "images:ubuntu/24.04/cloud";

    public string InstanceNamePrefix { get; init; } = "codeybox-";
    public string BaselineNamePrefix { get; init; } = "cb-incus-baseline-";
    public bool UseBaselineImages { get; init; } = true;

    /// <summary>Maps CodeyBox policy names to pre-existing, host-firewalled Linux bridges.</summary>
    public IReadOnlyDictionary<string, string> NetworkProfiles { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Canonical host directory roots from which direct virtiofs sources may be
    /// attached. Empty rejects all direct host mounts; provider-owned staging
    /// snapshots remain allowed.
    /// </summary>
    public IReadOnlyList<string> AllowedHostMountRoots { get; init; } = [];

    /// <summary>Operator-provided first-boot commands included in the baked baseline.</summary>
    public IReadOnlyList<string> ExtraRuncmd { get; init; } = [];

    /// <summary>
    /// Optional additional cloud-init top-level configuration. Generated keys such as
    /// <c>write_files</c> and <c>runcmd</c> are rejected to prevent ambiguous merging.
    /// </summary>
    public string? ExtraCloudInit { get; init; }

    /// <summary>
    /// Private persistent host staging root for isolation snapshots and mount grouping.
    /// Defaults beneath <c>XDG_STATE_HOME</c>, or <c>~/.local/state</c> when unset.
    /// </summary>
    public string? StagingDirectory { get; init; }

    public TimeSpan OperationTimeout { get; init; } = DefaultOperationTimeout;
    /// <summary>
    /// Provider-side upper bound for one guest command. An explicit sandbox
    /// wall-clock limit or caller cancellation can end the command sooner.
    /// </summary>
    public TimeSpan ExecTimeout { get; init; } = DefaultExecTimeout;
    /// <summary>Deadline for a cold image download/import and VM root initialization.</summary>
    public TimeSpan ImageProvisioningTimeout { get; init; } = DefaultImageProvisioningTimeout;
    public TimeSpan VmStartTimeout { get; init; } = DefaultVmStartTimeout;
    public TimeSpan VmStopTimeout { get; init; } = DefaultVmStopTimeout;
    public TimeSpan CloudInitTimeout { get; init; } = DefaultVmStartTimeout;
    public TimeSpan MountReadyTimeout { get; init; } = DefaultVmStartTimeout;
    public TimeSpan ReadinessPollInterval { get; init; } = DefaultReadinessPollInterval;
    /// <summary>Independent deadline for terminating and draining one Incus CLI process tree.</summary>
    public TimeSpan CliProcessCleanupTimeout { get; init; } = DefaultProcessRunnerOptions.DefaultCleanupTimeout;
    /// <summary>Delay between Linux Incus CLI process-group absence probes during cleanup.</summary>
    public TimeSpan CliProcessGroupExitPollInterval { get; init; } = DefaultProcessRunnerOptions.DefaultProcessGroupExitPollInterval;
    /// <summary>Attempts to read an active guest exec's process-group ID before forced cleanup.</summary>
    public int ExecPidPollAttempts { get; init; } = 5;
    /// <summary>Attempts to delete and verify absence of each transient guest exec control file.</summary>
    public int ExecControlFileCleanupAttempts { get; init; } = 3;
    /// <summary>Attempts to read and validate a guest exec completion sentinel.</summary>
    public int ExecCompletionProbeAttempts { get; init; } = 3;
    public int MaxConcurrentOperations { get; init; } = 2;
    public int MaxCliStdoutBytes { get; init; } = DefaultMaxCliOutputBytes;
    public int MaxCliStderrBytes { get; init; } = DefaultMaxCliOutputBytes;
    public bool CaptureResourceMetrics { get; init; }
    public TimeSpan ResourceMetricsCaptureTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ResourceMetricsSampleInterval { get; init; } = TimeSpan.FromSeconds(10);
    public IncusDiskGuardOptions? DiskGuard { get; init; } = new();

    /// <summary>
    /// Numeric guest identity used for all untrusted agent commands. Every
    /// host-backed mount requires an exact match with the provider process's
    /// effective host identity because VM virtiofs does not shift IDs.
    /// </summary>
    public uint GuestUserId { get; init; } = 1000;
    public uint GuestGroupId { get; init; } = 1000;
    public string GuestHome { get; init; } = "/home/ubuntu";

    public int BaselineCpus { get; init; } = 6;
    public long BaselineMemoryBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public long BaselineDiskBytes { get; init; } = SandboxResourceLimits.Default.DiskBytes ??
        8L * 1024 * 1024 * 1024;

    /// <summary>Maximum aggregate bytes copied into private staging for one sandbox.</summary>
    public long MaxSnapshotBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    /// <summary>Maximum files, directories, and links copied into private staging.</summary>
    public int MaxSnapshotEntries { get; init; } = 100_000;
    /// <summary>Maximum direct-mount entries inspected while choosing an identity probe.</summary>
    public int MaxReadinessProbeEntries { get; init; } = 4096;
    public long MaxTmpfsDeviceBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public long MaxAggregateTmpfsBytes { get; init; } = 32L * 1024 * 1024 * 1024;

    /// <summary>Validates configuration without accessing the host or Incus daemon.</summary>
    public static IReadOnlyList<string> Validate(IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            options = IncusInputSnapshot.CaptureOptions(options);
        }
        catch (ArgumentException ex)
        {
            return [ex.Message];
        }
        var errors = new List<string>();
        RequireText(options.BinaryPath, nameof(BinaryPath), 4096, errors);
        // Incus places virtiofs control sockets beneath
        // /var/lib/incus/devices/<project>_<instance>. Keep enough of Linux's
        // 107-byte sockaddr_un path budget for an entropy-bearing instance
        // name and the provider's device name.
        RequireName(options.ProjectName, nameof(ProjectName), 42, errors);
        if (string.Equals(options.ProjectName, "default", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{nameof(ProjectName)} must name a dedicated non-default Incus project.");
        RequireName(options.StoragePoolName, nameof(StoragePoolName), 63, errors);
        RequireText(options.DefaultImage, nameof(DefaultImage), 4096, errors);
        RequirePrefix(options.InstanceNamePrefix, nameof(InstanceNamePrefix), errors);
        RequirePrefix(options.BaselineNamePrefix, nameof(BaselineNamePrefix), errors);
        if (IsValidPrefix(options.BaselineNamePrefix))
        {
            var baselinePrefix = IncusBaselineNaming.NormalizeEffectivePrefix(options);
            if (IncusBaselineNaming.OverlapsBakeCandidateNamespace(baselinePrefix))
            {
                errors.Add(
                    $"{nameof(BaselineNamePrefix)} must not overlap the reserved " +
                    $"'{IncusBaselineNaming.BakeCandidatePrefix}' bake-candidate namespace.");
            }
        }
        RequirePositiveDuration(options.OperationTimeout, nameof(OperationTimeout), errors);
        RequirePositiveDuration(options.ExecTimeout, nameof(ExecTimeout), errors);
        RequirePositiveDuration(options.ImageProvisioningTimeout, nameof(ImageProvisioningTimeout), errors);
        RequirePositiveDuration(options.VmStartTimeout, nameof(VmStartTimeout), errors);
        RequirePositiveDuration(options.VmStopTimeout, nameof(VmStopTimeout), errors);
        RequirePositiveDuration(options.CloudInitTimeout, nameof(CloudInitTimeout), errors);
        RequirePositiveDuration(options.MountReadyTimeout, nameof(MountReadyTimeout), errors);
        RequirePositiveDuration(options.ReadinessPollInterval, nameof(ReadinessPollInterval), errors);
        try
        {
            DefaultProcessRunnerOptions.Validate(new DefaultProcessRunnerOptions
            {
                IsolateLinuxProcessGroup = true,
                CleanupTimeout = options.CliProcessCleanupTimeout,
                ProcessGroupExitPollInterval = options.CliProcessGroupExitPollInterval,
            });
        }
        catch (ArgumentException ex)
        {
            errors.Add(
                $"{nameof(CliProcessCleanupTimeout)} and {nameof(CliProcessGroupExitPollInterval)} are invalid: {ex.Message}");
        }
        RequireRetryAttempts(options.ExecPidPollAttempts, nameof(ExecPidPollAttempts), errors);
        RequireRetryAttempts(options.ExecControlFileCleanupAttempts, nameof(ExecControlFileCleanupAttempts), errors);
        RequireRetryAttempts(options.ExecCompletionProbeAttempts, nameof(ExecCompletionProbeAttempts), errors);
        RequirePositiveDuration(options.ResourceMetricsCaptureTimeout, nameof(ResourceMetricsCaptureTimeout), errors);
        RequirePositiveDuration(options.ResourceMetricsSampleInterval, nameof(ResourceMetricsSampleInterval), errors);
        if (options.MaxConcurrentOperations is < 1 or > 64)
            errors.Add($"{nameof(MaxConcurrentOperations)} must be between 1 and 64.");
        if (options.MaxCliStdoutBytes is < 1024 or > 64 * 1024 * 1024)
            errors.Add($"{nameof(MaxCliStdoutBytes)} must be between 1024 and 67108864.");
        if (options.MaxCliStderrBytes is < 1024 or > 64 * 1024 * 1024)
            errors.Add($"{nameof(MaxCliStderrBytes)} must be between 1024 and 67108864.");
        if (options.BaselineCpus is < 1 or > 256)
            errors.Add($"{nameof(BaselineCpus)} must be between 1 and 256.");
        if (options.BaselineMemoryBytes < 256L * 1024 * 1024
            || options.BaselineMemoryBytes > 2L * 1024 * 1024 * 1024 * 1024)
            errors.Add($"{nameof(BaselineMemoryBytes)} must be between 256 MiB and 2 TiB.");
        if (options.BaselineDiskBytes < 2L * 1024 * 1024 * 1024
            || options.BaselineDiskBytes > 16L * 1024 * 1024 * 1024 * 1024)
            errors.Add($"{nameof(BaselineDiskBytes)} must be between 2 GiB and 16 TiB.");
        if (options.MaxSnapshotBytes is < 1 or > 1024L * 1024 * 1024 * 1024)
            errors.Add($"{nameof(MaxSnapshotBytes)} must be between 1 byte and 1 TiB.");
        if (options.MaxSnapshotEntries is < 1 or > 1_000_000)
            errors.Add($"{nameof(MaxSnapshotEntries)} must be between 1 and 1000000.");
        if (options.MaxReadinessProbeEntries is < 1 or > 100_000)
            errors.Add($"{nameof(MaxReadinessProbeEntries)} must be between 1 and 100000.");
        if (options.MaxTmpfsDeviceBytes is < 1 or > 1024L * 1024 * 1024 * 1024)
            errors.Add($"{nameof(MaxTmpfsDeviceBytes)} must be between 1 byte and 1 TiB.");
        if (options.MaxAggregateTmpfsBytes < options.MaxTmpfsDeviceBytes
            || options.MaxAggregateTmpfsBytes > 4L * 1024 * 1024 * 1024 * 1024)
            errors.Add($"{nameof(MaxAggregateTmpfsBytes)} must be at least MaxTmpfsDeviceBytes and no more than 4 TiB.");
        if (options.DiskGuard is { } diskGuard)
        {
            if (diskGuard.MinFreeBytes < 0)
                errors.Add($"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.MinFreeBytes)} cannot be negative.");
            RequirePositiveDuration(
                diskGuard.RecheckIn,
                $"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.RecheckIn)}",
                errors);
            if (diskGuard.HostPaths.Count > MaximumEffectiveHostPathEntries)
            {
                errors.Add(
                    $"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} cannot contain more than {MaximumEffectiveHostPathEntries} paths.");
            }
            else
            {
                var hostPathBytes = 0L;
                foreach (var path in diskGuard.HostPaths)
                {
                    if (path is null)
                    {
                        errors.Add($"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} cannot contain null paths.");
                        continue;
                    }
                    if (!TryGetBoundedUtf8ByteCount(
                            path,
                            4096,
                            $"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} entry",
                            errors,
                            out var pathBytes))
                    {
                        continue;
                    }
                    hostPathBytes += pathBytes;
                    if (string.IsNullOrWhiteSpace(path)
                        || path.Any(char.IsControl)
                        || !Path.IsPathFullyQualified(path))
                        errors.Add($"Every {nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} entry must be an absolute bounded host path.");
                }
                if (hostPathBytes > 256 * 1024)
                    errors.Add($"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} exceeds 256 KiB in aggregate.");
            }
        }
        if (options.GuestUserId is 0 or uint.MaxValue || options.GuestGroupId is 0 or uint.MaxValue)
            errors.Add("GuestUserId and GuestGroupId must be neither root (0) nor the reserved uint.MaxValue identity.");
        if (!IsAbsoluteGuestPath(options.GuestHome))
            errors.Add($"{nameof(GuestHome)} must be a normalized absolute Unix path.");
        if (options.ExtraRuncmd.Count > MaximumExtraRuncmdCount)
        {
            errors.Add(
                $"{nameof(ExtraRuncmd)} cannot contain more than {MaximumExtraRuncmdCount} commands.");
        }
        else
        {
            var commandBytes = 0L;
            foreach (var command in options.ExtraRuncmd)
            {
                if (command is null)
                {
                    errors.Add($"{nameof(ExtraRuncmd)} cannot contain null commands.");
                    continue;
                }
                if (!TryGetBoundedUtf8ByteCount(
                        command,
                        MaximumExtraRuncmdCommandUtf8Bytes,
                        $"{nameof(ExtraRuncmd)} command",
                        errors,
                        out var bytes))
                {
                    continue;
                }
                commandBytes += bytes;
                if (command.Contains('\0'))
                    errors.Add($"An {nameof(ExtraRuncmd)} command contains NUL.");
            }
            if (commandBytes > MaximumAggregateExtraRuncmdUtf8Bytes)
                errors.Add($"{nameof(ExtraRuncmd)} exceeds 1 MiB in aggregate.");
        }
        var validateExtraCloudInit = true;
        if (options.ExtraCloudInit is { } cloudInit
            && !TryGetBoundedUtf8ByteCount(
                cloudInit,
                MaximumExtraCloudInitUtf8Bytes,
                nameof(ExtraCloudInit),
                errors,
                out _))
        {
            validateExtraCloudInit = false;
        }
        if (options.NetworkProfiles.Count > MaximumNetworkProfiles)
        {
            errors.Add(
                $"{nameof(NetworkProfiles)} cannot contain more than {MaximumNetworkProfiles} entries.");
        }
        else
        {
            var networkProfileBytes = 0L;
            foreach (var (profile, bridge) in options.NetworkProfiles)
            {
                if (profile is null || bridge is null)
                {
                    errors.Add($"{nameof(NetworkProfiles)} cannot contain null keys or values.");
                    continue;
                }
                if (!TryGetBoundedUtf8ByteCount(
                        profile,
                        63,
                        "NetworkProfiles key",
                        errors,
                        out var profileBytes)
                    || !TryGetBoundedUtf8ByteCount(
                        bridge,
                        15,
                        "NetworkProfiles value",
                        errors,
                        out var bridgeBytes))
                {
                    continue;
                }
                networkProfileBytes += profileBytes;
                networkProfileBytes += bridgeBytes;
                RequireName(profile, "NetworkProfiles key", 63, errors);
                if (string.IsNullOrWhiteSpace(bridge)
                    || bridge.Length > 15
                    || bridge.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
                {
                    errors.Add(
                        "NetworkProfiles values must be valid Linux interface names of at most 15 characters.");
                }
            }
            if (networkProfileBytes > MaximumNetworkProfileUtf8Bytes)
                errors.Add($"{nameof(NetworkProfiles)} exceeds 64 KiB in aggregate.");
        }
        if (options.AllowedHostMountRoots.Count > MaximumEffectiveHostPathEntries)
        {
            errors.Add(
                $"{nameof(AllowedHostMountRoots)} cannot contain more than {MaximumEffectiveHostPathEntries} entries.");
        }
        else
        {
            var allowedRootBytes = 0L;
            foreach (var root in options.AllowedHostMountRoots)
            {
                if (root is null)
                {
                    errors.Add($"{nameof(AllowedHostMountRoots)} cannot contain null roots.");
                    continue;
                }
                if (!TryGetBoundedUtf8ByteCount(
                        root,
                        4096,
                        $"{nameof(AllowedHostMountRoots)} entry",
                        errors,
                        out var rootBytes))
                {
                    continue;
                }
                allowedRootBytes += rootBytes;
                if (string.IsNullOrWhiteSpace(root)
                    || root.Contains(',')
                    || root.Any(char.IsControl)
                    || !Path.IsPathFullyQualified(root))
                {
                    errors.Add(
                        $"Each {nameof(AllowedHostMountRoots)} entry must be a bounded absolute path without commas or control characters.");
                }
                else if (IsHostFilesystemRoot(root))
                {
                    errors.Add($"{nameof(AllowedHostMountRoots)} cannot include the host filesystem root.");
                }
            }
            if (allowedRootBytes > 256 * 1024)
                errors.Add($"{nameof(AllowedHostMountRoots)} exceeds 256 KiB in aggregate.");
        }
        var stagingDirectoryValid = options.StagingDirectory is null
            || TryGetBoundedUtf8ByteCount(
                options.StagingDirectory,
                4096,
                nameof(StagingDirectory),
                errors,
                out _);
        if (stagingDirectoryValid
            && options.StagingDirectory is { Length: > 0 } stagingDirectory
            && !string.IsNullOrWhiteSpace(stagingDirectory)
            && (stagingDirectory.Contains(',')
                || stagingDirectory.Any(char.IsControl)
                || !Path.IsPathFullyQualified(stagingDirectory)
                || IsHostFilesystemRoot(stagingDirectory)))
        {
            errors.Add(
                $"{nameof(StagingDirectory)} must be a bounded non-root absolute host path without commas or control characters when configured.");
        }
        try
        {
            if (validateExtraCloudInit)
                IncusCloudInit.ValidateExtraFragment(options.ExtraCloudInit);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"{nameof(ExtraCloudInit)} is invalid: {ex.Message}");
        }
        return errors;
    }

    private static void RequireRetryAttempts(int value, string name, ICollection<string> errors)
    {
        if (value is < 1 or > MaximumExecRetryAttempts)
            errors.Add($"{name} must be between 1 and {MaximumExecRetryAttempts}.");
    }

    internal static bool IsAbsoluteGuestPath(string value)
    {
        if (value is null || value.Length is < 1 or > 4096)
            return false;
        try
        {
            _ = IncusInputValidation.GetBoundedUtf8ByteCount(
                value,
                4096,
                nameof(value),
                "Guest path");
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || (value.Length > 1 && value.EndsWith("/", StringComparison.Ordinal))
            || value.Contains("//", StringComparison.Ordinal)
            || value.Any(char.IsControl))
            return false;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(segment => segment is "." or "..");
    }

    private static void RequirePositiveDuration(TimeSpan value, string name, ICollection<string> errors)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromDays(1))
            errors.Add($"{name} must be greater than zero and no more than one day.");
    }

    private static bool IsHostFilesystemRoot(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(full);
            return root is not null
                && string.Equals(
                    full,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void RequireText(string value, string name, int maxLength, ICollection<string> errors)
    {
        if (value is null || value.Length is < 1 || value.Length > maxLength)
        {
            errors.Add($"{name} must be non-empty, contain no control characters, and be at most {maxLength} characters.");
            return;
        }
        if (!TryGetBoundedUtf8ByteCount(value, maxLength, name, errors, out _))
            return;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            errors.Add($"{name} must be non-empty, contain no control characters, and be at most {maxLength} characters.");
    }

    private static void RequirePrefix(string value, string name, ICollection<string> errors)
    {
        RequireText(value, name, 32, errors);
        if (!IsValidPrefix(value))
            errors.Add($"{name} may contain only ASCII letters, digits, and hyphens, and must start alphanumeric.");
    }

    private static bool IsValidPrefix(string value)
    {
        return value is { Length: > 0 and <= 32 }
            && !string.IsNullOrWhiteSpace(value)
            && !value.Any(char.IsControl)
            && char.IsAsciiLetterOrDigit(value[0])
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private static void RequireName(string value, string name, int maxLength, ICollection<string> errors)
    {
        RequireText(value, name, maxLength, errors);
        if (value is null || value.Length is < 1 || value.Length > maxLength)
            return;
        if (string.IsNullOrWhiteSpace(value)
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1])
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            errors.Add($"{name} has characters unsupported by the Incus provider.");
    }

    private static bool TryGetBoundedUtf8ByteCount(
        string value,
        int maximumUtf8Bytes,
        string name,
        ICollection<string> errors,
        out int bytes)
    {
        try
        {
            bytes = IncusInputValidation.GetBoundedUtf8ByteCount(
                value,
                maximumUtf8Bytes,
                name,
                name);
            return true;
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
            bytes = 0;
            return false;
        }
    }
}

public sealed record IncusDiskGuardOptions
{
    public long MinFreeBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public TimeSpan RecheckIn { get; init; } = TimeSpan.FromMinutes(5);
    public IReadOnlyList<string> HostPaths { get; init; } = [];
}
