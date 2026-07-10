using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.Incus;

internal sealed record IncusPreparedMount(
    string? HostSource,
    string GuestPath,
    bool ReadOnly,
    long? TmpfsSizeBytes = null,
    IncusMountReadinessProbe? ReadinessProbe = null,
    IncusPinnedDirectory? PinnedHostDirectory = null);

internal sealed record IncusMountReadinessProbe(string RelativeFilePath, string ExpectedSha256);

internal sealed record IncusGuestLink(string Target, string LinkPath);

internal sealed class IncusMountPlan : IDisposable
{
    private IReadOnlyList<IncusPinnedDirectory>? _pinnedDirectories;

    internal IncusMountPlan(
        IReadOnlyList<IncusPreparedMount> mounts,
        IReadOnlyList<IncusGuestLink> guestLinks,
        IReadOnlyList<IncusPinnedDirectory> pinnedDirectories)
    {
        Mounts = mounts;
        GuestLinks = guestLinks;
        _pinnedDirectories = pinnedDirectories;
    }

    internal IReadOnlyList<IncusPreparedMount> Mounts { get; }
    internal IReadOnlyList<IncusGuestLink> GuestLinks { get; }

    public void Dispose()
    {
        var pinnedDirectories = Interlocked.Exchange(ref _pinnedDirectories, null);
        if (pinnedDirectories is null)
            return;
        foreach (var pinnedDirectory in pinnedDirectories)
            pinnedDirectory.Dispose();
    }
}

internal sealed record IncusOwnedStagingTree(string Name, DateTimeOffset CreatedAt);

internal static class IncusMountStaging
{
    private const string OwnershipMarkerName = ".codeybox-incus-owner";
    private const string StagingRootMarkerName = ".codeybox-incus-staging-v1";
    private const string StagingRootMarkerPayload = "codeybox-incus-staging-v1\n";

    internal static void EnsureOwnedStagingRoot(string stagingRoot)
    {
        var fullPath = Path.GetFullPath(stagingRoot);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Incus staging root has no parent directory.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The parent of Incus StagingDirectory must already exist.");
        var canonicalParent = ResolveExistingRealPath(parent);
        if (!string.Equals(canonicalParent, parent, StringComparison.Ordinal))
            throw new InvalidOperationException("Incus staging-root parent must not traverse symbolic links.");
        var created = IncusSafeFile.TryCreateDirectoryExclusive(fullPath);
        if (created)
            WriteStagingRootMarker(fullPath);

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Incus staging root cannot be a symbolic link or reparse point.");
        var canonical = ResolveExistingRealPath(fullPath);
        if (!string.Equals(canonical, fullPath, StringComparison.Ordinal))
            throw new InvalidOperationException("Incus staging root must not traverse symbolic links.");

        using var pinned = IncusSafeFile.PinDirectoryNoFollow(fullPath);
        var identity = IncusHostIdentity.GetEffectiveIdentity();
        var expectedDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (pinned.UserId != identity.UserId
            || pinned.GroupId != identity.GroupId
            || pinned.Mode != expectedDirectoryMode)
        {
            throw new InvalidOperationException(
                "Incus staging root must be owned by the provider identity with exact mode 0700.");
        }

        var markerPath = Path.Combine(fullPath, StagingRootMarkerName);
        using var marker = IncusSafeFile.OpenReadNoFollow(markerPath);
        var markerStatus = IncusSafeFile.GetRegularFileStatus(marker);
        var expectedMarkerMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var expectedMarkerBytes = Encoding.UTF8.GetByteCount(StagingRootMarkerPayload);
        if (markerStatus.UserId != identity.UserId
            || markerStatus.GroupId != identity.GroupId
            || markerStatus.Mode != expectedMarkerMode
            || marker.Length != expectedMarkerBytes)
        {
            throw new InvalidOperationException("Incus staging root ownership marker is invalid.");
        }
        using var reader = new StreamReader(
            marker,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 256,
            leaveOpen: false);
        if (!string.Equals(reader.ReadToEnd(), StagingRootMarkerPayload, StringComparison.Ordinal))
            throw new InvalidOperationException("Incus staging root ownership marker does not match CodeyBox.");
    }

