using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.Incus;

internal sealed record IncusStagedExecutable(
    BaselineExecutableProvision Provision,
    string StagedPath,
    string ContentSha256);

/// <summary>
/// Raised when the per-staging-root coordination lease is momentarily held by
/// another concurrent provisioning/recovery pass. Distinct from a genuine I/O
/// fault so callers can treat opportunistic recovery contention as retryable
/// rather than fatal. Subclasses <see cref="IOException"/> to preserve the
/// existing broad contract.
/// </summary>
internal sealed class IncusProvisioningLeaseContendedException : IOException
{
    internal IncusProvisioningLeaseContendedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Private, bounded host-side inputs for one Incus provisioning pass. Executable
/// digests are computed while copying from no-follow descriptors, and the exact
/// staged files carrying those digests are the files later pushed to the VM.
/// </summary>
internal sealed class IncusProvisioningWorkspace : IDisposable
{
    // Incus instance names must start alphanumeric, so a leading dot makes
    // this destructive-recovery namespace impossible for sandbox staging
    // directories to enter regardless of InstanceNamePrefix truncation.
    internal const string DirectoryPrefix = ".codeybox-provision-";
    private const string OwnershipMarkerName = ".codeybox-incus-provision-v1";
    internal const string WorkspaceLeaseName = ".codeybox-incus-provision.lease";
    internal const string CoordinationLeaseName = ".codeybox-incus-provision-coordination.lease";
    private const int MaximumStagingRootEntries = 4096;
    private readonly string _stagingRoot;
    private readonly object _disposeGate = new();
    private FileStream? _lease;
    private string? _workspaceRoot;

    private IncusProvisioningWorkspace(
        string stagingRoot,
        string workspaceRoot,
        FileStream lease,
        IReadOnlyList<IncusStagedExecutable> executables)
    {
        _stagingRoot = stagingRoot;
        _workspaceRoot = workspaceRoot;
        _lease = lease;
        Executables = executables;
    }

    internal string Root => _workspaceRoot
        ?? throw new ObjectDisposedException(nameof(IncusProvisioningWorkspace));

    internal IReadOnlyList<IncusStagedExecutable> Executables { get; }

    internal IReadOnlyList<string> ExecutableContentSha256 =>
        Array.AsReadOnly(Executables.Select(static executable => executable.ContentSha256).ToArray());

