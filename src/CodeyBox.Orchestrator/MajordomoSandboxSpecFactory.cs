using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure construction of the majordomo <see cref="SandboxSpec"/>: restricted
/// network profile, read-only repository mounts, and a minimal environment
/// that exposes only the MCP tool-server URL (plus the configured model
/// credential) — never an orchestrator API key or host credential.
/// </summary>
public static class MajordomoSandboxSpecFactory
{
    /// <summary>
    /// Environment variable carrying the MCP tool-server URL inside the sandbox.
    /// The sandbox's only route to mutation is tools; it holds no orchestrator
    /// API key of its own beyond the majordomo identity.
    /// </summary>
    public const string McpUrlVariable = "CODEYBOX_MAJORDOMO_MCP_URL";

    /// <summary>Guest path prefix for read-only repository mounts.</summary>
    public const string RepoMountPrefix = "/repos/";

    /// <summary>
    /// Builds the sandbox spec. Throws when <paramref name="options"/> has no
    /// usable network profile — the caller must fail closed rather than fall
    /// back to open egress.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no restricted network profile is configured.
    /// </exception>
    public static SandboxSpec BuildSpec(
        MajordomoSandboxOptions options,
        string mcpServerUrl,
        string imageReference = "codeybox-majordomo")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpServerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var profile = options.NetworkProfile?.Trim() ?? string.Empty;
        if (profile.Length == 0)
            throw new InvalidOperationException(
                "Majordomo sandbox requires a restricted network profile: " +
                $"{MajordomoSandboxOptions.SectionName}:NetworkProfile is blank. " +
                "Refusing to create the sandbox with open egress.");

        var mounts = new List<SandboxMount>(options.RepositoryPaths.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var repo in options.RepositoryPaths)
        {
            if (string.IsNullOrWhiteSpace(repo))
                continue;
            var hostPath = repo.Trim();
            if (!seen.Add(hostPath))
                continue;
            mounts.Add(new SandboxMount
            {
                HostPath = hostPath,
                SandboxPath = RepoMountPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ReadOnly = true,
            });
            index++;
        }

        var allowedHosts = new List<string>(1 + options.AdditionalAllowedHosts.Count)
        {
            options.OrchestratorHost.Trim(),
        };
        foreach (var extra in options.AdditionalAllowedHosts)
        {
            var host = extra.Trim();
            if (host.Length == 0 || allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                continue;
            allowedHosts.Add(host);
        }

        return new SandboxSpec
        {
            ImageReference = imageReference,
            Purpose = SandboxPurpose.WorkItem,
            Mounts = mounts,
            Environment = new Dictionary<string, string>
            {
                [McpUrlVariable] = mcpServerUrl,
            },
            Network = new SandboxNetworkPolicy
            {
                ProfileName = profile,
                AllowedHosts = allowedHosts,
            },
            Flavor = SandboxProfileFlavor.Headless,
            WorkingDirectory = "/work",
        };
    }

    /// <summary>
    /// Fails closed when the majordomo <paramref name="requiredProfile"/> is not
    /// among the host's <paramref name="allowedProfiles"/>: the sandbox is refused
    /// rather than silently falling back to open egress. An empty allowlist means
    /// "all profiles accepted" and passes through.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the required profile is unavailable.
    /// </exception>
    public static void EnsureProfileAvailable(
        IReadOnlyList<string> allowedProfiles,
        string requiredProfile)
    {
        ArgumentNullException.ThrowIfNull(allowedProfiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredProfile);
        if (allowedProfiles.Count == 0)
            return;
        var required = requiredProfile.Trim();
        foreach (var candidate in allowedProfiles)
        {
            if (string.Equals(candidate?.Trim(), required, StringComparison.Ordinal))
                return;
            if (string.Equals(candidate?.Trim(), "*", StringComparison.Ordinal))
                return;
        }
        throw new InvalidOperationException(
            $"Majordomo sandbox network profile '{required}' is not available on this host " +
            $"({allowedProfiles.Count} profile(s) accepted: {string.Join(", ", allowedProfiles)}). " +
            "Refusing to fall back to open egress.");
    }

    /// <summary>
    /// Enforces the host-side egress path for one creation: the provider kind must
    /// be classified as enforcing egress for the named profile. A
    /// <c>NotEnforced</c> provider is refused explicitly, never quietly used.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the provider kind cannot enforce the required profile.
    /// </exception>
    public static void EnsureProviderEnforces(string providerKind, string requiredProfile) =>
        SandboxEgressPolicy.EnsureEnforcedEgressForProfile(providerKind, requiredProfile);
}