    private static void WriteStagingRootMarker(string stagingRoot)
    {
        var markerPath = Path.Combine(stagingRoot, StagingRootMarkerName);
        using (var marker = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 256,
            FileOptions.WriteThrough))
        {
            marker.Write(Encoding.UTF8.GetBytes(StagingRootMarkerPayload));
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(markerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static void InitializeOwnedTree(
        string sandboxRoot,
        string sandboxName,
        DateTimeOffset createdAt)
    {
        IncusInputValidation.ValidateInstanceName(sandboxName, nameof(sandboxName));
        var marker = Path.Combine(sandboxRoot, OwnershipMarkerName);
        using (var stream = new FileStream(
            marker,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 256,
            FileOptions.WriteThrough))
        {
            var payload = Encoding.UTF8.GetBytes(
                sandboxName + "\n" + createdAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\n");
            stream.Write(payload);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static IReadOnlyList<IncusOwnedStagingTree> EnumerateOwnedTrees(string stagingRoot)
    {
        const int maximumEntries = 4096;
        var result = new List<IncusOwnedStagingTree>();
        var entries = 0;
        foreach (var path in Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (++entries > maximumEntries)
                throw new IOException("Incus staging root exceeds the 4096-entry inventory safety bound.");
            var name = Path.GetFileName(path);
            try
            {
                IncusInputValidation.ValidateInstanceName(name, nameof(stagingRoot));
                if (ReadOwnershipMarker(path, name) is { } owned)
                    result.Add(owned);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // Unknown or concurrently changing entries are not attributed
                // to the provider and can never reach its destructive sink.
            }
        }
        return result;
    }

    internal static void DeleteOwnedTreeIfContained(string root, string candidate, string sandboxName)
    {
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
            return;
        if (ReadOwnershipMarker(candidate, sandboxName) is null)
            throw new InvalidOperationException("Refusing to delete an Incus staging tree without its exact ownership marker.");
        DeleteTreeIfContained(root, candidate);
    }

    private static IncusOwnedStagingTree? ReadOwnershipMarker(string sandboxRoot, string sandboxName)
    {
        IncusInputValidation.ValidateInstanceName(sandboxName, nameof(sandboxName));
        var marker = Path.Combine(sandboxRoot, OwnershipMarkerName);
        using var stream = IncusSafeFile.OpenReadNoFollow(marker);
        if (stream.Length is < 1 or > 128)
            return null;
        var bytes = new byte[checked((int)stream.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
                return null;
            offset += read;
        }
        var fields = Encoding.UTF8.GetString(bytes).Split('\n');
        if (fields.Length != 3
            || fields[2].Length != 0
            || !string.Equals(fields[0], sandboxName, StringComparison.Ordinal)
            || !DateTimeOffset.TryParseExact(
                fields[1],
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var createdAt))
        {
            return null;
        }
        return new IncusOwnedStagingTree(sandboxName, createdAt);
    }

    internal static IncusMountPlan Prepare(
        IncusSandboxOptions options,
        string stagingRoot,
        string sandboxRoot,
        IReadOnlyList<SandboxMount> mounts,
        long defaultTmpfsSizeBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mounts);
        if (mounts.Count > 256)
            throw new InvalidOperationException("An Incus sandbox cannot have more than 256 mounts.");
        var prepared = new List<IncusPreparedMount>();
        var links = new List<IncusGuestLink>();
        var fileGroups = new Dictionary<string, List<(SandboxMount Mount, string Source)>>(StringComparer.Ordinal);
        var guestPaths = new HashSet<string>(StringComparer.Ordinal);
        var pinnedDirectories = new List<IncusPinnedDirectory>();
        var aggregateTmpfsBytes = 0L;
        var aggregateStagedBytes = 0L;
        var aggregatePathBytes = 0L;
        ct.ThrowIfCancellationRequested();

        try
        {
            for (var index = 0; index < mounts.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var mount = mounts[index];
                IncusInputValidation.ValidateAbsoluteGuestPath(mount.SandboxPath, nameof(mounts));
                if (mount.SnapshotForIsolation && !mount.ReadOnly)
                    throw new InvalidOperationException("SnapshotForIsolation requires a read-only sandbox mount.");
                if (mount.SandboxPath == SandboxConventions.CredentialsDir && !mount.Tmpfs)
                    throw new InvalidOperationException("The reserved credentials mount must be a guest tmpfs.");
                aggregatePathBytes += Encoding.UTF8.GetByteCount(mount.SandboxPath);
                if (mount.HostPath is not null)
                    aggregatePathBytes += Encoding.UTF8.GetByteCount(mount.HostPath);
                if (aggregatePathBytes > 1024 * 1024)
                    throw new InvalidOperationException("Sandbox mount paths exceed the 1 MiB aggregate bound.");
                if (!guestPaths.Add(mount.SandboxPath))
                    throw new InvalidOperationException($"Duplicate sandbox mount path '{mount.SandboxPath}'.");

                if (mount.Tmpfs)
                {
                    ValidateAuthorizedMountGuestPath(
                        mount,
                        authorizedExistingHostSource: null,
                        hostSourceIsDirectory: false);
                    if (mount.HostPath is not null)
                        throw new InvalidOperationException($"Tmpfs mount '{mount.SandboxPath}' cannot also have a HostPath.");
                    var size = mount.SizeBytes ?? defaultTmpfsSizeBytes;
                    if (size <= 0)
                        throw new InvalidOperationException($"Tmpfs mount '{mount.SandboxPath}' must have a positive size.");
                    if (size > options.MaxTmpfsDeviceBytes)
                        throw new InvalidOperationException($"Tmpfs mount '{mount.SandboxPath}' exceeds the configured per-device limit.");
                    checked { aggregateTmpfsBytes += size; }
                    if (aggregateTmpfsBytes > options.MaxAggregateTmpfsBytes)
                        throw new InvalidOperationException("Sandbox tmpfs mounts exceed the configured aggregate limit.");
                    prepared.Add(new IncusPreparedMount(null, mount.SandboxPath, ReadOnly: false, size));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(mount.HostPath))
                {
                    ValidateAuthorizedMountGuestPath(
                        mount,
                        authorizedExistingHostSource: null,
                        hostSourceIsDirectory: false);
                    throw new InvalidOperationException($"Mount '{mount.SandboxPath}' has neither Tmpfs nor HostPath.");
                }
                IncusInputValidation.ValidateAbsoluteHostPath(mount.HostPath, nameof(mounts));
                var sourcePath = Path.GetFullPath(mount.HostPath);
                if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath))
                    throw new SandboxMountSourceMissingException(
                        sourcePath,
                        $"Mount source for '{mount.SandboxPath}' does not exist.");
                sourcePath = AuthorizeHostSource(options, stagingRoot, sourcePath);
                var canonicalStagingRoot = ResolveExistingRealPath(stagingRoot);
                if (IsContained(sourcePath, canonicalStagingRoot)
                    || IsContained(canonicalStagingRoot, sourcePath))
                {
                    throw new UnauthorizedAccessException(
                        "Caller-supplied host mounts cannot expose the Incus provider's private staging tree.");
                }
                var sourceIsDirectory = Directory.Exists(sourcePath);
                var sourceIsFile = File.Exists(sourcePath);
                if (!sourceIsDirectory && !sourceIsFile)
                    throw new SandboxMountSourceMissingException(
                        sourcePath,
                        $"Mount source for '{mount.SandboxPath}' disappeared during authorization.");
                ValidateAuthorizedMountGuestPath(mount, sourcePath, sourceIsDirectory);

                if (sourceIsFile)
                {
                    if (!mount.ReadOnly)
                    {
                        throw new NotSupportedException(
                            $"Incus VMs cannot safely expose writable individual host files via virtiofs ('{mount.SandboxPath}'). Mount a private directory instead.");
                    }
                    var guestParent = GetGuestParent(mount.SandboxPath);
                    if (!fileGroups.TryGetValue(guestParent, out var group))
                    {
                        group = [];
                        fileGroups.Add(guestParent, group);
                    }
                    group.Add((mount, sourcePath));
                    continue;
                }

                var effectiveSource = sourcePath;
                if (mount.SnapshotForIsolation)
                {
                    effectiveSource = Path.Combine(sandboxRoot, "snapshots", index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                    CopyDirectoryBounded(
                        sourcePath,
                        effectiveSource,
                        options.MaxSnapshotBytes,
                        options.MaxSnapshotEntries,
                        ref aggregateStagedBytes,
                        ct);
                }
                IncusPinnedDirectory? pinnedDirectory = null;
                if (!mount.SnapshotForIsolation)
                {
                    pinnedDirectory = IncusSafeFile.PinDirectoryNoFollow(sourcePath);
                    pinnedDirectories.Add(pinnedDirectory);
                }
                prepared.Add(new IncusPreparedMount(
                    effectiveSource,
                    mount.SandboxPath,
                    mount.ReadOnly,
                    PinnedHostDirectory: pinnedDirectory));
            }

            foreach (var (guestParent, group) in fileGroups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var groupHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(guestParent)))[..12];
                var stage = Path.Combine(sandboxRoot, "file-mounts", groupHash);
                var privateGuestDirectory = $"{IncusCloudInit.RuntimeDirectory}/file-mounts/{groupHash}";
                Directory.CreateDirectory(stage);
                SetPrivateMode(stage);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (mount, source) in group)
                {
                    var fileName = Path.GetFileName(mount.SandboxPath);
                    if (!names.Add(fileName))
                        throw new InvalidOperationException($"Duplicate individual-file mount '{mount.SandboxPath}'.");
                    CopyFileBounded(
                        source,
                        Path.Combine(stage, fileName),
                        options.MaxSnapshotBytes,
                        ref aggregateStagedBytes,
                        ct);
                    links.Add(new IncusGuestLink(
                        $"{privateGuestDirectory}/{fileName}",
                        mount.SandboxPath));
                }
                prepared.Add(new IncusPreparedMount(stage, privateGuestDirectory, ReadOnly: true));
            }

            ValidateNoOverlappingDevicePaths(prepared);
            ValidateGuestLinks(prepared, links);
            var probed = new List<IncusPreparedMount>(prepared.Count);
            foreach (var mount in prepared)
            {
                if (mount.HostSource is null)
                {
                    probed.Add(mount);
                    continue;
                }
                probed.Add(mount.ReadinessProbe is not null
                    ? mount
                    : mount with
                    {
                        ReadinessProbe = FindReadinessProbe(
                            mount.HostSource,
                            options.MaxReadinessProbeEntries,
                            ct),
                    });
            }
            var plan = new IncusMountPlan(probed, links, pinnedDirectories.ToArray());
            pinnedDirectories.Clear();
            return plan;
        }
        catch
        {
            foreach (var pinnedDirectory in pinnedDirectories)
                pinnedDirectory.Dispose();
            throw;
        }
    }

    internal static string ReauthorizeHostSource(
        IncusSandboxOptions options,
        string stagingRoot,
        string source) => AuthorizeHostSource(options, stagingRoot, source);

    internal static void EnsurePinnedHostSourceMatches(
        string source,
        IncusPinnedDirectory pinnedDirectory) =>
        IncusSafeFile.EnsurePinnedDirectoryMatches(source, pinnedDirectory);

    internal static void DeleteTreeIfContained(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (string.Equals(fullCandidate, fullRoot, StringComparison.Ordinal)
            || !IsContained(fullCandidate, fullRoot))
            throw new InvalidOperationException("Refusing to delete a staging path outside the configured root.");
        if (!Directory.Exists(fullCandidate) && !File.Exists(fullCandidate))
            return;
        var candidateAttributes = File.GetAttributes(fullCandidate);
        if ((candidateAttributes & FileAttributes.ReparsePoint) != 0)
        {
            File.Delete(fullCandidate);
            return;
        }
        var canonicalRoot = ResolveExistingRealPath(fullRoot);
        var canonicalCandidate = ResolveExistingRealPath(fullCandidate);
        if (!string.Equals(canonicalCandidate, fullCandidate, StringComparison.Ordinal)
            || !IsContained(canonicalCandidate, canonicalRoot))
            throw new InvalidOperationException("Refusing to delete a staging tree reached through symbolic links.");
        DeleteDirectoryNoFollow(canonicalCandidate, canonicalRoot);
    }

    private static void DeleteDirectoryNoFollow(string directory, string canonicalRoot)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var fullEntry = Path.GetFullPath(entry);
            if (!IsContained(fullEntry, canonicalRoot))
                throw new InvalidOperationException("Refusing to delete a staging entry outside its canonical root.");
            var attributes = File.GetAttributes(fullEntry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                File.Delete(fullEntry);
            }
            else if ((attributes & FileAttributes.Directory) != 0)
            {
                var canonicalEntry = ResolveExistingRealPath(fullEntry);
                if (!string.Equals(canonicalEntry, fullEntry, StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing to follow a staging-directory link during deletion.");
                DeleteDirectoryNoFollow(canonicalEntry, canonicalRoot);
            }
            else
            {
                File.Delete(fullEntry);
            }
        }
        Directory.Delete(directory);
    }

    internal static void SetPrivateMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void CopyDirectoryBounded(
        string source,
        string destination,
        long maxBytes,
        int maxEntries,
        ref long aggregateBytes,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        SetPrivateMode(destination);
        using var pinnedRoot = IncusSafeFile.PinDirectoryNoFollow(source);
        var entries = 0;
        CopyPinnedDirectoryBounded(
            pinnedRoot,
            destination,
            maxBytes,
            maxEntries,
            ref aggregateBytes,
            ref entries,
            depth: 0,
            ct);
        // Apply source directory modes only after all descendants exist. A
        // legitimate 0555/0500 source tree must not make its staged copy
        // unwritable halfway through construction.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, pinnedRoot.Mode);
        }
    }

