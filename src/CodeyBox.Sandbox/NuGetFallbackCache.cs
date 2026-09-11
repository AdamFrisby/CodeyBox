using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Sandbox;

/// <summary>
/// Provider-neutral shared NuGet package cache.
///
/// <para>A read-only copy of the operator's package cache is exposed to the
/// guest as a NuGet <em>fallback package folder</em> (consumed in place, never
/// written to) while the writable global-packages root stays per-sandbox, so
/// restore resolves cached packages without copying them and writes only
/// packages the cache does not carry. Providers satisfy the
/// <see cref="SandboxMount"/> produced here by whatever is cheapest for them
/// (kernel read-only bind, staged copy, or another equivalent isolation);
/// providers whose host directory is not reachable from the guest at all stage
/// the cache once per host through <see cref="SharedHostPackageCache"/> and
/// reuse it across sandboxes.</para>
/// </summary>
public static class NuGetFallbackCache
{
    /// <summary>
    /// Environment variable NuGet reads for fallback package folders
    /// (semicolon-separated on all platforms).
    /// </summary>
    public const string FallbackPackagesEnvironmentVariable = "NUGET_FALLBACK_PACKAGES";

    /// <summary>
    /// Writable global-packages folder variable, preserved here so providers
    /// keep the two concepts distinct.
    /// </summary>
    public const string GlobalPackagesEnvironmentVariable = "NUGET_PACKAGES";

    /// <summary>Default guest path of the first shared fallback folder.</summary>
    public const string DefaultGuestFallbackPath = "/opt/codeybox/nuget-fallback-packages";

    /// <summary>
    /// Maximum fallback folders per sandbox. Matches the maximum number of
    /// package-cache seeds so every configured seed can map to one folder;
    /// <see cref="GuestFallbackPathForSeed"/> rejects indexes beyond this.
    /// </summary>
    public const int MaximumFallbackPaths = BaselineProvisioningLimits.MaximumPackageCacheSeeds;

    /// <summary>Maximum package identities enumerated from one cache directory.</summary>
    public const int MaximumEnumeratedPackages = 1_000_000;

    /// <summary>Maximum UTF-8 bytes accepted for one guest fallback path.</summary>
    public const int MaximumFallbackPathUtf8Bytes = 4096;

    /// <summary>Maximum UTF-8 bytes accepted for the merged fallback environment value.</summary>
    public const int MaximumFallbackEnvironmentUtf8Bytes = 32 * 1024;

    /// <summary>
    /// Guest fallback path for the seed at <paramref name="index"/>: the base
    /// path for the first seed, <c>{base}-{index+1}</c> for the rest.
    /// </summary>
    public static string GuestFallbackPathForSeed(string basePath, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        if (index is < 0 or >= MaximumFallbackPaths)
            throw new ArgumentOutOfRangeException(nameof(index), "The seed index cannot be negative or exceed the fallback folder maximum.");
        if (!basePath.StartsWith('/'))
            throw new ArgumentException("The fallback base path must be an absolute guest path.", nameof(basePath));
        return index == 0 ? basePath : $"{basePath}-{index + 1}";
    }

