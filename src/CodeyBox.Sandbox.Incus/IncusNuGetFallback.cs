using CodeyBox.Core;
using CodeyBox.Sandbox;

namespace CodeyBox.Sandbox.Incus;

/// <summary>
/// How one package-cache seed reaches the guest.
/// </summary>
internal enum NuGetSeedProvisioning
{
    /// <summary>Legacy copy into the seed's configured guest destination.</summary>
    CopyToVmDest,
    /// <summary>
    /// Copy into the shared fallback folder inside a baked baseline image.
    /// Used only while baking; clones inherit the content with the image.
    /// </summary>
    CopyToFallback,
    /// <summary>
    /// Served by a read-only host mount at the shared fallback folder. Used
    /// only for full-launch VMs; nothing is copied for this seed.
    /// </summary>
    MountedShare,
    /// <summary>
    /// Host source is absent; the seed is skipped with a warning and restore
    /// fetches those packages from the network.
    /// </summary>
    SkippedMissing,
}

/// <summary>One seed's provisioning fate, decided before any host transfer.</summary>
internal sealed record SeedProvisioningStep(
    int Index,
    BaselinePackageCacheSeed Seed,
    NuGetSeedProvisioning Mode,
    string? EffectiveVmDestPath);

/// <summary>Full-launch fallback plan: per-seed steps plus host mounts.</summary>
internal sealed record FullLaunchFallbackPlan(
    IReadOnlyList<SeedProvisioningStep> Steps,
    IReadOnlyList<SandboxMount> FallbackMounts,
    IReadOnlyList<string> FallbackGuestPaths,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Translates NuGet-targeted package-cache seeds into read-only fallback
/// mounts plus a <c>NUGET_FALLBACK_PACKAGES</c> environment entry. The copy
/// path stays available seed-by-seed: non-NuGet seeds, file seeds, seeds
/// outside the allowed mount roots, and the operator opt-out all keep the
/// legacy tar-push-extract behavior.
/// </summary>
internal static class IncusNuGetFallback
{
    internal static bool IsSharingEnabled(IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ShareNuGetPackageSeedsAsFallback
            && PlanGuestPaths(options).Count != 0;
    }

    /// <summary>
    /// Guest fallback folders for the current options, in seed order. Empty
    /// unless sharing is enabled and at least one seed targets the guest
    /// NuGet home. Pure: no host access.
    /// </summary>
    internal static IReadOnlyList<string> PlanGuestPaths(IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.ShareNuGetPackageSeedsAsFallback)
            return [];
        var partition = NuGetFallbackCache.PartitionNuGetSeeds(options.PackageCacheSeeds, options.GuestHome);
        if (partition.NuGetSeeds.Count == 0)
            return [];
        var basePath = ValidatedBasePath(options);
        var paths = new List<string>(Math.Min(partition.NuGetSeeds.Count, NuGetFallbackCache.MaximumFallbackPaths));
        for (var position = 0; position < partition.NuGetSeeds.Count && position < NuGetFallbackCache.MaximumFallbackPaths; position++)
            paths.Add(NuGetFallbackCache.GuestFallbackPathForSeed(basePath, position));
        return paths;
    }

    /// <summary>
    /// Per-seed steps for baseline baking. NuGet seeds materialize into the
    /// fallback folder inside the image; everything else keeps its configured
    /// destination. Pure: no host access.
    /// </summary>
    internal static IReadOnlyList<SeedProvisioningStep> PlanBakeSteps(IncusSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var steps = new List<SeedProvisioningStep>(options.PackageCacheSeeds.Count);
        if (!options.ShareNuGetPackageSeedsAsFallback)
        {
            for (var i = 0; i < options.PackageCacheSeeds.Count; i++)
                steps.Add(new SeedProvisioningStep(i, options.PackageCacheSeeds[i], NuGetSeedProvisioning.CopyToVmDest, null));
            return steps;
        }
        var partition = NuGetFallbackCache.PartitionNuGetSeeds(options.PackageCacheSeeds, options.GuestHome);
        var basePath = partition.NuGetSeeds.Count == 0 ? null : ValidatedBasePath(options);
        var positionByIndex = new Dictionary<int, int>();
        for (var position = 0; position < partition.NuGetSeeds.Count; position++)
            positionByIndex[partition.NuGetSeeds[position].Index] = position;
        for (var i = 0; i < options.PackageCacheSeeds.Count; i++)
        {
            // Only the first MaximumFallbackPaths positions have fallback
            // folders; further NuGet seeds keep the copy path.
            if (positionByIndex.TryGetValue(i, out var position)
                && position < NuGetFallbackCache.MaximumFallbackPaths)
            {
                steps.Add(new SeedProvisioningStep(
                    i,
                    options.PackageCacheSeeds[i],
                    NuGetSeedProvisioning.CopyToFallback,
                    NuGetFallbackCache.GuestFallbackPathForSeed(basePath!, position)));
            }
            else
            {
                steps.Add(new SeedProvisioningStep(i, options.PackageCacheSeeds[i], NuGetSeedProvisioning.CopyToVmDest, null));
            }
        }
        return steps;
    }

    /// <summary>
    /// Per-seed steps plus host mounts for a full-launch VM. Shareable seeds
    /// (directories under an allowed mount root) become read-only mounts;
    /// file seeds, missing sources, and disallowed sources keep the copy path
    /// (missing sources are skipped with a warning).
    /// </summary>
    internal static FullLaunchFallbackPlan PlanFullLaunch(
        IncusSandboxOptions options,
        string stagingRoot,
        Func<string, string?> environmentVariableReader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(environmentVariableReader);
        var steps = new List<SeedProvisioningStep>(options.PackageCacheSeeds.Count);
        var mounts = new List<SandboxMount>();
        var guestPaths = new List<string>();
        var warnings = new List<string>();
        void Warn(string message) => warnings.Add(SanitizeForLog(message));
        if (!options.ShareNuGetPackageSeedsAsFallback)
        {
            for (var i = 0; i < options.PackageCacheSeeds.Count; i++)
                steps.Add(new SeedProvisioningStep(i, options.PackageCacheSeeds[i], NuGetSeedProvisioning.CopyToVmDest, null));
            return new FullLaunchFallbackPlan(steps, mounts, guestPaths, warnings);
        }
        var partition = NuGetFallbackCache.PartitionNuGetSeeds(options.PackageCacheSeeds, options.GuestHome);
        var sharedIndexes = new HashSet<int>(partition.NuGetSeeds.Select(static seed => seed.Index));
        var basePath = partition.NuGetSeeds.Count == 0 ? null : ValidatedBasePath(options);
        var position = 0;
        for (var i = 0; i < options.PackageCacheSeeds.Count; i++)
        {
            var seed = options.PackageCacheSeeds[i];
            if (!sharedIndexes.Contains(i))
            {
                steps.Add(new SeedProvisioningStep(i, seed, NuGetSeedProvisioning.CopyToVmDest, null));
                continue;
            }
            var fallbackPath = position < NuGetFallbackCache.MaximumFallbackPaths
                ? NuGetFallbackCache.GuestFallbackPathForSeed(basePath!, position)
                : null;
            if (fallbackPath is null)
            {
                // No fallback folders remain; keep the copy path.
                steps.Add(new SeedProvisioningStep(i, seed, NuGetSeedProvisioning.CopyToVmDest, null));
                continue;
            }
            position++;
            guestPaths.Add(fallbackPath);
            string resolved;
            try
            {
                resolved = IncusBaselineProvisioning.ResolveHostSourcePath(
                    seed.HostSourcePath,
                    environmentVariableReader);
            }
            catch (Exception ex)
            {
                Warn($"NuGet package-cache seed {i} has an unresolvable host source and will be copied instead: {ex.Message}");
                steps.Add(new SeedProvisioningStep(i, seed, NuGetSeedProvisioning.CopyToVmDest, null));
                guestPaths.RemoveAt(guestPaths.Count - 1);
                continue;
            }
            if (!Directory.Exists(resolved))
            {
                if (File.Exists(resolved))
                {
                    steps.Add(new SeedProvisioningStep(i, seed, NuGetSeedProvisioning.CopyToVmDest, null));
                    guestPaths.RemoveAt(guestPaths.Count - 1);
                    continue;
                }
                Warn($"NuGet package-cache seed source '{resolved}' does not exist; skipping seed {i} (restore will fetch those packages).");
                steps.Add(new SeedProvisioningStep(i, seed, NuGetSeedProvisioning.SkippedMissing, null));
                guestPaths.RemoveAt(guestPaths.Count - 1);
                continue;
            }
            if (!IncusMountStaging.IsHostSourceAllowed(options, stagingRoot, resolved))
            {
                Warn($"NuGet package-cache seed source '{resolved}' is outside the allowed host mount roots; copying seed {i} instead of sharing it.");
                steps.Add(new SeedProvisioningStep(i, seed, NuGetSeedProvisioning.CopyToVmDest, null));
                guestPaths.RemoveAt(guestPaths.Count - 1);
                continue;
            }
            mounts.Add(NuGetFallbackCache.BuildFallbackMount(resolved, fallbackPath));
            steps.Add(new SeedProvisioningStep(i, seed, NuGetSeedProvisioning.MountedShare, null));
        }
        return new FullLaunchFallbackPlan(steps, mounts, guestPaths, warnings);
    }

    /// <summary>
    /// Returns the spec augmented with fallback mounts and the merged
    /// <c>NUGET_FALLBACK_PACKAGES</c> environment entry. The input spec is
    /// not mutated.
    /// </summary>
    internal static SandboxSpec ApplyToSpec(
        SandboxSpec spec,
        IReadOnlyList<SandboxMount> fallbackMounts,
        IReadOnlyList<string> fallbackGuestPaths)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(fallbackMounts);
        ArgumentNullException.ThrowIfNull(fallbackGuestPaths);
        if (fallbackMounts.Count == 0 && fallbackGuestPaths.Count == 0)
            return spec;
        var mounts = new List<SandboxMount>(spec.Mounts.Count + fallbackMounts.Count);
        mounts.AddRange(spec.Mounts);
        mounts.AddRange(fallbackMounts);
        var environment = NuGetFallbackCache.MergeFallbackEnvironment(spec.Environment, fallbackGuestPaths);
        var augmented = spec with { Mounts = mounts, Environment = environment };
        IncusSandbox.ValidateEnvironment(augmented.Environment, nameof(spec));
        return augmented;
    }

    // Log-bound warning text embeds operator-configured host paths
    // (untrusted input), so control characters are stripped to keep one
    // warning on one log line.
    private static string SanitizeForLog(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > 4096)
            message = message[..4096];
        var builder = new System.Text.StringBuilder(message.Length);
        foreach (var c in message)
        {
            if (!char.IsControl(c))
                builder.Append(c);
        }
        return builder.ToString();
    }

    private static string ValidatedBasePath(IncusSandboxOptions options)
    {
        var basePath = options.NuGetFallbackGuestPath;
        if (string.IsNullOrWhiteSpace(basePath) || !basePath.StartsWith('/'))
            throw new InvalidOperationException("The Incus NuGet fallback guest path must be an absolute path.");
        return basePath;
    }
}
