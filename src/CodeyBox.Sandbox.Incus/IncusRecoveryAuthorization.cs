using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Immutable authorization evidence captured after provider creation has
/// proved the guest mount paths canonical and the effective device topology
/// exact. Directory handles pin every host source for the active sandbox
/// lifetime; durable adoption reconstructs those pins from persisted inode
/// identities before any VM start.
/// </summary>
internal sealed class IncusRecoveryAuthorization : IDisposable
{
    private readonly IReadOnlyList<IncusPreparedMount> _mounts;
    private readonly IReadOnlyList<string> _requestedGuestMountPaths;
    private readonly IReadOnlyList<string> _canonicalGuestPaths;
    private readonly IReadOnlyList<IncusGuestLink> _guestLinks;
    private readonly IReadOnlyList<IncusGuestLink> _executableLinks;
    private readonly IReadOnlyList<IncusPreparedMount> _guestTmpfsMounts;
    private int _disposed;

    private IncusRecoveryAuthorization(
        string? bridge,
        IReadOnlyList<IncusPreparedMount> mounts,
        IReadOnlyList<string> requestedGuestMountPaths,
        IReadOnlyList<string> canonicalGuestPaths,
        IReadOnlyList<IncusGuestLink> guestLinks,
        IReadOnlyList<IncusGuestLink> executableLinks,
        IReadOnlyList<IncusPreparedMount> guestTmpfsMounts)
    {
        Bridge = bridge;
        _mounts = mounts;
        _requestedGuestMountPaths = requestedGuestMountPaths;
        _canonicalGuestPaths = canonicalGuestPaths;
        _guestLinks = guestLinks;
        _executableLinks = executableLinks;
        _guestTmpfsMounts = guestTmpfsMounts;
    }

    internal string? Bridge { get; }
    internal IReadOnlyList<IncusPreparedMount> Mounts => _mounts;
    internal IReadOnlyList<string> RequestedGuestMountPaths => _requestedGuestMountPaths;
    internal IReadOnlyList<string> CanonicalGuestPaths => _canonicalGuestPaths;
    internal IReadOnlyList<IncusGuestLink> GuestLinks => _guestLinks;
    internal IReadOnlyList<IncusGuestLink> ExecutableLinks => _executableLinks;
    internal IReadOnlyList<IncusPreparedMount> GuestTmpfsMounts => _guestTmpfsMounts;
    internal bool HasHostDevices => _mounts.Any(static mount => mount.HostSource is not null);