    internal static IncusProvisioningWorkspace Create(
        IncusSandboxOptions options,
        string stagingRoot,
        Func<string, string?> environmentVariableReader,
        Func<Guid> newGuid,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        ArgumentNullException.ThrowIfNull(newGuid);
        ct.ThrowIfCancellationRequested();

        using var coordinationLease = AcquireCoordinationLease(stagingRoot);
        var workspaceName = $"{DirectoryPrefix}{newGuid():N}";
        var workspaceRoot = Path.Combine(stagingRoot, workspaceName);
        if (!IncusSafeFile.TryCreateDirectoryExclusive(workspaceRoot))
            throw new IOException("The private Incus provisioning workspace already exists.");

        FileStream? workspaceLease = null;
        Exception? primaryFailure = null;
        try
        {
            WriteOwnershipMarker(workspaceRoot, workspaceName);
            workspaceLease = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(
                Path.Combine(workspaceRoot, WorkspaceLeaseName));
            if (!IncusSafeFile.TryAcquireExclusiveLease(workspaceLease))
                throw new IOException("A newly-created Incus provisioning workspace lease is unexpectedly active.");
            var staged = new List<IncusStagedExecutable>(options.ExecutableProvisions.Count);
            var aggregateBytes = 0L;
            for (var i = 0; i < options.ExecutableProvisions.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var provision = options.ExecutableProvisions[i];
                var destination = Path.Combine(workspaceRoot, $"executable-{i:D3}");
                var digest = IncusBaselineProvisioning.CopyExecutableToPrivateStage(
                    provision.HostSourcePath,
                    destination,
                    options.MaxExecutableProvisionBytes,
                    options.MaxAggregateExecutableProvisionBytes,
                    ref aggregateBytes,
                    environmentVariableReader,
                    ct);
                staged.Add(new IncusStagedExecutable(provision, destination, digest));
            }
            var result = new IncusProvisioningWorkspace(
                stagingRoot,
                workspaceRoot,
                workspaceLease,
                Array.AsReadOnly(staged.ToArray()));
            workspaceLease = null;
            return result;
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
        finally
        {
            if (primaryFailure is not null)
            {
                Exception? cleanupFailure = null;
                try
                {
                    _ = TryDeleteVerifiedWorkspace(stagingRoot, workspaceRoot, workspaceLease);
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                workspaceLease?.Dispose();
                workspaceLease = null;
                if (cleanupFailure is not null)
                {
                    throw new AggregateException(
                        "Incus provisioning input staging failed and private workspace cleanup also failed.",
                        primaryFailure,
                        cleanupFailure);
                }
            }
            workspaceLease?.Dispose();
        }
    }

    internal string CreatePackageArchive(
        IncusSandboxOptions options,
        BaselinePackageCacheSeed seed,
        int index,
        Func<string, string?> environmentVariableReader,
        ref long aggregateBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        var perSeedLimit = IncusBaselineProvisioning.ResolvePackageSeedByteLimit(options, seed);
        var archivePath = Path.Combine(Root, $"package-cache-{index:D3}.tar");
        IncusBaselineProvisioning.CreatePackageArchive(
            seed.HostSourcePath,
            archivePath,
            perSeedLimit,
            options.MaxAggregatePackageCacheSeedBytes,
            options.MaxPackageCacheSeedEntries,
            ref aggregateBytes,
            environmentVariableReader,
            ct);
        return archivePath;
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            var root = _workspaceRoot;
            if (root is null)
                return;
            var lease = _lease
                ?? throw new InvalidOperationException("The Incus provisioning workspace lost its active lease.");
            if (!TryDeleteVerifiedWorkspace(_stagingRoot, root, lease))
                throw new IOException("The owned Incus provisioning workspace lease was not active during disposal.");
            _workspaceRoot = null;
            _lease = null;
            lease.Dispose();
        }
    }

    internal void ReleaseLeaseForRecovery()
    {
        lock (_disposeGate)
        {
            _lease?.Dispose();
            _lease = null;
            _workspaceRoot = null;
        }
    }

    internal static async Task<bool> RecoverStaleWorkspacesAsync(
        string stagingRoot,
        TimeSpan coordinationTimeout,
        TimeSpan coordinationPollInterval,
        CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (coordinationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(coordinationTimeout));
        if (coordinationPollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(coordinationPollInterval));
        timeProvider ??= TimeProvider.System;
        using var coordinationLease = await AcquireCoordinationLeaseAsync(
            stagingRoot,
            coordinationTimeout,
            coordinationPollInterval,
            timeProvider,
            ct).ConfigureAwait(false);
        var observed = 0;
        var allWorkspacesRecovered = true;
        foreach (var path in Directory.EnumerateFileSystemEntries(stagingRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (++observed > MaximumStagingRootEntries)
                throw new IOException("Incus staging root exceeds the 4096-entry recovery safety bound.");
            var name = Path.GetFileName(path);
            if (!name.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
                continue;
            if (!IsWorkspaceName(name))
            {
                throw new InvalidOperationException(
                    "Refusing Incus provisioning recovery because a deceptive reserved-prefix staging entry exists.");
            }
            if (!TryDeleteVerifiedWorkspace(stagingRoot, path, heldLease: null))
                allWorkspacesRecovered = false;
        }
        return allWorkspacesRecovered;
    }

    private static async Task<FileStream> AcquireCoordinationLeaseAsync(
        string stagingRoot,
        TimeSpan timeout,
        TimeSpan pollInterval,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var lease = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(
            Path.Combine(stagingRoot, CoordinationLeaseName));
        using var timeoutCancellation = new CancellationTokenSource(timeout, timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCancellation.Token);
        try
        {
            while (!IncusSafeFile.TryAcquireExclusiveLease(lease))
                await Task.Delay(pollInterval, timeProvider, deadline.Token).ConfigureAwait(false);
            return lease;
        }
        catch (OperationCanceledException ex) when (
            !ct.IsCancellationRequested && timeoutCancellation.IsCancellationRequested)
        {
            lease.Dispose();
            throw new IOException(
                "Another CodeyBox process is creating or recovering an Incus provisioning workspace; retry the operation.",
                new TimeoutException("The Incus provisioning coordination lease wait timed out.", ex));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static FileStream AcquireCoordinationLease(string stagingRoot)
    {
        var lease = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(
            Path.Combine(stagingRoot, CoordinationLeaseName));
        if (IncusSafeFile.TryAcquireExclusiveLease(lease))
            return lease;
        lease.Dispose();
        throw new IncusProvisioningLeaseContendedException(
            "Another CodeyBox process is creating or recovering an Incus provisioning workspace; retry the operation.");
    }

    private static void WriteOwnershipMarker(string workspaceRoot, string workspaceName)
    {
        var markerPath = Path.Combine(workspaceRoot, OwnershipMarkerName);
        using (var marker = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 256,
            FileOptions.WriteThrough))
        {
            marker.Write(Encoding.UTF8.GetBytes(workspaceName + "\n"));
        }
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(markerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool TryDeleteVerifiedWorkspace(
        string stagingRoot,
        string workspaceRoot,
        FileStream? heldLease)
    {
        if (!Directory.Exists(workspaceRoot) && !File.Exists(workspaceRoot))
            return true;
        var name = Path.GetFileName(workspaceRoot);
        if (!IsWorkspaceName(name))
            throw new InvalidOperationException("Refusing to delete an invalid Incus provisioning workspace name.");
        var attributes = File.GetAttributes(workspaceRoot);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw new InvalidOperationException(
                "Refusing to delete an Incus provisioning workspace that is not a real directory.");
        }
        using var pinned = IncusSafeFile.PinDirectoryNoFollow(workspaceRoot);
        var identity = IncusHostIdentity.GetEffectiveIdentity();
        var expectedMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (pinned.UserId != identity.UserId
            || pinned.GroupId != identity.GroupId
            || pinned.Mode != expectedMode)
        {
            throw new InvalidOperationException(
                "Refusing to delete an Incus provisioning workspace with foreign ownership or mode.");
        }
        // Validate without creating/chmodding a lease first. A reserved-prefix
        // directory with a foreign marker must remain completely untouched.
        ValidateCreationMarkerIfPresent(workspaceRoot, name);

        FileStream? acquiredLease = null;
        var lease = heldLease;
        if (lease is null)
        {
            acquiredLease = IncusSafeFile.OpenOrCreatePrivateLeaseNoFollow(
                Path.Combine(workspaceRoot, WorkspaceLeaseName));
            if (!IncusSafeFile.TryAcquireExclusiveLease(acquiredLease))
            {
                acquiredLease.Dispose();
                return false;
            }
            lease = acquiredLease;
        }
        try
        {
            _ = IncusSafeFile.GetRegularFileStatus(lease);
            // Recheck after acquiring the cross-process lease so marker changes
            // made before the lease became ours cannot authorize deletion.
            ValidateCreationMarkerIfPresent(workspaceRoot, name);
            IncusSafeFile.EnsurePinnedDirectoryMatches(workspaceRoot, pinned);
            IncusMountStaging.DeleteTreeIfContained(stagingRoot, workspaceRoot);
            return true;
        }
        finally
        {
            acquiredLease?.Dispose();
        }
    }

    private static void ValidateCreationMarkerIfPresent(string workspaceRoot, string name)
    {
        var markerPath = Path.Combine(workspaceRoot, OwnershipMarkerName);
        try
        {
            _ = File.GetAttributes(markerPath);
        }
        catch (FileNotFoundException)
        {
            // mkdir(0700) precedes marker creation. A process death in that
            // narrow window leaves no marker but the exact reserved name,
            // owner and mode still prove this provider-owned staging entry.
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        using (var marker = IncusSafeFile.OpenReadNoFollow(markerPath))
        {
            var status = IncusSafeFile.GetRegularFileStatus(marker);
            var identity = IncusHostIdentity.GetEffectiveIdentity();
            const UnixFileMode requiredMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            const UnixFileMode forbiddenMode =
                UnixFileMode.UserExecute |
                UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            var expectedPayload = name + "\n";
            var expectedLength = Encoding.UTF8.GetByteCount(expectedPayload);
            if (status.UserId != identity.UserId
                || status.GroupId != identity.GroupId
                || (status.Mode & requiredMode) != requiredMode
                || (status.Mode & forbiddenMode) != 0
                || marker.Length > expectedLength)
            {
                throw new InvalidOperationException(
                    "Refusing to delete an Incus provisioning workspace with an invalid ownership marker.");
            }
            using var reader = new StreamReader(
                marker,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 256,
                leaveOpen: false);
            var markerPayload = reader.ReadToEnd();
            if (!expectedPayload.StartsWith(markerPayload, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing to delete an Incus provisioning workspace with a mismatched ownership marker.");
            }
        }
    }

    private static bool IsWorkspaceName(string name)
    {
        if (name.Length != DirectoryPrefix.Length + 32
            || !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        return Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out var parsed)
            && string.Equals(
                parsed.ToString("N", CultureInfo.InvariantCulture),
                name[DirectoryPrefix.Length..],
                StringComparison.Ordinal);
    }
}

internal static class IncusBaselineProvisioning
{
    private const int CopyBufferBytes = 64 * 1024;
    private const int MaximumDirectoryDepth = 512;
    private const int MaximumArchivePathUtf8Bytes = 4096;
    private const int MaximumLinkTargetUtf8Bytes = 64 * 1024;

    internal static IReadOnlyList<string> FingerprintExecutables(
        IncusSandboxOptions options,
        Func<string, string?> environmentVariableReader,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        var result = new List<string>(options.ExecutableProvisions.Count);
        var aggregateBytes = 0L;
        foreach (var provision in options.ExecutableProvisions)
        {
            ct.ThrowIfCancellationRequested();
            using var source = OpenRegularFileNoFollow(
                ResolveHostSourcePath(provision.HostSourcePath, environmentVariableReader));
            result.Add(HashOpenedFileBounded(
                source,
                options.MaxExecutableProvisionBytes,
                options.MaxAggregateExecutableProvisionBytes,
                ref aggregateBytes,
                ct));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    internal static string ResolveHostSourcePath(
        string configuredPath,
        Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        _ = IncusInputValidation.GetBoundedUtf8ByteCount(
            configuredPath,
            IncusSandboxOptions.MaximumProvisioningTextUtf8Bytes,
            nameof(configuredPath),
            "Incus provisioning host source path");
        string expanded;
        if (string.Equals(configuredPath, "~", StringComparison.Ordinal)
            || configuredPath.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = environmentVariableReader("HOME");
            if (home is null || home.Length is < 1 or > IncusSandboxOptions.MaximumProvisioningTextUtf8Bytes)
                throw new InvalidOperationException("HOME must be a bounded absolute path to expand an Incus provisioning source.");
            _ = IncusInputValidation.GetBoundedUtf8ByteCount(
                home,
                IncusSandboxOptions.MaximumProvisioningTextUtf8Bytes,
                nameof(environmentVariableReader),
                "Incus provisioning HOME path");
            if (!Path.IsPathFullyQualified(home))
                throw new InvalidOperationException("HOME must be absolute to expand an Incus provisioning source.");
            expanded = configuredPath.Length == 1
                ? home
                : Path.Combine(home, configuredPath[2..]);
        }
        else
        {
            if (configuredPath.StartsWith('~'))
                throw new InvalidOperationException("Incus provisioning supports only '~' and '~/' HOME expansion.");
            expanded = configuredPath;
        }
        if (!Path.IsPathFullyQualified(expanded))
            throw new InvalidOperationException("Incus provisioning host source paths must be absolute or HOME-relative.");
        var fullPath = Path.GetFullPath(expanded);
        if (fullPath.Length > IncusSandboxOptions.MaximumProvisioningTextUtf8Bytes)
            throw new InvalidOperationException("The resolved Incus provisioning host source path exceeds 4096 characters.");
        return fullPath;
    }

    internal static long ResolvePackageSeedByteLimit(
        IncusSandboxOptions options,
        BaselinePackageCacheSeed seed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.MaxSizeMB is not { } maxSizeMb)
            return options.MaxPackageCacheSeedBytes;
        if (!double.IsFinite(maxSizeMb) || maxSizeMb <= 0)
            throw new InvalidOperationException("Package cache seed MaxSizeMB must be finite and greater than zero.");
        var bytes = Math.Ceiling(maxSizeMb * 1024d * 1024d);
        if (bytes > options.MaxPackageCacheSeedBytes || bytes > long.MaxValue)
            throw new InvalidOperationException("Package cache seed MaxSizeMB exceeds the configured per-seed byte limit.");
        return checked((long)bytes);
    }

    internal static string CopyExecutableToPrivateStage(
        string configuredPath,
        string destination,
        long perFileLimit,
        long aggregateLimit,
        ref long aggregateBytes,
        Func<string, string?> environmentVariableReader,
        CancellationToken ct)
    {
        var sourcePath = ResolveHostSourcePath(configuredPath, environmentVariableReader);
        using var source = OpenRegularFileNoFollow(sourcePath);
        EnsureLengthWithinBudget(source, perFileLimit, aggregateLimit, aggregateBytes, "executable provision");
        using var destinationStream = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        var fileBytes = 0L;
        while (true)
        {
            var remaining = Math.Min(perFileLimit - fileBytes, aggregateLimit - aggregateBytes);
            if (remaining < 0)
                throw new IOException("Executable provisioning exceeds its configured byte limit.");
            var request = (int)Math.Min(buffer.Length, remaining + 1);
            var read = ReadCancellable(source, buffer.AsSpan(0, request), ct);
            if (read == 0)
                break;
            if (read > remaining)
                throw new IOException("Executable provisioning exceeds its configured byte limit.");
            destinationStream.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            fileBytes += read;
            aggregateBytes += read;
        }
        destinationStream.Flush(flushToDisk: true);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void CreatePackageArchive(
        string configuredPath,
        string archivePath,
        long perSeedLimit,
        long aggregateLimit,
        int maximumEntries,
        ref long aggregateBytes,
        Func<string, string?> environmentVariableReader,
        CancellationToken ct)
    {
        var sourcePath = ResolveHostSourcePath(configuredPath, environmentVariableReader);
        using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.SequentialScan);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(archivePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        var state = new PackageArchiveState(
            perSeedLimit,
            aggregateLimit,
            maximumEntries,
            aggregateBytes,
            ct);

        try
        {
            if (Directory.Exists(sourcePath))
            {
                using var directory = IncusSafeFile.PinDirectoryNoFollow(sourcePath);
                WriteDirectoryChildren(writer, directory, prefix: string.Empty, depth: 0, state);
            }
            else
            {
                var name = Path.GetFileName(sourcePath);
                ValidateArchivePath(name);
                using var source = OpenRegularFileNoFollow(sourcePath);
                state.CountEntry();
                WriteRegularFile(writer, name, source, state);
            }
            aggregateBytes = state.AggregateBytes;
        }
        catch
        {
            aggregateBytes = state.AggregateBytes;
            throw;
        }
    }

    private static void WriteDirectoryChildren(
        TarWriter writer,
        IncusPinnedDirectory directory,
        string prefix,
        int depth,
        PackageArchiveState state)
    {
        if (depth > MaximumDirectoryDepth)
            throw new IOException("Package cache seed exceeds the 512-directory-depth safety bound.");
        foreach (var name in IncusSafeFile.EnumerateChildNames(directory))
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            state.CountEntry();
            var archivePath = prefix.Length == 0 ? name : $"{prefix}/{name}";
            ValidateArchivePath(archivePath);
            var metadata = IncusSafeFile.InspectChildNoFollow(directory, name);
            switch (metadata.Kind)
            {
                case IncusDirectoryEntryKind.Directory:
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, archivePath));
                    using (var child = IncusSafeFile.OpenChildDirectoryNoFollow(directory, name))
                    {
                        WriteDirectoryChildren(writer, child, archivePath, depth + 1, state);
                    }
                    break;
                case IncusDirectoryEntryKind.RegularFile:
                    using (var source = IncusSafeFile.OpenChildFileReadNoFollow(directory, name))
                    {
                        WriteRegularFile(writer, archivePath, source, state);
                    }
                    break;
                case IncusDirectoryEntryKind.SymbolicLink:
                    var target = IncusSafeFile.ReadChildSymbolicLinkNoFollow(directory, name);
                    _ = IncusInputValidation.GetBoundedUtf8ByteCount(
                        target,
                        MaximumLinkTargetUtf8Bytes,
                        nameof(target),
                        "Package cache symbolic-link target");
                    state.AddLogicalBytes(System.Text.Encoding.UTF8.GetByteCount(target));
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, archivePath)
                    {
                        LinkName = target,
                    });
                    break;
                default:
                    throw new IOException("Package cache seeds reject sockets, devices, FIFOs, and other special files.");
            }
        }
    }

    private static void WriteRegularFile(
        TarWriter writer,
        string archivePath,
        FileStream source,
        PackageArchiveState state)
    {
        state.EnsureLength(source.Length);
        using var bounded = new PackageBudgetReadStream(source, state);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, archivePath)
        {
            DataStream = bounded,
        });
    }

    private static void ValidateArchivePath(string path)
    {
        _ = IncusInputValidation.GetBoundedUtf8ByteCount(
            path,
            MaximumArchivePathUtf8Bytes,
            nameof(path),
            "Package cache archive path");
        if (path.Length == 0
            || path[0] == '/'
            || path.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            throw new IOException("Package cache seed produced an unsafe archive path.");
        }
    }

    private static FileStream OpenRegularFileNoFollow(string path)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Incus provisioning source file has no parent directory.");
        var name = Path.GetFileName(path);
        if (name.Length == 0 || !string.Equals(Path.Combine(parent, name), path, StringComparison.Ordinal))
            throw new IOException("Incus provisioning source file path is not canonical.");
        using var pinnedParent = IncusSafeFile.PinDirectoryNoFollow(parent);
        if (IncusSafeFile.InspectChildNoFollow(pinnedParent, name).Kind != IncusDirectoryEntryKind.RegularFile)
            throw new IOException("Incus executable and file cache sources must be regular files.");
        return IncusSafeFile.OpenChildFileReadNoFollow(pinnedParent, name);
    }

    private static string HashOpenedFileBounded(
        FileStream source,
        long perFileLimit,
        long aggregateLimit,
        ref long aggregateBytes,
        CancellationToken ct)
    {
        EnsureLengthWithinBudget(source, perFileLimit, aggregateLimit, aggregateBytes, "executable provision");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferBytes];
        var fileBytes = 0L;
        while (true)
        {
            var remaining = Math.Min(perFileLimit - fileBytes, aggregateLimit - aggregateBytes);
            if (remaining < 0)
                throw new IOException("Executable provisioning exceeds its configured byte limit.");
            var request = (int)Math.Min(buffer.Length, remaining + 1);
            var read = ReadCancellable(source, buffer.AsSpan(0, request), ct);
            if (read == 0)
                break;
            if (read > remaining)
                throw new IOException("Executable provisioning exceeds its configured byte limit.");
            hash.AppendData(buffer, 0, read);
            fileBytes += read;
            aggregateBytes += read;
        }
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static int ReadCancellable(
        Stream source,
        Span<byte> buffer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        return source.Read(buffer);
    }

    private static void EnsureLengthWithinBudget(
        FileStream source,
        long perFileLimit,
        long aggregateLimit,
        long aggregateBytes,
        string description)
    {
        if (source.Length > perFileLimit || source.Length > aggregateLimit - aggregateBytes)
            throw new IOException($"Incus {description} exceeds its configured byte limit.");
    }

    private sealed class PackageArchiveState
    {
        private readonly long _perSeedLimit;
        private readonly long _aggregateLimit;
        private readonly int _maximumEntries;
        private int _entries;
        private long _seedBytes;

        internal PackageArchiveState(
            long perSeedLimit,
            long aggregateLimit,
            int maximumEntries,
            long aggregateBytes,
            CancellationToken cancellationToken)
        {
            _perSeedLimit = perSeedLimit;
            _aggregateLimit = aggregateLimit;
            _maximumEntries = maximumEntries;
            AggregateBytes = aggregateBytes;
            CancellationToken = cancellationToken;
        }

        internal long AggregateBytes { get; private set; }
        internal CancellationToken CancellationToken { get; }

        internal void CountEntry()
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (++_entries > _maximumEntries)
                throw new IOException("Package cache seed exceeds its configured entry limit.");
        }

        internal void EnsureLength(long length)
        {
            if (length < 0
                || length > _perSeedLimit - _seedBytes
                || length > _aggregateLimit - AggregateBytes)
            {
                throw new IOException("Package cache seed exceeds its configured byte limit.");
            }
        }

        internal void AddLogicalBytes(int count)
        {
            if (count < 0
                || count > _perSeedLimit - _seedBytes
                || count > _aggregateLimit - AggregateBytes)
            {
                throw new IOException("Package cache seed exceeds its configured byte limit.");
            }
            _seedBytes += count;
            AggregateBytes += count;
        }

        internal int ReadBounded(Stream source, Span<byte> buffer)
        {
            var remaining = Math.Min(
                _perSeedLimit - _seedBytes,
                _aggregateLimit - AggregateBytes);
            if (remaining < 0)
                throw new IOException("Package cache seed exceeds its configured byte limit.");
            var request = (int)Math.Min(buffer.Length, remaining + 1);
            var read = ReadCancellable(source, buffer[..request], CancellationToken);
            if (read > remaining)
                throw new IOException("Package cache seed exceeds its configured byte limit.");
            _seedBytes += read;
            AggregateBytes += read;
            return read;
        }
    }

    private sealed class PackageBudgetReadStream : Stream
    {
        private readonly Stream _source;
        private readonly PackageArchiveState _state;

        internal PackageBudgetReadStream(Stream source, PackageArchiveState state)
        {
            _source = source;
            _state = state;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _source.Length;
        public override long Position
        {
            get => _source.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => _state.ReadBounded(_source, buffer);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns and disposes the pinned source descriptor.
            base.Dispose(disposing);
        }
    }
}