    /// <summary>
    /// Builds the read-only mount exposing <paramref name="hostSourcePath"/>
    /// at <paramref name="guestFallbackPath"/>. The mount is shared by
    /// reference (<see cref="SandboxMount.SnapshotForIsolation"/> is false);
    /// kernel read-only enforcement at the consuming provider is what keeps
    /// the shared source unmodified.
    /// </summary>
    public static SandboxMount BuildFallbackMount(string hostSourcePath, string guestFallbackPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestFallbackPath);
        if (!Path.IsPathRooted(hostSourcePath))
            throw new ArgumentException("The fallback host source must be an absolute path.", nameof(hostSourcePath));
        if (!guestFallbackPath.StartsWith('/'))
            throw new ArgumentException("The fallback guest path must be absolute.", nameof(guestFallbackPath));
        EnsureBoundedUtf8(guestFallbackPath, MaximumFallbackPathUtf8Bytes, nameof(guestFallbackPath));
        return new SandboxMount
        {
            SandboxPath = guestFallbackPath,
            HostPath = hostSourcePath,
            ReadOnly = true,
        };
    }

    /// <summary>
    /// Merges <paramref name="guestFallbackPaths"/> into the
    /// <c>NUGET_FALLBACK_PACKAGES</c> entry of <paramref name="environment"/>,
    /// preserving any operator-provided value first and appending only paths
    /// not already present (exact match). Returns a new dictionary; the input
    /// is not mutated.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MergeFallbackEnvironment(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> guestFallbackPaths)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(guestFallbackPaths);
        if (guestFallbackPaths.Count > MaximumFallbackPaths)
            throw new ArgumentOutOfRangeException(
                nameof(guestFallbackPaths),
                $"A sandbox cannot carry more than {MaximumFallbackPaths} NuGet fallback folders.");
        foreach (var path in guestFallbackPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
                throw new ArgumentException("Every fallback guest path must be absolute.", nameof(guestFallbackPaths));
            EnsureBoundedUtf8(path, MaximumFallbackPathUtf8Bytes, nameof(guestFallbackPaths));
        }

        var merged = new List<string>(guestFallbackPaths.Count + 1);
        if (environment.TryGetValue(FallbackPackagesEnvironmentVariable, out var existing)
            && !string.IsNullOrWhiteSpace(existing))
        {
            foreach (var entry in existing.Split(';'))
            {
                var trimmed = entry.Trim();
                if (trimmed.Length != 0 && !merged.Contains(trimmed, StringComparer.Ordinal))
                    merged.Add(trimmed);
            }
        }
        foreach (var path in guestFallbackPaths)
        {
            if (!merged.Contains(path, StringComparer.Ordinal))
                merged.Add(path);
        }
        var value = string.Join(';', merged);
        EnsureBoundedUtf8(value, MaximumFallbackEnvironmentUtf8Bytes, FallbackPackagesEnvironmentVariable);
        var result = new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            [FallbackPackagesEnvironmentVariable] = value,
        };
        return result;
    }

    /// <summary>
    /// Splits <paramref name="value"/> on semicolons, trimming whitespace and
    /// dropping empty entries.
    /// </summary>
    public static IReadOnlyList<string> SplitFallbackEnvironmentValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        var entries = new List<string>();
        foreach (var entry in value.Split(';'))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length != 0)
                entries.Add(trimmed);
        }
        return entries;
    }

    /// <summary>
    /// Partitions package-cache seeds into NuGet-targeted seeds (destined for
    /// <c>{guestHome}/.nuget/...</c> and therefore eligible for fallback
    /// sharing) and all other seeds, preserving the original indexes.
    /// </summary>
    public static NuGetSeedPartition PartitionNuGetSeeds(
        IReadOnlyList<BaselinePackageCacheSeed> seeds,
        string guestHome)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestHome);
        var nuget = new List<IndexedSeed>(seeds.Count);
        var other = new List<IndexedSeed>(seeds.Count);
        for (var i = 0; i < seeds.Count; i++)
        {
            var seed = seeds[i]
                ?? throw new ArgumentException($"Package cache seed {i} is null.", nameof(seeds));
            if (NuGetPackageCacheGuestPaths.TryGetNuGetHomeDirectory(seed.VmDestPath, guestHome) is not null)
                nuget.Add(new IndexedSeed(i, seed));
            else
                other.Add(new IndexedSeed(i, seed));
        }
        return new NuGetSeedPartition(nuget, other);
    }

    internal static void EnsureBoundedUtf8(string value, int maxBytes, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > maxBytes)
            throw new ArgumentOutOfRangeException(
                fieldName,
                $"The value exceeds the {maxBytes}-byte UTF-8 bound.");
    }
}

/// <summary>A package-cache seed with its original list index preserved.</summary>
public sealed record IndexedSeed(int Index, BaselinePackageCacheSeed Seed);

/// <summary>
/// Package-cache seeds split by NuGet targeting. <see cref="NuGetSeeds"/>
/// land under the guest NuGet home and may be served as fallback folders;
/// <see cref="OtherSeeds"/> always keep the legacy copy behavior.
/// </summary>
public sealed record NuGetSeedPartition(
    IReadOnlyList<IndexedSeed> NuGetSeeds,
    IReadOnlyList<IndexedSeed> OtherSeeds);

