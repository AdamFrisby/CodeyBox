using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeyBox.Core;

namespace CodeyBox.Sandbox.Incus;

internal sealed record IncusRecoveryPendingExec(
    string RunId,
    string EnvironmentPath,
    string PidPath,
    string CompletionPath,
    bool HostDevicesDetached);

internal sealed record IncusRecoveryManifestMount(
    string? HostSource,
    string GuestPath,
    bool ReadOnly,
    long? TmpfsSizeBytes,
    bool RootDiskDirectory,
    string? ReadinessRelativeFilePath,
    string? ReadinessExpectedSha256,
    uint? DeviceMajor,
    uint? DeviceMinor,
    ulong? Inode);

internal sealed record IncusRecoveryManifestGuestLink(string Target, string LinkPath);

internal sealed record IncusRecoveryManifest(
    int Version,
    string ProviderId,
    string SandboxId,
    string ProjectName,
    string StoragePoolName,
    string GuestHome,
    uint GuestUserId,
    uint GuestGroupId,
    string SpecSha256,
    string LeaseTokenSha256,
    string? BaselineRef,
    string? Bridge,
    bool Retained,
    IncusRecoveryPendingExec? PendingExec,
    IReadOnlyList<IncusRecoveryManifestMount> Mounts,
    IReadOnlyList<string> RequestedGuestMountPaths,
    IReadOnlyList<IncusRecoveryManifestGuestLink> GuestLinks,
    IReadOnlyList<IncusRecoveryManifestGuestLink> ExecutableLinks)
{
    internal const int CurrentVersion = 1;

    internal static IncusRecoveryManifest Create(
        string sandboxId,
        SandboxSpec spec,
        IncusSandboxOptions options,
        string leaseTokenSha256,
        string? baselineRef,
        IncusRecoveryAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authorization);
        return new IncusRecoveryManifest(
            CurrentVersion,
            IncusSandboxProvider.ProviderId,
            sandboxId,
            options.ProjectName,
            options.StoragePoolName,
            options.GuestHome,
            options.GuestUserId,
            options.GuestGroupId,
            IncusRecoveryManifestCodec.ComputeSpecSha256(spec),
            leaseTokenSha256,
            baselineRef,
            authorization.Bridge,
            Retained: false,
            PendingExec: null,
            authorization.Mounts.Select(static mount =>
            {
                var identity = mount.PinnedHostDirectory?.Identity;
                return new IncusRecoveryManifestMount(
                    mount.HostSource,
                    mount.GuestPath,
                    mount.ReadOnly,
                    mount.TmpfsSizeBytes,
                    mount.RootDiskDirectory,
                    mount.ReadinessProbe?.RelativeFilePath,
                    mount.ReadinessProbe?.ExpectedSha256,
                    identity?.DeviceMajor,
                    identity?.DeviceMinor,
                    identity?.Inode);
            }).ToArray(),
            authorization.RequestedGuestMountPaths.ToArray(),
            authorization.GuestLinks
                .Select(static link => new IncusRecoveryManifestGuestLink(link.Target, link.LinkPath))
                .ToArray(),
            authorization.ExecutableLinks
                .Select(static link => new IncusRecoveryManifestGuestLink(link.Target, link.LinkPath))
                .ToArray());
    }

    internal IncusRecoveryManifest Retain(IncusRecoveryPendingExec pending) => this with
    {
        Retained = true,
        PendingExec = pending,
    };

    internal IncusRecoveryAuthorization RestoreAuthorization(IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Mounts is null
            || RequestedGuestMountPaths is null
            || GuestLinks is null
            || ExecutableLinks is null)
            throw new InvalidDataException("Incus recovery manifest contains null authorization collections.");
        var maximumExecutableLinks = checked(
            IncusSandboxOptions.MaximumExecutableProvisions
            * IncusSandboxOptions.MaximumExecutableSymlinks);
        if (Mounts.Count > IncusMountStaging.MaximumMounts
            || RequestedGuestMountPaths.Count > IncusMountStaging.MaximumMounts
            || GuestLinks.Count > IncusMountStaging.MaximumMounts
            || ExecutableLinks.Count > maximumExecutableLinks)
        {
            throw new InvalidDataException("Incus recovery manifest authorization exceeds its collection bounds.");
        }

        var prepared = new List<IncusPreparedMount>(Mounts.Count);
        var identities = new List<IncusFileIdentity?>(Mounts.Count);
        foreach (var mount in Mounts)
        {
            if (mount is null)
                throw new InvalidDataException("Incus recovery manifest contains a null mount.");
            var hasAnyIdentity = mount.DeviceMajor.HasValue || mount.DeviceMinor.HasValue || mount.Inode.HasValue;
            var hasCompleteIdentity = mount.DeviceMajor.HasValue && mount.DeviceMinor.HasValue && mount.Inode.HasValue;
            if (hasAnyIdentity != hasCompleteIdentity
                || (mount.HostSource is null && hasCompleteIdentity)
                || (mount.HostSource is not null && !hasCompleteIdentity))
            {
                throw new InvalidDataException("Incus recovery manifest contains an invalid host mount identity.");
            }
            var hasAnyProbe = mount.ReadinessRelativeFilePath is not null
                || mount.ReadinessExpectedSha256 is not null;
            var hasCompleteProbe = mount.ReadinessRelativeFilePath is not null
                && mount.ReadinessExpectedSha256 is not null;
            if (hasAnyProbe != hasCompleteProbe)
                throw new InvalidDataException("Incus recovery manifest contains an incomplete mount readiness probe.");
            if (mount.ReadinessExpectedSha256 is { } probeHash)
                IncusRecoveryManifestCodec.ValidateHash(probeHash, "readiness hash");
            prepared.Add(new IncusPreparedMount(
                mount.HostSource,
                mount.GuestPath,
                mount.ReadOnly,
                mount.TmpfsSizeBytes,
                hasCompleteProbe
                    ? new IncusMountReadinessProbe(
                        mount.ReadinessRelativeFilePath!,
                        mount.ReadinessExpectedSha256!)
                    : null,
                PinnedHostDirectory: null,
                mount.RootDiskDirectory));
            identities.Add(hasCompleteIdentity
                ? new IncusFileIdentity(
                    mount.DeviceMajor!.Value,
                    mount.DeviceMinor!.Value,
                    mount.Inode!.Value)
                : null);
        }
        var links = GuestLinks.Select(static link => link is null
            ? throw new InvalidDataException("Incus recovery manifest contains a null guest link.")
            : new IncusGuestLink(link.Target, link.LinkPath)).ToArray();
        var executableLinks = ExecutableLinks.Select(static link => link is null
            ? throw new InvalidDataException("Incus recovery manifest contains a null executable link.")
            : new IncusGuestLink(link.Target, link.LinkPath)).ToArray();
        return IncusRecoveryAuthorization.Restore(
            Bridge,
            prepared,
            identities,
            RequestedGuestMountPaths,
            links,
            executableLinks,
            options);
    }

    internal void ValidatePendingExec()
    {
        if (!Retained || PendingExec is null)
            throw new InvalidDataException("Incus recovery manifest is not committed as retained.");
        var pending = PendingExec;
        if (!Guid.TryParseExact(pending.RunId, "N", out var runId) || runId == Guid.Empty)
            throw new InvalidDataException("Incus recovery manifest contains an invalid pending run id.");
        if (!string.Equals(
                pending.EnvironmentPath,
                $"{IncusCloudInit.ControlDirectory}/env-{pending.RunId}",
                StringComparison.Ordinal)
            || !string.Equals(
                pending.PidPath,
                $"{IncusCloudInit.ControlDirectory}/pid-{pending.RunId}",
                StringComparison.Ordinal)
            || !string.Equals(
                pending.CompletionPath,
                $"{IncusCloudInit.ControlDirectory}/complete-{pending.RunId}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Incus recovery manifest pending control paths do not match its run id.");
        }
    }
}