    private static void CopyPinnedDirectoryBounded(
        IncusPinnedDirectory source,
        string destination,
        long maxBytes,
        int maxEntries,
        ref long aggregateBytes,
        ref int entries,
        int depth,
        CancellationToken ct)
    {
        const int maxDepth = 512;
        if (depth > maxDepth)
            throw new IOException("Isolation snapshot exceeds the 512-directory-depth safety bound.");

        foreach (var name in IncusSafeFile.EnumerateChildNames(source))
        {
            ct.ThrowIfCancellationRequested();
            if (++entries > maxEntries)
                throw new IOException($"Isolation snapshot exceeds the configured {maxEntries}-entry safety bound.");
            var target = Path.Combine(destination, name);
            var metadata = IncusSafeFile.InspectChildNoFollow(source, name);
            switch (metadata.Kind)
            {
                case IncusDirectoryEntryKind.SymbolicLink:
                    CopySymbolicLinkBounded(
                        target,
                        IncusSafeFile.ReadChildSymbolicLinkNoFollow(source, name),
                        isDirectoryLink: false,
                        maxBytes,
                        ref aggregateBytes);
                    break;
                case IncusDirectoryEntryKind.Directory:
                    using (var child = IncusSafeFile.OpenChildDirectoryNoFollow(source, name))
                    {
                        Directory.CreateDirectory(target);
                        CopyPinnedDirectoryBounded(
                            child,
                            target,
                            maxBytes,
                            maxEntries,
                            ref aggregateBytes,
                            ref entries,
                            depth + 1,
                            ct);
                        if (!OperatingSystem.IsWindows())
                            File.SetUnixFileMode(target, child.Mode);
                    }
                    break;
                case IncusDirectoryEntryKind.RegularFile:
                    using (var input = IncusSafeFile.OpenChildFileReadNoFollow(source, name))
                    {
                        CopyOpenedFileBounded(
                            input,
                            target,
                            IncusSafeFile.GetRegularFileMode(input),
                            maxBytes,
                            ref aggregateBytes,
                            ct);
                    }
                    break;
                default:
                    throw new IOException(
                        $"Isolation snapshots reject unsupported special files: '{name}'.");
            }
        }
    }

