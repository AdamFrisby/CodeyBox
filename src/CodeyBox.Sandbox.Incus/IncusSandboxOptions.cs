using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

/// <summary>Hot-readable operational settings for <see cref="IncusSandboxProvider"/>.</summary>
public sealed record IncusSandboxOptions
{
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultExecTimeout = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultImageProvisioningTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultVmStartTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultVmStopTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultReadinessPollInterval = TimeSpan.FromSeconds(1);
    public const int DefaultMaxCliOutputBytes = 4 * 1024 * 1024;

    /// <summary>Path to an Incus 6.3-or-newer CLI. Incus 7.0 LTS is recommended.</summary>
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
        RequirePositiveDuration(options.OperationTimeout, nameof(OperationTimeout), errors);
        RequirePositiveDuration(options.ExecTimeout, nameof(ExecTimeout), errors);
        RequirePositiveDuration(options.ImageProvisioningTimeout, nameof(ImageProvisioningTimeout), errors);
        RequirePositiveDuration(options.VmStartTimeout, nameof(VmStartTimeout), errors);
        RequirePositiveDuration(options.VmStopTimeout, nameof(VmStopTimeout), errors);
        RequirePositiveDuration(options.CloudInitTimeout, nameof(CloudInitTimeout), errors);
        RequirePositiveDuration(options.MountReadyTimeout, nameof(MountReadyTimeout), errors);
        RequirePositiveDuration(options.ReadinessPollInterval, nameof(ReadinessPollInterval), errors);
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
            if (diskGuard.HostPaths.Count > 64)
                errors.Add($"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} cannot contain more than 64 paths.");
            var hostPathBytes = 0L;
            foreach (var path in diskGuard.HostPaths)
            {
                hostPathBytes += System.Text.Encoding.UTF8.GetByteCount(path);
                if (string.IsNullOrWhiteSpace(path)
                    || path.Length > 4096
                    || path.Any(char.IsControl)
                    || !Path.IsPathFullyQualified(path))
                    errors.Add($"Every {nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} entry must be an absolute bounded host path.");
            }
            if (hostPathBytes > 256 * 1024)
                errors.Add($"{nameof(DiskGuard)}.{nameof(IncusDiskGuardOptions.HostPaths)} exceeds 256 KiB in aggregate.");
        }
        if (options.GuestUserId is 0 or uint.MaxValue || options.GuestGroupId is 0 or uint.MaxValue)
            errors.Add("GuestUserId and GuestGroupId must be neither root (0) nor the reserved uint.MaxValue identity.");
        if (!IsAbsoluteGuestPath(options.GuestHome))
            errors.Add($"{nameof(GuestHome)} must be a normalized absolute Unix path.");
        if (options.ExtraRuncmd.Count > 256)
            errors.Add($"{nameof(ExtraRuncmd)} cannot contain more than 256 commands.");
        var commandBytes = 0L;
        foreach (var command in options.ExtraRuncmd)
        {
            commandBytes += System.Text.Encoding.UTF8.GetByteCount(command);
            if (command.Contains('\0'))
                errors.Add($"An {nameof(ExtraRuncmd)} command contains NUL.");
            if (System.Text.Encoding.UTF8.GetByteCount(command) > 64 * 1024)
                errors.Add($"An {nameof(ExtraRuncmd)} command exceeds 65536 UTF-8 bytes.");
        }
        if (commandBytes > 1024 * 1024)
            errors.Add($"{nameof(ExtraRuncmd)} exceeds 1 MiB in aggregate.");
        if (options.ExtraCloudInit is { } cloudInit
            && System.Text.Encoding.UTF8.GetByteCount(cloudInit) > 1024 * 1024)
            errors.Add($"{nameof(ExtraCloudInit)} exceeds 1 MiB.");
        foreach (var (profile, bridge) in options.NetworkProfiles)
        {
            RequireName(profile, $"NetworkProfiles key '{profile}'", 63, errors);
            if (string.IsNullOrWhiteSpace(bridge)
                || bridge.Length > 15
                || bridge.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
                errors.Add($"NetworkProfiles['{profile}'] must be a valid Linux interface name of at most 15 characters.");
        }
        if (options.AllowedHostMountRoots.Count > 64)
            errors.Add($"{nameof(AllowedHostMountRoots)} cannot contain more than 64 entries.");
        var allowedRootBytes = 0L;
        foreach (var root in options.AllowedHostMountRoots)
        {
            allowedRootBytes += System.Text.Encoding.UTF8.GetByteCount(root);
            if (string.IsNullOrWhiteSpace(root)
                || root.Length > 4096
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
        if (!string.IsNullOrWhiteSpace(options.StagingDirectory)
            && (options.StagingDirectory.Length > 4096
                || options.StagingDirectory.Contains(',')
                || options.StagingDirectory.Any(char.IsControl)
                || !Path.IsPathFullyQualified(options.StagingDirectory)
                || IsHostFilesystemRoot(options.StagingDirectory)))
        {
            errors.Add(
                $"{nameof(StagingDirectory)} must be a bounded non-root absolute host path without commas or control characters when configured.");
        }
        try
        {
            IncusCloudInit.ValidateExtraFragment(options.ExtraCloudInit);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"{nameof(ExtraCloudInit)} is invalid: {ex.Message}");
        }
        return errors;
    }

    internal static bool IsAbsoluteGuestPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
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
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            errors.Add($"{name} must be non-empty, contain no control characters, and be at most {maxLength} characters.");
    }

    private static void RequirePrefix(string value, string name, ICollection<string> errors)
    {
        RequireText(value, name, 32, errors);
        if (string.IsNullOrWhiteSpace(value)
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            errors.Add($"{name} may contain only ASCII letters, digits, and hyphens, and must start alphanumeric.");
    }

    private static void RequireName(string value, string name, int maxLength, ICollection<string> errors)
    {
        RequireText(value, name, maxLength, errors);
        if (string.IsNullOrWhiteSpace(value)
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1])
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            errors.Add($"{name} has characters unsupported by the Incus provider.");
    }
}

public sealed record IncusDiskGuardOptions
{
    public long MinFreeBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public TimeSpan RecheckIn { get; init; } = TimeSpan.FromMinutes(5);
    public IReadOnlyList<string> HostPaths { get; init; } = [];
}
