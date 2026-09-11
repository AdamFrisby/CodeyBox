using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// Host-side archive cache for package-cache seeds that keep the legacy copy
/// path. The first sandbox needing a seed builds its push archive once per
/// host into <c>{stagingRoot}/shared-package-archives</c>; every later
/// sandbox (and every re-bake with unchanged content) reuses the published
/// archive instead of re-walking and re-tarring gigabytes of packages.
/// Content is keyed by a metadata fingerprint of the seed source plus the
/// applicable limits, so a changed seed or changed cap rebuilds exactly once.
/// Callers serialize heavy builds with the provider provisioning gate; the
/// store additionally collapses concurrent populates for the same seed.
/// </summary>
internal sealed class IncusSharedPackageArchiveCache
{
    private const string ArchiveFileName = "package-cache.tar";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SharedHostPackageCache> _caches =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the host path of the push-ready tar archive for
    /// (<paramref name="seed"/>, <paramref name="index"/>), building it once
    /// per host when no publication matches the current fingerprint.
    /// </summary>
    internal async Task<string> ObtainAsync(
        IncusSandboxOptions options,
        string stagingRoot,
        BaselinePackageCacheSeed seed,
        int index,
        Func<string, string?> environmentVariableReader,
        Action<string> evictionWarning,
        Func<ArchiveBuildRequest, CancellationToken, Task>? buildArchive = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        ArgumentNullException.ThrowIfNull(evictionWarning);
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), "The seed index cannot be negative.");
        buildArchive ??= DefaultBuildArchive;
        var fingerprint = IncusBaselineProvisioning.ComputePackageSeedFingerprint(
            options,
            seed,
            environmentVariableReader,
            ct);
        var cache = _caches.GetOrAdd(
            stagingRoot,
            static root => new SharedHostPackageCache(Path.Combine(root, "shared-package-archives")));
        var cacheKey = $"package-cache-{index:D3}";
        var contentDirectory = await cache.EnsurePopulatedAsync(
            cacheKey,
            fingerprint,
            (directory, populateCt) => buildArchive(
                new ArchiveBuildRequest(
                    options,
                    seed,
                    Path.Combine(directory, ArchiveFileName),
                    environmentVariableReader),
                populateCt),
            ct).ConfigureAwait(false);
        var eviction = cache.TryEvictStaleFingerprints(cacheKey, fingerprint);
        foreach (var failure in eviction.Failures)
            evictionWarning(failure);
        return Path.Combine(contentDirectory, ArchiveFileName);
    }

    private static Task DefaultBuildArchive(ArchiveBuildRequest request, CancellationToken ct)
    {
        var localAggregateBytes = 0L;
        IncusBaselineProvisioning.CreatePackageArchive(
            request.Seed.HostSourcePath,
            request.ArchivePath,
            IncusBaselineProvisioning.ResolvePackageSeedByteLimit(request.Options, request.Seed),
            request.Options.MaxAggregatePackageCacheSeedBytes,
            request.Options.MaxPackageCacheSeedEntries,
            ref localAggregateBytes,
            request.EnvironmentVariableReader,
            ct);
        return Task.CompletedTask;
    }

    /// <summary>Inputs for one archive build.</summary>
    internal sealed record ArchiveBuildRequest(
        IncusSandboxOptions Options,
        BaselinePackageCacheSeed Seed,
        string ArchivePath,
        Func<string, string?> EnvironmentVariableReader);
}