    private static void CopySymbolicLinkBounded(
        string destination,
        string linkTarget,
        bool isDirectoryLink,
        long maxBytes,
        ref long aggregateBytes)
    {
        const int maximumLinkTargetBytes = 64 * 1024;
        var targetBytes = Encoding.UTF8.GetByteCount(linkTarget);
        if (targetBytes > maximumLinkTargetBytes)
            throw new IOException("Isolation snapshot symbolic-link target exceeds the 64 KiB safety bound.");
        checked { aggregateBytes += targetBytes; }
        if (aggregateBytes > maxBytes)
            throw new IOException($"Isolation snapshot exceeds the configured {maxBytes}-byte limit.");

        // Capture and recreate the link text itself. Never resolve or open the
        // target: absolute links resolve in the guest namespace, and relative
        // links retain repository semantics without exposing a mutable host
        // target through the staged virtiofs tree.
        if (OperatingSystem.IsWindows() && isDirectoryLink)
            Directory.CreateSymbolicLink(destination, linkTarget);
        else
            File.CreateSymbolicLink(destination, linkTarget);
    }

    private static void CopyFileBounded(
        string source,
        string destination,
        long maxBytes,
        ref long aggregateBytes,
        CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("File mount source has no parent directory.");
        var name = Path.GetFileName(source);
        if (!string.Equals(Path.Combine(parent, name), source, StringComparison.Ordinal))
            throw new IOException("The authorized individual-file source is not canonical.");
        using var pinnedParent = IncusSafeFile.PinDirectoryNoFollow(parent);
        var metadata = IncusSafeFile.InspectChildNoFollow(pinnedParent, name);
        if (metadata.Kind != IncusDirectoryEntryKind.RegularFile)
            throw new IOException("Individual-file mounts accept regular files only.");
        using var input = IncusSafeFile.OpenChildFileReadNoFollow(pinnedParent, name);
        CopyOpenedFileBounded(
            input,
            destination,
            IncusSafeFile.GetRegularFileMode(input),
            maxBytes,
            ref aggregateBytes,
            ct);
    }