/// <summary>
/// Coverage of one restore against the shared fallback cache: how many of
/// the restored package identities resolved from the shared cache versus how
/// many were fetched into the per-sandbox writable root.
/// </summary>
public sealed record NuGetFallbackCoverageReport(
    int SharedPackages,
    int FetchedPackages,
    int TotalPackages,
    int FallbackPackagesAvailable)
{
    /// <summary>
    /// One-line human-readable summary, suitable for a log row.
    /// </summary>
    public string Format() =>
        $"NuGet fallback coverage: {SharedPackages} shared, {FetchedPackages} fetched " +
        $"of {TotalPackages} referenced ({FallbackPackagesAvailable} available)";
}

/// <summary>
/// Reports restore coverage against a shared fallback cache. The pure core
/// (<see cref="Classify"/>) maps restored package identities onto the shared
/// set; the filesystem helpers enumerate real cache layouts and parse real
/// <c>project.assets.json</c> files with bounded inputs.
/// </summary>
public static class NuGetFallbackCoverage
{
    /// <summary>Maximum <c>project.assets.json</c> bytes parsed for coverage.</summary>
    public const int MaximumAssetsFileBytes = 16 * 1024 * 1024;

    /// <summary>Maximum restored libraries classified in one report.</summary>
    public const int MaximumClassifiedLibraries = 100_000;

    /// <summary>
    /// Normalizes a package identity to <c>lowercase-id/version</c>. NuGet
    /// package ids are case-insensitive; versions compare ordinally after
    /// lowercasing the id only... versions are normalized as-is (NuGet
    /// normalizes them at pack time) while the id folds to lowercase.
    /// </summary>
    public static string NormalizeIdentity(string id, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (id.Contains('/') || version.Contains('/'))
            throw new ArgumentException("A package identity cannot contain '/'.");
        return $"{id.ToLowerInvariant()}/{version}";
    }

    /// <summary>
    /// Enumerates <c>id/version</c> identities from a global-packages-folder
    /// layout (<c>{root}/{id}/{version}/</c>). Only two-level directory
    /// structure is considered; files, deeper trees, and malformed names are
    /// ignored. The walk is bounded by <paramref name="maxEntries"/>.
    /// </summary>
    public static HashSet<string> EnumeratePackageIdentities(string rootDirectory, int maxEntries = NuGetFallbackCache.MaximumEnumeratedPackages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "The entry bound must be positive.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(rootDirectory))
            return identities;
        var entries = 0;
        foreach (var idDir in Directory.EnumerateDirectories(rootDirectory))
        {
            var id = Path.GetFileName(idDir);
            if (string.IsNullOrEmpty(id))
                continue;
            string[] versionDirs;
            try
            {
                versionDirs = Directory.GetDirectories(idDir);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            foreach (var versionDir in versionDirs)
            {
                entries++;
                if (entries > maxEntries)
                    throw new IOException("The package cache exceeds the enumeration entry bound.");
                var version = Path.GetFileName(versionDir);
                if (string.IsNullOrEmpty(version))
                    continue;
                try
                {
                    identities.Add(NormalizeIdentity(id, version));
                }
                catch (ArgumentException)
                {
                    // Ignore directory names that cannot be package identities.
                }
            }
        }
        return identities;
    }

    /// <summary>
    /// Parses the <c>libraries</c> section of a <c>project.assets.json</c>
    /// file, returning <c>identity → library type</c> for entries shaped like
    /// <c>"Id/1.2.3"</c>. Returns false (with an empty map) when the document
    /// is not a recognizable assets file. Rejects inputs over
    /// <paramref name="maxBytes"/> before parsing and caps the library count.
    /// </summary>
    public static bool TryParseAssetsLibraries(
        string json,
        out Dictionary<string, string> libraries,
        int maxBytes = MaximumAssetsFileBytes)
    {
        libraries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json))
            return false;
        if (Encoding.UTF8.GetByteCount(json) > maxBytes)
            return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("libraries", out var librariesElement)
                || librariesElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            foreach (var property in librariesElement.EnumerateObject())
            {
                if (libraries.Count >= MaximumClassifiedLibraries)
                    return false;
                var separator = property.Name.IndexOf('/');
                if (separator <= 0 || separator == property.Name.Length - 1)
                    continue;
                var id = property.Name[..separator];
                var version = property.Name[(separator + 1)..];
                string type = string.Empty;
                if (property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("type", out var typeElement)
                    && typeElement.ValueKind == JsonValueKind.String)
                {
                    type = typeElement.GetString() ?? string.Empty;
                }
                string identity;
                try
                {
                    identity = NormalizeIdentity(id, version);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                libraries[identity] = type;
            }
            return true;
        }
        catch (JsonException)
        {
            libraries.Clear();
            return false;
        }
    }

    /// <summary>
    /// Classifies restored <c>type == "package"</c> libraries: present in
    /// <paramref name="fallbackIdentities"/> counts as shared, everything else
    /// counts as fetched. Non-package libraries (project references) are
    /// excluded from the totals.
    /// </summary>
    public static NuGetFallbackCoverageReport Classify(
        IReadOnlyDictionary<string, string> restoredLibraries,
        ISet<string> fallbackIdentities,
        int fallbackPackagesAvailable)
    {
        ArgumentNullException.ThrowIfNull(restoredLibraries);
        ArgumentNullException.ThrowIfNull(fallbackIdentities);
        if (fallbackPackagesAvailable < 0)
            throw new ArgumentOutOfRangeException(nameof(fallbackPackagesAvailable));
        var shared = 0;
        var fetched = 0;
        foreach (var (identity, type) in restoredLibraries)
        {
            if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                continue;
            if (fallbackIdentities.Contains(identity))
                shared++;
            else
                fetched++;
        }
        return new NuGetFallbackCoverageReport(shared, fetched, shared + fetched, fallbackPackagesAvailable);
    }

    /// <summary>
    /// End-to-end host-side report from a fallback directory and an assets
    /// document. A missing fallback directory counts as zero available;
    /// an unrecognizable assets document yields a zero-total report rather
    /// than an exception so best-effort reporting never breaks a build gate.
    /// </summary>
    public static NuGetFallbackCoverageReport ComputeFromDirectories(
        string fallbackDirectory,
        string assetsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);
        ArgumentNullException.ThrowIfNull(assetsJson);
        var fallback = EnumeratePackageIdentities(fallbackDirectory);
        if (!TryParseAssetsLibraries(assetsJson, out var libraries))
            return new NuGetFallbackCoverageReport(0, 0, 0, fallback.Count);
        return Classify(libraries, fallback, fallback.Count);
    }
}

