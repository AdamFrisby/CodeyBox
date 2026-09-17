namespace CodeyBox.Core;

/// <summary>
/// Well-known sandbox capability tags. Matched using exact ordinal equality
/// (case-insensitively per <see cref="ExecutorEligibility"/>) against phase
/// and work-item placement requirements.
/// </summary>
public static class SandboxCapabilities
{
    /// <summary>Provider can build and provision reusable baseline images.</summary>
    public const string BaselineBake = "baseline-bake";

    /// <summary>Provider supports package cache seeding into sandbox environments.</summary>
    public const string CacheSeeding = "cache-seeding";

    /// <summary>Provider monitors host disk space and enforces disk thresholds.</summary>
    public const string DiskGuard = "disk-guard";

    /// <summary>Provider can publish guest network ports to the host or callers.</summary>
    public const string PortPublishing = "port-publishing";

    /// <summary>Provider can suspend running sandboxes and resume them across restarts.</summary>
    public const string SuspendResume = "suspend-resume";

    /// <summary>Provider supports graceful host-shutdown teardown modes (stop/preserve, dispose).</summary>
    public const string Teardown = "teardown";

    /// <summary>
    /// All well-known sandbox capability tags.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        BaselineBake,
        CacheSeeding,
        DiskGuard,
        PortPublishing,
        SuspendResume,
        Teardown,
    ];

    /// <summary>
    /// Formats a human-readable capability matrix string for the given registered providers,
    /// suitable for startup logging.
    /// </summary>
    public static string FormatMatrix(IEnumerable<ISandboxProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return string.Join("; ", providers.Select(p =>
        {
            var caps = p.DeclaredCapabilities.Count > 0
                ? string.Join(", ", p.DeclaredCapabilities)
                : "none";
            return $"{p.Name}: [{caps}]";
        }));
    }
}

/// <summary>
/// Alias for <see cref="SandboxCapabilities"/> tags.
/// </summary>
public static class SandboxCapabilityTags
{
    public const string BaselineBake = SandboxCapabilities.BaselineBake;
    public const string CacheSeeding = SandboxCapabilities.CacheSeeding;
    public const string DiskGuard = SandboxCapabilities.DiskGuard;
    public const string PortPublishing = SandboxCapabilities.PortPublishing;
    public const string SuspendResume = SandboxCapabilities.SuspendResume;
    public const string Teardown = SandboxCapabilities.Teardown;
}

/// <summary>
/// Alias for <see cref="SandboxCapabilities"/> well-known tags.
/// </summary>
public static class WellKnownSandboxCapabilities
{
    public const string BaselineBake = SandboxCapabilities.BaselineBake;
    public const string CacheSeeding = SandboxCapabilities.CacheSeeding;
    public const string DiskGuard = SandboxCapabilities.DiskGuard;
    public const string PortPublishing = SandboxCapabilities.PortPublishing;
    public const string SuspendResume = SandboxCapabilities.SuspendResume;
    public const string Teardown = SandboxCapabilities.Teardown;
}