    private static void CopyOpenedFileBounded(
        FileStream input,
        string destination,
        UnixFileMode sourceMode,
        long maxBytes,
        ref long aggregateBytes,
        CancellationToken ct)
    {
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            checked { aggregateBytes += read; }
            if (aggregateBytes > maxBytes)
                throw new IOException($"Isolation snapshot exceeds the configured {maxBytes}-byte limit.");
            output.Write(buffer, 0, read);
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, sourceMode);
    }

    private static string GetGuestParent(string guestPath)
    {
        var slash = guestPath.LastIndexOf('/');
        return slash <= 0 ? "/" : guestPath[..slash];
    }

    private static void ValidateNoOverlappingDevicePaths(IReadOnlyList<IncusPreparedMount> mounts)
    {
        for (var i = 0; i < mounts.Count; i++)
        {
            for (var j = i + 1; j < mounts.Count; j++)
            {
                var first = mounts[i].GuestPath;
                var second = mounts[j].GuestPath;
                if (first == second || IsDescendant(first, second) || IsDescendant(second, first))
                    throw new InvalidOperationException($"Incus device mount paths '{first}' and '{second}' overlap.");
            }
        }
    }

    private static void ValidateGuestLinks(
        IReadOnlyList<IncusPreparedMount> mounts,
        IReadOnlyList<IncusGuestLink> links)
    {
        var linkPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            IncusInputValidation.ValidateAbsoluteGuestPath(link.Target, nameof(links));
            IncusInputValidation.ValidateAbsoluteGuestPath(link.LinkPath, nameof(links));
            if (!linkPaths.Add(link.LinkPath))
                throw new InvalidOperationException($"Duplicate guest link path '{link.LinkPath}'.");
            if (!mounts.Any(mount => IsDescendant(link.Target, mount.GuestPath)))
                throw new InvalidOperationException($"Guest link target '{link.Target}' is outside every configured device path.");
            if (mounts.Any(mount =>
                (link.LinkPath == mount.GuestPath
                    || IsDescendant(link.LinkPath, mount.GuestPath)
                    || IsDescendant(mount.GuestPath, link.LinkPath))
                && !(mount.TmpfsSizeBytes.HasValue && IsDescendant(link.LinkPath, mount.GuestPath))))
                throw new InvalidOperationException($"Guest link '{link.LinkPath}' overlaps an Incus device path.");
        }
    }

    private static IncusMountReadinessProbe? FindReadinessProbe(
        string hostSource,
        int maximumEntries,
        CancellationToken ct) =>
        FindReadinessProbe(
            Directory.EnumerateFiles(hostSource, "*", SearchOption.TopDirectoryOnly),
            maximumEntries,
            ct);

    internal static IncusMountReadinessProbe? FindReadinessProbe(
        IEnumerable<string> candidatePaths,
        int maximumEntries,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        const long maximumProbeBytes = 64 * 1024;
        var entries = 0;
        foreach (var path in candidatePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (++entries > maximumEntries)
                return null;
            try
            {
                var name = Path.GetFileName(path);
                if (name.Length > 255 || name.Any(char.IsControl))
                    continue;
                using var stream = IncusSafeFile.OpenReadNoFollow(path);
                if (stream.Length <= maximumProbeBytes)
                {
                    var hash = ComputeSha256Bounded(stream, maximumProbeBytes);
                    if (hash is not null)
                        return new IncusMountReadinessProbe(name, hash);
                }
            }
            catch (IOException)
            {
                // A concurrently changed, linked, or special entry is not a
                // safe readiness sentinel. Continue looking for a regular file.
            }
            catch (UnauthorizedAccessException)
            {
                // The mount itself may still be valid even when no individual
                // file is readable by the host readiness probe.
            }
        }
        return null;
    }

    private static string? ComputeSha256Bounded(Stream stream, long maximumBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4096];
        var total = 0L;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            total += read;
            if (total > maximumBytes)
                return null;
            hash.AppendData(buffer, 0, read);
        }
    }

    private static string AuthorizeHostSource(
        IncusSandboxOptions options,
        string stagingRoot,
        string source)
    {
        var canonicalSource = ResolveExistingRealPath(source);
        var roots = options.AllowedHostMountRoots
            .Select(ResolveExistingRealPath)
            .Append(ResolveExistingRealPath(stagingRoot));
        if (!roots.Any(root => IsContained(canonicalSource, root)))
            throw new UnauthorizedAccessException($"Host mount source '{source}' is outside Incus AllowedHostMountRoots.");
        return canonicalSource;
    }

    internal static string ResolveExistingRealPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
            throw new FileNotFoundException("Cannot canonicalize a path that does not exist.", fullPath);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("Host path has no filesystem root.");
        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"Unable to resolve host symlink '{current}'.");
        }
        return Path.GetFullPath(current);
    }

    private static bool IsContained(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(candidate, normalizedRoot, StringComparison.Ordinal)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsDescendant(string candidate, string parent) =>
        candidate.Length > parent.Length
        && candidate.StartsWith(parent, StringComparison.Ordinal)
        && (parent == "/" || candidate[parent.Length] == '/');

    /// <summary>
    /// Validates a guest mount path after any host source has passed
    /// canonicalization, existence, and allowed-root authorization. The narrow
    /// <c>/var</c> exception exists for Git alternates: Git stores the host's
    /// absolute object-directory path in the mounted bare repository, so that
    /// same directory must appear at the identical path in the guest.
    /// </summary>
    internal static void ValidateAuthorizedMountGuestPath(
        SandboxMount mount,
        string? authorizedExistingHostSource,
        bool hostSourceIsDirectory)
    {
        ArgumentNullException.ThrowIfNull(mount);
        var path = mount.SandboxPath;
        if (path == "/")
            throw new InvalidOperationException("Incus mount path '/' is protected.");
        string[] protectedRoots =
        [
            "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64",
            "/proc", "/root", "/sbin", "/sys", "/usr", "/var",
        ];
        var isNarrowReadOnlyVarMirror =
            hostSourceIsDirectory
            && mount.ReadOnly
            && !mount.Tmpfs
            && IsDescendant(path, "/var")
            && string.Equals(path, authorizedExistingHostSource, StringComparison.Ordinal);
        if (protectedRoots.Any(root => path == root || IsDescendant(path, root))
            && !isNarrowReadOnlyVarMirror)
            throw new InvalidOperationException($"Incus mount path '{path}' overlaps a protected guest system path.");
        var isCredentialPath = path == SandboxConventions.CredentialsDir
            || IsDescendant(path, SandboxConventions.CredentialsDir);
        if ((path == "/run" || IsDescendant(path, "/run"))
            && !isCredentialPath)
            throw new InvalidOperationException("Caller-supplied Incus mounts under /run are reserved except for the credentials tmpfs tree.");
    }
}