    /// <summary>
    /// Builds the exact path set that must resolve canonically while no host
    /// devices are attached. It includes caller-visible mount/link paths and
    /// provider-created prepared device targets.
    /// </summary>
    internal static IReadOnlyList<string> BuildCanonicalGuestPaths(
        IReadOnlyList<string> requestedGuestMountPaths,
        IReadOnlyList<IncusPreparedMount> mounts)
    {
        ArgumentNullException.ThrowIfNull(requestedGuestMountPaths);
        ArgumentNullException.ThrowIfNull(mounts);
        var paths = new List<string>(Math.Min(
            IncusMountStaging.MaximumMounts * 2,
            requestedGuestMountPaths.Count + mounts.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in requestedGuestMountPaths.Concat(mounts.Select(static mount => mount.GuestPath)))
        {
            if (paths.Count >= IncusMountStaging.MaximumMounts * 2)
                throw new ArgumentException("Incus canonical recovery paths exceed the safety bound.");
            IncusInputValidation.ValidateAbsoluteGuestPath(path, nameof(requestedGuestMountPaths));
            if (seen.Add(path))
                paths.Add(path);
        }
        return Array.AsReadOnly(paths.ToArray());
    }

    /// <summary>
    /// Captures the result of the provider's live canonical-path and device
    /// authorization checks. Call this only after those checks have succeeded.
    /// </summary>
    internal static IncusRecoveryAuthorization CaptureValidated(
        string? bridge,
        IReadOnlyList<IncusPreparedMount> mounts,
        IReadOnlyList<string> requestedGuestMountPaths,
        IReadOnlyList<IncusGuestLink> guestLinks,
        IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(mounts);
        ArgumentNullException.ThrowIfNull(requestedGuestMountPaths);
        ArgumentNullException.ThrowIfNull(guestLinks);
        ArgumentNullException.ThrowIfNull(options);
        if (bridge is not null)
            IncusInputValidation.ValidateBridgeName(bridge, nameof(bridge));

        var retainedPins = new List<IncusPinnedDirectory>();
        try
        {
            var capturedMounts = CaptureMounts(mounts, options, retainedPins, expectedIdentities: null);
            var requestedPaths = CaptureRequestedPaths(requestedGuestMountPaths);
            var capturedLinks = CaptureGuestLinks(capturedMounts, guestLinks);
            var executableLinks = CaptureExecutableLinks(capturedMounts, options, expectedLinks: null);
            var canonicalPaths = BuildCanonicalGuestPaths(requestedPaths, capturedMounts);
            var guestTmpfsMounts = capturedMounts
                .Where(static mount => mount.TmpfsSizeBytes.HasValue)
                .ToArray();
            return new IncusRecoveryAuthorization(
                bridge,
                capturedMounts,
                requestedPaths,
                canonicalPaths,
                capturedLinks,
                executableLinks,
                Array.AsReadOnly(guestTmpfsMounts));
        }
        catch
        {
            foreach (var retainedPin in retainedPins)
                retainedPin.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reconstructs authorization after a process restart and requires every
    /// current no-follow source pin to match the manifest's original inode.
    /// </summary>
    internal static IncusRecoveryAuthorization Restore(
        string? bridge,
        IReadOnlyList<IncusPreparedMount> mounts,
        IReadOnlyList<IncusFileIdentity?> expectedIdentities,
        IReadOnlyList<string> requestedGuestMountPaths,
        IReadOnlyList<IncusGuestLink> guestLinks,
        IReadOnlyList<IncusGuestLink> executableLinks,
        IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentities);
        if (expectedIdentities.Count != mounts.Count)
            throw new InvalidDataException("Incus recovery manifest mount identities are misaligned.");
        if (bridge is not null)
            IncusInputValidation.ValidateBridgeName(bridge, nameof(bridge));

        var retainedPins = new List<IncusPinnedDirectory>();
        try
        {
            var capturedMounts = CaptureMounts(mounts, options, retainedPins, expectedIdentities);
            var requestedPaths = CaptureRequestedPaths(requestedGuestMountPaths);
            var capturedLinks = CaptureGuestLinks(capturedMounts, guestLinks);
            var capturedExecutableLinks = CaptureExecutableLinks(
                capturedMounts,
                options,
                executableLinks);
            var canonicalPaths = BuildCanonicalGuestPaths(requestedPaths, capturedMounts);
            var guestTmpfsMounts = capturedMounts
                .Where(static mount => mount.TmpfsSizeBytes.HasValue)
                .ToArray();
            return new IncusRecoveryAuthorization(
                bridge,
                capturedMounts,
                requestedPaths,
                canonicalPaths,
                capturedLinks,
                capturedExecutableLinks,
                Array.AsReadOnly(guestTmpfsMounts));
        }
        catch
        {
            foreach (var retainedPin in retainedPins)
                retainedPin.Dispose();
            throw;
        }
    }

    internal void RevalidateForRestart(
        SandboxSpec spec,
        IncusSandboxOptions options,
        string stagingRoot)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        var expectedBridge = IncusSandboxProvider.ResolveBridge(options, spec.Network.ProfileName);
        if (!string.Equals(expectedBridge, Bridge, StringComparison.Ordinal))
            throw new InvalidOperationException("Incus recovery network authorization no longer matches the sandbox specification.");
        if (spec.Mounts.Count != _requestedGuestMountPaths.Count)
            throw new InvalidOperationException("Incus recovery guest mount authorization no longer matches the sandbox specification.");

        for (var index = 0; index < _requestedGuestMountPaths.Count; index++)
        {
            var requestedPath = _requestedGuestMountPaths[index];
            IncusInputValidation.ValidateAbsoluteGuestPath(requestedPath, nameof(spec));
            if (!string.Equals(spec.Mounts[index].SandboxPath, requestedPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Incus recovery guest mount authorization no longer matches the original sandbox path.");
            }
        }

        foreach (var mount in _mounts)
        {
            if (mount.HostSource is not { } source)
                continue;
            var authorizedSource = IncusMountStaging.ReauthorizeHostSource(options, stagingRoot, source);
            if (!string.Equals(authorizedSource, source, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "An Incus recovery host mount source changed canonical path after provisioning.");
            }
            var pinned = mount.PinnedHostDirectory
                ?? throw new InvalidOperationException("An Incus recovery host source has no retained identity pin.");
            IncusMountStaging.EnsurePinnedHostSourceMatches(authorizedSource, pinned);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var mount in _mounts)
            mount.PinnedHostDirectory?.Dispose();
    }

    private static IReadOnlyList<IncusPreparedMount> CaptureMounts(
        IReadOnlyList<IncusPreparedMount> mounts,
        IncusSandboxOptions options,
        List<IncusPinnedDirectory> retainedPins,
        IReadOnlyList<IncusFileIdentity?>? expectedIdentities)
    {
        var capturedMounts = new List<IncusPreparedMount>(
            Math.Clamp(mounts.Count, 0, IncusMountStaging.MaximumMounts));
        var guestPaths = new List<string>();
        var aggregateTmpfsBytes = 0L;
        foreach (var mount in mounts)
        {
            if (capturedMounts.Count >= IncusMountStaging.MaximumMounts)
                throw new ArgumentException("Incus recovery authorization exceeds the mount-count bound.", nameof(mounts));
            if (mount is null)
                throw new ArgumentException("Incus recovery authorization contains a null mount.", nameof(mounts));

            IncusInputValidation.ValidateAbsoluteGuestPath(mount.GuestPath, nameof(mounts));
            if (mount.HostSource is not null)
                IncusInputValidation.ValidateAbsoluteHostPath(mount.HostSource, nameof(mounts));

            var isGuestTmpfs = mount.TmpfsSizeBytes.HasValue;
            var isRootDiskDirectory = mount.RootDiskDirectory;
            var isHostDevice = mount.HostSource is not null;
            var shapeCount = (isGuestTmpfs ? 1 : 0)
                + (isRootDiskDirectory ? 1 : 0)
                + (isHostDevice ? 1 : 0);
            if (shapeCount != 1)
                throw new ArgumentException("Incus recovery authorization contains an invalid mount shape.", nameof(mounts));
            if (isGuestTmpfs)
            {
                if (mount.ReadOnly || mount.TmpfsSizeBytes is not { } sizeBytes
                    || sizeBytes < 1 || sizeBytes > options.MaxTmpfsDeviceBytes
                    || mount.ReadinessProbe is not null
                    || mount.PinnedHostDirectory is not null
                    || string.Equals(mount.GuestPath, SandboxConventions.WorkDir, StringComparison.Ordinal))
                {
                    throw new ArgumentException("Incus recovery authorization contains an invalid guest tmpfs descriptor.", nameof(mounts));
                }
                checked { aggregateTmpfsBytes += sizeBytes; }
                if (aggregateTmpfsBytes > options.MaxAggregateTmpfsBytes)
                    throw new ArgumentException("Incus recovery authorization tmpfs descriptors exceed their aggregate size bound.", nameof(mounts));
            }
            else if (isRootDiskDirectory
                && (mount.ReadOnly || mount.ReadinessProbe is not null || mount.PinnedHostDirectory is not null))
            {
                throw new ArgumentException("Incus recovery authorization contains an invalid root-disk mount descriptor.", nameof(mounts));
            }
            if (guestPaths.Any(existing => IncusGuestPaths.Overlap(existing, mount.GuestPath)))
                throw new ArgumentException("Incus recovery authorization contains overlapping prepared mount paths.", nameof(mounts));
            guestPaths.Add(mount.GuestPath);

            IncusPinnedDirectory? retainedPin = null;
            if (mount.HostSource is { } source)
            {
                if (mount.PinnedHostDirectory is { } provisioningPin)
                    IncusMountStaging.EnsurePinnedHostSourceMatches(source, provisioningPin);
                retainedPin = IncusSafeFile.PinDirectoryNoFollow(source);
                retainedPins.Add(retainedPin);
                var expectedIdentity = expectedIdentities?[capturedMounts.Count];
                if (expectedIdentities is not null
                    && (expectedIdentity is null || retainedPin.Identity != expectedIdentity.Value))
                {
                    throw new IOException("An Incus recovery host source no longer matches its durable inode identity.");
                }
            }
            else if (expectedIdentities?[capturedMounts.Count] is not null)
            {
                throw new InvalidDataException("Incus recovery manifest assigned a host identity to a guest-local mount.");
            }

            capturedMounts.Add(mount with
            {
                ReadinessProbe = mount.ReadinessProbe is null ? null : mount.ReadinessProbe with { },
                PinnedHostDirectory = retainedPin,
            });
        }
        return Array.AsReadOnly(capturedMounts.ToArray());
    }

    private static IReadOnlyList<string> CaptureRequestedPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count > IncusMountStaging.MaximumMounts)
            throw new ArgumentException("Incus requested recovery paths exceed the mount-count bound.", nameof(paths));
        var captured = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            IncusInputValidation.ValidateAbsoluteGuestPath(path, nameof(paths));
            captured.Add(path);
        }
        return Array.AsReadOnly(captured.ToArray());
    }

    private static IReadOnlyList<IncusGuestLink> CaptureGuestLinks(
        IReadOnlyList<IncusPreparedMount> mounts,
        IReadOnlyList<IncusGuestLink> links)
    {
        if (links.Count > IncusMountStaging.MaximumMounts)
            throw new ArgumentException("Incus recovery guest links exceed the mount-count bound.", nameof(links));
        var captured = links.Select(static link => link is null
            ? throw new ArgumentException("Incus recovery guest links contain null.", nameof(links))
            : link with { }).ToArray();
        IncusMountStaging.ValidateGuestLinks(mounts, captured);
        return Array.AsReadOnly(captured);
    }

    internal static IReadOnlyList<IncusGuestLink> SnapshotExecutableLinks(
        IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var maximum = checked(
            IncusSandboxOptions.MaximumExecutableProvisions
            * IncusSandboxOptions.MaximumExecutableSymlinks);
        var links = new List<IncusGuestLink>();
        var linkPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provision in options.ExecutableProvisions)
        {
            foreach (var linkPath in provision.VmSymlinks)
            {
                if (links.Count >= maximum)
                    throw new InvalidDataException("Incus executable-link authorization exceeds its collection bound.");
                IncusGuestPathAuthorization.EnsureProvisioningDestinationAllowed(provision.VmDestPath);
                IncusGuestPathAuthorization.EnsureProvisioningDestinationAllowed(linkPath);
                if (!linkPaths.Add(linkPath))
                    throw new InvalidDataException("Incus executable-link authorization contains a duplicate link path.");
                links.Add(new IncusGuestLink(provision.VmDestPath, linkPath));
            }
        }
        return Array.AsReadOnly(links.ToArray());
    }

    private static IReadOnlyList<IncusGuestLink> CaptureExecutableLinks(
        IReadOnlyList<IncusPreparedMount> mounts,
        IncusSandboxOptions options,
        IReadOnlyList<IncusGuestLink>? expectedLinks)
    {
        var links = expectedLinks is null
            ? SnapshotExecutableLinks(options)
            : CapturePersistedExecutableLinks(expectedLinks);
        foreach (var link in links)
        {
            if (mounts.Any(mount =>
                IncusGuestPaths.Overlap(link.Target, mount.GuestPath)
                || IncusGuestPaths.Overlap(link.LinkPath, mount.GuestPath)))
            {
                throw new InvalidDataException(
                    "Incus recovery executable-link authorization overlaps a sandbox mount path.");
            }
        }
        return links;
    }

    private static IReadOnlyList<IncusGuestLink> CapturePersistedExecutableLinks(
        IReadOnlyList<IncusGuestLink> links)
    {
        var maximum = checked(
            IncusSandboxOptions.MaximumExecutableProvisions
            * IncusSandboxOptions.MaximumExecutableSymlinks);
        if (links.Count > maximum)
            throw new InvalidDataException("Incus executable-link authorization exceeds its collection bound.");
        var captured = new List<IncusGuestLink>(links.Count);
        var linkPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (link is null)
                throw new InvalidDataException("Incus executable-link authorization contains a null link.");
            IncusGuestPathAuthorization.EnsureProvisioningDestinationAllowed(link.Target);
            IncusGuestPathAuthorization.EnsureProvisioningDestinationAllowed(link.LinkPath);
            if (!linkPaths.Add(link.LinkPath))
                throw new InvalidDataException("Incus executable-link authorization contains a duplicate link path.");
            captured.Add(link with { });
        }
        return Array.AsReadOnly(captured.ToArray());
    }
}