/// <summary>
/// Content-addressed per-host staging for package-cache payloads that cannot
/// be shared with a guest by reference. Each
/// (<paramref name="cacheKey"/>, <paramref name="contentFingerprint"/>) pair
/// is populated at most once per host: concurrent and repeated ensures reuse
/// the published directory instead of re-transferring. Stale fingerprints for
/// a key are evicted explicitly through
/// <see cref="TryEvictStaleFingerprints"/> so one reconfigured seed cannot
/// accumulate unbounded state.
/// </summary>
public sealed class SharedHostPackageCache
{
    private const string ContentDirectoryName = "content";
    private const string CompleteMarkerName = ".complete";
    private const string TempPrefix = ".tmp-";
    private const int MaximumKeyLength = 64;

    /// <summary>Maximum entries inspected while fingerprinting one cache directory.</summary>
    public const int MaximumFingerprintEntries = 100_000;
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

    public SharedHostPackageCache(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    /// <summary>Canonical host root backing this store.</summary>
    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// Returns the populated content directory for
    /// (<paramref name="cacheKey"/>, <paramref name="contentFingerprint"/>),
    /// invoking <paramref name="populateAsync"/> once per host when no
    /// complete publication exists yet. The callback receives an empty
    /// directory to fill; publication is atomic, so observers never see a
    /// partial population.
    /// </summary>
    public async Task<string> EnsurePopulatedAsync(
        string cacheKey,
        string contentFingerprint,
        Func<string, CancellationToken, Task> populateAsync,
        CancellationToken ct = default)
    {
        ValidateKey(cacheKey);
        ValidateFingerprint(contentFingerprint);
        ArgumentNullException.ThrowIfNull(populateAsync);
        var keyDirectory = Path.Combine(_cacheRoot, cacheKey);
        var contentDirectory = Path.Combine(keyDirectory, contentFingerprint, ContentDirectoryName);
        if (IsComplete(contentDirectory, contentFingerprint))
            return contentDirectory;
        var gate = _gates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (IsComplete(contentDirectory, contentFingerprint))
                return contentDirectory;
            Directory.CreateDirectory(keyDirectory);
            if (!OperatingSystem.IsWindows())
            {
                // The cache holds copies of package payloads (potentially
                // from private feeds) staged for guest delivery. Keep it
                // operator-only like the provider staging trees; a failure
                // here fails closed rather than staging world-readable.
                File.SetUnixFileMode(
                    keyDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // A previous crashed populate may have left a partial directory
            // without a completion marker; remove it before republishing so a
            // stale half-population can never be mistaken for content.
            if (Directory.Exists(contentDirectory) || File.Exists(contentDirectory))
                DeleteTree(contentDirectory);
            var tempParent = Path.Combine(keyDirectory, contentFingerprint);
            Directory.CreateDirectory(tempParent);
            var tempDirectory = Path.Combine(tempParent, $"{TempPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            try
            {
                await populateAsync(tempDirectory, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(
                    Path.Combine(tempDirectory, CompleteMarkerName),
                    contentFingerprint + "\n",
                    ct).ConfigureAwait(false);
                if (Directory.Exists(contentDirectory))
                    DeleteTree(contentDirectory);
                Directory.Move(tempDirectory, contentDirectory);
            }
            catch (Exception primaryFailure)
            {
                Exception? cleanupFailure = null;
                try
                {
                    if (Directory.Exists(tempDirectory))
                        DeleteTree(tempDirectory);
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                if (cleanupFailure is not null)
                {
                    throw new AggregateException(
                        "Populating the shared host package cache failed and temp cleanup also failed.",
                        primaryFailure,
                        cleanupFailure);
                }
                throw;
            }
            return contentDirectory;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deletes every fingerprint publication for <paramref name="cacheKey"/>
    /// except <paramref name="currentFingerprint"/>. Returns the number of
    /// evicted publications alongside any per-entry failures, so one
    /// unreadable stale directory cannot fail an otherwise successful
    /// provisioning pass.
    /// </summary>
    public EvictionResult TryEvictStaleFingerprints(string cacheKey, string currentFingerprint)
    {
        ValidateKey(cacheKey);
        ValidateFingerprint(currentFingerprint);
        var evicted = 0;
        var failures = new List<string>();
        var keyDirectory = Path.Combine(_cacheRoot, cacheKey);
        string[] fingerprintDirs;
        try
        {
            if (!Directory.Exists(keyDirectory))
                return new EvictionResult(0, []);
            fingerprintDirs = Directory.GetDirectories(keyDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EvictionResult(0, [$"{keyDirectory}: {ex.Message}"]);
        }
        foreach (var fingerprintDir in fingerprintDirs)
        {
            var name = Path.GetFileName(fingerprintDir);
            if (string.Equals(name, currentFingerprint, StringComparison.Ordinal))
                continue;
            if (name.StartsWith(TempPrefix, StringComparison.Ordinal))
            {
                // Orphaned temp directories from crashed populates are always safe to remove.
            }
            else if (!IsHexFingerprint(name))
            {
                continue;
            }
            try
            {
                DeleteTree(fingerprintDir);
                evicted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{fingerprintDir}: {ex.Message}");
            }
        }
        return new EvictionResult(evicted, failures);
    }

    /// <summary>
    /// Computes the content fingerprint of a package-cache source directory:
    /// SHA-256 over the sorted relative paths, entry kinds, file lengths,
    /// write timestamps, and symlink targets. Reads metadata only, so it is
    /// cheap relative to archiving the content. Bounded by
    /// <paramref name="maxEntries"/>; symlinks are recorded, never followed.
    /// </summary>
    public static string ComputeDirectoryFingerprint(
        string sourceDirectory,
        int maxEntries = MaximumFingerprintEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "The entry bound must be positive.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var entries = new List<string>();
        CollectEntries(Path.GetFullPath(sourceDirectory), string.Empty, entries, maxEntries, depth: 0);
        entries.Sort(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            hash.AppendData(bytes);
            hash.AppendData([(byte)'\n']);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void CollectEntries(
        string rootFullPath,
        string relative,
        List<string> entries,
        int maxEntries,
        int depth)
    {
        const int maxDepth = 512;
        if (depth > maxDepth)
            throw new IOException("The package cache exceeds the 512-directory-depth safety bound.");
        var current = relative.Length == 0 ? rootFullPath : Path.Combine(rootFullPath, relative);
        string[] children;
        try
        {
            children = Directory.GetFileSystemEntries(current);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        Array.Sort(children, StringComparer.Ordinal);
        foreach (var child in children)
        {
            if (entries.Count >= maxEntries)
                throw new IOException("The package cache exceeds the fingerprint entry bound.");
            var name = Path.GetFileName(child);
            var childRelative = relative.Length == 0 ? name : $"{relative}/{name}";
            var attributes = File.GetAttributes(child);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entries.Add($"l|{childRelative}|{ResolveLinkTargetSafe(child)}");
                continue;
            }
            if (Directory.Exists(child))
            {
                entries.Add($"d|{childRelative}");
                CollectEntries(rootFullPath, childRelative, entries, maxEntries, depth + 1);
            }
            else if (File.Exists(child))
            {
                var info = new FileInfo(child);
                entries.Add($"f|{childRelative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
        }
    }

    private static string ResolveLinkTargetSafe(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: false);
            return target?.FullName ?? "<unknown>";
        }
        catch (IOException)
        {
            return "<unresolvable>";
        }
    }

    private static bool IsComplete(string contentDirectory, string fingerprint)
    {
        try
        {
            if (!Directory.Exists(contentDirectory))
                return false;
            var marker = Path.Combine(contentDirectory, CompleteMarkerName);
            if (!File.Exists(marker))
                return false;
            var recorded = File.ReadAllText(marker).Trim();
            return string.Equals(recorded, fingerprint, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteTree(string path)
    {
        if (File.Exists(path) && !Directory.Exists(path))
        {
            File.Delete(path);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Directory.Exists(entry) && !File.Exists(entry))
            {
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                {
                    Directory.Delete(entry, recursive: false);
                    continue;
                }
                DeleteTree(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(path, recursive: false);
    }

    private static void ValidateKey(string cacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        if (cacheKey.Length > MaximumKeyLength
            || !cacheKey.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new ArgumentException(
                "A shared cache key must be 1-64 ASCII letters, digits, or '-'.",
                nameof(cacheKey));
        }
    }

    private static void ValidateFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (!IsHexFingerprint(fingerprint))
            throw new ArgumentException("A content fingerprint must be 64 lowercase hex characters.", nameof(fingerprint));
    }

    private static bool IsHexFingerprint(string value) =>
        value.Length == 64
        && value.All(static c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
}

/// <summary>Outcome of <see cref="SharedHostPackageCache.TryEvictStaleFingerprints"/>.</summary>
public sealed record EvictionResult(int Evicted, IReadOnlyList<string> Failures);

/// <summary>
/// Best-effort post-restore coverage reporting over an <see cref="ISandbox"/>.
/// Runs the <see cref="NuGetFallbackCensus"/> script in the guest, reads the
/// listed <c>project.assets.json</c> files back with bounded execs, and
/// classifies every restored package as shared (present in the fallback
/// folders) or fetched (resolved into the writable root). Any failure —
/// missing tooling, unparseable output, excess files, cancellation aside —
/// yields null so diagnostics never break the gate they observe.
/// </summary>
public static class NuGetFallbackCoverageReporter
{
    /// <summary>Maximum assets files read back for one report.</summary>
    public const int MaximumAssetsFilesRead = 8;

    /// <summary>Maximum bytes read back from one assets file.</summary>
    public const int MaximumAssetsFileReadBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reports coverage for restores under <paramref name="searchRoot"/>, or
    /// null when coverage cannot be determined. Never throws except on
    /// cancellation or invalid arguments.
    /// </summary>
    public static async Task<string?> TryReportAsync(
        ISandbox sandbox,
        string searchRoot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchRoot);
        SandboxExecResult census;
        try
        {
            census = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", NuGetFallbackCensus.BuildScript(searchRoot)],
                MaxStdoutBytes = NuGetFallbackCensus.MaximumCensusStdoutBytes,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (!census.Success)
            return null;
        var parsed = NuGetFallbackCensusParser.Parse(census.Stdout);
        if (parsed is null || parsed.AssetsPaths.Count == 0)
            return null;
        if (parsed.AssetsPaths.Count > MaximumAssetsFilesRead)
            return null;
        var fallback = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in parsed.FallbackFolders)
        {
            foreach (var package in folder.Packages)
                fallback.Add(package);
        }
        var libraries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetsPath in parsed.AssetsPaths)
        {
            SandboxExecResult read;
            try
            {
                // Argv (no shell) plus the `--` separator keep the
                // guest-listed path a single operand; the census parser only
                // admits absolute paths, and the content goes straight into
                // the bounded JSON parser below.
                read = await sandbox.ExecAsync(new SandboxExec
                {
                    Argv = ["cat", "--", assetsPath],
                    MaxStdoutBytes = MaximumAssetsFileReadBytes,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
            if (!read.Success)
                return null;
            if (!NuGetFallbackCoverage.TryParseAssetsLibraries(read.Stdout, out var fileLibraries))
                return null;
            foreach (var (identity, type) in fileLibraries)
                libraries.TryAdd(identity, type);
        }
        return NuGetFallbackCoverage.Classify(libraries, fallback, fallback.Count).Format();
    }
}

/// <summary>
/// Guest-side census backing <see cref="NuGetFallbackCoverage"/> reports.
/// The script reads <c>NUGET_FALLBACK_PACKAGES</c> from the guest environment
/// itself (no host values are embedded except the bounded search root and
/// integer caps), lists <c>id/version</c> package directories per fallback
/// folder, and lists <c>project.assets.json</c> files under the work tree.
/// The host parses the output with <see cref="NuGetFallbackCensusParser"/>
/// and classifies the assets documents locally, so no JSON parsing happens in
/// shell.
/// </summary>
public static class NuGetFallbackCensus
{
    /// <summary>Maximum <c>project.assets.json</c> paths listed by one census.</summary>
    public const int MaximumAssetsFiles = 64;

    /// <summary>Maximum package directories listed per fallback folder.</summary>
    public const int MaximumListedPackagesPerFolder = 100_000;

    /// <summary>Maximum stdout bytes requested for one census exec.</summary>
    public const int MaximumCensusStdoutBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Builds the census shell script. Only <paramref name="searchRoot"/>
    /// (which must be absolute and free of quotes, backslashes, and line
    /// breaks) is embedded; every other input comes from the guest
    /// environment or validated integer caps.
    /// </summary>
    public static string BuildScript(
        string searchRoot,
        int maxAssetsFiles = MaximumAssetsFiles,
        int maxListedPackagesPerFolder = MaximumListedPackagesPerFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchRoot);
        if (!searchRoot.StartsWith('/'))
            throw new ArgumentException("The census search root must be absolute.", nameof(searchRoot));
        if (searchRoot.Any(static c => c is '\'' or '\\' or '\r' or '\n'))
            throw new ArgumentException("The census search root cannot contain quotes, backslashes, or line breaks.", nameof(searchRoot));
        if (maxAssetsFiles <= 0 || maxAssetsFiles > MaximumAssetsFiles)
            throw new ArgumentOutOfRangeException(nameof(maxAssetsFiles));
        if (maxListedPackagesPerFolder <= 0 || maxListedPackagesPerFolder > MaximumListedPackagesPerFolder)
            throw new ArgumentOutOfRangeException(nameof(maxListedPackagesPerFolder));
        return $$"""
            set -eu
            search_root='{{searchRoot}}'
            max_assets={{maxAssetsFiles}}
            max_packages={{maxListedPackagesPerFolder}}
            echo "CODEYBOX-NUGET-CENSUS-V1"
            echo "FALLBACK-VALUE:${NUGET_FALLBACK_PACKAGES:-}"
            old_ifs=$IFS
            IFS=';'
            set -f
            for dir in ${NUGET_FALLBACK_PACKAGES:-}; do
              case "$dir" in
                ''|*'
                '*) ;;
                /*)
                  echo "DIR:$dir"
                  if [ -d "$dir" ]; then
                    (cd -- "$dir" && find . -mindepth 2 -maxdepth 2 -type d -print | sed 's|^\./||' | sort | head -n "$max_packages") || true
                  fi
                  echo "END-DIR"
                  ;;
              esac
            done
            set +f
            IFS=$old_ifs
            echo "ASSETS"
            if [ -d "$search_root" ]; then
              find "$search_root" -name 'project.assets.json' -type f -print 2>/dev/null | sort | head -n "$max_assets"
            fi
            echo "END-ASSETS"
            echo "END-CENSUS"
            """;
    }
}

/// <summary>Parsed <see cref="NuGetFallbackCensus"/> output.</summary>
public sealed record NuGetCensusResult(
    string FallbackValue,
    IReadOnlyList<NuGetCensusFallbackFolder> FallbackFolders,
    IReadOnlyList<string> AssetsPaths);

/// <summary>One fallback folder and the package identities listed inside it.</summary>
public sealed record NuGetCensusFallbackFolder(
    string Directory,
    IReadOnlyList<string> Packages);

/// <summary>
/// Parses <see cref="NuGetFallbackCensus"/> output. Lines that do not match
/// the expected framing or a strict <c>id/version</c> shape are ignored, so a
/// hostile or truncated guest environment degrades to fewer data rather than
/// a misreport. Returns null when the framing markers are absent.
/// </summary>
public static class NuGetFallbackCensusParser
{
    /// <summary>Maximum census stdout bytes parsed.</summary>
    public const int MaximumInputBytes = NuGetFallbackCensus.MaximumCensusStdoutBytes;

    /// <summary>Maximum total lines parsed from one census.</summary>
    public const int MaximumInputLines = 500_000;

    public static NuGetCensusResult? Parse(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return null;
        if (Encoding.UTF8.GetByteCount(stdout) > MaximumInputBytes)
            return null;
        var lines = stdout.Split('\n');
        if (lines.Length > MaximumInputLines)
            return null;
        var header = lines[0].TrimEnd('\r');
        if (!string.Equals(header, "CODEYBOX-NUGET-CENSUS-V1", StringComparison.Ordinal))
            return null;
        var fallbackValue = string.Empty;
        var folders = new List<NuGetCensusFallbackFolder>();
        var assets = new List<string>();
        string? currentDir = null;
        var currentPackages = new List<string>();
        var section = CensusSection.Head;
        var complete = false;
        foreach (var rawLine in lines.Skip(1))
        {
            var line = rawLine.TrimEnd('\r');
            switch (section)
            {
                case CensusSection.Head:
                    if (line.StartsWith("FALLBACK-VALUE:", StringComparison.Ordinal))
                    {
                        fallbackValue = line["FALLBACK-VALUE:".Length..];
                        section = CensusSection.Folders;
                    }
                    break;
                case CensusSection.Folders:
                    if (line.StartsWith("DIR:", StringComparison.Ordinal))
                    {
                        currentDir = line["DIR:".Length..];
                        currentPackages = [];
                    }
                    else if (string.Equals(line, "END-DIR", StringComparison.Ordinal))
                    {
                        if (currentDir is not null)
                            folders.Add(new NuGetCensusFallbackFolder(currentDir, currentPackages));
                        currentDir = null;
                        currentPackages = [];
                    }
                    else if (string.Equals(line, "ASSETS", StringComparison.Ordinal))
                    {
                        currentDir = null;
                        currentPackages = [];
                        section = CensusSection.Assets;
                    }
                    else if (currentDir is not null && TryParsePackageLine(line, out var identity))
                    {
                        currentPackages.Add(identity);
                    }
                    break;
                case CensusSection.Assets:
                    if (string.Equals(line, "END-ASSETS", StringComparison.Ordinal))
                        section = CensusSection.Tail;
                    else if (line.StartsWith('/') && line.Length <= 4096 && !line.Any(static c => c is '\0'))
                        assets.Add(line);
                    break;
                case CensusSection.Tail:
                    if (string.Equals(line, "END-CENSUS", StringComparison.Ordinal))
                        complete = true;
                    break;
            }
        }
        if (!complete)
            return null;
        return new NuGetCensusResult(
            fallbackValue,
            folders,
            assets.Count <= NuGetFallbackCensus.MaximumAssetsFiles
                ? assets
                : assets[..NuGetFallbackCensus.MaximumAssetsFiles]);
    }

    private static bool TryParsePackageLine(string line, out string identity)
    {
        identity = string.Empty;
        if (line.Length == 0 || line.Length > 256 || line.StartsWith('.') || line.StartsWith('/'))
            return false;
        var separator = line.IndexOf('/');
        if (separator <= 0 || separator == line.Length - 1 || line.IndexOf('/', separator + 1) >= 0)
            return false;
        var id = line[..separator];
        var version = line[(separator + 1)..];
        if (id.Any(static c => c is '/' or '\\' or '\r' or '\n') || version.Any(static c => c is '/' or '\\' or '\r' or '\n'))
            return false;
        try
        {
            identity = NuGetFallbackCoverage.NormalizeIdentity(id, version);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private enum CensusSection
    {
        Head,
        Folders,
        Assets,
        Tail,
    }
}