internal static class IncusRecoveryManifestCodec
{
    internal const int MaximumManifestBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    internal static byte[] Serialize(IncusRecoveryManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (bytes.Length is < 1 or > MaximumManifestBytes)
            throw new InvalidOperationException("Incus recovery manifest exceeds its serialized size bound.");
        return bytes;
    }

    internal static IncusRecoveryManifest Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumManifestBytes)
            throw new InvalidDataException("Incus recovery manifest size is outside the accepted bound.");
        try
        {
            return JsonSerializer.Deserialize<IncusRecoveryManifest>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Incus recovery manifest cannot be null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Incus recovery manifest is malformed.", ex);
        }
    }

    internal static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static string ComputeTokenSha256(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return ComputeSha256(Encoding.UTF8.GetBytes(token));
    }

    internal static string ComputeSpecSha256(SandboxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var canonical = new
        {
            version = 1,
            spec.ImageReference,
            mounts = spec.Mounts.Select(static mount => new
            {
                mount.SandboxPath,
                mount.HostPath,
                mount.ReadOnly,
                mount.Tmpfs,
                mount.SizeBytes,
                mount.SnapshotForIsolation,
            }).ToArray(),
            // Runtime environment (including rotating credentials and timing
            // stamps) is intentionally excluded. Adoption first deletes the
            // old private control file, and the replayed exec supplies the
            // current dispatch environment. The immutable VM/worktree and
            // work-item authorization surfaces below remain bound exactly.
            limits = new
            {
                spec.Limits.CpuCount,
                spec.Limits.MemoryBytes,
                spec.Limits.DiskBytes,
                wallClockTicks = spec.Limits.WallClock?.Ticks,
            },
            network = new
            {
                allowedHosts = spec.Network.AllowedHosts.ToArray(),
                spec.Network.HostGitEndpoint,
                spec.Network.ProfileName,
            },
            flavor = (int)spec.Flavor,
            spec.WorkingDirectory,
            timingWorkItemId = spec.TimingWorkItemId?.ToString(),
            spec.TimingPhase,
            spec.BaselineImageRef,
        };
        return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical));
    }

    internal static bool FixedTimeEqualsHash(string actual, string expected)
    {
        if (!TryDecodeHash(actual, out var actualBytes)
            || !TryDecodeHash(expected, out var expectedBytes))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    internal static void ValidateHash(string value, string fieldName)
    {
        if (!TryDecodeHash(value, out _))
            throw new InvalidDataException($"Incus recovery manifest {fieldName} is not a canonical SHA-256 value.");
    }

    private static bool TryDecodeHash(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != 64 || value.Any(static c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class IncusRecoveryManifestStore : IDisposable
{
    private const string LeaseFileName = ".codeybox-recovery.lock";
    private const string ManifestPrefix = ".codeybox-recovery-";
    private const string RetainedManifestName = ".codeybox-recovery-retained.json";
    private readonly IncusPinnedDirectory _sandboxRoot;
    private readonly FileStream _lease;
    private int _disposed;

    private IncusRecoveryManifestStore(IncusPinnedDirectory sandboxRoot, FileStream lease)
    {
        _sandboxRoot = sandboxRoot;
        _lease = lease;
    }

    internal static IncusRecoveryManifestStore Acquire(string sandboxRoot)
    {
        var pinnedRoot = IncusSafeFile.PinDirectoryNoFollow(sandboxRoot);
        FileStream? lease = null;
        try
        {
            lease = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(Path.Combine(sandboxRoot, LeaseFileName));
            IncusMountStaging.EnsurePinnedHostSourceMatches(sandboxRoot, pinnedRoot);
            if (!IncusSafeFile.TryAcquireExclusiveLease(lease))
                throw new InvalidOperationException("Incus retained sandbox is already owned by another process.");
            var result = new IncusRecoveryManifestStore(pinnedRoot, lease);
            lease = null;
            pinnedRoot = null!;
            return result;
        }
        finally
        {
            lease?.Dispose();
            pinnedRoot?.Dispose();
        }
    }

    internal string Write(IncusRecoveryManifest manifest, Guid nonce)
    {
        ThrowIfDisposed();
        if (nonce == Guid.Empty)
            throw new InvalidOperationException("Incus recovery manifest nonce cannot be empty.");
        var bytes = IncusRecoveryManifestCodec.Serialize(manifest);
        var hash = IncusRecoveryManifestCodec.ComputeSha256(bytes);
        var finalName = ManifestName(hash);
        var temporaryName = $"{ManifestPrefix}tmp-{nonce:N}";
        try
        {
            using (var stream = IncusSafeFile.CreatePrivateChildFileNoFollow(_sandboxRoot, temporaryName))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            IncusSafeFile.ReplaceChildFileAtomically(_sandboxRoot, temporaryName, finalName);
        }
        catch
        {
            IncusSafeFile.DeleteChildFileNoFollow(_sandboxRoot, temporaryName);
            throw;
        }
        return hash;
    }

    internal void WriteRetained(IncusRecoveryManifest manifest, Guid nonce)
    {
        if (!manifest.Retained || manifest.PendingExec is null)
            throw new InvalidOperationException("Only a committed retained manifest can be published as recovery state.");
        WriteNamed(IncusRecoveryManifestCodec.Serialize(manifest), RetainedManifestName, nonce);
    }

    internal IncusRecoveryManifest Read(string manifestHash)
    {
        ThrowIfDisposed();
        IncusRecoveryManifestCodec.ValidateHash(manifestHash, nameof(manifestHash));
        using var stream = IncusSafeFile.OpenChildFileReadNoFollow(_sandboxRoot, ManifestName(manifestHash));
        var status = IncusSafeFile.GetRegularFileStatus(stream);
        var identity = IncusHostIdentity.GetEffectiveIdentity();
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (status.UserId != identity.UserId
            || status.GroupId != identity.GroupId
            || status.Mode != expectedMode
            || stream.Length is < 1 or > IncusRecoveryManifestCodec.MaximumManifestBytes)
        {
            throw new InvalidDataException("Incus recovery manifest has unsafe ownership, mode, or size.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new EndOfStreamException("Incus recovery manifest ended before its declared length.");
            offset += read;
        }
        var actualHash = IncusRecoveryManifestCodec.ComputeSha256(bytes);
        if (!IncusRecoveryManifestCodec.FixedTimeEqualsHash(actualHash, manifestHash))
            throw new InvalidDataException("Incus recovery manifest content hash does not match its VM binding.");
        return IncusRecoveryManifestCodec.Deserialize(bytes);
    }

    internal IncusRecoveryManifest ReadRetained()
    {
        ThrowIfDisposed();
        return IncusRecoveryManifestCodec.Deserialize(ReadVerifiedFile(RetainedManifestName, expectedHash: null));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lease.Dispose();
        _sandboxRoot.Dispose();
    }

    private static string ManifestName(string hash) => $"{ManifestPrefix}{hash}.json";

    private void WriteNamed(byte[] bytes, string finalName, Guid nonce)
    {
        ThrowIfDisposed();
        if (nonce == Guid.Empty)
            throw new InvalidOperationException("Incus recovery manifest nonce cannot be empty.");
        var temporaryName = $"{ManifestPrefix}tmp-{nonce:N}";
        try
        {
            using (var stream = IncusSafeFile.CreatePrivateChildFileNoFollow(_sandboxRoot, temporaryName))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            IncusSafeFile.ReplaceChildFileAtomically(_sandboxRoot, temporaryName, finalName);
        }
        catch
        {
            IncusSafeFile.DeleteChildFileNoFollow(_sandboxRoot, temporaryName);
            throw;
        }
    }

    private byte[] ReadVerifiedFile(string fileName, string? expectedHash)
    {
        using var stream = IncusSafeFile.OpenChildFileReadNoFollow(_sandboxRoot, fileName);
        var status = IncusSafeFile.GetRegularFileStatus(stream);
        var identity = IncusHostIdentity.GetEffectiveIdentity();
        var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (status.UserId != identity.UserId
            || status.GroupId != identity.GroupId
            || status.Mode != expectedMode
            || stream.Length is < 1 or > IncusRecoveryManifestCodec.MaximumManifestBytes)
        {
            throw new InvalidDataException("Incus recovery manifest has unsafe ownership, mode, or size.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                throw new EndOfStreamException("Incus recovery manifest ended before its declared length.");
            offset += read;
        }
        if (expectedHash is not null)
        {
            var actualHash = IncusRecoveryManifestCodec.ComputeSha256(bytes);
            if (!IncusRecoveryManifestCodec.FixedTimeEqualsHash(actualHash, expectedHash))
                throw new InvalidDataException("Incus recovery manifest content hash does not match its VM binding.");
        }
        return bytes;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
